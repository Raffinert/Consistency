namespace Raffinert.Relations.Tests;

public sealed class ConservativePropagationTests
{
    [Fact]
    public void Right_key_move_invalidates_a_safe_superset_without_retaining_pairs()
    {
        var scenario = Create();
        var first = new Entry { Id = Guid.NewGuid(), Code = "A" };
        var second = new Entry { Id = Guid.NewGuid(), Code = "B" };
        var unrelated = new Entry { Id = Guid.NewGuid(), Code = "C" };
        var item = new Entry { Id = Guid.NewGuid(), Code = "A" };
        scenario.Runtime.Add(scenario.Sources, first);
        scenario.Runtime.Add(scenario.Sources, second);
        scenario.Runtime.Add(scenario.Sources, unrelated);
        scenario.Runtime.Add(scenario.Items, item);
        Assert.Equal(1, scenario.Runtime.Get(scenario.Count, first));
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, second));
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, unrelated));
        Assert.Equal(0, scenario.Runtime.MaterializedRelationPairCount);

        item.Code = "B";
        scenario.Runtime.Apply(Change.Property(scenario.Items, item, value => value.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, first));
        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, second));
        Assert.Equal(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.Count, unrelated));
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, first));
        Assert.Equal(1, scenario.Runtime.Get(scenario.Count, second));
        Assert.Equal(0, scenario.Runtime.MaterializedRelationPairCount);
    }

    [Fact]
    public void Right_add_and_remove_use_the_matching_candidate_bucket()
    {
        var scenario = Create();
        var matching = new Entry { Id = Guid.NewGuid(), Code = "A" };
        var unrelated = new Entry { Id = Guid.NewGuid(), Code = "B" };
        scenario.Runtime.Add(scenario.Sources, matching);
        scenario.Runtime.Add(scenario.Sources, unrelated);
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, matching));
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, unrelated));
        var item = new Entry { Id = Guid.NewGuid(), Code = "A" };

        var added = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Add(scenario.Items, item))).Result;

        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, matching));
        Assert.Equal(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.Count, unrelated));
        Assert.Equal([matching], Assert.Single(added.RelationImpacts).AffectedSources);
        Assert.Equal(1, scenario.Runtime.Get(scenario.Count, matching));

        var removed = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Remove(scenario.Items, item))).Result;

        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, matching));
        Assert.Equal(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.Count, unrelated));
        Assert.Equal([matching], Assert.Single(removed.RelationImpacts).AffectedSources);
        Assert.Equal(0, scenario.Runtime.MaterializedRelationPairCount);
    }

    [Fact]
    public void Forced_scan_conservative_plan_falls_back_to_all_sources()
    {
        var scenario = Create(forceScan: true);
        var matching = new Entry { Id = Guid.NewGuid(), Code = "A" };
        var unrelated = new Entry { Id = Guid.NewGuid(), Code = "B" };
        scenario.Runtime.Add(scenario.Sources, matching);
        scenario.Runtime.Add(scenario.Sources, unrelated);
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, matching));
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, unrelated));

        scenario.Runtime.Add(scenario.Items, new Entry { Id = Guid.NewGuid(), Code = "A" });

        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, matching));
        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, unrelated));
    }

    [Fact]
    public void Right_add_remove_and_batch_converge_to_predicate_scan_results()
    {
        var scenario = Create();
        var sources = new[]
        {
            new Entry { Id = Guid.NewGuid(), Code = "A" },
            new Entry { Id = Guid.NewGuid(), Code = "B" },
            new Entry { Id = Guid.NewGuid(), Code = "C" }
        };
        foreach (var source in sources)
            scenario.Runtime.Add(scenario.Sources, source);
        var first = new Entry { Id = Guid.NewGuid(), Code = "A" };
        var second = new Entry { Id = Guid.NewGuid(), Code = "B" };
        scenario.Runtime.Apply(MutationSet.Create(
            Change.Add(scenario.Items, first),
            Change.Add(scenario.Items, second)));

        Assert.Equal([1, 1, 0], sources.Select(source => scenario.Runtime.Get(scenario.Count, source)));

        scenario.Runtime.Remove(scenario.Items, first);
        Assert.Equal([0, 1, 0], sources.Select(source => scenario.Runtime.Get(scenario.Count, source)));
        Assert.Equal(0, scenario.Runtime.MaterializedRelationPairCount);
    }

    private static Scenario Create(bool forceScan = false)
    {
        var model = new RelationModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        var sources = model.Objects<Entry>().Key(value => value.Id);
        var items = model.Objects<Entry>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Conservatively()
            .Compute((_, matches) => matches.Count);
        var compiled = model.Build();
        var diagnostics = Assert.Single(compiled.Diagnostics.Relations);
        Assert.Equal(RelationPropagationPlanKind.ConservativeInvalidation, diagnostics.PropagationPlan);
        Assert.False(diagnostics.RetainsPairMembership);
        Assert.Contains("Explicit", diagnostics.PropagationReason, StringComparison.Ordinal);
        return new Scenario(compiled.CreateRuntime(), sources, items, count);
    }

    private sealed record Scenario(
        RelationRuntime Runtime,
        ObjectSet<Entry> Sources,
        ObjectSet<Entry> Items,
        Derived<Entry, int> Count);

    private sealed class Entry
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
    }
}
