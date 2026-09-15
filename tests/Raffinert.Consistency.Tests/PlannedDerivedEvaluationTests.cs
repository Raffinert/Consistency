namespace Raffinert.Consistency.Tests;

public sealed class PlannedDerivedEvaluationTests
{
    [Theory]
    [InlineData(RuntimeImpactDetailLevel.Summary)]
    [InlineData(RuntimeImpactDetailLevel.Causal)]
    public void Affected_source_derived_returns_final_value_and_commit_does_not_rerun(
        RuntimeImpactDetailLevel detailLevel)
    {
        var counter = new Counter(); var model = new RelationModelBuilder();
        var items = model.Objects<Item>().Named("items").Key(x => x.Id);
        var doubled = model.Derived(items).Compute(x => counter.Count(x.Value * 2))
            .AllowIncompleteDependencies().Named("doubled");
        var item = new Item { Id = 1, Value = 2 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(items, [item]));
        Assert.Equal(4, runtime.Get(doubled, item)); item.Value = 3;
        var prepared = runtime.Prepare(MutationSet.Create(Change.Property(items, item, x => x.Value, 2, 3)));

        var plan = runtime.PlanDetailed(prepared, detailLevel, PlannedInvariantEvaluationMode.None,
            PlannedDerivedEvaluationMode.Affected);

        var evaluation = Assert.Single(plan.DerivedEvaluations);
        Assert.Equal(6, evaluation.Value); Assert.Equal(DerivedValueState.Fresh, evaluation.State);
        Assert.Equal(0, runtime.Version); Assert.Equal(2, counter.Calls);
        runtime.Commit(plan);
        Assert.Equal(2, counter.Calls); Assert.Equal(6, runtime.Get(doubled, item));
    }

    [Fact]
    public void Affected_incremental_sum_and_composed_chain_return_final_values_only_for_affected_source()
    {
        var model = new RelationModelBuilder();
        var groups = model.Objects<Group>().Named("groups").Key(x => x.Id);
        var items = model.Objects<Item>().Named("sum-items").Key(x => x.Id);
        var relation = model.Relation(groups, items).Where((group, item) => group.Id == item.GroupId);
        var sum = model.Derived(groups).Using(relation).Incrementally().Compute((_, rows) => rows.Sum(x => x.Value)).Named("sum");
        var doubled = model.Derived(groups).Using(sum).Compute((_, value) => value * 2).Named("double-sum");
        var first = new Group { Id = 1 }; var other = new Group { Id = 2 };
        var item = new Item { Id = 1, GroupId = 1, Value = 2 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(groups, [first, other]); seed.Add(items, [item]); });
        _ = runtime.Get(doubled, first); _ = runtime.Get(doubled, other); item.Value = 4;

