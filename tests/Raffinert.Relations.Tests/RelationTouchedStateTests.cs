namespace Raffinert.Relations.Tests;

public sealed class RelationTouchedStateTests
{
    [Fact]
    public void Preview_and_plan_commit_restore_and_install_only_touched_relation_state()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        var touchedSource = new Source { Code = "A" };
        var unrelatedSource = new Source { Code = "U" };
        var touched = new Item { Code = "A" };
        var sharedBucket = new Item { Code = "A" };
        var unrelated = new Item { Code = "U" };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [touchedSource, unrelatedSource]);
            seed.Add(items, [touched, sharedBucket, unrelated]);
        });
        touched.Code = "B";
        var prepared = runtime.Prepare(MutationSet.Create(Change.Property(
            items, touched, item => item.Code, "A", "B")));

        _ = runtime.PreviewDetailed(prepared);

        Assert.True(runtime.HasMaterializedPair(relation, touchedSource, touched));
        Assert.True(runtime.HasMaterializedPair(relation, touchedSource, sharedBucket));
        Assert.True(runtime.HasMaterializedPair(relation, unrelatedSource, unrelated));

        var plan = runtime.PlanDetailed(prepared);
        Assert.True(runtime.HasMaterializedPair(relation, touchedSource, touched));
        runtime.Commit(plan);

        Assert.False(runtime.HasMaterializedPair(relation, touchedSource, touched));
        Assert.True(runtime.HasMaterializedPair(relation, touchedSource, sharedBucket));
        Assert.True(runtime.HasMaterializedPair(relation, unrelatedSource, unrelated));
    }

    [Fact]
    public void Relation_failure_restores_both_directions_of_materialized_pairs()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => MatchOrThrow(source, item))
            .AllowIncompleteDependencies();
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        var source = new Source { Code = "A" };
        var existing = new Item { Code = "A" };
        var failing = new Item { Code = "A", Fail = true };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [source]);
            seed.Add(items, [existing]);
        });

        var prepared = runtime.Prepare(MutationSet.Create(Change.Add(items, failing)));
        Assert.Throws<InvalidOperationException>(() => runtime.PreviewDetailed(prepared));

        Assert.True(runtime.HasMaterializedPair(relation, source, existing));
        Assert.True(runtime.HasReverseMaterializedPair(relation, source, existing));
        Assert.False(runtime.HasMaterializedPair(relation, source, failing));
        Assert.False(runtime.HasReverseMaterializedPair(relation, source, failing));
    }

    [Fact]
    public void Relation_patch_size_is_independent_of_unrelated_population()
    {
        var small = CreatePopulation(10_000);
        var large = CreatePopulation(100_000);

        var smallCount = small.Runtime.CaptureRelationPatchEntryCount(
            small.Relation, Array.Empty<Source>(), [small.Touched]);
        var largeCount = large.Runtime.CaptureRelationPatchEntryCount(
            large.Relation, Array.Empty<Source>(), [large.Touched]);

        Assert.True(largeCount < smallCount * 2,
            $"Touched patch grew from {smallCount} to {largeCount} entries.");
    }

    private static Population CreatePopulation(int count)
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        var source = new Source { Code = "touched" };
        var touched = new Item { Code = "touched" };
        var unrelated = Enumerable.Range(0, count)
            .Select(index => new Item { Code = $"unrelated-{index}" })
            .ToArray();
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [source]);
            seed.Add(items, unrelated.Append(touched));
        });
        return new Population(runtime, relation, touched);
    }

    private static bool MatchOrThrow(Source source, Item item) => item.Fail
        ? throw new InvalidOperationException("Injected relation predicate failure.")
        : source.Code == item.Code;

    private sealed record Population(
        RelationRuntime Runtime,
        Relation<Source, Item> Relation,
        Item Touched);

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }

    private sealed class Item
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
        public bool Fail { get; set; }
    }
}
