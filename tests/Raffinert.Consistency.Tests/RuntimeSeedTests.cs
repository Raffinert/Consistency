namespace Raffinert.Consistency.Tests;

public sealed class RuntimeSeedTests
{
    [Fact]
    public void Seed_builds_relations_lazily_without_version_or_policy_wave()
    {
        var callbacks = new List<Guid>();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Select((_, matches) => matches.Count);
        var invariant = model.Invariant(sources).From(count).Must((_, value) => value > 0)
            .RepairWhenViolated();
        var compiled = model.Build();
        var source = new Source { Code = "A" };
        var item = new Item { Code = "A" };

        var runtime = compiled.CreateRuntime(seed =>
        {
            seed.Add(items, [item]);
            seed.Add(sources, [source]);
        });

        Assert.Equal(0, runtime.Version);
        Assert.Empty(callbacks);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Unknown, runtime.GetState(invariant, source));
        Assert.Single(runtime.Related(relation, source));
        Assert.Equal(1, runtime.Evaluate(count, source));
        Assert.True(runtime.Evaluate(invariant, source));
    }

    [Fact]
    public void Seed_supports_empty_and_same_clr_type_sets_and_normal_mutations()
    {
        var model = new ConsistencyModelBuilder();
        var first = model.Objects<Source>().Key(source => source.Id);
        var second = model.Objects<Source>().Key(source => source.Id);
        var compiled = model.Build();
        var one = new Source();
        var two = new Source();

        var empty = compiled.CreateRuntime(_ => { });
        Assert.Equal(0, empty.Version);
        var runtime = compiled.CreateRuntime(seed => seed.Add(first, [one]).Add(second, [two]));
        Assert.True(runtime.Remove(first, one));
        Assert.True(runtime.Remove(second, two));
        Assert.Equal(2, runtime.Version);
    }

    [Fact]
    public void Duplicate_key_or_instance_rejects_seed_creation()
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var compiled = model.Build();
        var id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => compiled.CreateRuntime(seed => seed.Add(set,
            [new Source { Id = id }, new Source { Id = id }])));
        var source = new Source();
        Assert.Throws<InvalidOperationException>(() => compiled.CreateRuntime(seed =>
            seed.Add(set, [source]).Add(set, [source])));
    }

    [Fact]
    public void Conservative_seed_retains_zero_pairs_but_queries_authoritative_predicate()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var items = model.Objects<Item>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).From(relation).PreferConservativePropagation()
            .Select((_, matches) => matches.Count);
        var compiled = model.Build();
        var source = new Source { Code = "A" };
        var item = new Item { Code = "A" };

        var runtime = compiled.CreateRuntime(seed => seed.Add(sources, [source]).Add(items, [item]));

        Assert.Equal(0, runtime.MaterializedRelationPairCount);
        Assert.Single(runtime.Related(relation, source));
    }

    [Fact]
    public void Baseline_revision_changes_without_advancing_domain_version()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var source = new Source();

        Assert.Equal(0, runtime.BaselineRevision);
        Assert.Equal(0, runtime.Version);

        runtime.AdmitBaseline(sources.Definition, source);

        Assert.Equal(1, runtime.BaselineRevision);
        Assert.Equal(0, runtime.Version);
        runtime.AdmitBaseline(sources.Definition, source);
        Assert.Equal(1, runtime.BaselineRevision);

        runtime.RefreshBaseline(sources.Definition, source);

        Assert.Equal(2, runtime.BaselineRevision);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Failed_baseline_refresh_restores_indexes_and_does_not_publish_revision()
    {
        var model = new ConsistencyModelBuilder();
        var items = model.Objects<ProjectedItem>().Key(item => item.Id);
        var sources = model.Objects<ProjectedSource>().Key(source => source.Id);
        var value = model.Derived(items).Select(item => item.Value);
        model.Derived(sources).From(source => source.Item, value).Select((_, current) => current);
        var runtime = model.Build().CreateRuntime();
        var original = new ProjectedItem { Value = 1 };
        var replacement = new ProjectedItem { Value = 2 };
        var source = new ProjectedSource { Item = original };
        var member = typeof(ProjectedSource).GetProperty(nameof(ProjectedSource.Item))!;
        runtime.AdmitBaseline(items.Definition, original);
        runtime.AdmitBaseline(sources.Definition, source);
        runtime.ValidateBaseline();
        var revision = runtime.BaselineRevision;

        source.Item = replacement;

        Assert.Throws<InvalidOperationException>(() =>
            runtime.RefreshBaseline(sources.Definition, source));
        Assert.Equal(revision, runtime.BaselineRevision);
        Assert.Contains(source, runtime.GetProjectedDownstreams(member, original));
        Assert.DoesNotContain(source, runtime.GetProjectedDownstreams(member, replacement));
        Assert.Equal(0, runtime.Version);
    }

    private sealed class Source
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }

    private sealed class Item
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }

    private sealed class ProjectedSource
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ProjectedItem Item { get; set; } = null!;
    }

    private sealed class ProjectedItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Value { get; set; }
    }
}
