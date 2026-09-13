namespace Raffinert.Relations.Tests;

public sealed class CausalImpactTests
{
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
}
