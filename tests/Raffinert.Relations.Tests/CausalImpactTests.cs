namespace Raffinert.Relations.Tests;

public sealed class CausalImpactTests
{
    [Fact]
    public void Exact_added_and_removed_relation_causes_use_route_trigger_kind()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        var removedSource = new Source { Code = "A" };
        var addedSource = new Source { Code = "B" };
        var item = new Item { Code = "A" };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [removedSource, addedSource]);
            seed.Add(items, [item]);
        });
        item.Code = "B";

        var result = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            items, item, value => value.Code, "A", "B")), RuntimeImpactDetailLevel.Causal).Result;

        var impacts = result.DerivedImpacts.Single().Sources;
        var removed = Assert.IsType<RelationDependencyCause>(Assert.Single(
            impacts.Single(value => ReferenceEquals(value.Source, removedSource)).Causes));
        var added = Assert.IsType<RelationDependencyCause>(Assert.Single(
            impacts.Single(value => ReferenceEquals(value.Source, addedSource)).Causes));
        Assert.Equal(RelationImpactCauseKind.MembershipRemoved, removed.Kind);
        Assert.Equal(RelationImpactCauseKind.MembershipAdded, added.Kind);
        Assert.Equal(ImpactCausePrecision.Exact, removed.Precision);
        Assert.Equal(ImpactCausePrecision.Exact, added.Precision);
        Assert.Equal([0], removed.OriginIds);
        Assert.Equal([0], added.OriginIds);
    }

    [Fact]
    public void Causal_mode_explains_direct_and_upstream_impacts_without_changing_semantics()
    {
        var summary = CreateScenario();
        var causal = CreateScenario();
        summary.Source.Quantity = 8;
        causal.Source.Quantity = 8;

        var summaryApplication = summary.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            summary.Set, summary.Source, source => source.Quantity, 10, 8)));
        var causalApplication = causal.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            causal.Set, causal.Source, source => source.Quantity, 10, 8)), RuntimeImpactDetailLevel.Causal);

        Assert.Equal(RuntimeImpactDetailLevel.Summary, summaryApplication.Result.DetailLevel);
        Assert.Empty(summaryApplication.Result.MutationOrigins);
        Assert.All(summaryApplication.Result.DerivedImpacts.SelectMany(impact => impact.Sources),
            impact => Assert.Empty(impact.Causes));
        Assert.Equal(RuntimeImpactDetailLevel.Causal, causalApplication.Result.DetailLevel);
        Assert.Single(causalApplication.Result.MutationOrigins);
        var direct = causalApplication.Result.DerivedImpacts.Single(impact => impact.DefinitionKey == "capacity")
            .Sources.Single();
        Assert.Equal(DependencySeverity.Invalid, direct.Severity);
        Assert.IsType<DirectSourceMemberCause>(Assert.Single(direct.Causes));
        var downstream = causalApplication.Result.DerivedImpacts.Single(impact => impact.DefinitionKey == "validity")
            .Sources.Single();
        Assert.Contains(downstream.Causes, cause => cause is UpstreamDerivedCause { DefinitionKey: "capacity" });
        Assert.Equal(summary.Runtime.GetState(summary.Validity, summary.Source),
            causal.Runtime.GetState(causal.Validity, causal.Source));
        Assert.Equal(
            summaryApplication.Result.RepairRequests.Select(request => request.Reason),
            causalApplication.Result.RepairRequests.Select(request => request.Reason));
        Assert.Contains("capacity -> Invalid", RuntimeImpactTraceRenderer.Render(causalApplication.Result));
    }

    [Fact]
    public void Conservative_relation_causes_are_marked_conservative()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code)
            .Named("matches");
        var count = model.Derived(sources).Using(relation).PreferConservativePropagation()
            .Compute((_, matches) => matches.Count).Named("count");
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Code = "A" };
        var item = new Item { Code = "A" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(1, runtime.Get(count, source));
        item.Code = "B";

        var result = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            items, item, value => value.Code, "A", "B")), RuntimeImpactDetailLevel.Causal).Result;

        var cause = Assert.IsType<RelationDependencyCause>(Assert.Single(
            result.DerivedImpacts.Single().Sources.Single().Causes));
        Assert.Equal(ImpactCausePrecision.Conservative, cause.Precision);
        Assert.Equal(RelationImpactCauseKind.ConservativeCandidate, cause.Kind);
        Assert.Equal([0], cause.OriginIds);
    }

    [Fact]
    public void Relation_cause_does_not_claim_unrelated_batch_origin()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).PreferConservativePropagation()
            .Compute((_, matches) => matches.Count).Named("count");
        var source = new Source { Code = "A" };
        var relevant = new Item { Code = "A" };
        var unrelated = new Source { Code = "Z", Reserved = 1 };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [source, unrelated]);
            seed.Add(items, [relevant]);
        });
        relevant.Code = "B";
        unrelated.Reserved = 2;

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, relevant, item => item.Code, "A", "B"),
            Change.Property(sources, unrelated, value => value.Reserved, 1, 2)),
            RuntimeImpactDetailLevel.Causal).Result;

        var cause = Assert.IsType<RelationDependencyCause>(Assert.Single(
            result.DerivedImpacts.Single().Sources.Single().Causes));
        Assert.Equal([0], cause.OriginIds);
    }

    [Fact]
    public void Two_conservative_right_changes_produce_precise_origin_sets_per_left()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).PreferConservativePropagation()
            .Compute((_, matches) => matches.Count);
        var firstSource = new Source { Code = "A" };
        var secondSource = new Source { Code = "B" };
        var firstItem = new Item { Code = "A" };
        var secondItem = new Item { Code = "B" };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [firstSource, secondSource]);
            seed.Add(items, [firstItem, secondItem]);
        });
        firstItem.Code = "C";
        secondItem.Code = "D";

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, firstItem, item => item.Code, "A", "C"),
            Change.Property(items, secondItem, item => item.Code, "B", "D")),
            RuntimeImpactDetailLevel.Causal).Result;

        var impacts = result.DerivedImpacts.Single().Sources;
        Assert.Equal([0], Assert.IsType<RelationDependencyCause>(Assert.Single(
            impacts.Single(impact => ReferenceEquals(impact.Source, firstSource)).Causes)).OriginIds);
        Assert.Equal([1], Assert.IsType<RelationDependencyCause>(Assert.Single(
            impacts.Single(impact => ReferenceEquals(impact.Source, secondSource)).Causes)).OriginIds);
    }

    [Fact]
    public void Removed_source_origin_captures_durable_identity_before_lifecycle_cleanup()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Named("sources").Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var source = new Source();
        runtime.Add(set, source);

        var result = runtime.ApplyDetailed(
            MutationSet.Create(Change.Remove(set, source)), RuntimeImpactDetailLevel.Causal).Result;

        var origin = Assert.Single(result.MutationOrigins);
        Assert.Equal(MutationOriginKind.ObjectRemoved, origin.Kind);
        Assert.True(origin.SourceIdentity!.IsDurable);
        Assert.Equal(source.Id.ToString("D"), origin.SourceIdentity.DurableIdentity!.KeyParts.Single().Value);
    }

    [Fact]
    public void Added_source_origin_uses_its_final_key_without_prior_registration()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Named("sources").Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var source = new Source();

        var result = runtime.ApplyDetailed(
            MutationSet.Create(Change.Add(set, source)), RuntimeImpactDetailLevel.Causal).Result;

        var origin = Assert.Single(result.MutationOrigins);
        Assert.Equal(MutationOriginKind.ObjectAdded, origin.Kind);
        Assert.True(origin.SourceIdentity!.IsDurable);
        Assert.Equal(source.Id.ToString("D"), origin.SourceIdentity.DurableIdentity!.KeyParts.Single().Value);
    }

    [Fact]
    public void Summary_impacts_do_not_allocate_public_causal_node_ids()
    {
        var scenario = CreateScenario();
        scenario.Source.Quantity = 8;

        var result = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Quantity, 10, 8))).Result;

        Assert.All(result.DerivedImpacts.SelectMany(impact => impact.Sources), source =>
        {
            Assert.Null(source.ImpactId);
            Assert.Empty(source.Causes);
        });
    }

    [Fact]
    public void Randomized_summary_and_causal_waves_have_equivalent_states_and_requests()
    {
        var summary = CreateScenario();
        var causal = CreateScenario();
        var random = new Random(7421);
        var summaryValue = 10;
        var causalValue = 10;
        for (var step = 0; step < 40; step++)
        {
            var next = random.Next(6, 15);
            summary.Source.Quantity = next;
            causal.Source.Quantity = next;
            var summaryResult = summary.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
                summary.Set, summary.Source, source => source.Quantity, summaryValue, next))).Result;
            var causalResult = causal.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
                causal.Set, causal.Source, source => source.Quantity, causalValue, next)),
                RuntimeImpactDetailLevel.Causal).Result;
            summaryValue = causalValue = next;

            Assert.Equal(summary.Runtime.GetState(summary.Validity, summary.Source),
                causal.Runtime.GetState(causal.Validity, causal.Source));
            Assert.Equal(summaryResult.RepairRequests.Select(request => request.Reason),
                causalResult.RepairRequests.Select(request => request.Reason));
            Assert.Equal(summaryResult.DerivedImpacts.SelectMany(impact => impact.Sources).Select(value => value.Severity),
                causalResult.DerivedImpacts.SelectMany(impact => impact.Sources).Select(value => value.Severity));
        }
    }

    [Fact]
    public void Diamond_has_one_downstream_impact_with_both_direct_upstream_causes()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var left = model.Derived(set).Compute(source => source.Quantity).Named("left");
        var right = model.Derived(set).Compute(source => source.Quantity * 2).Named("right");
        var bottom = model.Derived(set).Using(left, right)
            .Compute((_, first, second) => first + second).Named("bottom");
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Quantity = 1 };
        runtime.Add(set, source);
        Assert.Equal(3, runtime.Get(bottom, source));
        source.Quantity = 2;

        var result = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            set, source, value => value.Quantity, 1, 2)), RuntimeImpactDetailLevel.Causal).Result;

        var impact = result.DerivedImpacts.Single(value => value.DefinitionKey == "bottom");
        var causes = impact.Sources.Single().Causes.OfType<UpstreamDerivedCause>().ToArray();
        Assert.Equal(2, causes.Length);
        Assert.Equal(["left", "right"], causes.Select(cause => cause.DefinitionKey));
        Assert.All(causes, cause => Assert.NotNull(cause.UpstreamImpactId));
        Assert.Contains("because left -> Dirty", RuntimeImpactTraceRenderer.Render(result));
    }

    [Fact]
    public void Projected_upstream_cause_references_the_upstream_source_scoped_impact()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var links = model.Objects<Link>().Key(link => link.Id);
        var quantity = model.Derived(sources).Compute(source => source.Quantity).Named("quantity");
        model.Derived(links).Using(link => link.Source, quantity)
            .Compute((_, value) => value).Named("projected");
        var first = new Source { Quantity = 1 };
        var second = new Source { Quantity = 2 };
        var link = new Link { Source = second };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [first, second]);
            seed.Add(links, [link]);
        });
        first.Quantity = 3;
        second.Quantity = 4;

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(sources, first, value => value.Quantity, 1, 3),
            Change.Property(sources, second, value => value.Quantity, 2, 4)),
            RuntimeImpactDetailLevel.Causal).Result;

        var upstream = result.DerivedImpacts.Single(impact => impact.DefinitionKey == "quantity").Sources;
        Assert.Equal(2, upstream.Select(source => source.ImpactId).Distinct().Count());
        var cause = Assert.IsType<UpstreamDerivedCause>(Assert.Single(
            result.DerivedImpacts.Single(impact => impact.DefinitionKey == "projected").Sources.Single().Causes));
        Assert.Equal(upstream.Single(source => ReferenceEquals(source.Source, second)).ImpactId,
            cause.UpstreamImpactId);
    }

    [Fact]
    public void Direct_cause_keeps_local_dirty_severity_when_upstream_makes_final_invalid()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var upstream = model.Derived(set)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(source => source.Quantity).Named("upstream");
        var downstream = model.Derived(set).Using(upstream)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Dirty))
            .Compute((source, value) => value + source.Reserved).Named("downstream");
        var source = new Source { Quantity = 1, Reserved = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(set, [source]));
        _ = runtime.Get(downstream, source);
        source.Quantity = 2;
        source.Reserved = 2;

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(set, source, value => value.Quantity, 1, 2),
            Change.Property(set, source, value => value.Reserved, 1, 2)),
            RuntimeImpactDetailLevel.Causal).Result;

        var impact = result.DerivedImpacts.Single(value => value.DefinitionKey == "downstream").Sources.Single();
        Assert.Equal(DependencySeverity.Invalid, impact.Severity);
        Assert.Equal(DependencySeverity.Dirty,
            impact.Causes.OfType<DirectSourceMemberCause>().Single().ClassifiedSeverity);
    }

    [Fact]
    public void Schedule_repair_escalation_is_explicit()
    {
        var scenario = CreateScenario();
        scenario.Source.Quantity = 11;

        var result = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Quantity, 10, 11)),
            RuntimeImpactDetailLevel.Causal).Result;

        Assert.Contains(result.InvariantImpacts.Single().Sources.Single().Causes,
            cause => cause is InvariantReactionCause { Reaction: InvariantReaction.ScheduleRepair });
    }

    [Fact]
    public void Already_invalid_inherited_impact_has_no_fake_dirty_escalation()
    {
        var scenario = CreateScenario();
        scenario.Source.Quantity = 8;

        var result = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Quantity, 10, 8)),
            RuntimeImpactDetailLevel.Causal).Result;

        Assert.DoesNotContain(result.InvariantImpacts.Single().Sources.Single().Causes,
            cause => cause is InvariantReactionCause);
    }

    private static Scenario CreateScenario()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Named("po-lines").Key(source => source.Id);
        var capacity = model.Derived(set)
            .Impact(policy => policy.SourceMemberChanged(
                source => source.Quantity,
                (oldValue, newValue) => newValue < oldValue
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty))
            .Compute(source => source.Quantity).Named("capacity");
        var validity = model.Derived(set).Using(capacity)
            .Compute((source, available) => available >= source.Reserved).Named("validity");
        model.Invariant(set).Using(validity).Must((_, valid) => valid).Named("link-invariant")
            .ScheduleRepairWith(_ => { });
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Quantity = 10, Reserved = 9, Code = "A" };
        runtime.Add(set, source);
        Assert.True(runtime.Get(validity, source));
        return new Scenario(runtime, set, source, validity);
    }

    private sealed record Scenario(
        RelationRuntime Runtime,
        ObjectSet<Source> Set,
        Source Source,
        Derived<Source, bool> Validity);

    private sealed class Source
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Quantity { get; set; }
        public int Reserved { get; set; }
        public string Code { get; set; } = "";
    }

    private sealed class Item
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }

    private sealed class Link
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Source Source { get; set; } = null!;
    }
}
