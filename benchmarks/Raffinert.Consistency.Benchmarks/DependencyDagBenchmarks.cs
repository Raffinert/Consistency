using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class DependencyDagBenchmarks
{
    private MutationScenario _deep32 = null!;
    private MutationScenario _deep128 = null!;
    private MutationScenario _diamond32 = null!;
    private MutationScenario _sparse128 = null!;
    private PlanningScenario _planning128 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _deep32 = CreateDeepChain(32);
        _deep128 = CreateDeepChain(128);
        _diamond32 = CreateDiamond(32);
        _sparse128 = CreateSparseDag(128);
        _planning128 = CreatePlanningScenario(128);
    }

    [Benchmark]
    public CompiledConsistencyModel CompileDeepDag() => BuildDeepChainModel(128).Model.Build();

    [Benchmark(Baseline = true)]
    public ChangeImpact DeepChain_32() => _deep32.ApplyNext();

    [Benchmark]
    public ChangeImpact DeepChain_128() => _deep128.ApplyNext();

    [Benchmark]
    public ChangeImpact DiamondLayers_32() => _diamond32.ApplyNext();

    [Benchmark]
    public ChangeImpact SparseDag_128() => _sparse128.ApplyNext();

    [Benchmark]
    public PreparedImpactPlan PreparePlanDeepDag() =>
        _planning128.Runtime.PlanDetailed(_planning128.Prepared);

    private static MutationScenario CreateDeepChain(int depth)
    {
        var (model, sources, final) = BuildDeepChainModel(depth);
        var source = new Source();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(final, source);
        return new MutationScenario(runtime, sources, source);
    }

    private static (ConsistencyModelBuilder Model, ObjectSet<Source> Sources, Derived<Source, int> Final)
        BuildDeepChainModel(int depth)
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var current = model.Derived(sources).Select(source => source.Value);
        for (var index = 1; index < depth; index++)
        {
            var upstream = current;
            current = model.Derived(sources).From(upstream).Select((_, value) => value + 1);
        }
        return (model, sources, current);
    }

    private static MutationScenario CreateDiamond(int layers)
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var current = model.Derived(sources).Select(source => source.Value);
        for (var index = 0; index < layers; index++)
        {
            var upstream = current;
            var left = model.Derived(sources).From(upstream).Select((_, value) => value + 1);
            var right = model.Derived(sources).From(upstream).Select((_, value) => value + 2);
            current = model.Derived(sources).From(left).From(right)
                .Select((_, first, second) => first + second);
        }
        var source = new Source();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(current, source);
        return new MutationScenario(runtime, sources, source);
    }

    private static MutationScenario CreateSparseDag(int nodeCount)
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var nodes = new List<Derived<Source, int>>
        {
            model.Derived(sources).Select(source => source.Value)
        };
        for (var index = 1; index < nodeCount; index++)
        {
            var upstream = nodes[(index - 1) / 2];
            nodes.Add(model.Derived(sources).From(upstream).Select((_, value) => value + 1));
        }
        var source = new Source();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        foreach (var leaf in nodes.Skip(nodeCount / 2))
            _ = runtime.Evaluate(leaf, source);
        return new MutationScenario(runtime, sources, source);
    }

    private static PlanningScenario CreatePlanningScenario(int depth)
    {
        var (model, sources, final) = BuildDeepChainModel(depth);
        var source = new Source();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(final, source);
        source.Value = 1;
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(sources, source, value => value.Value, 0, 1)));
        return new PlanningScenario(runtime, prepared);
    }

    private sealed class MutationScenario(
        ConsistencyRuntime runtime,
        ObjectSet<Source> sources,
        Source source)
    {
        private int _value;

        public ChangeImpact ApplyNext()
        {
            var previous = _value;
            source.Value = ++_value;
            return runtime.Apply(Change.Property(
                sources, source, value => value.Value, previous, _value));
        }
    }

    private sealed record PlanningScenario(ConsistencyRuntime Runtime, PreparedMutation Prepared);

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
    }
}
