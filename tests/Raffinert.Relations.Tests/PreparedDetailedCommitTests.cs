namespace Raffinert.Relations.Tests;

public sealed class PreparedDetailedCommitTests
{
    [Theory]
    [InlineData(RuntimeImpactDetailLevel.Summary)]
    [InlineData(RuntimeImpactDetailLevel.Causal)]
    public void Preview_is_repeatable_non_mutating_and_matches_commit(
        RuntimeImpactDetailLevel detailLevel)
    {
        var callbacks = new List<int>();
        var scenario = CreateScenario(callbacks);
        scenario.Source.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Value, 1, 2)));
        var version = scenario.Runtime.Version;
        var diagnostics = scenario.Runtime.Diagnostics;

        var first = scenario.Runtime.PreviewDetailed(prepared, detailLevel);
        var second = scenario.Runtime.PreviewDetailed(prepared, detailLevel);

        Assert.Equal(version, scenario.Runtime.Version);
        Assert.False(prepared.IsCommitted);
        Assert.False(prepared.IsDispatched);
        Assert.Equal(diagnostics, scenario.Runtime.Diagnostics);
        Assert.Empty(callbacks);
        Assert.Equal(first.ChangeImpact, second.ChangeImpact);
        AssertEquivalent(first, second);

        var committed = scenario.Runtime.CommitDetailed(prepared, detailLevel);

        Assert.Equal(first.ChangeImpact, committed.ChangeImpact);
        AssertEquivalent(first, committed);
        Assert.Empty(callbacks);
    }

    [Fact]
    public void Preview_rejects_stale_and_committed_prepared_mutations()
    {
        var scenario = CreateScenario([]);
        scenario.Source.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Value, 1, 2)));
        var other = new Source();
        scenario.Runtime.Add(scenario.Set, other);

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.PreviewDetailed(prepared));

        var current = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Note, null, null)));
        scenario.Runtime.Commit(current);
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.PreviewDetailed(current));
    }

    [Fact]
    public void Randomized_preview_and_commit_keep_source_scoped_semantics_equivalent()
    {
        var scenario = CreateScenario([]);
        var random = new Random(90441);
        var current = scenario.Source.Value;
        for (var step = 0; step < 75; step++)
        {
            var next = random.Next(0, 20);
            scenario.Source.Value = next;
            var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
                scenario.Set, scenario.Source, source => source.Value, current, next)));
            var before = scenario.Runtime.Diagnostics;

            var summary = scenario.Runtime.PreviewDetailed(prepared);
            var causal = scenario.Runtime.PreviewDetailed(prepared, RuntimeImpactDetailLevel.Causal);
            Assert.Equal(before, scenario.Runtime.Diagnostics);
            Assert.Equal(summary.DerivedImpacts.SelectMany(value => value.Sources).Select(value => value.Severity),
                causal.DerivedImpacts.SelectMany(value => value.Sources).Select(value => value.Severity));

            var committed = scenario.Runtime.CommitDetailed(prepared, RuntimeImpactDetailLevel.Causal);
            AssertEquivalent(causal, committed);
            current = next;
        }
    }

    [Theory]
    [InlineData(RuntimeImpactDetailLevel.Summary)]
    [InlineData(RuntimeImpactDetailLevel.Causal)]
    public void Binding_plan_installs_exact_result_without_rerunning_classifier(
        RuntimeImpactDetailLevel detailLevel)
    {
        var calls = 0;
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(set)
            .Impact(policy => policy.SourceMemberChanged(source => source.Value, (_, _) =>
                ++calls == 1 ? DependencySeverity.Invalid : DependencySeverity.Dirty))
            .Compute(source => source.Value);
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(set, [source]));
        Assert.Equal(1, runtime.Get(value, source));
        source.Value = 2;
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(set, source, item => item.Value, 1, 2)));

        var plan = runtime.PlanDetailed(prepared, detailLevel);

        Assert.Equal(1, calls);
        Assert.False(plan.IsCommitted);
        Assert.False(prepared.IsCommitted);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(DependencySeverity.Invalid,
            plan.Result.DerivedImpacts.Single().Sources.Single().Severity);

        var committed = runtime.Commit(plan);

        Assert.Same(plan.Result, committed);
        Assert.Equal(1, calls);
        Assert.True(plan.IsCommitted);
        Assert.True(prepared.IsCommitted);
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(value, source));
        Assert.Throws<InvalidOperationException>(() => runtime.Commit(plan));
    }

    [Fact]
    public void Binding_plan_rejects_stale_domain_drift_and_foreign_runtime()
    {
        var scenario = CreateScenario([]);
        scenario.Source.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Value, 1, 2)));
        var plan = scenario.Runtime.PlanDetailed(prepared);
        var other = CreateScenario([]).Runtime;

        Assert.Throws<ArgumentException>(() => other.Commit(plan));
        scenario.Source.Value = 3;
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.Commit(plan));

        scenario.Source.Value = 2;
        scenario.Runtime.Add(scenario.Set, new Source());
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.Commit(plan));
    }

    [Theory]
    [InlineData(RuntimeImpactDetailLevel.Summary)]
    [InlineData(RuntimeImpactDetailLevel.Causal)]
    public void Prepared_mutation_can_be_committed_with_details_before_dispatch(
        RuntimeImpactDetailLevel detailLevel)
    {
        var callbacks = new List<int>();
        var scenario = CreateScenario(callbacks);
        scenario.Source.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Value, 1, 2)));

        var result = scenario.Runtime.CommitDetailed(prepared, detailLevel);

        Assert.Equal(detailLevel, result.DetailLevel);
        Assert.Empty(callbacks);
        scenario.Runtime.Dispatch(prepared);
        Assert.Equal([2], callbacks);
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.Commit(prepared));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.CommitDetailed(prepared));
    }

    [Fact]
    public void Basic_commit_prevents_later_detailed_commit()
    {
        var scenario = CreateScenario([]);
        scenario.Source.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Value, 1, 2)));
        scenario.Runtime.Commit(prepared);

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.CommitDetailed(prepared));
    }

    [Fact]
    public void Normalized_provenance_preserves_collection_kind_item_and_property_net_transition()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var source = new Source();
        runtime.Add(set, source);
        var item = new object();
        source.Items.Add(item);
        source.Value = 3;
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.CollectionAdd(set, source, value => value.Items, item),
            Change.Property(set, source, value => value.Value, 1, 2),
            Change.Property(set, source, value => value.Value, 2, 3)));

        var collection = prepared.Provenance.Single(value => value.Kind == MutationOriginKind.CollectionChanged);
        Assert.Equal(CollectionChangeKind.Add, collection.CollectionKind);
        Assert.Same(item, collection.CollectionItem);
        var property = prepared.Provenance.Single(value => value.Kind == MutationOriginKind.SourceMemberChanged);
        Assert.Equal(1, property.OldValue);
        Assert.Equal(3, property.NewValue);
    }

    [Fact]
    public void Null_to_null_property_signal_is_not_inferred_as_collection()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var source = new Source();
        runtime.Add(set, source);

        var result = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            set, source, value => value.Note, null, null)), RuntimeImpactDetailLevel.Causal).Result;

        Assert.Equal(MutationOriginKind.SourceMemberChanged, Assert.Single(result.MutationOrigins).Kind);
    }

    [Theory]
    [InlineData(CollectionChangeKind.Remove)]
    [InlineData(CollectionChangeKind.Reset)]
    public void Normalized_provenance_retains_remove_and_reset(CollectionChangeKind kind)
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var item = new object();
        var source = new Source();
        source.Items.Add(item);
        runtime.Add(set, source);
        if (kind == CollectionChangeKind.Remove)
            source.Items.Remove(item);
        var mutation = kind == CollectionChangeKind.Remove
            ? Change.CollectionRemove(set, source, value => value.Items, item)
            : Change.CollectionReset(set, source, value => value.Items);

        var prepared = runtime.Prepare(MutationSet.Create(mutation));

        var provenance = Assert.Single(prepared.Provenance);
        Assert.Equal(kind, provenance.CollectionKind);
        Assert.Equal(kind == CollectionChangeKind.Remove ? item : null, provenance.CollectionItem);
    }

    private static Scenario CreateScenario(List<int> callbacks)
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(set).Compute(source => source.Value);
        model.Invariant(set).Using(value).Must((_, current) => current <= 1)
            .ScheduleRepairWith(source => callbacks.Add(source.Value));
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Value = 1 };
        runtime.Add(set, source);
        Assert.Equal(1, runtime.Get(value, source));
        return new Scenario(runtime, set, source);
    }

    private static void AssertEquivalent(RuntimeApplyResult expected, RuntimeApplyResult actual)
    {
        Assert.Equal(expected.DetailLevel, actual.DetailLevel);
        Assert.Equal(expected.RelationImpacts.Count, actual.RelationImpacts.Count);
        Assert.Equal(
            expected.DerivedImpacts.SelectMany(impact => impact.Sources)
                .Select(source => source.Severity),
            actual.DerivedImpacts.SelectMany(impact => impact.Sources)
                .Select(source => source.Severity));
        Assert.Equal(
            expected.InvariantImpacts.SelectMany(impact => impact.Sources)
                .Select(source => source.Severity),
            actual.InvariantImpacts.SelectMany(impact => impact.Sources)
                .Select(source => source.Severity));
        Assert.Equal(expected.RepairRequests.Select(request => request.Reason),
            actual.RepairRequests.Select(request => request.Reason));
        Assert.Equal(expected.ImmediateEvaluationRequests.Count, actual.ImmediateEvaluationRequests.Count);
        Assert.Equal(expected.MutationOrigins.Count, actual.MutationOrigins.Count);
    }

    private sealed record Scenario(RelationRuntime Runtime, ObjectSet<Source> Set, Source Source);

    private sealed class Source
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public int Value { get; set; } = 1;
        public string? Note { get; set; }
        public List<object> Items { get; } = [];
    }
}
