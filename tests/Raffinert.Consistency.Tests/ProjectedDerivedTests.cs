namespace Raffinert.Consistency.Tests;

public sealed class ProjectedDerivedTests
{
    [Fact]
    public void Cross_set_upstream_invalidates_only_sources_referencing_the_changed_owner()
    {
        var model = new ConsistencyModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(value => value.Total);
        var validity = model.Derived(links).From(value => value.Order, total)
            .Select((link, currentTotal) => link.CapturedTotal == currentTotal);
        var firstOrder = new Order { Total = 10 };
        var secondOrder = new Order { Total = 20 };
        var first = new Link { Order = firstOrder, CapturedTotal = 10 };
        var second = new Link { Order = secondOrder, CapturedTotal = 20 };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(orders, [firstOrder, secondOrder]);
            seed.Add(links, [first, second]);
        });
        Assert.True(runtime.Evaluate(validity, first));
        Assert.True(runtime.Evaluate(validity, second));

        firstOrder.Total = 11;
        runtime.Apply(Change.Property(orders, firstOrder, value => value.Total, 10, 11));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(validity, first));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(validity, second));
        Assert.False(runtime.Evaluate(validity, first));
    }

    [Fact]
    public void Replacing_projected_reference_dirties_the_downstream_value()
    {
        var model = new ConsistencyModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Select(value => value.Total);
        var validity = model.Derived(links).From(value => value.Order, total)
            .Select((link, currentTotal) => link.CapturedTotal == currentTotal);
        var oldOrder = new Order { Total = 10 };
        var newOrder = new Order { Total = 20 };
        var link = new Link { Order = oldOrder, CapturedTotal = 10 };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(orders, [oldOrder, newOrder]);
            seed.Add(links, [link]);
        });
        Assert.True(runtime.Evaluate(validity, link));

        link.Order = newOrder;
        runtime.Apply(Change.Property(links, link, value => value.Order, oldOrder, newOrder));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(validity, link));
        Assert.False(runtime.Evaluate(validity, link));
    }

    [Fact]
    public void Bootstrap_rejects_missing_or_wrong_set_projected_target()
    {
        var model = new ConsistencyModelBuilder();
        var upstream = model.Objects<Order>().Key(value => value.Id);
        var other = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(upstream).Select(value => value.Total);
        _ = model.Derived(links).From(value => value.Order, total)
            .Select((_, currentTotal) => currentTotal);
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
        Assert.Equal(1, scenario.Runtime.Evaluate(scenario.Value, link));
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
        Assert.Equal(1, scenario.Runtime.Evaluate(scenario.Value, link));

        link.Order = newTarget;
        scenario.Runtime.Apply(MutationSet.Create(
            Change.Property(scenario.Links, link, value => value.Order, oldTarget, newTarget),
            Change.Remove(scenario.Orders, oldTarget)));

        Assert.Equal(2, scenario.Runtime.Evaluate(scenario.Value, link));
        newTarget.Total = 3;
        scenario.Runtime.Apply(Change.Property(
            scenario.Orders, newTarget, value => value.Total, 2, 3));
        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Value, link));
    }

    [Fact]
    public void Unsupported_or_null_projection_is_rejected()
    {
        var model = new ConsistencyModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Select(value => value.Total);
        Assert.Throws<ArgumentException>(() => model.Derived(links)
            .From(value => Select(value), total)
            .Select((_, value) => value));
        Assert.Throws<ArgumentException>(() => model.Derived(links)
            .From(value => value.Container.Order, total)
            .Select((_, value) => value));

        var validModel = new ConsistencyModelBuilder();
        var validOrders = validModel.Objects<Order>().Key(value => value.Id);
        var validLinks = validModel.Objects<Link>().Key(value => value.Id);
        var validTotal = validModel.Derived(validOrders).Select(value => value.Total);
        _ = validModel.Derived(validLinks).From(value => value.Order, validTotal)
            .Select((_, value) => value);
        var link = new Link { Order = null!, CapturedTotal = 0 };
        Assert.Throws<InvalidOperationException>(() => validModel.Build().CreateRuntime(seed =>
            seed.Add(validLinks, [link])));
    }

    [Fact]
    public void Two_projected_upstreams_merge_severity_and_recompute_from_one_target()
    {
        var model = new ConsistencyModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Select(value => value.Total);
        var doubled = model.Derived(orders)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(value => value.Total * 2);
        var combined = model.Derived(links).From(value => value.Order, total, doubled)
            .Select((_, first, second) => first + second);
        var order = new Order { Total = 2 };
        var link = new Link { Order = order, CapturedTotal = 2 };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(orders, [order]);
            seed.Add(links, [link]);
        });
        Assert.Equal(2, runtime.Diagnostics.ProjectedDependencyConsumerCount);
        Assert.Equal(1, runtime.Diagnostics.ProjectionIndexCount);
        Assert.Equal(1, runtime.Diagnostics.ReverseProjectionEntryCount);
        Assert.Equal(6, runtime.Evaluate(combined, link));

        order.Total = 3;
        runtime.Apply(Change.Property(orders, order, value => value.Total, 2, 3));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(combined, link));
        Assert.Equal(9, runtime.Evaluate(combined, link));
    }

    [Fact]
    public void Randomized_indexed_projection_matches_authoritative_recompute()
    {
        var model = new ConsistencyModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Select(value => value.Total);
        var doubled = model.Derived(orders)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(value => value.Total * 2);
        var combined = model.Derived(links).From(value => value.Order, total, doubled)
            .Select((link, first, second) => first + second + link.CapturedTotal);
        var owners = Enumerable.Range(0, 8).Select(index => new Order { Total = index }).ToArray();
        var downstream = Enumerable.Range(0, 40)
            .Select(index => new Link { Order = owners[index % owners.Length], CapturedTotal = index }).ToArray();
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(links, downstream);
            seed.Add(orders, owners);
        });
        foreach (var link in downstream)
            _ = runtime.Evaluate(combined, link);
        var random = new Random(34040);

        for (var step = 0; step < 150; step++)
        {
            if (random.Next(2) == 0)
            {
                var owner = owners[random.Next(owners.Length)];
                var oldValue = owner.Total;
                owner.Total = random.Next(100);
                runtime.Apply(Change.Property(orders, owner, value => value.Total, oldValue, owner.Total));
            }
            else
            {
                var link = downstream[random.Next(downstream.Length)];
                var oldOwner = link.Order;
                link.Order = owners[random.Next(owners.Length)];
                runtime.Apply(Change.Property(links, link, value => value.Order, oldOwner, link.Order));
            }

            foreach (var link in downstream)
                Assert.Equal(link.Order.Total * 3 + link.CapturedTotal, runtime.Evaluate(combined, link));
        }
    }

    private static IntegrityScenario CreateIntegrityScenario()
    {
        var model = new ConsistencyModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Select(value => value.Total);
        var value = model.Derived(links).From(link => link.Order, total)
            .Select((_, current) => current);
        return new IntegrityScenario(model.Build().CreateRuntime(), orders, links, value);
    }

    private static Order Select(Link link) => link.Order;

    private sealed record IntegrityScenario(
        ConsistencyRuntime Runtime,
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
        public Container Container { get; init; } = new();
        public int CapturedTotal { get; init; }
    }

    private sealed class Container
    {
        public Order Order { get; init; } = new();
    }
}
