namespace Raffinert.Relations.Tests;

public sealed class CollectionNavigationTests
{
    [Fact]
    public void Collection_add_and_remove_update_relation_derived_and_invariant_state()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<CollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber));
        var derived = model.Derived(orders).Using(relation)
            .Compute((_, matches) => matches.Count);
        var invariant = model.Invariant(orders).Using(derived)
            .Must((_, count) => count == 0);
        var runtime = model.Build().CreateRuntime();
        var order = new CollectionOrder { Id = Guid.NewGuid() };
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "A" };
        var line = new CollectionOrderLine { ItemNumber = "A" };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoice);
        Assert.Equal(0, runtime.Get(derived, order));
        Assert.True(runtime.Evaluate(invariant, order));

        order.Lines.Add(line);
        runtime.Apply(Change.CollectionAdd(orders, order, value => value.Lines, line));

        Assert.Equal([invoice], runtime.Related(relation, order));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(derived, order));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(invariant, order));
        Assert.Equal(1, runtime.Get(derived, order));

        order.Lines.Remove(line);
        runtime.Apply(Change.CollectionRemove(order, value => value.Lines, line));

        Assert.Empty(runtime.Related(relation, order));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(derived, order));
    }

    [Fact]
    public void Collection_item_change_resolves_its_owner_incrementally()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<CollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber));
        var runtime = model.Build().CreateRuntime();
        var line = new CollectionOrderLine { ItemNumber = "A" };
        var order = new CollectionOrder { Id = Guid.NewGuid() };
        order.Lines.Add(line);
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "B" };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoice);
        Assert.Empty(runtime.Related(relation, order));

        line.ItemNumber = "B";
        var impact = runtime.Apply(Change.Property(line, value => value.ItemNumber, "A", "B"));

        Assert.Equal([invoice], runtime.Related(relation, order));
        Assert.Equal(1, impact.Semantic.AffectedRoots);
    }

    [Fact]
    public void Collection_reset_refreshes_owner_membership()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<CollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber));
        var runtime = model.Build().CreateRuntime();
        var order = new CollectionOrder { Id = Guid.NewGuid() };
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "B" };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoice);

        order.Lines.Add(new CollectionOrderLine { ItemNumber = "B" });
        runtime.Apply(Change.CollectionReset(orders, order, value => value.Lines));

        Assert.Equal([invoice], runtime.Related(relation, order));
    }

    [Fact]
    public void Collection_change_must_describe_the_current_domain_state()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<CollectionOrder>().Key(order => order.Id);
        var runtime = model.Build().CreateRuntime();
        var order = new CollectionOrder { Id = Guid.NewGuid() };
        var line = new CollectionOrderLine { ItemNumber = "A" };
        runtime.Add(orders, order);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.Apply(Change.CollectionAdd(orders, order, value => value.Lines, line)));
    }
}