        var plan = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(items, item, x => x.Value, 2, 4))), RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected);

        Assert.Equal([("sum", 4), ("double-sum", 8)], plan.DerivedEvaluations
            .Select(x => (x.DefinitionKey!, (int)x.Value!)));
        Assert.All(plan.DerivedEvaluations, value => Assert.Same(first, value.Source));
    }

    [Fact]
    public void None_does_not_eagerly_evaluate_derived_value()
    {
        var counter = new Counter(); var model = new RelationModelBuilder();
        var items = model.Objects<Item>().Key(x => x.Id);
        model.Derived(items).Compute(x => counter.Count(x.Value)).AllowIncompleteDependencies();
        var item = new Item { Id = 1, Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(items, [item])); item.Value = 2;
        var plan = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(items, item, x => x.Value, 1, 2))));
        Assert.Empty(plan.DerivedEvaluations); Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public void Added_source_is_evaluated_and_removed_source_is_not_returned()
    {
        var model = new RelationModelBuilder(); var items = model.Objects<Item>().Named("lifecycle").Key(x => x.Id);
        model.Derived(items).Compute(x => x.Value * 2).Named("double");
        var removed = new Item { Id = 1, Value = 2 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(items, [removed]));
        var added = new Item { Id = 2, Value = 3 };
        var plan = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(Change.Remove(items, removed), Change.Add(items, added))),
            RuntimeImpactDetailLevel.Summary, PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected);
        var evaluation = Assert.Single(plan.DerivedEvaluations);
        Assert.Same(added, evaluation.Source); Assert.Equal(6, evaluation.Value); Assert.Equal(2, evaluation.SourceIdentity!.SourceKey);
    }

    [Fact]
    public void Stale_and_domain_drift_reject_without_reexecution()
    {
        var counter = new Counter(); var model = new RelationModelBuilder(); var items = model.Objects<Item>().Key(x => x.Id);
        model.Derived(items).Compute(x => counter.Count(x.Value)).AllowIncompleteDependencies();
        var item = new Item { Id = 1, Value = 1 }; var runtime = model.Build().CreateRuntime(seed => seed.Add(items, [item]));
        item.Value = 2; var prepared = runtime.Prepare(MutationSet.Create(Change.Property(items, item, x => x.Value, 1, 2)));
        var plan = runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Summary, PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected);
        var calls = counter.Calls; item.Value = 3;
        Assert.Throws<InvalidOperationException>(() => runtime.Commit(plan)); Assert.Equal(calls, counter.Calls); Assert.False(plan.IsCommitted);
        item.Value = 2; runtime.Add(items, new Item { Id = 9 });
        Assert.Throws<InvalidOperationException>(() => runtime.Commit(plan)); Assert.Equal(calls, counter.Calls);
    }

    [Fact]
    public void Affected_mode_does_not_scan_unrelated_registered_sources()
    {
        var counter = new Counter(); var model = new RelationModelBuilder(); var items = model.Objects<Item>().Key(x => x.Id);
        model.Derived(items).Compute(x => counter.Count(x.Value)).AllowIncompleteDependencies();
        var all = Enumerable.Range(1, 101).Select(id => new Item { Id = id, Value = id }).ToArray();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(items, all)); all[0].Value = 500;
        var plan = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(Change.Property(items, all[0], x => x.Value, 1, 500))),
            RuntimeImpactDetailLevel.Summary, PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected);
        Assert.Single(plan.DerivedEvaluations); Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void Affected_full_relation_recompute_returns_planned_value()
    {
        var model = new RelationModelBuilder(); var groups = model.Objects<Group>().Key(x => x.Id); var items = model.Objects<Item>().Key(x => x.Id);
        var relation = model.Relation(groups, items).Where((g, i) => g.Id == i.GroupId);
        var total = model.Derived(groups).Using(relation).Compute((_, rows) => rows.Sum(x => x.Value)).Named("full-total");
        var group = new Group { Id = 1 }; var item = new Item { Id = 1, GroupId = 1, Value = 2 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(groups, [group]); seed.Add(items, [item]); });
        Assert.Equal(2, runtime.Get(total, group)); item.Value = 5;
        var plan = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(Change.Property(items, item, x => x.Value, 2, 5))),
            RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected);
        Assert.Equal(5, Assert.Single(plan.DerivedEvaluations).Value);
    }

    [Fact]
    public void Projected_change_evaluates_only_actual_downstream_source()
    {
        var model = new RelationModelBuilder(); var owners = model.Objects<Owner>().Named("owners").Key(x => x.Id);
        var links = model.Objects<ProjectedLink>().Named("links").Key(x => x.Id);
        var amount = model.Derived(owners).Compute(x => x.Amount).Named("amount");
        model.Derived(links).Using(x => x.Owner, amount).Compute((_, value) => value * 2).Named("projected");
        var first = new Owner { Id = 1, Amount = 2 }; var second = new Owner { Id = 2, Amount = 3 };
        var firstLink = new ProjectedLink { Id = 1, Owner = first }; var secondLink = new ProjectedLink { Id = 2, Owner = second };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(owners, [first, second]); seed.Add(links, [firstLink, secondLink]); });
        first.Amount = 4;
        var plan = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(Change.Property(owners, first, x => x.Amount, 2, 4))),
            RuntimeImpactDetailLevel.Summary, PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected);
        Assert.Contains(plan.DerivedEvaluations, x => ReferenceEquals(x.Source, firstLink) && Equals(x.Value, 8));
        Assert.DoesNotContain(plan.DerivedEvaluations, x => ReferenceEquals(x.Source, secondLink));
    }

    [Fact]
    public void Affected_derived_exception_restores_runtime_state_atomically()
    {
        var model = new RelationModelBuilder(); var items = model.Objects<Item>().Key(x => x.Id);
        var value = model.Derived(items).Compute(x => ThrowAtNine(x.Value)).AllowIncompleteDependencies();
        var item = new Item { Id = 1, Value = 2 }; var runtime = model.Build().CreateRuntime(seed => seed.Add(items, [item]));
        Assert.Equal(2, runtime.Get(value, item)); item.Value = 9;
        var prepared = runtime.Prepare(MutationSet.Create(Change.Property(items, item, x => x.Value, 2, 9)));
        Assert.Throws<InvalidOperationException>(() => runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected));
        Assert.Equal(0, runtime.Version); Assert.Equal(DerivedValueState.Fresh, runtime.GetState(value, item));
    }

    private static int ThrowAtNine(int value) => value == 9 ? throw new InvalidOperationException("planned failure") : value;

    private sealed class Counter { public int Calls { get; private set; } public int Count(int value) { Calls++; return value; } }
    private sealed class Item { public int Id { get; init; } public int GroupId { get; init; } public int Value { get; set; } }
    private sealed class Group { public int Id { get; init; } }
    private sealed class Owner { public int Id { get; init; } public int Amount { get; set; } }
    private sealed class ProjectedLink { public int Id { get; init; } public required Owner Owner { get; init; } }
}
