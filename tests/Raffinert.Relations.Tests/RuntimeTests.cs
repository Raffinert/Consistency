namespace Raffinert.Relations.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void Adds_and_queries_with_a_composite_hash_index_and_full_predicate()
    {
        var model = CreateLineModel(out var invoices, out var poLines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var matching = Line("PO-100", "A", enabled: true);
        var sameKeyButDisabled = Line("PO-100", "A", enabled: false);
        var other = Line("PO-200", "A", enabled: true);

        runtime.Add(invoices, invoice);
        runtime.Add(poLines, matching);
        runtime.Add(poLines, sameKeyButDisabled);
        runtime.Add(poLines, other);

        Assert.Equal([matching], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Indexed_property_change_removes_and_then_adds_a_match()
    {
        var model = CreateLineModel(out var invoices, out var poLines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var line = Line("PO-100", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(poLines, line);

        line.ItemNumber = "B";
        runtime.Apply(Change.Property(line, x => x.ItemNumber, "A", "B"));
        Assert.Empty(runtime.Related(relation, invoice));

        line.ItemNumber = "A";
        runtime.Apply(Change.Property(poLines, line, x => x.ItemNumber, "B", "A"));
        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Removing_a_right_object_removes_it_from_relation_results()
    {
        var model = CreateLineModel(out var invoices, out var poLines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var line = Line("PO-100", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(poLines, line);

        Assert.True(runtime.Remove(poLines, line));

        Assert.Empty(runtime.Related(invoice, relation));
        Assert.False(runtime.Remove(poLines, line));
    }

    [Fact]
    public void Opaque_predicate_falls_back_to_a_semantically_correct_scan()
    {
        var model = new InvariantModelBuilder();
        var left = model.Objects<CodeHolder>().Key(x => x.Id);
        var right = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(left, right).Where((a, b) => MatchesPrefix(a.Code, b.ItemNumber));
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "ITEM-123" };
        var match = Line("PO", "ITEM");
        var miss = Line("PO", "OTHER");
        runtime.Add(left, source);
        runtime.Add(right, match);
        runtime.Add(right, miss);

        Assert.Equal([match], runtime.Related(relation, source));
        Assert.Contains("Access: Scan", compiled.DebugView);
        Assert.Contains("Dependency analysis: Incomplete", compiled.DebugView);
    }

    [Fact]
    public void Nested_property_change_reindexes_referencing_roots()
    {
        var model = new InvariantModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var orders = model.Objects<PurchaseOrder>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            invoice.PurchaseOrderNumber == line.PurchaseOrder!.Number &&
            invoice.ItemNumber == line.ItemNumber);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var order = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-100" };
        var line = Line("ignored", "A");
        line.PurchaseOrder = order;
        runtime.Add(invoices, invoice);
        runtime.Add(orders, order);
        runtime.Add(lines, line);

        Assert.Equal([line], runtime.Related(relation, invoice));

        order.Number = "PO-200";
        runtime.Apply(Change.Property(orders, order, x => x.Number, "PO-100", "PO-200"));

        Assert.Empty(runtime.Related(relation, invoice));
        Assert.Contains("PurchaseOrderLine.PurchaseOrder.Number", compiled.DebugView);
    }

    [Fact]
    public void Reference_navigation_change_updates_reverse_navigation_and_index()
    {
        var model = new InvariantModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var orders = model.Objects<PurchaseOrder>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines)
            .Where((invoice, line) => invoice.PurchaseOrderNumber == line.PurchaseOrder!.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-2", "A");
        var first = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-1" };
        var second = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-2" };
        var line = Line("", "A");
        line.PurchaseOrder = first;
        runtime.Add(invoices, invoice);
        runtime.Add(orders, first);
        runtime.Add(orders, second);
        runtime.Add(lines, line);

        line.PurchaseOrder = second;
        runtime.Apply(Change.Property(lines, line, x => x.PurchaseOrder, first, second));

        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Type_inference_works_when_a_type_has_one_object_set()
    {
        var model = CreateLineModel(out _, out _, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "A");
        var line = Line("PO", "A");

        runtime.Add(invoice);
        runtime.Add(line);

        Assert.Equal([line], runtime.Related(relation, invoice));
        Assert.True(runtime.Remove(line));
    }

    private static InvariantModelBuilder CreateLineModel(
        out ObjectSetBuilder<InvoiceLine> invoices,
        out ObjectSetBuilder<PurchaseOrderLine> poLines,
        out Relation<InvoiceLine, PurchaseOrderLine> relation)
    {
        var model = new InvariantModelBuilder();
        invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        poLines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        relation = model.Relation(invoices, poLines).Where((invoice, line) =>
            invoice.PurchaseOrderNumber == line.PurchaseOrderNumber &&
            invoice.ItemNumber == line.ItemNumber &&
            line.Enabled);
        return model;
    }

    private static InvoiceLine Invoice(string order, string item) => new()
    {
        Id = Guid.NewGuid(),
        PurchaseOrderNumber = order,
        ItemNumber = item
    };

    private static PurchaseOrderLine Line(string order, string item, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        PurchaseOrderNumber = order,
        ItemNumber = item,
        Enabled = enabled
    };

    private static bool MatchesPrefix(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal);
}
