using BenchmarkDotNet.Attributes;

namespace Raffinert.Relations.Benchmarks;

/// <summary>
/// Measures the cost of exception-atomic commit snapshots while mutation size remains fixed.
/// The comparison path is internal to the benchmark assembly and is not a supported runtime mode.
/// </summary>
[MemoryDiagnoser]
public class CommitSafetyBenchmarks
{
    private Scenario _safe = null!;
    private Scenario _withoutSnapshot = null!;

    [Params(1_000, 10_000, 100_000)]
    public int RegisteredObjects { get; set; }

    [Params(1, 100, 1_000)]
    public int MaterializedPairs { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (MaterializedPairs > RegisteredObjects)
            throw new InvalidOperationException("MaterializedPairs cannot exceed RegisteredObjects.");

        _safe = CreateScenario(disableSnapshots: false);
        _withoutSnapshot = CreateScenario(disableSnapshots: true);
    }

    [Benchmark(Baseline = true)]
    public ChangeImpact CommitWithRollbackSnapshot() => _safe.ApplySinglePropertyMutation();

    [Benchmark]
    public ChangeImpact CommitWithoutSnapshotBenchmarkOnly() =>
        _withoutSnapshot.ApplySinglePropertyMutation();

    private Scenario CreateScenario(bool disableSnapshots)
    {
        var builder = new RelationModelBuilder();
        var sources = builder.Objects<Source>().Key(source => source.Id);
        var items = builder.Objects<Item>().Key(item => item.Id);
        var flags = builder.Objects<Flag>().Key(flag => flag.Id);
        var relation = builder.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        builder.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);

        var runtime = builder.Build().CreateRuntime();
        if (disableSnapshots)
            runtime.DisableRollbackSnapshotsForBenchmarking();

        var source = new Source { Id = 1, Code = "match" };
        var flag = new Flag { Id = 1 };
        var retainedItems = Enumerable.Range(0, RegisteredObjects)
            .Select(index => new Item
            {
                Id = index,
                Code = index < MaterializedPairs ? "match" : $"other-{index}"
            })
            .ToArray();

        runtime.Apply(MutationSet.Create(
            new[] { Change.Add(sources, source) }
                .Concat(retainedItems.Select(item => Change.Add(items, item)))
                .Append(Change.Add(flags, flag))
                .ToArray()));

        return new Scenario(runtime, flag);
    }

    private sealed class Scenario(RelationRuntime runtime, Flag flag)
    {
        public ChangeImpact ApplySinglePropertyMutation()
        {
            var oldEnabled = flag.Enabled;
            flag.Enabled = !oldEnabled;
            return runtime.Apply(Change.Property(flag, value => value.Enabled, oldEnabled, flag.Enabled));
        }
    }

    private sealed class Source
    {
        public int Id { get; init; }
        public string Code { get; set; } = "";
    }

    private sealed class Item
    {
        public int Id { get; init; }
        public string Code { get; init; } = "";
    }

    private sealed class Flag
    {
        public int Id { get; init; }
        public bool Enabled { get; set; }
    }
}
