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
