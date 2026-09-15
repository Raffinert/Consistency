namespace Raffinert.Consistency.Tests;

public sealed class RelationImpactTests
{
    [Fact]
    public void Mutation_batch_classifies_final_membership_impact_once()
    {
        var policy = new RecordingImpactPolicy();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime(policy);
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };

        runtime.Apply(MutationSet.Create(
            Change.Add(sources, source),
            Change.Add(items, item)));

        var impact = Assert.Single(policy.Impacts);
        Assert.Equal([(source, item)], impact.RelationImpact.AddedPairs
            .Select(pair => (pair.Left, pair.Right)));
    }

    [Fact]
    public void Relation_impact_unifies_delta_semantic_and_access_roots()
    {
        var policy = new RecordingImpactPolicy();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime(policy);
        var losing = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var gaining = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, losing);
        runtime.Add(sources, gaining);
        runtime.Add(items, item);
        policy.Impacts.Clear();

        item.Code = "B";
        runtime.Apply(Change.Property(items, item, x => x.Code, "A", "B"));

        var dependencyImpact = Assert.Single(policy.Impacts);
        var impact = dependencyImpact.RelationImpact;
        Assert.Same(relation.Definition, impact.Relation);
        var added = Assert.Single(impact.AddedPairs);
        Assert.Same(gaining, added.Left);
        Assert.Same(item, added.Right);
        var removed = Assert.Single(impact.RemovedPairs);
        Assert.Same(losing, removed.Left);
        Assert.Same(item, removed.Right);
        Assert.Equal(2, impact.AffectedLefts.Count);
        Assert.Contains(losing, impact.AffectedLefts);
        Assert.Contains(gaining, impact.AffectedLefts);
        Assert.Equal([item], impact.AffectedRights);
        Assert.Equal([item], impact.SemanticRights);
        Assert.Empty(impact.SemanticLefts);
        Assert.Equal([item], impact.ReindexedRights);
        Assert.Empty(impact.ReindexedLefts);
        Assert.True(impact.HasMembershipChanges);
    }

    private sealed class RecordingImpactPolicy : IDependencyImpactPolicy
    {
        public List<RelationMembershipDependencyImpact> Impacts { get; } = [];

        public DependencyImpactKind Classify(RelationMembershipDependencyImpact impact)
        {
            Impacts.Add(impact);
            return DependencyImpactKind.Dirty;
        }
    }
}
