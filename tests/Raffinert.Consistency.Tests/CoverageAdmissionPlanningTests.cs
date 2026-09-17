namespace Raffinert.Consistency.Tests;

public sealed class CoverageAdmissionPlanningTests
{
    [Fact]
    public void Coverage_admission_on_relation_right_is_invisible_after_planning()
    {
        var scenario = CreateRelationScenario(admitRight: true);
        var before = scenario.Runtime.Version;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(scenario.Rights.Definition, scenario.Right)));

        _ = scenario.Runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal);

        Assert.Equal(before, scenario.Runtime.Version);
        Assert.False(scenario.Runtime.IsRegistered(scenario.Rights.Definition, scenario.Right));
        Assert.False(scenario.Runtime.HasMaterializedPair(scenario.Relation, scenario.Left, scenario.Right));
        Assert.Empty(scenario.Runtime.LastRelationImpacts);
    }

    [Fact]
    public void Coverage_admission_on_relation_left_is_invisible_after_planning()
    {
        var scenario = CreateRelationScenario(admitRight: false);
        var before = scenario.Runtime.Version;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(scenario.Lefts.Definition, scenario.Left)));

        _ = scenario.Runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal);

        Assert.Equal(before, scenario.Runtime.Version);
        Assert.False(scenario.Runtime.IsRegistered(scenario.Lefts.Definition, scenario.Left));
        Assert.False(scenario.Runtime.HasMaterializedPair(scenario.Relation, scenario.Left, scenario.Right));
        Assert.Empty(scenario.Runtime.LastRelationImpacts);
    }

    [Fact]
    public void Coverage_admission_relation_baseline_installs_once_after_exact_plan_commit()
    {
        var scenario = CreateRelationScenario(admitRight: true);
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(scenario.Rights.Definition, scenario.Right)));
        var plan = scenario.Runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal);

        var result = scenario.Runtime.Commit(plan);

        Assert.Equal(1, scenario.Runtime.Version);
        Assert.True(scenario.Runtime.IsRegistered(scenario.Rights.Definition, scenario.Right));
        Assert.True(scenario.Runtime.HasMaterializedPair(scenario.Relation, scenario.Left, scenario.Right));
        Assert.Empty(result.RelationImpacts);
        Assert.DoesNotContain(result.MutationOrigins, origin => origin.Kind == MutationOriginKind.ObjectAdded);
    }

    [Fact]
    public void Coverage_admission_forward_patch_install_failure_restores_relation_state()
    {
        var scenario = CreateRelationScenario(admitRight: true);
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(scenario.Rights.Definition, scenario.Right)));
        var plan = scenario.Runtime.PlanDetailed(prepared);
        var diagnostics = scenario.Runtime.Diagnostics;
        scenario.Runtime.FailAfterNextForwardPatchApplyForTesting();

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.Commit(plan));

        Assert.Equal(0, scenario.Runtime.Version);
        var after = scenario.Runtime.Diagnostics;
        Assert.Equal(diagnostics.PredicateEvaluations, after.PredicateEvaluations);
        Assert.Equal(diagnostics.RelationPairsAdded, after.RelationPairsAdded);
        Assert.Equal(diagnostics.RelationPairsRemoved, after.RelationPairsRemoved);
        Assert.False(scenario.Runtime.IsRegistered(scenario.Rights.Definition, scenario.Right));
        Assert.False(scenario.Runtime.HasMaterializedPair(scenario.Relation, scenario.Left, scenario.Right));
    }

    [Fact]
    public void Coverage_admission_forward_patch_install_failure_restores_relation_and_projection_state()
    {
        var scenario = CreateProjectionScenario();
        var before = scenario.Runtime.Diagnostics;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(scenario.Links.Definition, scenario.Link)));
        var plan = scenario.Runtime.PlanDetailed(prepared);
        scenario.Runtime.FailAfterNextForwardPatchApplyForTesting();

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.Commit(plan));

        var after = scenario.Runtime.Diagnostics;
        Assert.Equal(0, scenario.Runtime.Version);
        Assert.Equal(before.ReverseProjectionEntryCount, after.ReverseProjectionEntryCount);
        Assert.Equal(before.ProjectedTargetCount, after.ProjectedTargetCount);
        Assert.False(scenario.Runtime.IsRegistered(scenario.Links.Definition, scenario.Link));
    }

    [Fact]
    public void Coverage_admission_projection_state_is_invisible_after_planning()
    {
        var scenario = CreateProjectionScenario();
        var before = scenario.Runtime.Diagnostics;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(scenario.Links.Definition, scenario.Link)));

        _ = scenario.Runtime.PlanDetailed(prepared);

        var after = scenario.Runtime.Diagnostics;
        Assert.Equal(0, scenario.Runtime.Version);
        Assert.Equal(before.ReverseProjectionEntryCount, after.ReverseProjectionEntryCount);
        Assert.Equal(before.ProjectedTargetCount, after.ProjectedTargetCount);
        Assert.False(scenario.Runtime.IsRegistered(scenario.Links.Definition, scenario.Link));
    }

    [Fact]
    public void Coverage_admission_projection_state_installs_once_after_exact_plan_commit()
    {
        var scenario = CreateProjectionScenario();
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(scenario.Links.Definition, scenario.Link)));
        var plan = scenario.Runtime.PlanDetailed(prepared);

        _ = scenario.Runtime.Commit(plan);

        Assert.Equal(1, scenario.Runtime.Version);
        Assert.True(scenario.Runtime.IsRegistered(scenario.Links.Definition, scenario.Link));
        Assert.Equal(1, scenario.Runtime.Diagnostics.ReverseProjectionEntryCount);
        Assert.Equal(1, scenario.Runtime.Diagnostics.ProjectedTargetCount);
    }

    [Fact]
    public void Dependency_patch_entry_count_includes_coverage_admission_scope()
    {
        var model = new ConsistencyModelBuilder();
        var consumers = model.Objects<Consumer>().Key(value => value.Id);
        var mirror = model.Derived(consumers).Compute(value => value.Amount * 2);
        var existing = new Consumer { Amount = 3 };
        var discovered = new Consumer { Amount = 7 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(consumers, [existing]));
        _ = runtime.Get(mirror, existing);

        var prepared = runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(consumers.Definition, discovered)));
        var helperCount = runtime.CaptureDependencyPatchEntryCount(prepared);
        var plan = runtime.PlanDetailed(prepared);
        var forwardScope = runtime.GetForwardPatchScopeCounts(plan);

        Assert.Equal(forwardScope.DependencyEntries, helperCount);
        Assert.True(helperCount > 0);
        Assert.Equal(0, runtime.Version);
        Assert.False(prepared.IsCommitted);
    }

    [Fact]
    public void Different_instances_with_same_runtime_key_across_admissions_fail_closed()
    {
        var model = new ConsistencyModelBuilder();
        var consumers = model.Objects<KeyedConsumer>().Key(value => value.Id);
        var registered = new KeyedConsumer { Id = 42, Amount = 1 };
        var first = new KeyedConsumer { Id = 7, Amount = 2 };
        var duplicate = new KeyedConsumer { Id = 7, Amount = 3 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(consumers, [registered]));

        var error = Assert.Throws<InvalidOperationException>(() => runtime.Prepare(MutationSet.Create(
            new CoverageAdmission(consumers.Definition, first),
            new CoverageAdmission(consumers.Definition, duplicate))));

        Assert.Contains("already registered", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.Version);
        Assert.True(runtime.IsRegistered(consumers.Definition, registered));
        Assert.False(runtime.IsRegistered(consumers.Definition, first));
        Assert.False(runtime.IsRegistered(consumers.Definition, duplicate));
    }

    private sealed class Consumer
    {
        public Guid Id { get; } = Guid.NewGuid();
        public decimal Amount { get; set; }
    }

    private sealed class KeyedConsumer
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
    }

    private static RelationScenario CreateRelationScenario(bool admitRight)
    {
        var model = new ConsistencyModelBuilder();
        var lefts = model.Objects<Left>().Key(value => value.Id);
        var rights = model.Objects<Right>().Key(value => value.Id);
        var relation = model.Relation(lefts, rights).Where((left, right) => left.Code == right.Code);
        model.Derived(lefts).Using(relation).Compute((_, matches) => matches.Count);
        var left = new Left { Code = "shared" };
        var right = new Right { Code = "shared" };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            if (admitRight)
                seed.Add(lefts, [left]);
            else
                seed.Add(rights, [right]);
        });
        return new RelationScenario(runtime, lefts, rights, relation, left, right);
    }

    private static ProjectionScenario CreateProjectionScenario()
    {
        var model = new ConsistencyModelBuilder();
        var orders = model.Objects<Order>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var total = model.Derived(orders).Compute(value => value.Total);
        _ = model.Derived(links).Using(value => value.Order, total).Compute((_, current) => current);
        var order = new Order { Total = 11 };
        var link = new Link { Order = order };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(orders, [order]));
        return new ProjectionScenario(runtime, links, link);
    }

    private sealed record RelationScenario(
        ConsistencyRuntime Runtime,
        ObjectSet<Left> Lefts,
        ObjectSet<Right> Rights,
        Relation<Left, Right> Relation,
        Left Left,
        Right Right);

    private sealed record ProjectionScenario(ConsistencyRuntime Runtime, ObjectSet<Link> Links, Link Link);

    private sealed class Left
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }

    private sealed class Right
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }

    private sealed class Order
    {
        public Guid Id { get; } = Guid.NewGuid();
        public decimal Total { get; set; }
    }

    private sealed class Link
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required Order Order { get; init; }
    }
}
