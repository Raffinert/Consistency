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

    [Fact]
    public void Equal_items_are_tracked_by_reference_identity()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<ReplaceableCollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.Any(line => ReferenceEquals(line, invoice.Tag)));
        var runtime = model.Build().CreateRuntime();
        var first = new ValueEqualCollectionLine { ItemNumber = "A" };
        var equalButDistinct = new ValueEqualCollectionLine { ItemNumber = "A" };
        var order = new ReplaceableCollectionOrder { Id = Guid.NewGuid(), Lines = [first] };
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), Tag = equalButDistinct };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoice);
        Assert.Empty(runtime.Related(relation, order));

        order.Lines.Add(equalButDistinct);
        runtime.Apply(Change.CollectionAdd(orders, order, value => value.Lines, equalButDistinct));

        Assert.Equal([invoice], runtime.Related(relation, order));
    }

    [Fact]
    public void Duplicate_references_have_set_semantics()
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
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "A" };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoice);

        order.Lines.Add(line);
        runtime.Apply(Change.CollectionAdd(orders, order, value => value.Lines, line));
        order.Lines.Remove(line);
        runtime.Apply(Change.CollectionReset(orders, order, value => value.Lines));

        Assert.Equal([invoice], runtime.Related(relation, order));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.Apply(Change.CollectionRemove(orders, order, value => value.Lines, line)));

        order.Lines.Remove(line);
        runtime.Apply(Change.CollectionRemove(orders, order, value => value.Lines, line));
        Assert.Empty(runtime.Related(relation, order));
    }

    [Fact]
    public void Collection_reset_observes_order_changes()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<ReplaceableCollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.First().ItemNumber == invoice.ItemNumber);
        var runtime = model.Build().CreateRuntime();
        var first = new ValueEqualCollectionLine { ItemNumber = "A" };
        var second = new ValueEqualCollectionLine { ItemNumber = "B" };
        var order = new ReplaceableCollectionOrder { Id = Guid.NewGuid(), Lines = [first, second] };
        var invoiceA = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "A" };
        var invoiceB = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "B" };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoiceA);
        runtime.Add(invoices, invoiceB);
        Assert.Equal([invoiceA], runtime.Related(relation, order));

        order.Lines.Reverse();
        runtime.Apply(Change.CollectionReset(orders, order, value => value.Lines));

        Assert.Equal([invoiceB], runtime.Related(relation, order));
    }

    [Fact]
    public void Collection_property_replacement_refreshes_navigation_membership()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<ReplaceableCollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber));
        var runtime = model.Build().CreateRuntime();
        var oldLines = new List<ValueEqualCollectionLine> { new() { ItemNumber = "A" } };
        var newLines = new List<ValueEqualCollectionLine> { new() { ItemNumber = "B" } };
        var order = new ReplaceableCollectionOrder { Id = Guid.NewGuid(), Lines = oldLines };
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "B" };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoice);
        order.Lines = newLines;

        runtime.Apply(Change.Property(orders, order, value => value.Lines, oldLines, newLines));

        Assert.Equal([invoice], runtime.Related(relation, order));
    }

    [Fact]
    public void Nested_collection_item_changes_resolve_registered_roots()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<ReplaceableCollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Container.Lines.Any(line => line.ItemNumber == invoice.ItemNumber));
        var runtime = model.Build().CreateRuntime();
        var line = new ValueEqualCollectionLine { ItemNumber = "A" };
        var order = new ReplaceableCollectionOrder { Id = Guid.NewGuid() };
        order.Container.Lines.Add(line);
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "B" };
        runtime.Add(orders, order);
        runtime.Add(invoices, invoice);
        line.ItemNumber = "B";

        runtime.Apply(Change.Property(line, value => value.ItemNumber, "A", "B"));

        Assert.Equal([invoice], runtime.Related(relation, order));
    }

    [Fact]
    public void Removed_item_mutations_no_longer_affect_former_owner()
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
        order.Lines.Remove(line);
        runtime.Apply(Change.CollectionRemove(orders, order, value => value.Lines, line));
        line.ItemNumber = "B";

        var impact = runtime.Apply(Change.Property(line, value => value.ItemNumber, "A", "B"));

        Assert.Empty(runtime.Related(relation, order));
        Assert.Equal(0, impact.Semantic.AffectedRoots);
    }

    [Fact]
    public void Shared_item_mutation_affects_every_owner_by_reference()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<CollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber));
        var runtime = model.Build().CreateRuntime();
        var shared = new CollectionOrderLine { ItemNumber = "A" };
        var first = new CollectionOrder { Id = Guid.NewGuid() };
        var second = new CollectionOrder { Id = Guid.NewGuid() };
        first.Lines.Add(shared);
        second.Lines.Add(shared);
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "B" };
        runtime.Add(orders, first);
        runtime.Add(orders, second);
        runtime.Add(invoices, invoice);
        shared.ItemNumber = "B";

        var impact = runtime.Apply(Change.Property(shared, value => value.ItemNumber, "A", "B"));

        Assert.Equal([invoice], runtime.Related(relation, first));
        Assert.Equal([invoice], runtime.Related(relation, second));
        Assert.Equal(2, impact.Semantic.AffectedRoots);
    }

    [Fact]
    public void Collection_and_lifecycle_changes_commit_in_one_mutation_batch()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<CollectionOrder>().Key(order => order.Id);
        var invoices = model.Objects<InvoiceLine>().Key(invoice => invoice.Id);
        var relation = model.Relation(orders, invoices).Where((order, invoice) =>
            order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber));
        var runtime = model.Build().CreateRuntime();
        var order = new CollectionOrder { Id = Guid.NewGuid() };
        var line = new CollectionOrderLine { ItemNumber = "A" };
        var invoice = new InvoiceLine { Id = Guid.NewGuid(), ItemNumber = "A" };
        runtime.Add(orders, order);
        order.Lines.Add(line);

        runtime.Apply(MutationSet.Create(
            Change.CollectionAdd(orders, order, value => value.Lines, line),
            Change.Add(invoices, invoice)));

        Assert.Equal([invoice], runtime.Related(relation, order));
    }
}
