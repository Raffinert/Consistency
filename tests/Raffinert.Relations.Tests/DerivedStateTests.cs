namespace Raffinert.Relations.Tests;

public sealed class DerivedStateTests
{
    [Fact]
    public void Item_scalar_used_only_by_computation_marks_derived_value_dirty()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var receivedQuantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = Source("A");
        var item = Item("A", quantity: 2m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(2m, runtime.Get(receivedQuantity, source));

        item.Quantity = 5m;
        runtime.Apply(Change.Property(items, item, x => x.Quantity, 2m, 5m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(5m, runtime.Get(receivedQuantity, source));
        Assert.Contains("RelationItem: DerivedItemRecord.Quantity", compiled.DebugView);
    }

    [Fact]
    public void Nested_item_property_used_only_by_computation_marks_derived_value_dirty()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var receivedQuantity,
            (source, matches) => matches.Sum(item => item.Details!.Quantity));
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var details = new DerivedItemDetails { Quantity = 2m };
        var item = Item("A", details: details);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(2m, runtime.Get(receivedQuantity, source));

        details.Quantity = 7m;
        runtime.Apply(Change.Property(details, x => x.Quantity, 2m, 7m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(7m, runtime.Get(receivedQuantity, source));

        var replacement = new DerivedItemDetails { Quantity = 9m };
        item.Details = replacement;
        runtime.Apply(Change.Property(items, item, x => x.Details, details, replacement));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(9m, runtime.Get(receivedQuantity, source));

        replacement.Quantity = 11m;
        runtime.Apply(Change.Property(replacement, x => x.Quantity, 9m, 11m));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(11m, runtime.Get(receivedQuantity, source));
    }

    [Fact]
    public void Source_computation_dependency_dirties_only_the_changed_source()
    {
        var model = CreateQuantityModel(
            out var sources,
            out _,
            out var receivedQuantity,
            (source, matches) => matches.Sum(item => item.Quantity) + source.Adjustment);
        var runtime = model.Build().CreateRuntime();
        var first = Source("A", adjustment: 1m);
        var second = Source("B", adjustment: 2m);
        runtime.Add(sources, first);
        runtime.Add(sources, second);
        Assert.Equal(1m, runtime.Get(receivedQuantity, first));
        Assert.Equal(2m, runtime.Get(receivedQuantity, second));

        first.Adjustment = 3m;
        runtime.Apply(Change.Property(sources, first, x => x.Adjustment, 1m, 3m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, first));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(receivedQuantity, second));
    }

    [Fact]
    public void Opaque_computation_reports_incomplete_dependency_analysis()
    {
        var model = CreateQuantityModel(
            out _,
            out _,
            out var receivedQuantity,
            (source, matches) => OpaqueTotal(matches));

        var compiled = model.Build();

        Assert.True(receivedQuantity.Definition.Analysis.Flags.HasFlag(
            Expressions.DependencyAnalysisFlags.ContainsOpaqueCode));
        Assert.Contains("Dependency analysis: ContainsOpaqueCode", compiled.DebugView);
    }

    [Fact]
    public void Supported_linq_operators_extract_item_dependencies_without_becoming_opaque()
    {
        var model = CreateQuantityModel(
            out _,
            out _,
            out var derived,
            (source, matches) =>
                matches.Where(item => item.Quantity > 0m).Select(item => item.Quantity).Sum() +
                matches.Min(item => item.Quantity) +
                matches.Max(item => item.Quantity) +
                matches.Average(item => item.Quantity) +
                matches.Count() +
                (matches.Any(item => item.Quantity > 0m) ? 1m : 0m) +
                (matches.All(item => item.Quantity >= 0m) ? 1m : 0m));

        var analysis = derived.Definition.Analysis;

        Assert.Equal(Expressions.DependencyAnalysisFlags.Complete, analysis.Flags);
        Assert.True(analysis.HasRelationMembershipDependency);
        var dependency = Assert.Single(analysis.Dependencies);
        Assert.Equal(Expressions.ExpressionParameterRole.RelationItem, dependency.Role);
        Assert.Equal("DerivedItemRecord.Quantity", dependency.Path.DisplayName);
    }

    [Fact]
    public void Captured_computation_state_is_reported_as_external()
    {
        var multiplier = 2m;
        var model = CreateQuantityModel(
            out _,
            out _,
            out var derived,
            (source, matches) => matches.Sum(item => item.Quantity) * multiplier);

        Assert.True(derived.Definition.Analysis.Flags.HasFlag(
            Expressions.DependencyAnalysisFlags.ContainsExternalState));
    }

    [Fact]
    public void Invariant_only_nested_source_dependency_dirties_invariant_but_not_derived_value()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var receivedQuantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var invariant = model.Invariant(sources).Using(receivedQuantity)
            .Must((source, received) => received <= source.Policy!.Maximum);
        var runtime = model.Build().CreateRuntime();
        var policy = new ReceiptPolicy { Maximum = 10m };
        var source = Source("A", policy: policy);
        var item = Item("A", quantity: 2m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(2m, runtime.Get(receivedQuantity, source));
        Assert.True(runtime.Evaluate(invariant, source));

        policy.Maximum = 1m;
        runtime.Apply(Change.Property(policy, x => x.Maximum, 10m, 1m));

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(receivedQuantity, source));
        Assert.Equal(InvariantEvaluationState.Dirty, runtime.GetState(invariant, source));
        Assert.False(runtime.Evaluate(invariant, source));
    }

