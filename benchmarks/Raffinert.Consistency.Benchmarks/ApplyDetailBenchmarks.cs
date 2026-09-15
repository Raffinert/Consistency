using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class ApplyDetailBenchmarks
{
    private ConsistencyRuntime _basic = null!;
    private ConsistencyRuntime _detailed = null!;
    private ConsistencyRuntime _causal = null!;
    private ObjectSet<Source> _basicSet = null!;
    private ObjectSet<Source> _detailedSet = null!;
    private Source[] _basicSources = null!;
    private Source[] _detailedSources = null!;
    private ObjectSet<Source> _causalSet = null!;
    private Source[] _causalSources = null!;
    private int _value;

    [Params(1, 10, 100)]
    public int ChangeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (_basic, _basicSet, _basicSources) = CreateScenario(ChangeCount);
        (_detailed, _detailedSet, _detailedSources) = CreateScenario(ChangeCount);
        (_causal, _causalSet, _causalSources) = CreateScenario(ChangeCount);
    }

    [IterationSetup]
    public void Mutate()
    {
        _value++;
        foreach (var source in _basicSources)
            source.Value = _value;
        foreach (var source in _detailedSources)
            source.Value = _value;
        foreach (var source in _causalSources)
            source.Value = _value;
    }

    [Benchmark(Baseline = true)]
    public ChangeImpact Apply() => _basic.Apply(CreateChanges(_basicSet, _basicSources));

    [Benchmark]
    public RuntimeApplyResult ApplyDetailedAndDispatch()
    {
        var application = _detailed.ApplyDetailed(CreateChanges(_detailedSet, _detailedSources));
        application.Dispatch.Invoke();
        return application.Result;
    }

    [Benchmark]
    public RuntimeApplyResult ApplyDetailedCausalAndDispatch()
    {
        var application = _causal.ApplyDetailed(
            CreateChanges(_causalSet, _causalSources), RuntimeImpactDetailLevel.Causal);
        application.Dispatch.Invoke();
        return application.Result;
    }

    private MutationSet CreateChanges(ObjectSet<Source> set, IEnumerable<Source> sources) =>
        MutationSet.Create(sources.Select(source =>
            Change.Property(set, source, value => value.Value, _value - 1, _value)).ToArray());

    private static (ConsistencyRuntime Runtime, ObjectSet<Source> Set, Source[] Sources) CreateScenario(int count)
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        model.Derived(set).Compute(source => source.Value);
        var runtime = model.Build().CreateRuntime();
        var sources = Enumerable.Range(0, count).Select(_ => new Source()).ToArray();
        runtime.Apply(MutationSet.Create(sources.Select(source => Change.Add(set, source)).ToArray()));
        return (runtime, set, sources);
    }

    private sealed class Source
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Value { get; set; }
    }
}
