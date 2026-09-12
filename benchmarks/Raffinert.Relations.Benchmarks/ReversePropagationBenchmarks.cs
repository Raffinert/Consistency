using BenchmarkDotNet.Attributes;

namespace Raffinert.Relations.Benchmarks;

[MemoryDiagnoser]
public class ReversePropagationBenchmarks
{
    private Scenario _hash = null!;
    private Scenario _scan = null!;

    [Params(10_000, 100_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _hash = CreateScenario(forceScan: false);
        _scan = CreateScenario(forceScan: true);
    }

    [Benchmark(Baseline = true)]
    public ChangeImpact ReverseHashMutation() => _hash.ToggleCode();

    [Benchmark]
    public ChangeImpact ReverseScanMutation() => _scan.ToggleCode();

    private Scenario CreateScenario(bool forceScan)
    {
        var model = new RelationModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        var sources = model.Objects<BenchItem>().Key(item => item.Id);
        var items = model.Objects<BenchItem>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        runtime.Add(sources, new BenchItem { Id = Guid.NewGuid(), Code = "A" });
        runtime.Add(sources, new BenchItem { Id = Guid.NewGuid(), Code = "B" });
        for (var index = 2; index < Size; index++)
            runtime.Add(sources, new BenchItem { Id = Guid.NewGuid(), Code = $"U-{index}" });
        var item = new BenchItem { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(items, item);
        return new Scenario(runtime, items, item);
    }

    private sealed class Scenario(
        RelationRuntime runtime,
        ObjectSet<BenchItem> items,
        BenchItem item)
    {
        public ChangeImpact ToggleCode()
        {
            var oldCode = item.Code;
            item.Code = oldCode == "A" ? "B" : "A";
            return runtime.Apply(Change.Property(items, item, value => value.Code, oldCode, item.Code));
        }
    }
}
