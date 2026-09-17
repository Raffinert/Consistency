namespace Raffinert.Consistency.Tests;

public sealed class DependencyDagCausalEvidenceTests
{
    [Fact]
    public void Causal_evidence_is_identical_for_compiled_DAG_chain_and_diamond_after_topology_refactor()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(source => source.Value).Named("root");
        var chain = model.Derived(sources).Using(root)
            .Compute((_, value) => value + 1).Named("chain");
        var left = model.Derived(sources).Using(chain)
            .Compute((_, value) => value + 2).Named("left");
        var right = model.Derived(sources).Using(chain)
            .Compute((_, value) => value + 3).Named("right");
        var join = model.Derived(sources).Using(left, right)
            .Compute((_, first, second) => first + second).Named("join");
        model.Invariant(sources).Using(join)
            .Must((_, value) => value < 100).Named("limit");
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Get(join, source);
        source.Value = 2;

        var result = runtime.ApplyDetailed(
            MutationSet.Create(Change.Property(sources, source, value => value.Value, 1, 2)),
            RuntimeImpactDetailLevel.Causal).Result;

        AssertCause(result, "chain", ["root"]);
        AssertCause(result, "left", ["chain"]);
        AssertCause(result, "right", ["chain"]);
        AssertCause(result, "join", ["left", "right"]);
        var invariantSource = Assert.Single(Assert.Single(result.InvariantImpacts).Sources);
        var invariantCause = Assert.IsType<UpstreamDerivedCause>(Assert.Single(invariantSource.Causes));
        Assert.Equal("join", invariantCause.DefinitionKey);
        Assert.Equal(ImpactCausePrecision.Exact, invariantCause.Precision);
    }

    private static void AssertCause(
        RuntimeApplyResult result,
        string definitionKey,
        string[] expectedUpstreams)
    {
        var source = Assert.Single(result.DerivedImpacts
            .Single(impact => impact.DefinitionKey == definitionKey).Sources);
        var causes = source.Causes.OfType<UpstreamDerivedCause>().ToArray();
        Assert.Equal(
            expectedUpstreams.Order(StringComparer.Ordinal),
            causes.Select(cause => cause.DefinitionKey).Order(StringComparer.Ordinal));
        Assert.All(causes, cause => Assert.Equal(ImpactCausePrecision.Exact, cause.Precision));
        Assert.All(causes, cause => Assert.NotNull(cause.UpstreamImpactId));
    }

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
    }
}
