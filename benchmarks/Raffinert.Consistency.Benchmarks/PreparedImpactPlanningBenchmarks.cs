using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class PreparedImpactPlanningBenchmarks
{
    [Params(10_000, 100_000)]
    public int Population { get; set; }

    private Scenario _summary = null!;
    private Scenario _causal = null!;
    private Scenario _previewSummary = null!;
    private Scenario _previewCausal = null!;
    private Scenario _plannedSummary = null!;
    private Scenario _plannedCausal = null!;

    [GlobalSetup]
    public void Setup()
    {
        _summary = CreateScenario(Population);
        _causal = CreateScenario(Population);
        _previewSummary = CreateScenario(Population);
        _previewCausal = CreateScenario(Population);
        _plannedSummary = CreateScenario(Population);
        _plannedCausal = CreateScenario(Population);
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

    private static Scenario CreateScenario(int population)
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(set, items).Where((source, item) => source.Code == item.Code);
        var value = model.Derived(set).Compute(source => source.Child.Value + source.Value);
        var count = model.Derived(set).Using(relation).Incrementally()
            .Compute((_, matches) => matches.Count);
        var sources = Enumerable.Range(0, population).Select(index => new Source
        {
            Code = index == 0 ? "A" : $"U-{index}",
            Child = new Child { Value = index }
        }).ToArray();
        var relatedItems = Enumerable.Range(0, population).Select(index => new Item
        {
            Code = index == 0 ? "A" : $"U-{index}"
        }).ToArray();
        var source = sources[0];
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(set, sources);
            seed.Add(items, relatedItems);
        });
        foreach (var candidate in sources)
        {
            _ = runtime.Get(value, candidate);
            _ = runtime.Get(count, candidate);
        }
        return new Scenario(runtime, set, source);
    }

    private sealed class Scenario(ConsistencyRuntime runtime, ObjectSet<Source> set, Source source)
    {
        private int _value;
        private string _code = "A";
        private PreparedMutation? _pending;
        public ConsistencyRuntime Runtime => runtime;

        public PreparedMutation Next()
        {
            var oldValue = _value++;
            source.Value = _value;
            var oldCode = _code;
            _code = _code == "A" ? "B" : "A";
            source.Code = _code;
            return runtime.Prepare(MutationSet.Create(
                Change.Property(set, source, value => value.Value, oldValue, _value),
                Change.Property(set, source, value => value.Code, oldCode, _code)));
        }

        public PreparedMutation Pending() => _pending ??= Next();
    }

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
        public string Code { get; set; } = "";
        public Child Child { get; set; } = null!;
    }

    private sealed class Child { public int Value { get; set; } }
    private sealed class Item
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }
}

[MemoryDiagnoser, InvocationCount(1)]
public class PreparedPatchInstallBenchmarks
{
    [Params(10_000, 100_000)]
    public int Population { get; set; }

    private ConsistencyRuntime _runtime = null!;
    private PreparedImpactPlan _summary = null!;
    private PreparedImpactPlan _causal = null!;

    [IterationSetup(Target = nameof(CommitPlannedSummary))]
    public void SetupSummary() => (_runtime, _summary) = CreatePlan(RuntimeImpactDetailLevel.Summary, Population);

    [IterationSetup(Target = nameof(CommitPlannedCausal))]
    public void SetupCausal() => (_runtime, _causal) = CreatePlan(RuntimeImpactDetailLevel.Causal, Population);

    [Benchmark]
    public RuntimeApplyResult CommitPlannedSummary() => _runtime.Commit(_summary);

    [Benchmark]
    public RuntimeApplyResult CommitPlannedCausal() => _runtime.Commit(_causal);

    private static (ConsistencyRuntime Runtime, PreparedImpactPlan Plan) CreatePlan(
        RuntimeImpactDetailLevel detail,
        int population)
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<PatchSource>().Key(source => source.Id);
        model.Derived(set).Compute(source => source.Value);
        var sources = Enumerable.Range(0, population).Select(_ => new PatchSource()).ToArray();
        var source = sources[0];
        var runtime = model.Build().CreateRuntime(seed => seed.Add(set, sources));
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

[MemoryDiagnoser]
public class PreparedPatchInstallComponentBenchmarks
{
    [Params(10_000, 100_000)]
    public int Population { get; set; }

    private ConsistencyRuntime _runtime = null!;
    private PreparedImpactPlan _plan = null!;
    private object _rollbackJournal = null!;

    [GlobalSetup]
    public void Setup()
    {
        (_runtime, _plan) = CreatePlan(Population);
        _rollbackJournal = _runtime.CaptureInstallRollbackForBenchmark(_plan.Prepared);
    }

    [Benchmark(Baseline = true)]
    public void ValidatePreparedPlan() => _runtime.ValidatePlanInstallForBenchmark(_plan);

    [Benchmark]
    public object CaptureInstallRollbackJournal() =>
        _runtime.CaptureInstallRollbackForBenchmark(_plan.Prepared);

    [Benchmark]
    public void ApplyForwardPatchAndRestoreRollbackJournal() =>
        _runtime.ApplyForwardPatchAndRestoreForBenchmark(_plan, _rollbackJournal);

    private static (ConsistencyRuntime Runtime, PreparedImpactPlan Plan) CreatePlan(int population)
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<ComponentSource>().Key(source => source.Id);
        var items = model.Objects<ComponentItem>().Key(item => item.Id);
        var relation = model.Relation(sources, items)
            .Where((source, item) => source.Code == item.Code);
        var value = model.Derived(sources).Compute(source => source.Value);
        var count = model.Derived(sources).Using(relation).Incrementally()
            .Compute((_, matches) => matches.Count);
        var sourcePopulation = Enumerable.Range(0, population).Select(index => new ComponentSource
        {
            Code = index == 0 ? "A" : $"U-{index}"
        }).ToArray();
        var itemPopulation = Enumerable.Range(0, population).Select(index => new ComponentItem
        {
            Code = index == 0 ? "A" : $"U-{index}"
        }).ToArray();
        var source = sourcePopulation[0];
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, sourcePopulation);
            seed.Add(items, itemPopulation);
        });
        _ = runtime.Get(value, source);
        _ = runtime.Get(count, source);
        source.Value = 1;
        source.Code = "B";
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(sources, source, candidate => candidate.Value, 0, 1),
            Change.Property(sources, source, candidate => candidate.Code, "A", "B")));
        return (runtime, runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal));
    }

    private sealed class ComponentSource
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
        public string Code { get; set; } = "";
    }

    private sealed class ComponentItem
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }
}
