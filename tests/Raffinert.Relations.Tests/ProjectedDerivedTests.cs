namespace Raffinert.Relations.Tests;

public sealed class ProjectedDerivedTests
{
    [Fact]
    public void Cross_set_upstream_invalidates_only_sources_referencing_the_changed_owner()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(value => value.Total);
        var validity = model.Derived(links).Using(value => value.Order, total)
            .Compute((link, currentTotal) => link.CapturedTotal == currentTotal);
        var firstOrder = new Order { Total = 10 };
        var secondOrder = new Order { Total = 20 };
        var first = new Link { Order = firstOrder, CapturedTotal = 10 };
        var second = new Link { Order = secondOrder, CapturedTotal = 20 };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(orders, [firstOrder, secondOrder]);
            seed.Add(links, [first, second]);
        });
        Assert.True(runtime.Get(validity, first));
        Assert.True(runtime.Get(validity, second));

        firstOrder.Total = 11;
        runtime.Apply(Change.Property(orders, firstOrder, value => value.Total, 10, 11));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(validity, first));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(validity, second));
        Assert.False(runtime.Get(validity, first));
    }

    [Fact]
    public void Replacing_projected_reference_dirties_the_downstream_value()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Compute(value => value.Total);
        var validity = model.Derived(links).Using(value => value.Order, total)
            .Compute((link, currentTotal) => link.CapturedTotal == currentTotal);
        var oldOrder = new Order { Total = 10 };
        var newOrder = new Order { Total = 20 };
        var link = new Link { Order = oldOrder, CapturedTotal = 10 };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(orders, [oldOrder, newOrder]);
            seed.Add(links, [link]);
        });
        Assert.True(runtime.Get(validity, link));

        link.Order = newOrder;
        runtime.Apply(Change.Property(links, link, value => value.Order, oldOrder, newOrder));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(validity, link));
        Assert.False(runtime.Get(validity, link));
    }

    [Fact]
    public void Bootstrap_rejects_missing_or_wrong_set_projected_target()
    {
        var model = new RelationModelBuilder();
        var upstream = model.Objects<Order>().Key(value => value.Id);
        var other = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(upstream).Compute(value => value.Total);
        _ = model.Derived(links).Using(value => value.Order, total)
            .Compute((_, currentTotal) => currentTotal);
        var compiled = model.Build();
        var target = new Order { Total = 1 };
        var link = new Link { Order = target, CapturedTotal = 1 };

        Assert.Throws<InvalidOperationException>(() => compiled.CreateRuntime(seed =>
            seed.Add(links, [link])));
        Assert.Throws<InvalidOperationException>(() => compiled.CreateRuntime(seed =>
        {
            seed.Add(other, [target]);
            seed.Add(links, [link]);
        }));
    }

    [Fact]
    public void Lifecycle_validation_uses_final_batch_state()
    {
        var scenario = CreateIntegrityScenario();
        var target = new Order { Total = 1 };
        var link = new Link { Order = target, CapturedTotal = 1 };

        scenario.Runtime.Apply(MutationSet.Create(
            Change.Add(scenario.Links, link),
            Change.Add(scenario.Orders, target)));
        Assert.Equal(1, scenario.Runtime.Get(scenario.Value, link));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.Prepare(
            MutationSet.Create(Change.Remove(scenario.Orders, target))));

        scenario.Runtime.Apply(MutationSet.Create(
            Change.Remove(scenario.Orders, target),
            Change.Remove(scenario.Links, link)));
        Assert.Equal(2, scenario.Runtime.Version);
    }

    [Fact]
    public void Retarget_and_old_target_removal_are_atomic_and_indexed()
    {
        var scenario = CreateIntegrityScenario();
        var oldTarget = new Order { Total = 1 };
        var newTarget = new Order { Total = 2 };
        var link = new Link { Order = oldTarget, CapturedTotal = 1 };
        scenario.Runtime.Apply(MutationSet.Create(
            Change.Add(scenario.Orders, oldTarget),
            Change.Add(scenario.Orders, newTarget),
            Change.Add(scenario.Links, link)));
        Assert.Equal(1, scenario.Runtime.Get(scenario.Value, link));

        link.Order = newTarget;
        scenario.Runtime.Apply(MutationSet.Create(
            Change.Property(scenario.Links, link, value => value.Order, oldTarget, newTarget),
            Change.Remove(scenario.Orders, oldTarget)));

        Assert.Equal(2, scenario.Runtime.Get(scenario.Value, link));
        newTarget.Total = 3;
        scenario.Runtime.Apply(Change.Property(
            scenario.Orders, newTarget, value => value.Total, 2, 3));
        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Value, link));
    }

    [Fact]
    public void Unsupported_or_null_projection_is_rejected()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Compute(value => value.Total);
        Assert.Throws<ArgumentException>(() => model.Derived(links)
            .Using(value => Select(value), total)
            .Compute((_, value) => value));

        var validModel = new RelationModelBuilder();
        var validOrders = validModel.Objects<Order>().Key(value => value.Id);
        var validLinks = validModel.Objects<Link>().Key(value => value.Id);
        var validTotal = validModel.Derived(validOrders).Compute(value => value.Total);
        _ = validModel.Derived(validLinks).Using(value => value.Order, validTotal)
            .Compute((_, value) => value);
        var link = new Link { Order = null!, CapturedTotal = 0 };
        Assert.Throws<InvalidOperationException>(() => validModel.Build().CreateRuntime(seed =>
            seed.Add(validLinks, [link])));
    }

    private static IntegrityScenario CreateIntegrityScenario()
    {
        var model = new RelationModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Compute(value => value.Total);
        var value = model.Derived(links).Using(link => link.Order, total)
            .Compute((_, current) => current);
        return new IntegrityScenario(model.Build().CreateRuntime(), orders, links, value);
    }

    private static Order Select(Link link) => link.Order;

    private sealed record IntegrityScenario(
        RelationRuntime Runtime,
        ObjectSet<Order> Orders,
        ObjectSet<Link> Links,
        Derived<Link, int> Value);

    private sealed class Order
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Total { get; set; }
    }

    private sealed class Link
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required Order Order { get; set; }
        public int CapturedTotal { get; init; }
    }
}
