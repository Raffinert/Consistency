using BenchmarkDotNet.Attributes;

namespace Raffinert.Relations.Benchmarks;

[MemoryDiagnoser]
public class PropagationPlanBenchmarks
{
    private Scenario _exact = null!;
    private Scenario _conservative = null!;

    [Params(100, 1_000)]
    public int SourceCount { get; set; }

    [Params(10, 100)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _exact = Create(conservative: false);
        _conservative = Create(conservative: true);
    }

    [Benchmark(Baseline = true)]
    public ChangeImpact ExactMutation() => _exact.Mutate(readAfterWrite: false);

    [Benchmark]
    public ChangeImpact ConservativeMutation() => _conservative.Mutate(readAfterWrite: false);

    [Benchmark]
    public ChangeImpact ExactMutationAndRead() => _exact.Mutate(readAfterWrite: true);

    [Benchmark]
    public ChangeImpact ConservativeMutationAndRead() => _conservative.Mutate(readAfterWrite: true);

    private Scenario Create(bool conservative)
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Entry>().Key(entry => entry.Id);
        var items = model.Objects<Entry>().Key(entry => entry.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var usingRelation = model.Derived(sources).Using(relation);
        if (conservative)
            usingRelation.Conservatively();
        var count = usingRelation.Compute((_, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var retainedSources = Enumerable.Range(0, SourceCount)
            .Select(index => new Entry { Id = Guid.NewGuid(), Code = "dense" }).ToArray();
        var retainedItems = Enumerable.Range(0, ItemCount)
            .Select(index => new Entry { Id = Guid.NewGuid(), Code = "dense" }).ToArray();
        runtime.Apply(MutationSet.Create(retainedSources.Select(source => Change.Add(sources, source))
            .Concat(retainedItems.Select(item => Change.Add(items, item))).ToArray()));
        foreach (var source in retainedSources)
            runtime.Get(count, source);
        return new Scenario(runtime, items, count, retainedSources[0], retainedItems[0]);
    }

    private sealed class Scenario(RelationRuntime runtime, ObjectSet<Entry> items,
        Derived<Entry, int> count, Entry readSource, Entry changedItem)
    {
        public ChangeImpact Mutate(bool readAfterWrite)
        {
            var oldCode = changedItem.Code;
            changedItem.Code = oldCode == "dense" ? "other" : "dense";
            var impact = runtime.Apply(Change.Property(items, changedItem, entry => entry.Code, oldCode, changedItem.Code));
            if (readAfterWrite)
                _ = runtime.Get(count, readSource);
            return impact;
        }
    }

    private sealed class Entry
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
    }
}
