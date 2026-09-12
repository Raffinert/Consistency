namespace Raffinert.Relations.Tests;

public sealed class DerivedStateTests
{
    [Fact]
    public void Derived_state_distinguishes_dirty_from_invalid_and_recomputes_lazily()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code && item.Enabled);
        var count = model.Derived(sources)
            .Using(relation)
            .Compute((source, matches) => matches.Count);
        var atMostOne = model.Invariant(sources)
            .Using(count)
            .Must((source, value) => value <= 1);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = true };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B", Enabled = true };
        runtime.Add(sources, source);
        runtime.Add(items, first);
        runtime.Add(items, second);

        Assert.Equal(1, runtime.Get(count, source));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, source));
        Assert.True(runtime.Evaluate(atMostOne, source));

        second.Code = "A";
        runtime.Apply(Change.Property(items, second, x => x.Code, "B", "A"));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Invalid, runtime.GetState(atMostOne, source));
        Assert.Equal(2, runtime.Get(count, source));
        Assert.False(runtime.Evaluate(atMostOne, source));

        second.Enabled = false;
        runtime.Apply(Change.Property(items, second, x => x.Enabled, true, false));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(atMostOne, source));
        Assert.Equal(1, runtime.Get(count, source));
    }

    [Fact]
    public void Adding_and_removing_relation_items_invalidates_cached_derived_state()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Get(count, source));

        runtime.Add(items, item);
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, source));
        Assert.Equal(1, runtime.Get(count, source));

        runtime.Remove(items, item);
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(count, source));
        Assert.Equal(0, runtime.Get(count, source));
    }

    [Fact]
    public void Direct_source_change_marks_a_cached_derived_computation_dirty()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var score = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Count + (source.Enabled ? 1 : 0));
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = false };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Get(score, source));

        source.Enabled = true;
        runtime.Apply(Change.Property(sources, source, x => x.Enabled, false, true));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(score, source));
        Assert.Equal(1, runtime.Get(score, source));
    }

    [Fact]
    public void Immediate_invariant_policy_recomputes_after_relation_impact()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        var invariant = model.Invariant(sources).Using(count).Must((source, value) => value <= 1)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, first);
        runtime.Add(items, second);
        Assert.True(runtime.Evaluate(invariant, source));

        second.Code = "A";
        runtime.Apply(Change.Property(items, second, x => x.Code, "B", "A"));

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Violated, runtime.GetState(invariant, source));
    }

    [Fact]
    public void Repair_policy_schedules_affected_source_objects()
    {
        var scheduled = new List<CodeHolder>();
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((source, matches) => matches.Count);
        model.Invariant(sources).Using(count).Must((source, value) => value <= 1)
            .ScheduleRepairWith(scheduled.Add);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        scheduled.Clear();

        item.Code = "A";
        runtime.Apply(Change.Property(items, item, x => x.Code, "B", "A"));

        Assert.Equal([source], scheduled);
    }
}
