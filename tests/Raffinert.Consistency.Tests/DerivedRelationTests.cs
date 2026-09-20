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
            .From(relation)
            .Select((source, matches) => matches.Count);
        var atMostOne = model.Invariant(sources)
            .From(count)
            .Must((source, value) => value <= 1);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = true };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B", Enabled = true };
        runtime.Add(sources, source);
        runtime.Add(items, first);
        runtime.Add(items, second);

        Assert.Equal(1, runtime.Evaluate(count, source));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, source));
        Assert.True(runtime.Evaluate(atMostOne, source));

        second.Code = "A";
        runtime.Apply(Change.Property(items, second, x => x.Code, "B", "A"));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(atMostOne, source));
        Assert.Equal(2, runtime.Evaluate(count, source));
        Assert.False(runtime.Evaluate(atMostOne, source));

        second.Enabled = false;
        runtime.Apply(Change.Property(items, second, x => x.Enabled, true, false));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(atMostOne, source));
        Assert.Equal(1, runtime.Evaluate(count, source));
    }

    [Fact]
    public void Adding_and_removing_relation_items_dirty_cached_derived_state()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Select((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Evaluate(count, source));

        runtime.Add(items, item);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(1, runtime.Evaluate(count, source));

        runtime.Remove(items, item);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, source));
        Assert.Equal(0, runtime.Evaluate(count, source));
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
        Assert.Equal(2m, runtime.Evaluate(quantity, matching));
        Assert.Equal(0m, runtime.Evaluate(quantity, unrelated));

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
            runtime.Evaluate(quantity, source);

        item.Code = "B";
        runtime.Apply(Change.Property(items, item, x => x.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, gaining));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, unrelated));
        Assert.Equal(0m, runtime.Evaluate(quantity, losing));
        Assert.Equal(2m, runtime.Evaluate(quantity, gaining));
    }

    [Fact]
    public void Nested_join_key_change_dirties_sources_that_lose_and_gain_membership()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Details!.Code);
        var quantity = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var losing = Source("A");
        var gaining = Source("B");
        var details = new DerivedItemDetails { Code = "A" };
        var item = Item("unused", quantity: 2m, details: details);
        runtime.Add(sources, losing);
        runtime.Add(sources, gaining);
        runtime.Add(items, item);
        runtime.Evaluate(quantity, losing);
        runtime.Evaluate(quantity, gaining);

        details.Code = "B";
        runtime.Apply(Change.Property(details, x => x.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, gaining));
        Assert.Equal(0m, runtime.Evaluate(quantity, losing));
        Assert.Equal(2m, runtime.Evaluate(quantity, gaining));
    }

    [Fact]
    public void Residual_predicate_change_dirties_only_source_that_loses_membership()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code && item.Enabled);
        var quantity = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var losing = Source("A");
        var unrelated = Source("B");
        var item = Item("A", quantity: 2m);
        item.Enabled = true;
        runtime.Add(sources, losing);
        runtime.Add(sources, unrelated);
        runtime.Add(items, item);
        runtime.Evaluate(quantity, losing);
        runtime.Evaluate(quantity, unrelated);

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
        var quantity = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Sum(item => item.Quantity));
        var invariant = model.Invariant(sources).From(quantity)
            .Must((source, value) => value <= 10m);
        var runtime = model.Build().CreateRuntime(new InvalidMembershipImpactPolicy());
        var source = Source("A");
        var item = Item("A", quantity: 2m);
        item.Enabled = true;
        runtime.Add(sources, source);
        runtime.Add(items, item);
        runtime.Evaluate(quantity, source);
        Assert.True(runtime.Evaluate(invariant, source));

        item.Enabled = false;
        runtime.Apply(Change.Property(items, item, x => x.Enabled, true, false));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(quantity, source));
        Assert.Equal(InvariantEvaluationState.Invalid, runtime.GetState(invariant, source));

        Assert.Equal(0m, runtime.Evaluate(quantity, source));
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
        var quantity = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Sum(item => item.Quantity));
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
        runtime.Evaluate(quantity, losing);
        runtime.Evaluate(quantity, gaining);
        var item = Item("A", quantity: 1m);

        PredicateProbe.Reset();
        runtime.Add(items, item);
        Assert.Equal(forceScan ? unrelatedCount + 2 : 1, PredicateProbe.Evaluations);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, gaining));
        runtime.Evaluate(quantity, losing);

        PredicateProbe.Reset();
        item.Code = "B";
        runtime.Apply(Change.Property(items, item, x => x.Code, "A", "B"));
        Assert.Equal(forceScan ? unrelatedCount + 2 : 1, PredicateProbe.Evaluations);
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, losing));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, gaining));
        runtime.Evaluate(quantity, gaining);

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
        var count = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Count);
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
        runtime.Evaluate(count, matching);
        runtime.Evaluate(count, partial);

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
        var count = model.Derived(sources).From(relation)
            .Select((source, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var matching = new CodeHolder { Id = Guid.NewGuid(), Code = "ABC" };
        var unrelated = new CodeHolder { Id = Guid.NewGuid(), Code = "XYZ" };
        runtime.Add(sources, matching);
        runtime.Add(sources, unrelated);
        runtime.Evaluate(count, matching);
        runtime.Evaluate(count, unrelated);

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
        Assert.Equal(1m, runtime.Evaluate(quantity, source));

        source.Code = "B";
        runtime.Apply(Change.Property(sources, source, x => x.Code, "A", "B"));
        Assert.Equal(0m, runtime.Evaluate(quantity, source));

        runtime.Add(items, Item("B", quantity: 2m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, source));
        Assert.Equal(2m, runtime.Evaluate(quantity, source));
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
        runtime.Evaluate(quantity, matching);
        runtime.Evaluate(quantity, unrelated);

        runtime.Add(items, item);

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, matching));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, unrelated));
        runtime.Evaluate(quantity, matching);

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
        var invariant = model.Invariant(sources).From(quantity)
            .Must((source, value) => value <= 10m);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        runtime.Add(sources, source);
        runtime.Add(items, Item("A", quantity: 2m));
        Assert.Equal(2m, runtime.Evaluate(quantity, source));
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
        Assert.Equal(2m, runtime.Evaluate(quantity, source));
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
        var invariant = model.Invariant(sources).From(quantity)
            .Must((source, value) => value <= 10m);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        runtime.Add(items, Item("A", quantity: 2m));

        for (var iteration = 0; iteration < 100; iteration++)
        {
            runtime.Add(sources, source);
            runtime.Evaluate(quantity, source);
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
        var immediate = model.Invariant(sources).From(quantity)
            .Must((source, value) => value <= 10m)
            .ReactWith(InvariantReaction.EvaluateImmediately);
        var repair = model.Invariant(sources).From(quantity)
            .Must((source, value) => value <= 10m)
            .RepairWhenViolated();
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
        runtime.Evaluate(quantity, affected);
        foreach (var source in unrelated)
            runtime.Evaluate(quantity, source);

        item.Quantity = 2m;
        runtime.Apply(Change.Property(items, item, x => x.Quantity, 1m, 2m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, affected));
        Assert.All(unrelated, source =>
            Assert.Equal(DerivedValueState.Fresh, runtime.GetState(quantity, source)));
    }

    [Theory]
    [InlineData(3, 7, DerivedValueState.Invalid)]
    [InlineData(7, 3, DerivedValueState.Dirty)]
    public void Relation_item_member_classifier_controls_directional_severity(
        decimal oldQuantity, decimal newQuantity, DerivedValueState expected)
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var quantity = model.Derived(sources).From(relation)
            .Impact(policy => policy
                .ItemChanged(DependencySeverity.Invalid)
                .ItemMemberChanged(
                    item => item.Quantity,
                    (oldValue, newValue) => newValue > oldValue
                        ? DependencySeverity.Invalid
                        : DependencySeverity.Dirty))
            .Select((_, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("A", oldQuantity);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(oldQuantity, runtime.Evaluate(quantity, source));

        item.Quantity = newQuantity;
        runtime.Apply(Change.Property(items, item, value => value.Quantity, oldQuantity, newQuantity));

        Assert.Equal(expected, runtime.GetState(quantity, source));
    }

    [Fact]
    public void Relation_item_member_rules_merge_with_invalid_dominance_and_fallback_to_item_changed()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var quantity = model.Derived(sources).From(relation)
            .Impact(policy => policy
                .ItemChanged(DependencySeverity.Invalid)
                .ItemMemberChanged(item => item.Quantity, (_, _) => DependencySeverity.Dirty)
                .ItemMemberChanged(item => item.Enabled, (_, _) => DependencySeverity.Invalid))
            .Select((_, matches) => matches.Sum(item => item.Quantity) +
                (matches.Any(item => item.Details != null) ? 0m : 0m));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("A", 3m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(3m, runtime.Evaluate(quantity, source));

        item.Quantity = 4m;
        item.Enabled = true;
        runtime.Apply(ChangeSet.Create(
            Change.Property(items, item, value => value.Quantity, 3m, 4m),
            Change.Property(items, item, value => value.Enabled, false, true)));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(quantity, source));

        runtime.Evaluate(quantity, source);
        item.Details = new DerivedItemDetails { Code = "unrelated" };
        runtime.Apply(Change.Property(items, item, value => value.Details, null, item.Details));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(quantity, source));
    }

    [Fact]
    public void Relation_membership_changes_use_membership_policy_not_item_member_classifier()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var quantity = model.Derived(sources).From(relation)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Dirty)
                .ItemChanged(DependencySeverity.Invalid)
                .ItemMemberChanged(item => item.Quantity, (_, _) => DependencySeverity.Invalid))
            .Select((_, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        runtime.Add(sources, source);
        Assert.Equal(0m, runtime.Evaluate(quantity, source));

        var item = Item("A", 3m);
        runtime.Add(items, item);

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, source));

        Assert.Equal(3m, runtime.Evaluate(quantity, source));
        runtime.Remove(items, item);

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(quantity, source));
    }

    [Fact]
    public void Relation_item_member_classifier_reports_invalid_enum_values()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var quantity = model.Derived(sources).From(relation)
            .Impact(policy => policy.ItemMemberChanged(item => item.Quantity,
                (_, _) => (DependencySeverity)99))
            .Select((_, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("A", 3m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(3m, runtime.Evaluate(quantity, source));
        item.Quantity = 4m;

        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.Apply(
            Change.Property(items, item, value => value.Quantity, 3m, 4m)));
    }

    [Fact]
    public void Projected_relation_membership_reuses_relation_semantics_and_routes_changes()
    {
        var model = new ConsistencyModelBuilder();
        var demands = model.Objects<ProjectedDemand>().Key(value => value.Id);
        var supplies = model.Objects<ProjectedSupply>().Key(value => value.Id);
        var allocations = model.Objects<ProjectedAllocation>().Key(value => value.Id);
        var candidates = model.Relation(demands, supplies)
            .Where((demand, supply) => demand.Code == supply.Code)
            .Named("candidate-supplies");
        var compatible = model.Derived(allocations)
            .FromMembership(candidates, allocation => allocation.Demand, allocation => allocation.Supply)
            .Named("allocation-compatible");
        var invariant = model.Invariant(allocations)
            .From(compatible)
            .Must((_, value) => value);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var demand = new ProjectedDemand { Id = Guid.NewGuid(), Code = "A" };
        var supply = new ProjectedSupply { Id = Guid.NewGuid(), Code = "A" };
        var replacement = new ProjectedSupply { Id = Guid.NewGuid(), Code = "A" };
        var allocation = new ProjectedAllocation
        {
            Id = Guid.NewGuid(),
            Demand = demand,
            Supply = supply
        };
        runtime.Add(demands, demand);
        runtime.Add(supplies, supply);
        runtime.Add(supplies, replacement);
        runtime.Add(allocations, allocation);

        Assert.True(runtime.Evaluate(compatible, allocation));
        Assert.True(runtime.Evaluate(invariant, allocation));
        var diagnostics = compiled.Diagnostics.DerivedValues.Single(value =>
            value.DefinitionKey == "allocation-compatible");
        var membershipDiagnostics = Assert.Single(diagnostics.SemanticDependencies,
            value => value.Kind == DerivedDependencyKind.ProjectedRelationMembership);
        Assert.Equal("Demand", membershipDiagnostics.LeftSelectorPath);
        Assert.Equal("Supply", membershipDiagnostics.RightSelectorPath);

        demand.Code = "B";
        runtime.Apply(Change.Property(demands, demand, value => value.Code, "A", "B"));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(compatible, allocation));
        Assert.False(runtime.Evaluate(compatible, allocation));
        demand.Code = "A";
        runtime.Apply(Change.Property(demands, demand, value => value.Code, "B", "A"));
        Assert.True(runtime.Evaluate(compatible, allocation));

        supply.Code = "B";
        runtime.Apply(Change.Property(supplies, supply, value => value.Code, "A", "B"));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(compatible, allocation));
        Assert.False(runtime.Evaluate(invariant, allocation));

        allocation.Supply = replacement;
        runtime.Apply(Change.Property(allocations, allocation, value => value.Supply, supply, replacement));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(compatible, allocation));
        Assert.True(runtime.Evaluate(compatible, allocation));
        Assert.True(runtime.Evaluate(invariant, allocation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Projected_membership_join_key_changes_route_only_exact_consumers_and_report_selectors(
        bool changeLeft)
    {
        var scenario = CreateProjectedMembershipScenario();
        RuntimeApplication application;
        if (changeLeft)
        {
            scenario.DemandA.Code = "X";
            application = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
                scenario.Demands, scenario.DemandA, value => value.Code, "A", "X")),
                RuntimeImpactDetailLevel.Causal);
        }
        else
        {
            scenario.SupplyA.Code = "X";
            application = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
                scenario.Supplies, scenario.SupplyA, value => value.Code, "A", "X")),
                RuntimeImpactDetailLevel.Causal);
        }

        var impacted = Assert.Single(application.Result.DerivedImpacts).Sources;
        Assert.Equal(2, impacted.Count);
        Assert.Contains(impacted, value => ReferenceEquals(value.Source, scenario.First));
        Assert.Contains(impacted, value => ReferenceEquals(value.Source, scenario.Second));
        Assert.DoesNotContain(impacted, value => ReferenceEquals(value.Source, scenario.Unrelated));
        Assert.Equal(2, application.Result.RepairRequests.Count);
        foreach (var allocation in new[] { scenario.First, scenario.Second })
        {
            Assert.Equal(DerivedValueState.Fresh,
                scenario.Runtime.GetState(scenario.Compatible, allocation));
            Assert.Equal(InvariantEvaluationState.Violated,
                scenario.Runtime.GetState(scenario.Invariant, allocation));
            var sourceImpact = Assert.Single(impacted, value => ReferenceEquals(value.Source, allocation));
            var cause = Assert.IsType<RelationDependencyCause>(Assert.Single(sourceImpact.Causes));
            Assert.Equal("candidate-supplies", cause.DefinitionKey);
            Assert.Equal("Demand", cause.LeftSelectorPath);
            Assert.Equal("Supply", cause.RightSelectorPath);
            Assert.Equal(RelationImpactCauseKind.MembershipRemoved, cause.Kind);
        }
        Assert.Equal(DerivedValueState.Fresh,
            scenario.Runtime.GetState(scenario.Compatible, scenario.Unrelated));
        Assert.Equal(InvariantEvaluationState.Valid,
            scenario.Runtime.GetState(scenario.Invariant, scenario.Unrelated));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Projected_membership_selector_retarget_affects_only_changed_source(bool retargetLeft)
    {
        var scenario = CreateProjectedMembershipScenario();
        RuntimeApplication application;
        if (retargetLeft)
        {
            scenario.First.Demand = scenario.DemandB;
            application = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
                scenario.Allocations, scenario.First, value => value.Demand,
                scenario.DemandA, scenario.DemandB)));
        }
        else
        {
            scenario.First.Supply = scenario.SupplyB;
            application = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
                scenario.Allocations, scenario.First, value => value.Supply,
                scenario.SupplyA, scenario.SupplyB)));
        }

        var impacted = Assert.Single(application.Result.DerivedImpacts).Sources;
        Assert.Same(scenario.First, Assert.Single(impacted).Source);
        Assert.Same(scenario.First, Assert.Single(application.Result.RepairRequests).Source);
        Assert.Equal(DerivedValueState.Fresh,
            scenario.Runtime.GetState(scenario.Compatible, scenario.First));
        Assert.Equal(InvariantEvaluationState.Violated,
            scenario.Runtime.GetState(scenario.Invariant, scenario.First));
        Assert.Equal(DerivedValueState.Fresh,
            scenario.Runtime.GetState(scenario.Compatible, scenario.Second));
        Assert.Equal(InvariantEvaluationState.Valid,
            scenario.Runtime.GetState(scenario.Invariant, scenario.Second));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Projected_membership_selected_target_can_be_atomically_retargeted_and_removed(
        bool removeLeft)
    {
        var scenario = CreateProjectedMembershipScenario();
        RuntimeApplication application;
        if (removeLeft)
        {
            scenario.First.Demand = scenario.ReplacementDemandA;
            scenario.Second.Demand = scenario.ReplacementDemandA;
            application = scenario.Runtime.ApplyDetailed(MutationSet.Create(
                Change.Property(scenario.Allocations, scenario.First, value => value.Demand,
                    scenario.DemandA, scenario.ReplacementDemandA),
                Change.Property(scenario.Allocations, scenario.Second, value => value.Demand,
                    scenario.DemandA, scenario.ReplacementDemandA),
                Change.Remove(scenario.Demands, scenario.DemandA)));
        }
        else
        {
            scenario.First.Supply = scenario.ReplacementSupplyA;
            scenario.Second.Supply = scenario.ReplacementSupplyA;
            application = scenario.Runtime.ApplyDetailed(MutationSet.Create(
                Change.Property(scenario.Allocations, scenario.First, value => value.Supply,
                    scenario.SupplyA, scenario.ReplacementSupplyA),
                Change.Property(scenario.Allocations, scenario.Second, value => value.Supply,
                    scenario.SupplyA, scenario.ReplacementSupplyA),
                Change.Remove(scenario.Supplies, scenario.SupplyA)));
        }

        var impacted = Assert.Single(application.Result.DerivedImpacts).Sources;
        Assert.Equal(2, impacted.Count);
        Assert.DoesNotContain(impacted, value => ReferenceEquals(value.Source, scenario.Unrelated));
        Assert.Empty(application.Result.RepairRequests);
        foreach (var allocation in new[] { scenario.First, scenario.Second })
        {
            Assert.Equal(DerivedValueState.Fresh,
                scenario.Runtime.GetState(scenario.Compatible, allocation));
            Assert.Equal(InvariantEvaluationState.Valid,
                scenario.Runtime.GetState(scenario.Invariant, allocation));
        }
    }

    [Fact]
    public void Projected_membership_unselected_relation_pair_does_not_invalidate_any_source()
    {
        var scenario = CreateProjectedMembershipScenario();
        var unselected = new ProjectedSupply { Id = Guid.NewGuid(), Code = "A" };

        var application = scenario.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Add(scenario.Supplies, unselected)));

        Assert.Empty(application.Result.DerivedImpacts);
        Assert.Empty(application.Result.InvariantImpacts);
        Assert.Empty(application.Result.RepairRequests);
        Assert.Equal(DerivedValueState.Fresh,
            scenario.Runtime.GetState(scenario.Compatible, scenario.First));
        Assert.Equal(InvariantEvaluationState.Valid,
            scenario.Runtime.GetState(scenario.Invariant, scenario.First));
    }

    private static ProjectedMembershipScenario CreateProjectedMembershipScenario()
    {
        var model = new ConsistencyModelBuilder();
        var demands = model.Objects<ProjectedDemand>().Key(value => value.Id);
        var supplies = model.Objects<ProjectedSupply>().Key(value => value.Id);
        var allocations = model.Objects<ProjectedAllocation>().Key(value => value.Id);
        var candidates = model.Relation(demands, supplies)
            .Where((demand, supply) => demand.Code == supply.Code)
            .Named("candidate-supplies");
        var compatible = model.Derived(allocations)
            .FromMembership(candidates, allocation => allocation.Demand, allocation => allocation.Supply)
            .Named("allocation-compatible");
        var invariant = model.Invariant(allocations).From(compatible)
            .Must((_, value) => value)
            .RepairWhenViolated()
            .Named("allocation-compatible-valid");
        var demandA = new ProjectedDemand { Id = Guid.NewGuid(), Code = "A" };
        var replacementDemandA = new ProjectedDemand { Id = Guid.NewGuid(), Code = "A" };
        var demandB = new ProjectedDemand { Id = Guid.NewGuid(), Code = "B" };
        var supplyA = new ProjectedSupply { Id = Guid.NewGuid(), Code = "A" };
        var replacementSupplyA = new ProjectedSupply { Id = Guid.NewGuid(), Code = "A" };
        var supplyB = new ProjectedSupply { Id = Guid.NewGuid(), Code = "B" };
        var first = new ProjectedAllocation
        {
            Id = Guid.NewGuid(),
            Demand = demandA,
            Supply = supplyA
        };
        var second = new ProjectedAllocation
        {
            Id = Guid.NewGuid(),
            Demand = demandA,
            Supply = supplyA
        };
        var unrelated = new ProjectedAllocation
        {
            Id = Guid.NewGuid(),
            Demand = demandB,
            Supply = supplyB
        };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(demands, [demandA, replacementDemandA, demandB]);
            seed.Add(supplies, [supplyA, replacementSupplyA, supplyB]);
            seed.Add(allocations, [first, second, unrelated]);
        });
        foreach (var allocation in new[] { first, second, unrelated })
        {
            Assert.True(runtime.Evaluate(compatible, allocation));
            Assert.True(runtime.Evaluate(invariant, allocation));
        }
        return new ProjectedMembershipScenario(
            runtime, demands, supplies, allocations, compatible, invariant,
            demandA, replacementDemandA, demandB,
            supplyA, replacementSupplyA, supplyB,
            first, second, unrelated);
    }

    private sealed record ProjectedMembershipScenario(
        ConsistencyRuntime Runtime,
        ObjectSet<ProjectedDemand> Demands,
        ObjectSet<ProjectedSupply> Supplies,
        ObjectSet<ProjectedAllocation> Allocations,
        Derived<ProjectedAllocation, bool> Compatible,
        Invariant<ProjectedAllocation> Invariant,
        ProjectedDemand DemandA,
        ProjectedDemand ReplacementDemandA,
        ProjectedDemand DemandB,
        ProjectedSupply SupplyA,
        ProjectedSupply ReplacementSupplyA,
        ProjectedSupply SupplyB,
        ProjectedAllocation First,
        ProjectedAllocation Second,
        ProjectedAllocation Unrelated);

}
