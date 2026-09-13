namespace Raffinert.Relations.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public void Runtime_diagnostics_distinguish_query_indexes_from_exact_materialization()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var queryOnly = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var materialized = model.Relation(sources, items).Where((source, item) => source.Enabled == item.Enabled);
        model.Derived(sources).Using(materialized).Compute((source, matches) => matches.Count);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime(new RuntimeDiagnosticOptions
        {
            MaterializedPairWarningThreshold = 5,
            AverageFanOutWarningThreshold = 10
        });
        var lefts = new[]
        {
            new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = true },
            new CodeHolder { Id = Guid.NewGuid(), Code = "B", Enabled = true }
        };
        var rights = Enumerable.Range(0, 3).Select(index => new CodeHolder
        {
            Id = Guid.NewGuid(),
            Code = index == 0 ? "A" : "X",
            Enabled = true
        }).ToArray();
        foreach (var source in lefts)
            runtime.Add(sources, source);
        foreach (var item in rights)
            runtime.Add(items, item);

        var diagnostics = runtime.Diagnostics;

        var query = diagnostics.Relations[0];
        Assert.Equal(0, query.RelationId);
        Assert.Equal(RelationMaterializationMode.None, query.Materialization);
        Assert.Equal(3, query.ForwardIndexEntries);
        Assert.Equal(0, query.ReverseIndexEntries);
        Assert.Equal(0, query.MaterializedPairCount);
        Assert.False(query.HasDensityWarning);

        var exact = diagnostics.Relations[1];
        Assert.Equal(RelationMaterializationMode.ExactPropagation, exact.Materialization);
        Assert.Equal(3, exact.ForwardIndexEntries);
        Assert.Equal(2, exact.ReverseIndexEntries);
        Assert.Equal(6, exact.MaterializedPairCount);
        Assert.Equal(3, exact.AverageFanOut);
        Assert.True(exact.HasDensityWarning);
        Assert.Contains("Relation materialization: None", compiled.DebugView);
        Assert.Contains("Relation materialization: ExactPropagation", compiled.DebugView);
        _ = queryOnly;
    }

    [Fact]
    public void Opaque_predicate_falls_back_to_a_semantically_correct_scan()
    {
        var model = new RelationModelBuilder();
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
        Assert.Contains("Access plan: Scan", compiled.DebugView);
        Assert.Contains("Dependency analysis: ContainsOpaqueCode", compiled.DebugView);
    }

    [Fact]
    public void Nested_property_change_reindexes_referencing_roots()
    {
        var model = new RelationModelBuilder();
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
        var model = new RelationModelBuilder();
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

    [Fact]
    public void Null_guarded_nested_relation_is_safe_and_reindexes_when_navigation_is_assigned()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.PurchaseOrder != null &&
            invoice.PurchaseOrderNumber == line.PurchaseOrder.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var line = Line("ignored", "A");

        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        Assert.Empty(runtime.Related(relation, invoice));

        var order = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-100" };
        line.PurchaseOrder = order;
        runtime.Apply(Change.Property(lines, line, x => x.PurchaseOrder, null, order));

        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Hash_and_scan_plans_remain_equivalent_after_runtime_changes()
    {
        var hashModel = new RelationModelBuilder();
        var hashLeft = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRight = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRelation = hashModel.Relation(hashLeft, hashRight).Where((a, b) => a.Code == b.Code);
        var hashRuntime = hashModel.Build().CreateRuntime();

        var scanModel = new RelationModelBuilder();
        var scanLeft = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRight = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRelation = scanModel.Relation(scanLeft, scanRight).Where((a, b) => CodesEqual(a, b));
        var scanRuntime = scanModel.Build().CreateRuntime();

        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        hashRuntime.Add(hashLeft, source);
        scanRuntime.Add(scanLeft, source);
        foreach (var candidate in new[] { first, second })
        {
            hashRuntime.Add(hashRight, candidate);
            scanRuntime.Add(scanRight, candidate);
        }

        AssertEquivalent();

        second.Code = "A";
        hashRuntime.Apply(Change.Property(hashRight, second, x => x.Code, "B", "A"));
        scanRuntime.Apply(Change.Property(scanRight, second, x => x.Code, "B", "A"));
        AssertEquivalent();

        Assert.True(hashRuntime.Remove(hashRight, first));
        Assert.True(scanRuntime.Remove(scanRight, first));
        AssertEquivalent();

        void AssertEquivalent() => Assert.Equal(
            scanRuntime.Related(scanRelation, source).Select(item => item.Id).Order(),
            hashRuntime.Related(hashRelation, source).Select(item => item.Id).Order());
    }

    [Fact]
    public void Arbitrary_depth_change_from_an_unregistered_nested_object_reindexes_all_roots()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.PurchaseOrder != null &&
            line.PurchaseOrder.Supplier != null &&
            line.PurchaseOrder.Supplier.Country != null &&
            invoice.PurchaseOrderNumber == line.PurchaseOrder.Supplier.Country.Code);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("NO", "A");
        var country = new Country { Id = Guid.NewGuid(), Code = "MATCH" };
        var supplier = new Supplier { Id = Guid.NewGuid(), Country = country };
        var order = new PurchaseOrder { Id = Guid.NewGuid(), Supplier = supplier };
        var first = Line("", "A");
        var second = Line("", "B");
        first.PurchaseOrder = order;
        second.PurchaseOrder = order;
        runtime.Add(invoices, invoice);
        runtime.Add(lines, first);
        runtime.Add(lines, second);

        Assert.Empty(runtime.Related(relation, invoice));

        invoice.PurchaseOrderNumber = "MATCH";
        runtime.Apply(Change.Property(invoices, invoice, x => x.PurchaseOrderNumber, "NO", "MATCH"));
        Assert.Equal(2, runtime.Related(relation, invoice).Count);

        country.Code = "OTHER";
        var impact = runtime.Apply(Change.Property(country, x => x.Code, "MATCH", "OTHER"));

        Assert.Empty(runtime.Related(relation, invoice));
        Assert.Equal(1, impact.Access.ReindexedRelations);
        Assert.Equal(2, impact.Access.ReindexedRoots);
        Assert.Equal(1, impact.Semantic.AffectedRelations);
        Assert.Equal(2, impact.Semantic.AffectedRoots);
    }

    [Fact]
    public void Reference_navigation_can_change_from_object_to_null()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.PurchaseOrder != null && invoice.PurchaseOrderNumber == line.PurchaseOrder.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "A");
        var order = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO" };
        var line = Line("", "A");
        line.PurchaseOrder = order;
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);
        Assert.Equal([line], runtime.Related(relation, invoice));

        line.PurchaseOrder = null;
        runtime.Apply(Change.Property(lines, line, x => x.PurchaseOrder, order, null));

        Assert.Empty(runtime.Related(relation, invoice));
    }

}
