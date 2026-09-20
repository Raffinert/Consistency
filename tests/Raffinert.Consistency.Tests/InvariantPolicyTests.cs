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
        var score = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Count + (source.Enabled ? 1 : 0));
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = false };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Evaluate(score, source));

        source.Enabled = true;
        runtime.Apply(Change.Property(sources, source, x => x.Enabled, false, true));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(score, source));
        Assert.Equal(1, runtime.Evaluate(score, source));
    }

    [Fact]
    public void Immediate_invariant_policy_recomputes_after_relation_impact()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Select((source, matches) => matches.Count);
        var invariant = model.Invariant(sources).From(count).Must((source, value) => value <= 1)
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
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Select((source, matches) => matches.Count);
        model.Invariant(sources).From(count).Must((source, value) => value < 1)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        item.Code = "A";
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, x => x.Code, "B", "A")));

        var request = Assert.Single(application.Result.RepairRequests);
        Assert.Same(source, request.Source);
    }

    [Fact]
    public void Structured_repair_requests_are_isolated_between_runtimes_sharing_one_model()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var value = model.Derived(sources).Select(source => source.Enabled);
        var invariant = model.Invariant(sources)
            .From(value)
            .Must((_, enabled) => !enabled)
            .RepairWhenViolated();
        var compiled = model.Build();
        var firstRuntime = compiled.CreateRuntime();
        var secondRuntime = compiled.CreateRuntime();
        var first = new CodeHolder { Id = Guid.NewGuid() };
        var second = new CodeHolder { Id = Guid.NewGuid() };
        firstRuntime.Add(sources, first);
        secondRuntime.Add(sources, second);
        Assert.True(firstRuntime.Evaluate(invariant, first));
        Assert.True(secondRuntime.Evaluate(invariant, second));

        first.Enabled = true;
        var firstResult = firstRuntime.ApplyDetailed(MutationSet.Create(
            Change.Property(sources, first, source => source.Enabled, false, true)));
        second.Enabled = true;
        var secondResult = secondRuntime.ApplyDetailed(MutationSet.Create(
            Change.Property(sources, second, source => source.Enabled, false, true)));

        Assert.Same(first, Assert.Single(firstResult.Result.RepairRequests).Source);
        Assert.Same(second, Assert.Single(secondResult.Result.RepairRequests).Source);
    }

    [Fact]
    public void Public_impact_policy_can_invalidate_removed_membership()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid))
            .Select((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(1, runtime.Evaluate(count, source));

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
        var count = model.Derived(sources).From(relation)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid))
            .Select((_, matches) => matches.Count);
        var invariant = model.Invariant(sources).From(count).Must((_, value) => value >= 0);
        var addedSource = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var removedSource = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        var runtime = model.Build().CreateRuntime();
        runtime.Add(sources, addedSource);
        runtime.Add(sources, removedSource);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Evaluate(count, addedSource));
        Assert.Equal(1, runtime.Evaluate(count, removedSource));
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
        var count = model.Derived(sources).From(relation)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select((source, matches) => source.Enabled ? matches.Count : 0);
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = true };
        var runtime = model.Build().CreateRuntime();
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Evaluate(count, source));
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
        var total = model.Derived(sources).From(relation)
            .Impact(policy => policy.ItemChanged(DependencySeverity.Invalid))
            .Select((source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("A", 1m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(1m, runtime.Evaluate(total, source));

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
        var count = model.Derived(sources).From(relation)
            .Impact(policy => policy.MembershipAdded(DependencySeverity.Invalid))
            .Select((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Evaluate(count, source));

        item.Code = "A";
        runtime.Apply(Change.Property(items, item, x => x.Code, "B", "A"));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, source));
    }

    [Fact]
    public void Prepared_mutation_exposes_repair_request_before_explicit_dispatch()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Select((source, matches) => matches.Count);
        model.Invariant(sources).From(count).Must((source, value) => value <= 1)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, first);
        runtime.Add(items, second);
        second.Code = "A";
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(items, second, x => x.Code, "B", "A")));

        var result = runtime.CommitDetailed(prepared, RuntimeImpactDetailLevel.Summary);

        var request = Assert.Single(result.RepairRequests);
        Assert.Same(source, request.Source);
        Assert.Equal([first, second], runtime.Related(relation, source));

        runtime.Dispatch(prepared);

        Assert.True(prepared.IsDispatched);
    }

    [Fact]
    public void Policy_dispatch_is_independent_from_structured_repair_requests()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Select((_, matches) => matches.Count);
        model.Invariant(sources).From(count).Must((_, value) => value >= 0)
            .RepairWhenViolated();
        model.Invariant(sources).From(count).Must((_, value) => value >= 0)
            .RepairWhenViolated();
        model.Invariant(sources).From(count).Must((_, value) => value >= 0)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        item.Code = "A";
        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, value => value.Code, "B", "A")));

        result.Dispatch.Invoke();
        Assert.True(result.Dispatch.IsDispatched);
        Assert.Throws<InvalidOperationException>(result.Dispatch.Invoke);
    }

    [Fact]
    public void Detailed_apply_exposes_stable_impacts_and_repair_requests_before_dispatch()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation)
            .Impact(policy => policy.MembershipAdded(DependencySeverity.Invalid))
            .Select((source, matches) => matches.Count);
        model.Invariant(sources).From(count).Must((source, value) => value == 0)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Evaluate(count, source));
        item.Code = "A";

        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, x => x.Code, "B", "A")));
        var result = application.Result;

        Assert.Single(result.RepairRequests);
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

        Assert.True(application.Dispatch.IsDispatched);
    }

    [Fact]
    public void Detailed_apply_exposes_immediate_evaluation_requests()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Select((source, matches) => matches.Count);
        var invariant = model.Invariant(sources).From(count).Must((source, value) => value == 0)
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
    public void Structured_repair_request_observes_committed_state_without_callback_dispatch()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code);
        var count = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Count);
        var invariant = model.Invariant(sources).From(count)
            .Must((source, value) => value < 1)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(0, runtime.Evaluate(count, source));
        Assert.True(runtime.Evaluate(invariant, source));

        item.Code = "A";
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, x => x.Code, "B", "A")));

        Assert.Single(application.Result.RepairRequests);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, source));
        Assert.Equal([item], runtime.Related(relation, source));
        Assert.Equal(1, runtime.Evaluate(count, source));
    }

    [Fact]
    public void Immediate_evaluations_finish_before_structured_repair_dispatch()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code);
        var count = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Count);
        var immediate = model.Invariant(sources).From(count)
            .Must((source, value) => value == 0)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(immediate, source));

        runtime.Add(items, new CodeHolder { Id = Guid.NewGuid(), Code = "A" });

        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(immediate, source));
    }

    [Fact]
    public void Repair_enabled_safe_dirty_transition_is_evaluated_without_request()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var length = model.Derived(sources).Select(value => value.Code.Length);
        var invariant = model.Invariant(sources).From(length)
            .Must((_, value) => value > 0)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(invariant, source));

        source.Code = "AA";
        var application = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "AA")));

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(length, source));
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, source));
        Assert.Empty(application.Result.RepairRequests);
    }

    [Fact]
    public void Repair_enabled_unsafe_dirty_transition_emits_one_request_after_evaluation()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var length = model.Derived(sources).Select(value => value.Code.Length);
        var invariant = model.Invariant(sources).From(length)
            .Must((_, value) => value > 0)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(invariant, source));

        source.Code = "";
        var application = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "")));

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(length, source));
        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, source));
        Assert.Same(source, Assert.Single(application.Result.RepairRequests).Source);
    }

    [Fact]
    public void Repair_enabled_unsafe_invalid_transition_becomes_fresh_and_emits_one_request()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var length = model.Derived(sources)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(value => value.Code.Length);
        var invariant = model.Invariant(sources).From(length)
            .Must((_, value) => value > 0)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(invariant, source));

        source.Code = "";
        var application = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "")));

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(length, source));
        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, source));
        Assert.Equal(DependencySeverity.Invalid,
            Assert.Single(application.Result.RepairRequests).Reason);
    }

    [Fact]
    public void Unevaluated_repair_invariant_preserves_invalid_operation_reason()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var length = model.Derived(sources)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(value => value.Code.Length);
        var invariant = model.Invariant(sources).From(length)
            .Must((_, value) => value > 0)
            .RepairWhenViolated();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        Assert.Equal(InvariantEvaluationState.Unknown, runtime.GetState(invariant, source));

        source.Code = "";
        var application = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "")));

        Assert.Equal(DependencySeverity.Invalid,
            Assert.Single(application.Result.RepairRequests).Reason);
    }

    [Fact]
    public void Unevaluated_repair_invariant_preserves_dirty_operation_reason()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var length = model.Derived(sources)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Dirty))
            .Select(value => value.Code.Length);
        var invariant = model.Invariant(sources).From(length)
            .Must((_, value) => value > 0)
            .RepairWhenViolated();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        Assert.Equal(InvariantEvaluationState.Unknown, runtime.GetState(invariant, source));

        source.Code = "";
        var application = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "")));

        Assert.Equal(DependencySeverity.Dirty,
            Assert.Single(application.Result.RepairRequests).Reason);
    }

    [Fact]
    public void Previously_valid_repair_invariant_uses_current_invalid_operation_reason()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var length = model.Derived(sources)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(value => value.Code.Length);
        var invariant = model.Invariant(sources).From(length)
            .Must((_, value) => value > 0)
            .RepairWhenViolated();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        Assert.True(runtime.Evaluate(invariant, source));

        source.Code = "";
        var application = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "")));

        Assert.Equal(DependencySeverity.Invalid,
            Assert.Single(application.Result.RepairRequests).Reason);
    }

    [Fact]
    public void Repair_request_merges_dirty_and_invalid_causes_with_invalid_dominance()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var score = model.Derived(sources)
            .Impact(policy => policy
                .SourceMemberChanged(value => value.Code, (_, _) => DependencySeverity.Dirty)
                .SourceMemberChanged(value => value.Enabled, (_, _) => DependencySeverity.Invalid))
            .Select(value => value.Code.Length + (value.Enabled ? 1 : 0));
        var invariant = model.Invariant(sources).From(score)
            .Must((_, value) => value == 0)
            .RepairWhenViolated();
        var source = new CodeHolder { Id = Guid.NewGuid() };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));

        source.Code = "A";
        source.Enabled = true;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(sources, source, value => value.Code, "", "A"),
            Change.Property(sources, source, value => value.Enabled, false, true)));

        Assert.Equal(DependencySeverity.Invalid,
            Assert.Single(application.Result.RepairRequests).Reason);
    }

    [Fact]
    public void Repair_request_deduplication_is_operation_scoped()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var score = model.Derived(sources)
            .Select(value => value.Code.Length + (value.Enabled ? 1 : 0));
        model.Invariant(sources).From(score)
            .Must((_, value) => value == 0)
            .RepairWhenViolated();
        var source = new CodeHolder { Id = Guid.NewGuid() };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));

        source.Code = "A";
        source.Enabled = true;
        var first = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(sources, source, value => value.Code, "", "A"),
            Change.Property(sources, source, value => value.Enabled, false, true)));

        source.Code = "AA";
        var second = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "AA")));

        Assert.Single(first.Result.RepairRequests);
        Assert.Single(second.Result.RepairRequests);
    }

    [Fact]
    public void Repair_disabled_affected_invariant_retains_lazy_dirty_state()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var length = model.Derived(sources).Select(value => value.Code.Length);
        var invariant = model.Invariant(sources).From(length).Must((_, value) => value > 0);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(invariant, source));

        source.Code = "";
        var application = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Code, "A", "")));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(length, source));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(invariant, source));
        Assert.Empty(application.Result.RepairRequests);
    }

    [Fact]
    public void Repair_requests_are_deduplicated_for_each_violated_invariant_and_source()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var score = model.Derived(sources)
            .Select(value => value.Code.Length + (value.Enabled ? 1 : 0));
        var invariant = model.Invariant(sources).From(score)
            .Must((_, value) => value == 0)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid() };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(invariant, source));

        source.Code = "A";
        source.Enabled = true;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(sources, source, value => value.Code, "", "A"),
            Change.Property(sources, source, value => value.Enabled, false, true)));

        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, source));
        Assert.Same(source, Assert.Single(application.Result.RepairRequests).Source);
    }

    [Fact]
    public void Repair_request_does_not_leak_after_later_valid_operation()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var enabled = model.Derived(sources).Select(value => value.Enabled);
        var invariant = model.Invariant(sources).From(enabled)
            .Must((_, value) => !value)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid() };
        runtime.Add(sources, source);
        Assert.True(runtime.Evaluate(invariant, source));

        source.Enabled = true;
        var violated = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Enabled, false, true)));
        Assert.Single(violated.Result.RepairRequests);

        source.Enabled = false;
        var valid = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            sources, source, value => value.Enabled, true, false)));

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(enabled, source));
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, source));
        Assert.Empty(valid.Result.RepairRequests);
    }

}
