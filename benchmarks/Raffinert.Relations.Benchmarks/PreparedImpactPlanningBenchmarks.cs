using BenchmarkDotNet.Attributes;

namespace Raffinert.Relations.Benchmarks;

[MemoryDiagnoser]
public class PreparedImpactPlanningBenchmarks
{
    private Scenario _summary = null!;
    private Scenario _causal = null!;
    private Scenario _previewSummary = null!;
    private Scenario _previewCausal = null!;

    [GlobalSetup]
    public void Setup()
    {
        _summary = CreateScenario();
        _causal = CreateScenario();
        _previewSummary = CreateScenario();
        _previewCausal = CreateScenario();
    }

    [Benchmark(Baseline = true)]
    public RuntimeApplyResult CommitSummary() => Commit(_summary, RuntimeImpactDetailLevel.Summary);

    [Benchmark]
    public RuntimeApplyResult CommitCausal() => Commit(_causal, RuntimeImpactDetailLevel.Causal);

    [Benchmark]
    public RuntimeApplyResult PreviewSummary() => Preview(_previewSummary, RuntimeImpactDetailLevel.Summary);

    [Benchmark]
    public RuntimeApplyResult PreviewCausal() => Preview(_previewCausal, RuntimeImpactDetailLevel.Causal);

    private static RuntimeApplyResult Commit(Scenario scenario, RuntimeImpactDetailLevel detail)
    {
        var prepared = scenario.Next();
        return scenario.Runtime.CommitDetailed(prepared, detail);
    }

    private static RuntimeApplyResult Preview(Scenario scenario, RuntimeImpactDetailLevel detail)
    {
        var prepared = scenario.Next();
        var result = scenario.Runtime.PreviewDetailed(prepared, detail);
        scenario.Runtime.Commit(prepared);
        return result;
    }

    private static Scenario CreateScenario()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        model.Derived(set).Compute(source => source.Value);
        var source = new Source();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(set, [source]));
        return new Scenario(runtime, set, source);
    }

    private sealed class Scenario(RelationRuntime runtime, ObjectSet<Source> set, Source source)
    {
        private int _value;
        public RelationRuntime Runtime => runtime;

        public PreparedMutation Next()
        {
            var oldValue = _value++;
            source.Value = _value;
            return runtime.Prepare(MutationSet.Create(
                Change.Property(set, source, value => value.Value, oldValue, _value)));
        }
    }

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
    }
}
