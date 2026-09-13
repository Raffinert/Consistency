namespace Raffinert.Relations.Tests;

public sealed class ConservativePropagationTests
{
    [Fact]
    public void Right_key_move_invalidates_a_safe_superset_without_retaining_pairs()
    {
        var scenario = Create();
        var first = new Entry { Id = Guid.NewGuid(), Code = "A" };
        var second = new Entry { Id = Guid.NewGuid(), Code = "B" };
        var item = new Entry { Id = Guid.NewGuid(), Code = "A" };
        scenario.Runtime.Add(scenario.Sources, first);
        scenario.Runtime.Add(scenario.Sources, second);
        scenario.Runtime.Add(scenario.Items, item);
        Assert.Equal(1, scenario.Runtime.Get(scenario.Count, first));
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, second));
        Assert.Equal(0, scenario.Runtime.MaterializedRelationPairCount);

        item.Code = "B";
        scenario.Runtime.Apply(Change.Property(scenario.Items, item, value => value.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, first));
        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Count, second));
        Assert.Equal(0, scenario.Runtime.Get(scenario.Count, first));
        Assert.Equal(1, scenario.Runtime.Get(scenario.Count, second));
        Assert.Equal(0, scenario.Runtime.MaterializedRelationPairCount);
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

    private static Scenario Create()
    {
        var model = new RelationModelBuilder();
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
