namespace Raffinert.Consistency.Tests;

public sealed partial class DerivedStateTests
{
    [Fact]
    public void Direct_source_change_marks_a_cached_derived_computation_dirty()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var score = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Count + (source.Enabled ? 1 : 0));
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = false };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Get(score, source));

        source.Enabled = true;
        runtime.Apply(Change.Property(sources, source, x => x.Enabled, false, true));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(score, source));
        Assert.Equal(1, runtime.Get(score, source));
    }

    [Fact]
    public void Immediate_invariant_policy_recomputes_after_relation_impact()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        var invariant = model.Invariant(sources).Using(count).Must((source, value) => value <= 1)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, first);
        runtime.Add(items, second);
        Assert.True(runtime.Evaluate(invariant, source));

        second.Code = "A";
        runtime.Apply(Change.Property(items, second, x => x.Code, "B", "A"));

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, source));
    }

    [Fact]
    public void Repair_policy_schedules_affected_source_objects()
    {
        var scheduled = new List<CodeHolder>();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        model.Invariant(sources).Using(count).Must((source, value) => value <= 1)
            .ScheduleRepairWith(scheduled.Add);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        scheduled.Clear();

        item.Code = "A";
        runtime.Apply(Change.Property(items, item, x => x.Code, "B", "A"));

        Assert.Equal([source], scheduled);
    }

    [Fact]
    public void Public_impact_policy_can_invalidate_removed_membership()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid))
            .Compute((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(1, runtime.Get(count, source));

        runtime.Remove(items, item);

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, source));
    }

    [Fact]
    public void Mixed_membership_delta_is_classified_per_source()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid))
            .Compute((_, matches) => matches.Count);
        var invariant = model.Invariant(sources).Using(count).Must((_, value) => value >= 0);
        var addedSource = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var removedSource = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        var runtime = model.Build().CreateRuntime();
        runtime.Add(sources, addedSource);
        runtime.Add(sources, removedSource);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Get(count, addedSource));
        Assert.Equal(1, runtime.Get(count, removedSource));
        Assert.True(runtime.Evaluate(invariant, addedSource));
        Assert.True(runtime.Evaluate(invariant, removedSource));
        item.Code = "A";

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, value => value.Code, "B", "A")));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, addedSource));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, removedSource));
        var publicSources = Assert.Single(result.Result.DerivedImpacts).Sources;
        Assert.Equal(DependencySeverity.Dirty,
            Assert.Single(publicSources, value => ReferenceEquals(value.Source, addedSource)).Severity);
        Assert.Equal(DependencySeverity.Invalid,
            Assert.Single(publicSources, value => ReferenceEquals(value.Source, removedSource)).Severity);
        var invariantSources = Assert.Single(result.Result.InvariantImpacts).Sources;
        Assert.Equal(DependencySeverity.Dirty,
            Assert.Single(invariantSources, value => ReferenceEquals(value.Source, addedSource)).Severity);
        Assert.Equal(DependencySeverity.Invalid,
            Assert.Single(invariantSources, value => ReferenceEquals(value.Source, removedSource)).Severity);
    }

    [Fact]
    public void Public_impact_policy_can_invalidate_direct_source_changes()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute((source, matches) => source.Enabled ? matches.Count : 0);
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = true };
        var runtime = model.Build().CreateRuntime();
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Get(count, source));
        source.Enabled = false;

        runtime.Apply(Change.Property(sources, source, value => value.Enabled, true, false));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, source));
    }

    [Fact]
    public void Public_impact_policy_can_invalidate_item_changes()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var total = model.Derived(sources).Using(relation)
            .Impact(policy => policy.ItemChanged(DependencySeverity.Invalid))
            .Compute((source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("A", 1m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(1m, runtime.Get(total, source));

        item.Quantity = 2m;
        runtime.Apply(Change.Property(items, item, x => x.Quantity, 1m, 2m));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(total, source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Public_impact_policy_is_independent_of_access_plan(bool forceScan)
    {
        var model = new ConsistencyModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation)
            .Impact(policy => policy.MembershipAdded(DependencySeverity.Invalid))
            .Compute((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Get(count, source));

        item.Code = "A";
        runtime.Apply(Change.Property(items, item, x => x.Code, "B", "A"));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, source));
    }

    [Fact]
    public void Prepared_mutation_dispatches_callbacks_only_after_explicit_dispatch()
    {
        var scheduled = new List<CodeHolder>();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        model.Invariant(sources).Using(count).Must((source, value) => value <= 1)
            .ScheduleRepairWith(scheduled.Add);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, first);
        runtime.Add(items, second);
        scheduled.Clear();
        second.Code = "A";
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(items, second, x => x.Code, "B", "A")));

        runtime.Commit(prepared);

        Assert.Empty(scheduled);
        Assert.Equal([first, second], runtime.Related(relation, source));

        runtime.Dispatch(prepared);

        Assert.Equal([source], scheduled);
        Assert.True(prepared.IsDispatched);
    }

    [Fact]
    public void Policy_dispatch_retry_resumes_at_failed_action_without_replaying_successes()
    {
        var calls = new int[3];
        var failSecond = true;
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        model.Invariant(sources).Using(count).Must((_, value) => value >= 0)
            .ScheduleRepairWith(_ => calls[0]++);
        model.Invariant(sources).Using(count).Must((_, value) => value >= 0)
            .ScheduleRepairWith(_ =>
            {
                calls[1]++;
                if (failSecond)
                    throw new DeliberateDispatchException();
            });
        model.Invariant(sources).Using(count).Must((_, value) => value >= 0)
            .ScheduleRepairWith(_ => calls[2]++);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Array.Clear(calls);
        item.Code = "A";
        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, value => value.Code, "B", "A")));

        Assert.Throws<DeliberateDispatchException>(result.Dispatch.Invoke);
        Assert.Equal([1, 1, 0], calls);
        Assert.False(result.Dispatch.IsDispatched);

        failSecond = false;
        result.Dispatch.Invoke();
        Assert.Equal([1, 2, 1], calls);
        Assert.True(result.Dispatch.IsDispatched);
        Assert.Throws<InvalidOperationException>(result.Dispatch.Invoke);
    }

    [Fact]
    public void Detailed_apply_exposes_stable_impacts_and_repair_requests_before_dispatch()
    {
        var scheduled = new List<CodeHolder>();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation)
            .Impact(policy => policy.MembershipAdded(DependencySeverity.Invalid))
            .Compute((source, matches) => matches.Count);
        model.Invariant(sources).Using(count).Must((source, value) => value == 0)
            .ScheduleRepairWith(scheduled.Add);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Get(count, source));
        scheduled.Clear();
        item.Code = "A";

        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, x => x.Code, "B", "A")));
        var result = application.Result;

        Assert.Empty(scheduled);
        var relationImpact = Assert.Single(result.RelationImpacts);
        Assert.Equal(0, relationImpact.RelationId);
        Assert.Equal(typeof(CodeHolder), relationImpact.LeftType);
        Assert.Equal(typeof(CodeHolder), relationImpact.RightType);
        var addedPair = Assert.Single(relationImpact.AddedPairs);
        Assert.Same(source, addedPair.Left);
        Assert.Same(item, addedPair.Right);
        var derivedImpact = Assert.Single(result.DerivedImpacts);
        Assert.Equal(0, derivedImpact.DerivedId);
        var derivedSource = Assert.Single(derivedImpact.Sources);
        Assert.Same(source, derivedSource.Source);
        Assert.Equal(DependencySeverity.Invalid, derivedSource.Severity);
        var invariantImpact = Assert.Single(result.InvariantImpacts);
        Assert.Equal(0, invariantImpact.InvariantId);
        var invariantSource = Assert.Single(invariantImpact.Sources);
        Assert.Same(source, invariantSource.Source);
        Assert.Equal(DependencySeverity.Invalid, invariantSource.Severity);
        var repair = Assert.Single(result.RepairRequests);
        Assert.Equal(0, repair.InvariantId);
        Assert.Same(source, repair.Source);
        Assert.Equal(DependencySeverity.Invalid, repair.Reason);
        Assert.Empty(result.ImmediateEvaluationRequests);

        application.Dispatch.Invoke();

        Assert.Equal([source], scheduled);
        Assert.True(application.Dispatch.IsDispatched);
    }

    [Fact]
    public void Detailed_apply_exposes_immediate_evaluation_requests()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        var invariant = model.Invariant(sources).Using(count).Must((source, value) => value == 0)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.True(runtime.Evaluate(invariant, source));
        item.Code = "A";

        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, x => x.Code, "B", "A")));
        var result = application.Result;

        var request = Assert.Single(result.ImmediateEvaluationRequests);
        Assert.Equal(0, request.InvariantId);
        Assert.Same(source, request.Source);
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(invariant, source));

        application.Dispatch.Invoke();

        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, source));
    }

    [Fact]
    public void Throwing_repair_callback_observes_committed_state_without_rollback()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code);
        var count = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Count);
        ConsistencyRuntime? runtime = null;
        Invariant<CodeHolder>? invariant = null;
        var observedRelatedCount = -1;
        var observedDerivedState = DerivedValueState.Fresh;
        var observedInvariantState = InvariantEvaluationState.Unknown;
        invariant = model.Invariant(sources).Using(count)
            .Must((source, value) => value <= 1)
            .ScheduleRepairWith(source =>
            {
                observedRelatedCount = runtime!.Related(relation, source).Count;
                observedDerivedState = runtime.GetState(count, source);
                observedInvariantState = runtime.GetState(invariant!, source);
                throw new RepairCallbackException();
            });
        runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Get(count, source));
        Assert.True(runtime.Evaluate(invariant, source));

        item.Code = "A";
        Assert.Throws<RepairCallbackException>(() =>
            runtime.Apply(Change.Property(items, item, x => x.Code, "B", "A")));

        Assert.Equal(1, observedRelatedCount);
        Assert.Equal(DerivedValueState.Dirty, observedDerivedState);
        Assert.Equal(InvariantEvaluationState.Invalid, observedInvariantState);
        Assert.Equal([item], runtime.Related(relation, source));
        Assert.Equal(1, runtime.Get(count, source));
    }

    [Fact]
    public void Immediate_evaluations_finish_before_repair_callbacks_dispatch()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code);
        var count = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Count);
        var immediate = model.Invariant(sources).Using(count)
            .Must((source, value) => value == 0)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        ConsistencyRuntime? runtime = null;
        var observedImmediateState = InvariantEvaluationState.Unknown;
        model.Invariant(sources).Using(count)
            .Must((source, value) => value <= 1)
            .ScheduleRepairWith(source =>
                observedImmediateState = runtime!.GetState(immediate, source));
        runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(immediate, source));

        runtime.Add(items, new CodeHolder { Id = Guid.NewGuid(), Code = "A" });

        Assert.Equal(InvariantEvaluationState.Violated, observedImmediateState);
    }

    [Fact]
    public void Repair_requests_are_deduplicated_for_each_invariant_and_source()
    {
        var repairCount = 0;
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var invariant = model.Invariant(sources).Using(quantity)
            .Must((source, value) => value <= source.Adjustment)
            .ScheduleRepairWith(_ => repairCount++);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("B", quantity: 1m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.True(runtime.Evaluate(invariant, source));

        source.Code = "B";
        source.Adjustment = 1m;
        runtime.Apply(ChangeSet.Create(
            Change.Property(sources, source, x => x.Code, "A", "B"),
            Change.Property(sources, source, x => x.Adjustment, 0m, 1m)));

        Assert.Equal(1, repairCount);
    }

}
