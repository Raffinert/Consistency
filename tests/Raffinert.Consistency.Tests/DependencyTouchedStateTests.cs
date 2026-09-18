namespace Raffinert.Consistency.Tests;

public sealed class DependencyTouchedStateTests
{
    [Fact]
    public void Preview_restores_touched_and_unrelated_cached_values_and_invariant_states()
    {
        var scenario = CreateScenario(2);
        var touched = scenario.Sources[0];
        var unrelated = scenario.Sources[1];
        Assert.Equal(1, scenario.Runtime.Evaluate(scenario.Value, touched));
        Assert.Equal(2, scenario.Runtime.Evaluate(scenario.Value, unrelated));
        Assert.True(scenario.Runtime.Evaluate(scenario.Invariant, touched));
        Assert.True(scenario.Runtime.Evaluate(scenario.Invariant, unrelated));
        touched.Value = 3;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, touched, source => source.Value, 1, 3)));

        _ = scenario.Runtime.PreviewDetailed(prepared);

        Assert.Equal(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.Value, touched));
        Assert.Equal(1, scenario.Runtime.Evaluate(scenario.Value, touched));
        Assert.Equal(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.Value, unrelated));
        Assert.Equal(2, scenario.Runtime.Evaluate(scenario.Value, unrelated));
        Assert.Equal(InvariantEvaluationState.Valid, scenario.Runtime.GetState(scenario.Invariant, touched));
        Assert.Equal(InvariantEvaluationState.Valid, scenario.Runtime.GetState(scenario.Invariant, unrelated));
    }

    [Fact]
    public void Plan_commit_installs_dependency_state_only_for_planned_sources()
    {
        var scenario = CreateScenario(2);
        var touched = scenario.Sources[0];
        var unrelated = scenario.Sources[1];
        _ = scenario.Runtime.Evaluate(scenario.Value, touched);
        _ = scenario.Runtime.Evaluate(scenario.Value, unrelated);
        _ = scenario.Runtime.Evaluate(scenario.Invariant, touched);
        _ = scenario.Runtime.Evaluate(scenario.Invariant, unrelated);
        touched.Value = 3;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, touched, source => source.Value, 1, 3)));

        var plan = scenario.Runtime.PlanDetailed(prepared);
        scenario.Runtime.Commit(plan);

        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Value, touched));
        Assert.Equal(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.Value, unrelated));
        Assert.Equal(InvariantEvaluationState.Dirty, scenario.Runtime.GetState(scenario.Invariant, touched));
        Assert.Equal(InvariantEvaluationState.Valid, scenario.Runtime.GetState(scenario.Invariant, unrelated));
    }

    [Fact]
    public void Incremental_relation_value_is_identical_after_plan_commit()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Count();
        var source = new Source { Code = "A" };
        var first = new Item { Code = "A" };
        var second = new Item { Code = "A" };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [source]);
            seed.Add(items, [first]);
        });
        Assert.Equal(1, runtime.Evaluate(count, source));
        var prepared = runtime.Prepare(MutationSet.Create(Change.Add(items, second)));

        var plan = runtime.PlanDetailed(prepared);
        Assert.Equal(1, runtime.Evaluate(count, source));
        runtime.Commit(plan);

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, source));
        Assert.Equal(2, runtime.Evaluate(count, source));
    }

    [Fact]
    public void Dependency_patch_size_is_population_independent_for_one_touched_source()
    {
        var small = CreateScenario(10_000);
        var large = CreateScenario(100_000);
        var smallPrepared = PrepareFirstChange(small);
        var largePrepared = PrepareFirstChange(large);

        var smallCount = small.Runtime.CaptureDependencyPatchEntryCount(smallPrepared);
        var largeCount = large.Runtime.CaptureDependencyPatchEntryCount(largePrepared);

        Assert.True(largeCount < smallCount * 2,
            $"Dependency patch grew from {smallCount} to {largeCount} entries.");
    }

    private static PreparedMutation PrepareFirstChange(Scenario scenario)
    {
        var source = scenario.Sources[0];
        var oldValue = source.Value;
        source.Value++;
        return scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, source, value => value.Value, oldValue, source.Value)));
    }

    private static Scenario CreateScenario(int count)
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(set).Select(source => source.Value);
        var invariant = model.Invariant(set).From(value).Must((_, current) => current >= 0);
        var sources = Enumerable.Range(1, count).Select(index => new Source { Value = index }).ToArray();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(set, sources));
        foreach (var source in sources)
        {
            _ = runtime.Evaluate(value, source);
            _ = runtime.Evaluate(invariant, source);
        }
        return new Scenario(runtime, set, value, invariant, sources);
    }

    private sealed record Scenario(
        ConsistencyRuntime Runtime,
        ObjectSet<Source> Set,
        Derived<Source, int> Value,
        Invariant<Source> Invariant,
        Source[] Sources);

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
        public string Code { get; set; } = "";
    }

    private sealed class Item
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }
}
