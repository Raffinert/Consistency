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
        runtime.Remove(lines, line);
        Assert.Equal(0, runtime.DerivedStateEntryCount);
    }

    [Fact]
    public void Relation_derived_value_propagates_to_composed_downstream_value()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Line>().Key(line => line.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.LineId);
        var count = model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        var doubled = model.Derived(sources).Using(count).Compute((_, value) => value * 2);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid() };
        runtime.Add(sources, line);
        Assert.Equal(0, runtime.Get(doubled, line));

        runtime.Add(items, new Item { Id = Guid.NewGuid(), LineId = line.Id });
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(doubled, line));
        Assert.Equal(2, runtime.Get(doubled, line));
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

    [Fact]
    public void Mutation_declaration_order_does_not_change_topological_impacts()
    {
        var upstreamFirst = ApplyOrderedBatch(upstreamFirst: true);
        var downstreamFirst = ApplyOrderedBatch(upstreamFirst: false);

        Assert.Equal(upstreamFirst, downstreamFirst);
        Assert.Equal(
            [
                (0, DependencySeverity.Invalid),
                (1, DependencySeverity.Invalid),
                (2, DependencySeverity.Invalid),
                (3, DependencySeverity.Invalid)
            ],
            upstreamFirst);
    }

    [Fact]
    public void Multi_input_invariant_merges_upstreams_and_schedules_once_per_source()
    {
        var repairs = new List<Line>();
        var model = new RelationModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var ordered = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(line => line.Ordered);
        var received = model.Derived(lines).Compute(line => line.Received);
        var invariant = model.Invariant(lines).Using(received, ordered)
            .Must((line, receivedValue, orderedValue) => receivedValue <= orderedValue)
            .ScheduleRepairWith(repairs.Add);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        runtime.Add(lines, line);
        Assert.True(runtime.Evaluate(invariant, line));
        repairs.Clear();

        line.Ordered = 1m;
        line.Received = 3m;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(lines, line, value => value.Ordered, 10m, 1m),
            Change.Property(lines, line, value => value.Received, 2m, 3m)));

        Assert.Equal(InvariantEvaluationState.Invalid, runtime.GetState(invariant, line));
        Assert.Single(application.Result.RepairRequests);
        application.Dispatch.Invoke();
        Assert.Equal([line], repairs);
    }

    [Fact]
    public void Multi_input_immediate_invariant_refreshes_all_upstreams_after_commit()
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var ordered = model.Derived(lines).Compute(line => line.Ordered);
        var received = model.Derived(lines).Compute(line => line.Received);
        var invariant = model.Invariant(lines).Using(received, ordered)
            .Must((_, receivedValue, orderedValue) => receivedValue <= orderedValue)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        runtime.Add(lines, line);
        Assert.True(runtime.Evaluate(invariant, line));

        line.Received = 12m;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(lines, line, value => value.Received, 2m, 12m)));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(invariant, line));

        application.Dispatch.Invoke();
        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, line));
    }

    private sealed class Line
    {
        public Guid Id { get; init; }
        public decimal Ordered { get; set; }
        public decimal Received { get; set; }
    }

    private static (int Id, DependencySeverity Severity)[] ApplyOrderedBatch(bool upstreamFirst)
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
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        runtime.Add(lines, line);
        Assert.Equal(28m, runtime.Get(combined, line));
        line.Ordered = 12m;
        line.Received = 3m;
        var upstream = Change.Property(lines, line, value => value.Ordered, 10m, 12m);
        var downstream = Change.Property(lines, line, value => value.Received, 2m, 3m);

        var result = runtime.ApplyDetailed(upstreamFirst
            ? MutationSet.Create(upstream, downstream)
            : MutationSet.Create(downstream, upstream)).Result;

        return result.DerivedImpacts
            .Select(impact => (impact.DerivedId, Assert.Single(impact.Sources).Severity))
            .ToArray();
    }

    private sealed class Item
    {
        public Guid Id { get; init; }
        public Guid LineId { get; init; }
    }
}
