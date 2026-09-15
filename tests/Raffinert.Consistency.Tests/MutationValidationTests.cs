namespace Raffinert.Consistency.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public void Residual_property_change_has_semantic_but_not_access_impact()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER", "A");
        var line = Line("ORDER", "A", enabled: true);
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.Enabled = false;
        var impact = runtime.Apply(Change.Property(lines, line, x => x.Enabled, true, false));

        Assert.Empty(runtime.Related(relation, invoice));
        Assert.Equal(0, impact.Access.ReindexedRelations);
        Assert.Equal(1, impact.Semantic.AffectedRelations);
    }

    [Fact]
    public void Change_set_reindexes_a_root_once_when_two_key_fields_change()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER-2", "B");
        var line = Line("ORDER-1", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.OrderNumber = "ORDER-2";
        line.ItemNumber = "B";
        var impact = runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.OrderNumber, "ORDER-1", "ORDER-2"),
            Change.Property(lines, line, x => x.ItemNumber, "A", "B")));

        Assert.Equal([line], runtime.Related(relation, invoice));
        Assert.Equal(1, impact.Access.ReindexedRelations);
        Assert.Equal(1, impact.Access.ReindexedRoots);
    }

    [Fact]
    public void Contiguous_repeated_changes_are_normalized_to_their_net_effect()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER", "C");
        var line = Line("ORDER", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.ItemNumber = "C";
        var impact = runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            Change.Property(lines, line, x => x.ItemNumber, "B", "C")),
            ChangeValidationMode.StrictNewValue);

        Assert.Equal([line], runtime.Related(relation, invoice));
        Assert.Equal(1, impact.Access.ReindexedRoots);
    }

    [Fact]
    public void Conflicting_repeated_changes_are_rejected_before_runtime_updates()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER", "C");
        var line = Line("ORDER", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.ItemNumber = "C";
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            Change.Property(lines, line, x => x.ItemNumber, "A", "C"))));

        line.ItemNumber = "A";
        runtime.Apply(Change.Property(lines, line, x => x.ItemNumber, "C", "A"));
        Assert.Empty(runtime.Related(relation, invoice));
    }

    [Fact]
    public void Strict_validation_rejects_an_unapplied_domain_mutation()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER", "B");
        var line = Line("ORDER", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        Assert.Throws<InvalidOperationException>(() => runtime.Apply(
            Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            ChangeValidationMode.StrictNewValue));

        line.ItemNumber = "B";
        runtime.Apply(Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            ChangeValidationMode.StrictNewValue);
        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Invalid_change_rejects_the_entire_change_set_before_index_updates()
    {
        var model = new ConsistencyModelBuilder();
        var invoices = model.Objects<RequestLine>().Key(x => x.Id);
        var lines = model.Objects<OrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.Order != null && invoice.OrderNumber == line.Order.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER-2", "A");
        var first = new Order { Id = Guid.NewGuid(), Number = "ORDER-1" };
        var second = new Order { Id = Guid.NewGuid(), Number = "ORDER-2" };
        var line = Line("", "A");
        line.Order = first;
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        var oldId = line.Id;
        line.Order = second;
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.Order, first, second),
            Change.Property(lines, line, x => x.Id, oldId, line.Id))));

        runtime.Apply(Change.Property(lines, line, x => x.Order, first, second));
        Assert.Equal([line], runtime.Related(relation, invoice));
    }

}
