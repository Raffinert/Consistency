namespace Raffinert.Relations.Tests;

public sealed class SourceDerivedTests
{
    [Fact]
    public void Source_only_value_tracks_used_members_and_recomputes_lazily()
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var amount = model.Derived(lines).Compute(line => line.Quantity * line.UnitRate);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Quantity = 2m, UnitRate = 3m };
        runtime.Add(lines, line);

        Assert.Equal(6m, runtime.Get(amount, line));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(amount, line));

        line.Note = "unrelated";
        runtime.Apply(Change.Property(lines, line, value => value.Note, "", line.Note));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(amount, line));

        line.Quantity = 4m;
        runtime.Apply(Change.Property(lines, line, value => value.Quantity, 2m, 4m));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(amount, line));
        Assert.Equal(12m, runtime.Get(amount, line));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(amount, line));
    }

    [Fact]
    public void Source_only_value_honors_invalid_severity_and_lifecycle_cleanup()
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var amount = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(line => line.Quantity * line.UnitRate);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Quantity = 2m, UnitRate = 3m };
        runtime.Add(lines, line);
        Assert.Equal(6m, runtime.Get(amount, line));

        line.UnitRate = 5m;
        runtime.Apply(Change.Property(lines, line, value => value.UnitRate, 3m, 5m));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(amount, line));
        Assert.Equal(1, runtime.DerivedStateEntryCount);

        runtime.Remove(lines, line);
        Assert.Equal(0, runtime.DerivedStateEntryCount);
    }

    [Fact]
    public void Failed_source_recomputation_does_not_publish_a_partial_value()
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var amount = model.Derived(lines).Compute(line => line.ThrowOnRead
            ? Throw()
            : line.Quantity * line.UnitRate).AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Quantity = 2m, UnitRate = 3m };
        runtime.Add(lines, line);
        Assert.Equal(6m, runtime.Get(amount, line));

        line.ThrowOnRead = true;
        runtime.Apply(Change.Property(lines, line, value => value.ThrowOnRead, false, true));
        Assert.Throws<DeliberateSourceException>(() => runtime.Get(amount, line));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(amount, line));
    }

    private sealed class Line
    {
        public Guid Id { get; init; }
        public decimal Quantity { get; set; }
        public decimal UnitRate { get; set; }
        public string Note { get; set; } = "";
        public bool ThrowOnRead { get; set; }
    }

    private sealed class DeliberateSourceException : Exception;

    private static decimal Throw() => throw new DeliberateSourceException();
}
