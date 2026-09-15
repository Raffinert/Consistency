using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class RangePlanningBenchmarks
{
    private ConsistencyRuntime _hashRuntime = null!;
    private ConsistencyRuntime _scanRuntime = null!;
    private Relation<RangeSource, RangeRule> _hashRelation = null!;
    private Relation<RangeSource, RangeRule> _scanRelation = null!;
    private RangeSource _hashSource = null!;
    private RangeSource _scanSource = null!;

    [Params(10_000, 100_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (_hashRuntime, _hashRelation, _hashSource) = CreateScenario(forceScan: false);
        (_scanRuntime, _scanRelation, _scanSource) = CreateScenario(forceScan: true);
    }

    [Benchmark(Baseline = true)]
    public int EqualityPrefixWithRangeResidual() =>
        _hashRuntime.Related(_hashRelation, _hashSource).Count;

    [Benchmark]
    public int FullScanWithRangeResidual() =>
        _scanRuntime.Related(_scanRelation, _scanSource).Count;

    private (ConsistencyRuntime Runtime, Relation<RangeSource, RangeRule> Relation, RangeSource Source)
        CreateScenario(bool forceScan)
    {
        var model = new ConsistencyModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        var sources = model.Objects<RangeSource>().Key(source => source.Id);
        var rules = model.Objects<RangeRule>().Key(rule => rule.Id);
        var relation = model.Relation(sources, rules).Where((source, rule) =>
            source.SupplierId == rule.SupplierId &&
            rule.ValidFrom <= source.Date &&
            source.Date < rule.ValidTo);
        var runtime = model.Build().CreateRuntime();
        var source = new RangeSource
        {
            Id = Guid.NewGuid(),
            SupplierId = 42,
            Date = new DateTime(2026, 6, 1)
        };
        runtime.Add(sources, source);
        for (var index = 0; index < Size; index++)
        {
            var start = new DateTime(2026, 1, 1).AddDays(index % 365);
            runtime.Add(rules, new RangeRule
            {
                Id = Guid.NewGuid(),
                SupplierId = index % 100,
                ValidFrom = start,
                ValidTo = start.AddDays(30)
            });
        }
        return (runtime, relation, source);
    }

    private sealed class RangeSource
    {
        public Guid Id { get; init; }
        public int SupplierId { get; init; }
        public DateTime Date { get; init; }
    }

    private sealed class RangeRule
    {
        public Guid Id { get; init; }
        public int SupplierId { get; init; }
        public DateTime ValidFrom { get; init; }
        public DateTime ValidTo { get; init; }
    }
}
