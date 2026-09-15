namespace Raffinert.Consistency.Tests;

public sealed partial class DerivedStateTests
{
    [Fact]
    public void Relation_membership_changes_default_to_dirty_and_recompute_lazily()
    {
        var model = new ConsistencyModelBuilder();
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

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(atMostOne, source));
        Assert.Equal(2, runtime.Get(count, source));
        Assert.False(runtime.Evaluate(atMostOne, source));

        second.Enabled = false;
        runtime.Apply(Change.Property(items, second, x => x.Enabled, true, false));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(atMostOne, source));
        Assert.Equal(1, runtime.Get(count, source));
    }

    [Fact]
    public void Adding_and_removing_relation_items_dirty_cached_derived_state()
    {
        var model = new ConsistencyModelBuilder();
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
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(1, runtime.Get(count, source));

        runtime.Remove(items, item);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(0, runtime.Get(count, source));
    }

    [Fact]
    public void Item_value_change_dirties_only_sources_currently_containing_the_item()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var matching = Source("A");
        var unrelated = Source("B");
        var item = Item("A", quantity: 2m);
        runtime.Add(sources, matching);
        runtime.Add(sources, unrelated);
        runtime.Add(items, item);
        Assert.Equal(2m, runtime.Get(quantity, matching));
        Assert.Equal(0m, runtime.Get(quantity, unrelated));

        item.Quantity = 3m;
        runtime.Apply(Change.Property(items, item, x => x.Quantity, 2m, 3m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, matching));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, unrelated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Item_join_key_change_dirties_sources_that_lose_and_gain_membership(bool forceScan)
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity),
            forceScan);
        var compiled = model.Build();
        Assert.Contains($"Access plan: {(forceScan ? "Scan" : "HashJoin")}", compiled.DebugView);
        var runtime = compiled.CreateRuntime();
        var losing = Source("A");
        var gaining = Source("B");
        var unrelated = Source("C");
        var item = Item("A", quantity: 2m);
        foreach (var source in new[] { losing, gaining, unrelated })
            runtime.Add(sources, source);
        runtime.Add(items, item);
        foreach (var source in new[] { losing, gaining, unrelated })
            runtime.Get(quantity, source);

        item.Code = "B";
        runtime.Apply(Change.Property(items, item, x => x.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, gaining));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, unrelated));
        Assert.Equal(0m, runtime.Get(quantity, losing));
        Assert.Equal(2m, runtime.Get(quantity, gaining));
    }

    [Fact]
    public void Nested_join_key_change_dirties_sources_that_lose_and_gain_membership()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Details!.Code);
        var quantity = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var losing = Source("A");
        var gaining = Source("B");
        var details = new DerivedItemDetails { Code = "A" };
        var item = Item("unused", quantity: 2m, details: details);
        runtime.Add(sources, losing);
        runtime.Add(sources, gaining);
        runtime.Add(items, item);
        runtime.Get(quantity, losing);
        runtime.Get(quantity, gaining);

        details.Code = "B";
        runtime.Apply(Change.Property(details, x => x.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, gaining));
        Assert.Equal(0m, runtime.Get(quantity, losing));
        Assert.Equal(2m, runtime.Get(quantity, gaining));
    }

    [Fact]
    public void Residual_predicate_change_dirties_only_source_that_loses_membership()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code && item.Enabled);
        var quantity = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var losing = Source("A");
        var unrelated = Source("B");
        var item = Item("A", quantity: 2m);
        item.Enabled = true;
        runtime.Add(sources, losing);
        runtime.Add(sources, unrelated);
        runtime.Add(items, item);
        runtime.Get(quantity, losing);
        runtime.Get(quantity, unrelated);

        item.Enabled = false;
        runtime.Apply(Change.Property(items, item, x => x.Enabled, true, false));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, unrelated));
    }

    [Fact]
    public void Explicit_dependency_policy_can_make_residual_membership_change_invalid()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code && item.Enabled);
        var quantity = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Sum(item => item.Quantity));
        var invariant = model.Invariant(sources).Using(quantity)
            .Must((source, value) => value <= 10m);
        var runtime = model.Build().CreateRuntime(new InvalidMembershipImpactPolicy());
        var source = Source("A");
        var item = Item("A", quantity: 2m);
        item.Enabled = true;
        runtime.Add(sources, source);
        runtime.Add(items, item);
        runtime.Get(quantity, source);
        Assert.True(runtime.Evaluate(invariant, source));

        item.Enabled = false;
        runtime.Apply(Change.Property(items, item, x => x.Enabled, true, false));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(quantity, source));
        Assert.Equal(InvariantEvaluationState.Invalid, runtime.GetState(invariant, source));

        Assert.Equal(0m, runtime.Get(quantity, source));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, source));
        Assert.Equal(InvariantEvaluationState.Invalid, runtime.GetState(invariant, source));
        Assert.True(runtime.Evaluate(invariant, source));
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reverse_plan_limits_right_mutations_to_candidate_lefts(bool forceScan)
    {
        const int unrelatedCount = 10_000;
        var model = new ConsistencyModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            PredicateProbe.Observe() && source.Code == item.Code)
            .AllowIncompleteDependencies();
        var quantity = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Sum(item => item.Quantity));
        var compiled = model.Build();
        Assert.Contains(
            $"Reverse access plan: {(forceScan ? "Scan" : "HashJoin")}",
            compiled.DebugView);
        var runtime = compiled.CreateRuntime();
        var losing = Source("A");
        var gaining = Source("B");
        runtime.Add(sources, losing);
        runtime.Add(sources, gaining);
        for (var index = 0; index < unrelatedCount; index++)
            runtime.Add(sources, Source($"U-{index}"));
        runtime.Get(quantity, losing);
        runtime.Get(quantity, gaining);
        var item = Item("A", quantity: 1m);

        PredicateProbe.Reset();
        runtime.Add(items, item);
        Assert.Equal(forceScan ? unrelatedCount + 2 : 1, PredicateProbe.Evaluations);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, gaining));
        runtime.Get(quantity, losing);

        PredicateProbe.Reset();
        item.Code = "B";
        runtime.Apply(Change.Property(items, item, x => x.Code, "A", "B"));
        Assert.Equal(forceScan ? unrelatedCount + 2 : 1, PredicateProbe.Evaluations);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, gaining));
        runtime.Get(quantity, gaining);

        PredicateProbe.Reset();
        runtime.Remove(items, item);
        Assert.Equal(0, PredicateProbe.Evaluations);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, gaining));
    }

    [Fact]
    public void Reverse_hash_plan_supports_composite_keys()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<RequestLine>().Key(x => x.Id);
        var items = model.Objects<OrderLine>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.OrderNumber == item.OrderNumber &&
            source.ItemNumber == item.ItemNumber);
        var count = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Count);
        var compiled = model.Build();
        Assert.Contains("Reverse access plan: HashJoin", compiled.DebugView);
        var runtime = compiled.CreateRuntime();
        var matching = new RequestLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER",
            ItemNumber = "A"
        };
        var partial = new RequestLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER",
            ItemNumber = "B"
        };
        runtime.Add(sources, matching);
        runtime.Add(sources, partial);
        runtime.Get(count, matching);
        runtime.Get(count, partial);

        runtime.Add(items, new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER",
            ItemNumber = "A"
        });

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, matching));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, partial));
    }

    [Fact]
    public void Reverse_hash_plan_preserves_string_comparer_semantics()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            string.Equals(source.Code, item.Code, StringComparison.OrdinalIgnoreCase));
        var count = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var matching = new CodeHolder { Id = Guid.NewGuid(), Code = "ABC" };
        var unrelated = new CodeHolder { Id = Guid.NewGuid(), Code = "XYZ" };
        runtime.Add(sources, matching);
        runtime.Add(sources, unrelated);
        runtime.Get(count, matching);
        runtime.Get(count, unrelated);

        runtime.Add(items, new CodeHolder { Id = Guid.NewGuid(), Code = "abc" });

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, matching));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, unrelated));
    }

    [Fact]
    public void Left_join_key_change_reindexes_reverse_candidates()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        runtime.Add(sources, source);
        runtime.Add(items, Item("A", quantity: 1m));
        Assert.Equal(1m, runtime.Get(quantity, source));

        source.Code = "B";
        runtime.Apply(Change.Property(sources, source, x => x.Code, "A", "B"));
        Assert.Equal(0m, runtime.Get(quantity, source));

        runtime.Add(items, Item("B", quantity: 2m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, source));
        Assert.Equal(2m, runtime.Get(quantity, source));
    }

    [Fact]
    public void Item_addition_and_removal_dirty_only_matching_sources()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var matching = Source("A");
        var unrelated = Source("B");
        var item = Item("A", quantity: 2m);
        runtime.Add(sources, matching);
        runtime.Add(sources, unrelated);
        runtime.Get(quantity, matching);
        runtime.Get(quantity, unrelated);

        runtime.Add(items, item);

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, matching));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, unrelated));
        runtime.Get(quantity, matching);

        runtime.Remove(items, item);

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, matching));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, unrelated));
    }

    [Fact]
    public void Removing_and_readding_source_cleans_all_source_scoped_runtime_state()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var invariant = model.Invariant(sources).Using(quantity)
            .Must((source, value) => value <= 10m);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        runtime.Add(sources, source);
        runtime.Add(items, Item("A", quantity: 2m));
        Assert.Equal(2m, runtime.Get(quantity, source));
        Assert.True(runtime.Evaluate(invariant, source));
        Assert.Equal(1, runtime.DerivedStateEntryCount);
        Assert.Equal(1, runtime.InvariantStateEntryCount);
        Assert.Equal(1, runtime.MaterializedRelationPairCount);

        Assert.True(runtime.Remove(sources, source));

        Assert.Throws<InvalidOperationException>(() => runtime.GetState(quantity, source));
        Assert.Throws<InvalidOperationException>(() => runtime.GetState(invariant, source));
        Assert.Equal(0, runtime.DerivedStateEntryCount);
        Assert.Equal(0, runtime.InvariantStateEntryCount);
        Assert.Equal(0, runtime.MaterializedRelationPairCount);

        runtime.Add(sources, source);

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, source));
        Assert.Equal(InvariantEvaluationState.Unknown, runtime.GetState(invariant, source));
        Assert.Equal(0, runtime.DerivedStateEntryCount);
        Assert.Equal(0, runtime.InvariantStateEntryCount);
        Assert.Equal(1, runtime.MaterializedRelationPairCount);
        Assert.Equal(2m, runtime.Get(quantity, source));
        Assert.True(runtime.Evaluate(invariant, source));
    }

    [Fact]
    public void Repeated_source_add_remove_cycles_do_not_grow_runtime_state()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var invariant = model.Invariant(sources).Using(quantity)
            .Must((source, value) => value <= 10m);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        runtime.Add(items, Item("A", quantity: 2m));

        for (var iteration = 0; iteration < 100; iteration++)
        {
            runtime.Add(sources, source);
            runtime.Get(quantity, source);
            runtime.Evaluate(invariant, source);
            Assert.True(runtime.Remove(sources, source));

            Assert.Equal(0, runtime.DerivedStateEntryCount);
            Assert.Equal(0, runtime.InvariantStateEntryCount);
            Assert.Equal(0, runtime.MaterializedRelationPairCount);
        }
    }

    [Fact]
    public void Source_removal_does_not_recreate_state_or_schedule_repairs()
    {
        var scheduled = new List<DerivedSourceRecord>();
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var immediate = model.Invariant(sources).Using(quantity)
            .Must((source, value) => value <= 10m)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        var repair = model.Invariant(sources).Using(quantity)
            .Must((source, value) => value <= 10m)
            .ScheduleRepairWith(scheduled.Add);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        runtime.Add(sources, source);
        runtime.Add(items, Item("A", quantity: 2m));
        Assert.True(runtime.Evaluate(immediate, source));
        Assert.True(runtime.Evaluate(repair, source));
        scheduled.Clear();

        Assert.True(runtime.Remove(sources, source));

        Assert.Empty(scheduled);
        Assert.Equal(0, runtime.DerivedStateEntryCount);
        Assert.Equal(0, runtime.InvariantStateEntryCount);
        Assert.Equal(0, runtime.MaterializedRelationPairCount);
    }

    [Fact]
    public void One_item_mutation_leaves_ten_thousand_unrelated_source_caches_fresh()
    {
        const int unrelatedCount = 10_000;
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var quantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var affected = Source("MATCH");
        var unrelated = Enumerable.Range(0, unrelatedCount)
            .Select(index => Source($"U-{index}"))
            .ToArray();
        runtime.Add(sources, affected);
        foreach (var source in unrelated)
            runtime.Add(sources, source);
        var item = Item("MATCH", quantity: 1m);
        runtime.Add(items, item);
        runtime.Get(quantity, affected);
        foreach (var source in unrelated)
            runtime.Get(quantity, source);

        item.Quantity = 2m;
        runtime.Apply(Change.Property(items, item, x => x.Quantity, 1m, 2m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, affected));
        Assert.All(unrelated, source =>
            Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, source)));
    }

}
