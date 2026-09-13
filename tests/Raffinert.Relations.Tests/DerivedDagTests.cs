namespace Raffinert.Relations.Tests;

public sealed class DerivedDagTests
{
    [Fact]
    public void Source_derived_values_compose_lazily_through_a_chain()
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var ordered = model.Derived(lines).Compute(line => line.Ordered);
        var remaining = model.Derived(lines).Using(ordered)
            .Compute((line, value) => value - line.Received);
        var display = model.Derived(lines).Using(remaining)
            .Compute((_, value) => value + 1m);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        runtime.Add(lines, line);

        Assert.Equal(9m, runtime.Get(display, line));
        line.Ordered = 12m;
        runtime.Apply(Change.Property(lines, line, value => value.Ordered, 10m, 12m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(ordered, line));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(remaining, line));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(display, line));
        Assert.Equal(11m, runtime.Get(display, line));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(ordered, line));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(remaining, line));
    }

    [Fact]
    public void Two_upstreams_merge_source_scoped_severity_in_a_diamond()
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var basis = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(line => line.Ordered);
        var doubled = model.Derived(lines).Using(basis).Compute((_, value) => value * 2m);
        var reduced = model.Derived(lines).Using(basis).Compute((line, value) => value - line.Received);
        var combined = model.Derived(lines).Using(doubled, reduced)
            .Compute((_, left, right) => left + right);
        var runtime = model.Build().CreateRuntime();
        var first = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        var second = new Line { Id = Guid.NewGuid(), Ordered = 5m, Received = 1m };
        runtime.Add(lines, first);
        runtime.Add(lines, second);
        Assert.Equal(28m, runtime.Get(combined, first));
        Assert.Equal(14m, runtime.Get(combined, second));

        first.Ordered = 12m;
        runtime.Apply(Change.Property(lines, first, value => value.Ordered, 10m, 12m));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(combined, first));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(combined, second));
        Assert.Equal(34m, runtime.Get(combined, first));
    }

    private sealed class Line
    {
        public Guid Id { get; init; }
        public decimal Ordered { get; set; }
        public decimal Received { get; set; }
    }
}
