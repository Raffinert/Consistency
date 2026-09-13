namespace Raffinert.Relations.Tests;

public sealed partial class RuntimeTests
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

}
