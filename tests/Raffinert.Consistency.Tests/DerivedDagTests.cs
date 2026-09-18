namespace Raffinert.Consistency.Tests;

public sealed class DerivedDagTests
{
    [Fact]
    public void Source_derived_values_compose_lazily_through_a_chain()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var ordered = model.Derived(lines).Select(line => line.Ordered);
        var remaining = model.Derived(lines).From(ordered)
            .Select((line, value) => value - line.Received);
        var display = model.Derived(lines).From(remaining)
            .Select((_, value) => value + 1m);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        runtime.Add(lines, line);

        Assert.Equal(9m, runtime.Evaluate(display, line));
        line.Ordered = 12m;
        runtime.Apply(Change.Property(lines, line, value => value.Ordered, 10m, 12m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(ordered, line));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(remaining, line));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(display, line));
        Assert.Equal(11m, runtime.Evaluate(display, line));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(ordered, line));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(remaining, line));
        runtime.Remove(lines, line);
        Assert.Equal(0, runtime.DerivedStateEntryCount);
    }

    [Fact]
    public void Relation_derived_value_propagates_to_composed_downstream_value()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Line>().Key(line => line.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.LineId);
        var count = model.Derived(sources).From(relation).Select((_, matches) => matches.Count);
        var doubled = model.Derived(sources).From(count).Select((_, value) => value * 2);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid() };
        runtime.Add(sources, line);
        Assert.Equal(0, runtime.Evaluate(doubled, line));

        runtime.Add(items, new Item { Id = Guid.NewGuid(), LineId = line.Id });
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(doubled, line));
        Assert.Equal(2, runtime.Evaluate(doubled, line));
    }

    [Fact]
    public void Two_upstreams_merge_source_scoped_severity_in_a_diamond()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var basis = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(line => line.Ordered);
        var doubled = model.Derived(lines).From(basis).Select((_, value) => value * 2m);
        var reduced = model.Derived(lines).From(basis).Select((line, value) => value - line.Received);
        var combined = model.Derived(lines).From(doubled).From(reduced)
            .Select((_, left, right) => left + right);
        var runtime = model.Build().CreateRuntime();
        var first = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        var second = new Line { Id = Guid.NewGuid(), Ordered = 5m, Received = 1m };
        runtime.Add(lines, first);
        runtime.Add(lines, second);
        Assert.Equal(28m, runtime.Evaluate(combined, first));
        Assert.Equal(14m, runtime.Evaluate(combined, second));

        first.Ordered = 12m;
        runtime.Apply(Change.Property(lines, first, value => value.Ordered, 10m, 12m));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(combined, first));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(combined, second));
        Assert.Equal(34m, runtime.Evaluate(combined, first));
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
    public void Composed_value_configures_direct_source_severity_without_weakening_upstream_severity()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var basis = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(line => line.Ordered);
        var available = model.Derived(lines).From(basis)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Dirty))
            .Select((line, ordered) => ordered - line.Received);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        runtime.Add(lines, line);
        Assert.Equal(8m, runtime.Evaluate(available, line));

        line.Received = 3m;
        runtime.Apply(Change.Property(lines, line, value => value.Received, 2m, 3m));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(basis, line));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(available, line));
        Assert.Equal(7m, runtime.Evaluate(available, line));

        line.Ordered = 12m;
        line.Received = 4m;
        runtime.Apply(MutationSet.Create(
            Change.Property(lines, line, value => value.Received, 3m, 4m),
            Change.Property(lines, line, value => value.Ordered, 10m, 12m)));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(available, line));
    }

    [Fact]
    public void Two_upstream_builder_supports_invalid_direct_source_impact_and_normalized_inputs()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var ordered = model.Derived(lines).Select(line => line.Ordered);
        var received = model.Derived(lines).Select(line => line.Received);
        var remaining = model.Derived(lines).From(ordered).From(received)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select((line, first, second) => first - second + line.Offset);
        var invariant = model.Invariant(lines).From(remaining).From(ordered)
            .Must((_, value, maximum) => value <= maximum);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m, Offset = 1m };
        runtime.Add(lines, line);
        Assert.Equal(9m, runtime.Evaluate(remaining, line));

        line.Offset = 2m;
        runtime.Apply(Change.Property(lines, line, value => value.Offset, 1m, 2m));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(remaining, line));
        Assert.Same(lines.Definition, invariant.Definition.SourceSet);
        Assert.All(remaining.Definition.Inputs, input => Assert.IsType<UpstreamDerivedInput>(input));
    }

    [Fact]
    public void Multi_input_invariant_merges_upstreams_and_schedules_once_per_source()
    {
        var repairs = new List<Line>();
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var ordered = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(line => line.Ordered);
        var received = model.Derived(lines).Select(line => line.Received);
        var invariant = model.Invariant(lines).From(received).From(ordered)
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
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var ordered = model.Derived(lines).Select(line => line.Ordered);
        var received = model.Derived(lines).Select(line => line.Received);
        var invariant = model.Invariant(lines).From(received).From(ordered)
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
        public decimal Offset { get; set; }
    }

    private static (int Id, DependencySeverity Severity)[] ApplyOrderedBatch(bool upstreamFirst)
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var basis = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(line => line.Ordered);
        var doubled = model.Derived(lines).From(basis).Select((_, value) => value * 2m);
        var reduced = model.Derived(lines).From(basis).Select((line, value) => value - line.Received);
        var combined = model.Derived(lines).From(doubled).From(reduced)
            .Select((_, left, right) => left + right);
        var runtime = model.Build().CreateRuntime();
        var line = new Line { Id = Guid.NewGuid(), Ordered = 10m, Received = 2m };
        runtime.Add(lines, line);
        Assert.Equal(28m, runtime.Evaluate(combined, line));
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
