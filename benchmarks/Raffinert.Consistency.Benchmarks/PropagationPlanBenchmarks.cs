using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

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
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Entry>().Key(entry => entry.Id);
        var items = model.Objects<Entry>().Key(entry => entry.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var usingRelation = model.Derived(sources).Using(relation);
        if (conservative)
            usingRelation.PreferConservativePropagation();
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

    private sealed class Scenario(ConsistencyRuntime runtime, ObjectSet<Entry> items,
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

[MemoryDiagnoser]
public class SelectivePropagationPlanBenchmarks
{
    private SelectiveScenario _exact = null!;
    private SelectiveScenario _conservative = null!;

    [Params(10_000)]
    public int SourceCount { get; set; }

    [Params(100, 1_000)]
    public int DistinctKeyCount { get; set; }

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

    private SelectiveScenario Create(bool conservative)
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<SelectiveEntry>().Key(entry => entry.Id);
        var items = model.Objects<SelectiveEntry>().Key(entry => entry.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var builder = model.Derived(sources).Using(relation);
        if (conservative)
            builder.PreferConservativePropagation();
        var count = builder.Compute((_, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var retainedSources = Enumerable.Range(0, SourceCount)
            .Select(index => new SelectiveEntry { Id = Guid.NewGuid(), Code = Key(index % DistinctKeyCount) })
            .ToArray();
        var retainedItems = Enumerable.Range(0, DistinctKeyCount)
            .Select(index => new SelectiveEntry { Id = Guid.NewGuid(), Code = Key(index) })
            .ToArray();
        runtime.Apply(MutationSet.Create(retainedSources.Select(source => Change.Add(sources, source))
            .Concat(retainedItems.Select(item => Change.Add(items, item))).ToArray()));
        foreach (var source in retainedSources)
            runtime.Get(count, source);
        runtime.ResetDiagnostics();
        return new SelectiveScenario(runtime, items, count, retainedSources[0], retainedItems[0]);
    }

    private static string Key(int value) => $"key-{value}";

    private sealed class SelectiveScenario(ConsistencyRuntime runtime, ObjectSet<SelectiveEntry> items,
        Derived<SelectiveEntry, int> count, SelectiveEntry readSource, SelectiveEntry changedItem)
    {
        public ChangeImpact Mutate(bool readAfterWrite)
        {
            var oldCode = changedItem.Code;
            changedItem.Code = oldCode == "key-0" ? "key-1" : "key-0";
            runtime.ResetDiagnostics();
            var impact = runtime.Apply(Change.Property(items, changedItem, entry => entry.Code,
                oldCode, changedItem.Code));
            if (readAfterWrite)
                _ = runtime.Get(count, readSource);
            return impact;
        }
    }

    private sealed class SelectiveEntry
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
    }
}
