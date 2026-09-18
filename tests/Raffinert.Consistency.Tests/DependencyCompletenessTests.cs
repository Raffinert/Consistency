namespace Raffinert.Consistency.Tests;

public sealed partial class DerivedStateTests
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
        Assert.Equal(2m, runtime.Evaluate(receivedQuantity, source));

        item.Quantity = 5m;
        runtime.Apply(Change.Property(items, item, x => x.Quantity, 2m, 5m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(5m, runtime.Evaluate(receivedQuantity, source));
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
        Assert.Equal(2m, runtime.Evaluate(receivedQuantity, source));

        details.Quantity = 7m;
        runtime.Apply(Change.Property(details, x => x.Quantity, 2m, 7m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(7m, runtime.Evaluate(receivedQuantity, source));

        var replacement = new DerivedItemDetails { Quantity = 9m };
        item.Details = replacement;
        runtime.Apply(Change.Property(items, item, x => x.Details, details, replacement));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(9m, runtime.Evaluate(receivedQuantity, source));

        replacement.Quantity = 11m;
        runtime.Apply(Change.Property(replacement, x => x.Quantity, 9m, 11m));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, source));
        Assert.Equal(11m, runtime.Evaluate(receivedQuantity, source));
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
        Assert.Equal(1m, runtime.Evaluate(receivedQuantity, first));
        Assert.Equal(2m, runtime.Evaluate(receivedQuantity, second));

        first.Adjustment = 3m;
        runtime.Apply(Change.Property(sources, first, x => x.Adjustment, 1m, 3m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(receivedQuantity, first));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(receivedQuantity, second));
    }

    [Fact]
    public void Opaque_computation_is_rejected_by_default()
    {
        var model = CreateQuantityModel(
            out _,
            out _,
            out var receivedQuantity,
            (source, matches) => OpaqueTotal(matches));

        var error = Assert.Throws<InvalidOperationException>(() => model.Build());

        Assert.Contains("Derived computation", error.Message);
        Assert.Contains("ContainsOpaqueCode", error.Message);
        Assert.Contains("AllowIncompleteDependencies", error.Message);
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
    public void Captured_computation_state_is_rejected_by_default()
    {
        var multiplier = 2m;
        var model = CreateQuantityModel(
            out _,
            out _,
            out var derived,
            (source, matches) => matches.Sum(item => item.Quantity) * multiplier);

        Assert.True(derived.Definition.Analysis.Flags.HasFlag(
            Expressions.DependencyAnalysisFlags.ContainsExternalState));
        Assert.Contains("ContainsExternalState",
            Assert.Throws<InvalidOperationException>(() => model.Build()).Message);
    }

    [Fact]
    public void Explicit_opt_in_allows_incomplete_derived_tracking_without_claiming_freshness_guarantees()
    {
        var model = CreateQuantityModel(
            out _,
            out _,
            out var derived,
            (source, matches) => OpaqueTotal(matches));
        derived.AllowIncompleteDependencies();

        var compiled = model.Build();

        Assert.Contains(
            "Dependency tracking: Incomplete, explicitly allowed (ContainsOpaqueCode); cached freshness is not guaranteed",
            compiled.DebugView);
    }

    [Fact]
    public void Invariant_only_nested_source_dependency_dirties_invariant_but_not_derived_value()
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var receivedQuantity,
            (source, matches) => matches.Sum(item => item.Quantity));
        var invariant = model.Invariant(sources).From(receivedQuantity)
            .Must((source, received) => received <= source.Policy!.Maximum);
        var runtime = model.Build().CreateRuntime();
        var policy = new ReceiptPolicy { Maximum = 10m };
        var source = Source("A", policy: policy);
        var item = Item("A", quantity: 2m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(2m, runtime.Evaluate(receivedQuantity, source));
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
            hash.Runtime.Evaluate(hash.Derived, hash.Source),
            scan.Runtime.Evaluate(scan.Derived, scan.Source));
    }

}
