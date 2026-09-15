namespace Raffinert.Relations.Tests;

public sealed class PlannedInvariantEvaluationTests
{
    [Fact]
    public void Plan_affected_evaluation_reports_violation_without_advancing_runtime_version()
    {
        var scenario = Create();
        scenario.Item.Value = -1;
        var plan = Plan(scenario, Change.Property(scenario.Items, scenario.Item, x => x.Value, 1, -1));

        Assert.True(plan.HasInvariantViolations);
        var evaluation = Assert.Single(plan.InvariantEvaluations);
        Assert.Equal("positive-invariant", evaluation.DefinitionKey);
        Assert.Equal(InvariantEvaluationState.Violated, evaluation.State);
        Assert.Same(scenario.Item, evaluation.Source);
        Assert.NotNull(evaluation.SourceIdentity);
        Assert.Equal(0, scenario.Runtime.Version);
        Assert.Equal(InvariantEvaluationState.Valid, scenario.Runtime.GetState(scenario.Invariant, scenario.Item));
    }

    [Fact]
    public void Plan_affected_evaluation_reports_valid_final_state_and_commit_installs_it_without_rerun()
    {
        var calls = 0;
        var scenario = Create(() => calls++);
        scenario.Item.Value = 2;
        var plan = Plan(scenario, Change.Property(scenario.Items, scenario.Item, x => x.Value, 1, 2));

        Assert.False(plan.HasInvariantViolations);
        Assert.Equal(InvariantEvaluationState.Valid, Assert.Single(plan.InvariantEvaluations).State);
        Assert.Equal(2, calls); // bootstrap priming + planned evaluation

        scenario.Runtime.Commit(plan);

        Assert.Equal(2, calls);
        Assert.Equal(1, scenario.Runtime.Version);
        Assert.Equal(InvariantEvaluationState.Valid, scenario.Runtime.GetState(scenario.Invariant, scenario.Item));
    }

    [Fact]
    public void Plan_none_does_not_execute_invariant_predicate()
    {
        var calls = 0;
        var scenario = Create(() => calls++);
        scenario.Item.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            Change.Property(scenario.Items, scenario.Item, x => x.Value, 1, 2)));

        var plan = scenario.Runtime.PlanDetailed(prepared);

        Assert.Empty(plan.InvariantEvaluations);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Plan_affected_evaluates_only_impacted_sources_and_deduplicates_each_source()
    {
        var calls = 0;
        var scenario = Create(() => calls++, includeSecond: true);
        scenario.Item.Value = 2;
        scenario.Item.Note = "changed";
        var plan = Plan(scenario,
            Change.Property(scenario.Items, scenario.Item, x => x.Value, 1, 2),
            Change.Property(scenario.Items, scenario.Item, x => x.Note, null, "changed"));

        Assert.Single(plan.InvariantEvaluations);
        Assert.Equal(3, calls); // two primed sources + one affected evaluation
    }

    [Fact]
    public void Plan_affected_skips_removed_source_and_evaluates_added_source()
    {
        var removed = Create();
        var removePlan = Plan(removed, Change.Remove(removed.Items, removed.Item));
        Assert.Empty(removePlan.InvariantEvaluations);

        var added = Create(empty: true);
        var item = new Item { Id = 42, Value = -1 };
        var addPlan = Plan(added, Change.Add(added.Items, item));
        var evaluation = Assert.Single(addPlan.InvariantEvaluations);
        Assert.Equal(InvariantEvaluationState.Violated, evaluation.State);
        Assert.Equal(42, evaluation.SourceIdentity!.SourceKey);
    }

    [Fact]
    public void Plan_affected_predicate_exception_restores_runtime_state()
    {
        var scenario = Create();
        scenario.Item.Value = -999;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            Change.Property(scenario.Items, scenario.Item, x => x.Value, 1, -999)));

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.PlanDetailed(
            prepared, RuntimeImpactDetailLevel.Summary, PlannedInvariantEvaluationMode.Affected));
        Assert.Equal(0, scenario.Runtime.Version);
        Assert.Equal(InvariantEvaluationState.Valid, scenario.Runtime.GetState(scenario.Invariant, scenario.Item));
    }

    [Fact]
    public void Discarded_violating_plan_leaves_runtime_state_unchanged()
    {
        var scenario = Create();
        var diagnostics = scenario.Runtime.Diagnostics;
        scenario.Item.Value = -1;

        _ = Plan(scenario, Change.Property(scenario.Items, scenario.Item, x => x.Value, 1, -1));

        Assert.Equal(0, scenario.Runtime.Version);
        Assert.Equal(diagnostics, scenario.Runtime.Diagnostics);
        Assert.Equal(InvariantEvaluationState.Valid, scenario.Runtime.GetState(scenario.Invariant, scenario.Item));
    }

    private static PreparedImpactPlan Plan(Scenario scenario, params RuntimeMutation[] mutations) =>
        scenario.Runtime.PlanDetailed(
            scenario.Runtime.Prepare(MutationSet.Create(mutations)),
            RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.Affected);

    private static Scenario Create(Action? onEvaluate = null, bool includeSecond = false, bool empty = false)
    {
        var model = new RelationModelBuilder();
        var items = model.Objects<Item>().Named("items").Key(x => x.Id);
        var value = model.Derived(items).Compute(x => x.Value).Named("value");
        var invariant = model.Invariant(items).Using(value).Must((source, current) =>
            Evaluate(source, current, onEvaluate)).AllowIncompleteDependencies().Named("positive-invariant");
        var first = new Item { Id = 1, Value = 1 };
        var second = new Item { Id = 2, Value = 1 };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            if (!empty)
                seed.Add(items, includeSecond ? [first, second] : [first]);
        });
        if (!empty)
        {
            Assert.True(runtime.Evaluate(invariant, first));
            if (includeSecond)
                Assert.True(runtime.Evaluate(invariant, second));
        }
        return new Scenario(runtime, items, invariant, first);
    }

    private static bool Evaluate(Item source, int value, Action? onEvaluate)
    {
        onEvaluate?.Invoke();
        if (value == -999)
            throw new InvalidOperationException("planned predicate failure");
        return value >= 0;
    }

    private sealed record Scenario(
        RelationRuntime Runtime,
        ObjectSet<Item> Items,
        Invariant<Item> Invariant,
        Item Item);

    private sealed class Item
    {
        public int Id { get; init; }
        public int Value { get; set; }
        public string? Note { get; set; }
    }
}
