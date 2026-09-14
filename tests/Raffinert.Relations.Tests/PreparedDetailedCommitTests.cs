namespace Raffinert.Relations.Tests;

public sealed class PreparedDetailedCommitTests
{
    [Theory]
    [InlineData(RuntimeImpactDetailLevel.Summary)]
    [InlineData(RuntimeImpactDetailLevel.Causal)]
    public void Prepared_mutation_can_be_committed_with_details_before_dispatch(
        RuntimeImpactDetailLevel detailLevel)
    {
        var callbacks = new List<int>();
        var scenario = CreateScenario(callbacks);
        scenario.Source.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Value, 1, 2)));

        var result = scenario.Runtime.CommitDetailed(prepared, detailLevel);

        Assert.Equal(detailLevel, result.DetailLevel);
        Assert.Empty(callbacks);
        scenario.Runtime.Dispatch(prepared);
        Assert.Equal([2], callbacks);
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.Commit(prepared));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.CommitDetailed(prepared));
    }

    [Fact]
    public void Basic_commit_prevents_later_detailed_commit()
    {
        var scenario = CreateScenario([]);
        scenario.Source.Value = 2;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Set, scenario.Source, source => source.Value, 1, 2)));
        scenario.Runtime.Commit(prepared);

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.CommitDetailed(prepared));
    }

    [Fact]
    public void Normalized_provenance_preserves_collection_kind_item_and_property_net_transition()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var source = new Source();
        runtime.Add(set, source);
        var item = new object();
        source.Items.Add(item);
        source.Value = 3;
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.CollectionAdd(set, source, value => value.Items, item),
            Change.Property(set, source, value => value.Value, 1, 2),
            Change.Property(set, source, value => value.Value, 2, 3)));

        var collection = prepared.Provenance.Single(value => value.Kind == MutationOriginKind.CollectionChanged);
        Assert.Equal(CollectionChangeKind.Add, collection.CollectionKind);
        Assert.Same(item, collection.CollectionItem);
        var property = prepared.Provenance.Single(value => value.Kind == MutationOriginKind.SourceMemberChanged);
        Assert.Equal(1, property.OldValue);
        Assert.Equal(3, property.NewValue);
    }

    [Fact]
    public void Null_to_null_property_signal_is_not_inferred_as_collection()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var source = new Source();
        runtime.Add(set, source);

        var result = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            set, source, value => value.Note, null, null)), RuntimeImpactDetailLevel.Causal).Result;

        Assert.Equal(MutationOriginKind.SourceMemberChanged, Assert.Single(result.MutationOrigins).Kind);
    }

    [Theory]
    [InlineData(CollectionChangeKind.Remove)]
    [InlineData(CollectionChangeKind.Reset)]
    public void Normalized_provenance_retains_remove_and_reset(CollectionChangeKind kind)
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var runtime = model.Build().CreateRuntime();
        var item = new object();
        var source = new Source();
        source.Items.Add(item);
        runtime.Add(set, source);
        if (kind == CollectionChangeKind.Remove)
            source.Items.Remove(item);
        var mutation = kind == CollectionChangeKind.Remove
            ? Change.CollectionRemove(set, source, value => value.Items, item)
            : Change.CollectionReset(set, source, value => value.Items);

        var prepared = runtime.Prepare(MutationSet.Create(mutation));

        var provenance = Assert.Single(prepared.Provenance);
        Assert.Equal(kind, provenance.CollectionKind);
        Assert.Equal(kind == CollectionChangeKind.Remove ? item : null, provenance.CollectionItem);
    }

    private static Scenario CreateScenario(List<int> callbacks)
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(set).Compute(source => source.Value);
        model.Invariant(set).Using(value).Must((_, current) => current <= 1)
            .ScheduleRepairWith(source => callbacks.Add(source.Value));
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Value = 1 };
        runtime.Add(set, source);
        Assert.Equal(1, runtime.Get(value, source));
        return new Scenario(runtime, set, source);
    }

    private sealed record Scenario(RelationRuntime Runtime, ObjectSet<Source> Set, Source Source);

    private sealed class Source
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public int Value { get; set; } = 1;
        public string? Note { get; set; }
        public List<object> Items { get; } = [];
    }
}
