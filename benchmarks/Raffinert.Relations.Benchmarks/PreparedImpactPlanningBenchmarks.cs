using BenchmarkDotNet.Attributes;

namespace Raffinert.Relations.Benchmarks;

[MemoryDiagnoser]
public class PreparedImpactPlanningBenchmarks
{
    private Scenario _summary = null!;
    private Scenario _causal = null!;
    private Scenario _previewSummary = null!;
    private Scenario _previewCausal = null!;
    private Scenario _plannedSummary = null!;
    private Scenario _plannedCausal = null!;

    [GlobalSetup]
    public void Setup()
    {
        _summary = CreateScenario();
        _causal = CreateScenario();
        _previewSummary = CreateScenario();
        _previewCausal = CreateScenario();
        _plannedSummary = CreateScenario();
        _plannedCausal = CreateScenario();
    }

    [Benchmark(Baseline = true)]
    public RuntimeApplyResult CommitSummary() => Commit(_summary, RuntimeImpactDetailLevel.Summary);

    [Benchmark]
    public RuntimeApplyResult CommitCausal() => Commit(_causal, RuntimeImpactDetailLevel.Causal);

    [Benchmark]
    public RuntimeApplyResult PreviewSummary() => Preview(_previewSummary, RuntimeImpactDetailLevel.Summary);

    [Benchmark]
    public RuntimeApplyResult PreviewCausal() => Preview(_previewCausal, RuntimeImpactDetailLevel.Causal);

    [Benchmark]
    public PreparedImpactPlan PlanSummary() => Plan(_plannedSummary, RuntimeImpactDetailLevel.Summary);

    [Benchmark]
    public PreparedImpactPlan PlanCausal() => Plan(_plannedCausal, RuntimeImpactDetailLevel.Causal);

    private static RuntimeApplyResult Commit(Scenario scenario, RuntimeImpactDetailLevel detail)
    {
        var prepared = scenario.Next();
        return scenario.Runtime.CommitDetailed(prepared, detail);
    }

    private static RuntimeApplyResult Preview(Scenario scenario, RuntimeImpactDetailLevel detail)
    {
        return scenario.Runtime.PreviewDetailed(scenario.Pending(), detail);
    }

    private static PreparedImpactPlan Plan(Scenario scenario, RuntimeImpactDetailLevel detail) =>
        scenario.Runtime.PlanDetailed(scenario.Pending(), detail);

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
        private PreparedMutation? _pending;
        public RelationRuntime Runtime => runtime;

        public PreparedMutation Next()
        {
            var oldValue = _value++;
            source.Value = _value;
            return runtime.Prepare(MutationSet.Create(
                Change.Property(set, source, value => value.Value, oldValue, _value)));
        }

        public PreparedMutation Pending() => _pending ??= Next();
    }

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
    }
}

[MemoryDiagnoser, InvocationCount(1)]
public class PreparedPatchInstallBenchmarks
{
    private RelationRuntime _runtime = null!;
    private PreparedImpactPlan _summary = null!;
    private PreparedImpactPlan _causal = null!;

    [IterationSetup(Target = nameof(CommitPlannedSummary))]
    public void SetupSummary() => (_runtime, _summary) = CreatePlan(RuntimeImpactDetailLevel.Summary);

    [IterationSetup(Target = nameof(CommitPlannedCausal))]
    public void SetupCausal() => (_runtime, _causal) = CreatePlan(RuntimeImpactDetailLevel.Causal);

    [Benchmark]
    public RuntimeApplyResult CommitPlannedSummary() => _runtime.Commit(_summary);

    [Benchmark]
    public RuntimeApplyResult CommitPlannedCausal() => _runtime.Commit(_causal);

    private static (RelationRuntime Runtime, PreparedImpactPlan Plan) CreatePlan(RuntimeImpactDetailLevel detail)
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<PatchSource>().Key(source => source.Id);
        model.Derived(set).Compute(source => source.Value);
        var source = new PatchSource();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(set, [source]));
        source.Value = 1;
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(set, source, value => value.Value, 0, 1)));
        return (runtime, runtime.PlanDetailed(prepared, detail));
    }

    private sealed class PatchSource
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
    }
}