    [Fact]
    public void Forced_scan_and_hash_plans_have_identical_derived_dependency_behavior()
    {
        var hash = CreateQuantityScenario(forceScan: false);
        var scan = CreateQuantityScenario(forceScan: true);

        hash.Item.Quantity = 9m;
        scan.Item.Quantity = 9m;
        hash.Runtime.Apply(Change.Property(hash.Items, hash.Item, x => x.Quantity, 2m, 9m));
        scan.Runtime.Apply(Change.Property(scan.Items, scan.Item, x => x.Quantity, 2m, 9m));

        Assert.Equal(DerivedValueState.Dirty, hash.Runtime.GetState(hash.Derived, hash.Source));
        Assert.Equal(DerivedValueState.Dirty, scan.Runtime.GetState(scan.Derived, scan.Source));
        Assert.Equal(
            hash.Runtime.Get(hash.Derived, hash.Source),
            scan.Runtime.Get(scan.Derived, scan.Source));
    }

    [Fact]
    public void Relation_membership_changes_default_to_dirty_and_recompute_lazily()
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
        var model = new RelationModelBuilder();
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
        var model = new RelationModelBuilder();
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
        var model = new RelationModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Code == item.Code && item.Enabled);
        var quantity = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Sum(item => item.Quantity));
        var runtime = model.Build().CreateRuntime(new InvalidMembershipImpactPolicy());
        var source = Source("A");
        var item = Item("A", quantity: 2m);
        item.Enabled = true;
        runtime.Add(sources, source);
        runtime.Add(items, item);
        runtime.Get(quantity, source);

        item.Enabled = false;
        runtime.Apply(Change.Property(items, item, x => x.Enabled, true, false));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(quantity, source));
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

    private static RelationModelBuilder CreateQuantityModel(
        out ObjectSetBuilder<DerivedSourceRecord> sources,
        out ObjectSetBuilder<DerivedItemRecord> items,
        out Derived<DerivedSourceRecord, DerivedItemRecord, decimal> derived,
        System.Linq.Expressions.Expression<Func<DerivedSourceRecord, IReadOnlyList<DerivedItemRecord>, decimal>> computation,
        bool forceScan = false)
    {
        var model = new RelationModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        derived = model.Derived(sources).Using(relation).Compute(computation);
        return model;
    }

    private static QuantityScenario CreateQuantityScenario(bool forceScan)
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var derived,
            (source, matches) => matches.Sum(item => item.Quantity),
            forceScan);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("A", quantity: 2m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(2m, runtime.Get(derived, source));
        return new QuantityScenario(runtime, sources, items, derived, source, item);
    }

    private static DerivedSourceRecord Source(
        string code,
        decimal adjustment = 0m,
        ReceiptPolicy? policy = null) => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Adjustment = adjustment,
            Policy = policy
        };

    private static DerivedItemRecord Item(
        string code,
        decimal quantity = 0m,
        DerivedItemDetails? details = null) => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Quantity = quantity,
            Details = details
        };

    private static decimal OpaqueTotal(IReadOnlyList<DerivedItemRecord> items) =>
        items.Sum(item => item.Quantity);

    private sealed record QuantityScenario(
        RelationRuntime Runtime,
        ObjectSetBuilder<DerivedSourceRecord> Sources,
        ObjectSetBuilder<DerivedItemRecord> Items,
        Derived<DerivedSourceRecord, DerivedItemRecord, decimal> Derived,
        DerivedSourceRecord Source,
        DerivedItemRecord Item);

    private sealed class InvalidMembershipImpactPolicy : IDependencyImpactPolicy
    {
        public DependencyImpactKind Classify(RelationMembershipDependencyImpact impact) =>
            DependencyImpactKind.Invalid;
    }
}
