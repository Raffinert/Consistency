namespace Raffinert.Consistency.Tests;

public sealed class ConsistencyScopeRequirementTests
{
    [Fact]
    public void Source_only_derived_and_invariant_have_no_requirements()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var local = model.Derived(sources).Compute(x => x.Value);
        var invariant = model.Invariant(sources).Using(local).Must((_, value) => value >= 0);
        var runtime = model.Build().CreateRuntime();

        Assert.Empty(runtime.GetScopeRequirements(local.Definition));
        Assert.Empty(runtime.GetScopeRequirements(invariant.Definition));
    }

    [Fact]
    public void Nested_navigation_requires_consumer_coverage_but_terminal_reference_does_not()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var scalar = model.Derived(sources).Compute(x => x.Value);
        var terminal = model.Derived(sources).Compute(x => x.Parent);
        var nested = model.Derived(sources).Compute(x => x.Parent!.Value);
        var twoHop = model.Derived(sources).Compute(x => x.Parent!.Parent!.Value);
        var runtime = model.Build().CreateRuntime();

        Assert.Empty(runtime.GetScopeRequirements(scalar.Definition));
        Assert.Empty(runtime.GetScopeRequirements(terminal.Definition));
        Assert.Equal([(sources.Definition.Id, ScopeRequirementReason.NavigationConsumerCoverage)],
            Shape(runtime.GetScopeRequirements(nested.Definition)));
        Assert.Equal([(sources.Definition.Id, ScopeRequirementReason.NavigationConsumerCoverage)],
            Shape(runtime.GetScopeRequirements(twoHop.Definition)));
    }

    [Fact]
    public void Value_object_path_requires_no_navigation_coverage()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var nested = model.Derived(sources).Compute(x => x.Address.PostCode);
        var runtime = model.Build().CreateRuntime();

        Assert.Empty(runtime.GetScopeRequirements(nested.Definition));
    }

    [Fact]
    public void Navigation_requirement_composes_and_invariant_source_navigation_is_included()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var nested = model.Derived(sources).Compute(x => x.Parent!.Value);
        var composed = model.Derived(sources).Using(nested).Compute((_, value) => value + 1);
        var invariant = model.Invariant(sources).Using(composed)
            .Must((source, value) => value <= source.Parent!.Value);
        var runtime = model.Build().CreateRuntime();

        var expected = new[] { (sources.Definition.Id, ScopeRequirementReason.NavigationConsumerCoverage) };
        Assert.Equal(expected, Shape(runtime.GetScopeRequirements(composed.Definition)));
        Assert.Equal(expected, Shape(runtime.GetScopeRequirements(invariant.Definition)));
    }

    [Fact]
    public void Projected_selector_is_not_misclassified_as_navigation_coverage()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var consumers = model.Objects<Consumer>().Key(x => x.Id);
        var local = model.Derived(sources).Compute(x => x.Value);
        var projected = model.Derived(consumers).Using(x => x.Source, local).Compute((_, value) => value);
        var runtime = model.Build().CreateRuntime();

        Assert.Equal([(consumers.Definition.Id, ScopeRequirementReason.ProjectedConsumerCoverage)],
            Shape(runtime.GetScopeRequirements(projected.Definition)));
    }

    [Fact]
    public void Relation_backed_and_composed_definitions_retain_both_relation_sets()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var items = model.Objects<Item>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.SourceId);
        var count = model.Derived(sources).Using(relation).Compute((_, rows) => rows.Count);
        var composed = model.Derived(sources).Using(count).Compute((_, value) => value > 0);
        var invariant = model.Invariant(sources).Using(composed).Must((_, value) => value);
        var runtime = model.Build().CreateRuntime();

        var expected = new[]
        {
            (sources.Definition.Id, ScopeRequirementReason.RelationSourceCoverage),
            (items.Definition.Id, ScopeRequirementReason.RelationTargetCoverage)
        };
        Assert.Equal(expected, Shape(runtime.GetScopeRequirements(count.Definition)));
        Assert.Equal(expected, Shape(runtime.GetScopeRequirements(composed.Definition)));
        Assert.Equal(expected, Shape(runtime.GetScopeRequirements(invariant.Definition)));
    }

    [Fact]
    public void Two_upstreams_union_requirements_without_duplicates()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var firstItems = model.Objects<Item>().Key(x => x.Id);
        var secondItems = model.Objects<OtherItem>().Key(x => x.Id);
        var firstRelation = model.Relation(sources, firstItems).Where((source, item) => source.Id == item.SourceId);
        var secondRelation = model.Relation(sources, secondItems).Where((source, item) => source.Id == item.SourceId);
        var first = model.Derived(sources).Using(firstRelation).Compute((_, rows) => rows.Count);
        var second = model.Derived(sources).Using(secondRelation).Compute((_, rows) => rows.Count);
        var combined = model.Derived(sources).Using(first, second).Compute((_, left, right) => left + right);
        var runtime = model.Build().CreateRuntime();

        Assert.Equal(new[]
        {
            (sources.Definition.Id, ScopeRequirementReason.RelationSourceCoverage),
            (firstItems.Definition.Id, ScopeRequirementReason.RelationTargetCoverage),
            (secondItems.Definition.Id, ScopeRequirementReason.RelationTargetCoverage)
        }, Shape(runtime.GetScopeRequirements(combined.Definition)));
    }

    [Fact]
    public void Projected_composition_adds_consumer_and_retains_upstream_requirements()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var items = model.Objects<Item>().Key(x => x.Id);
        var consumers = model.Objects<Consumer>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.SourceId);
        var count = model.Derived(sources).Using(relation).Compute((_, rows) => rows.Count);
        var projected = model.Derived(consumers).Using(x => x.Source, count).Compute((_, value) => value);
        var runtime = model.Build().CreateRuntime();

        Assert.Equal(new[]
        {
            (sources.Definition.Id, ScopeRequirementReason.RelationSourceCoverage),
            (items.Definition.Id, ScopeRequirementReason.RelationTargetCoverage),
            (consumers.Definition.Id, ScopeRequirementReason.ProjectedConsumerCoverage)
        }, Shape(runtime.GetScopeRequirements(projected.Definition)));
    }

    [Fact]
    public void One_set_can_be_required_for_distinct_reasons_in_deterministic_order()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var items = model.Objects<Item>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.SourceId);
        var count = model.Derived(sources).Using(relation).Compute((_, rows) => rows.Count);
        var projected = model.Derived(sources).Using(x => x.Parent!, count).Compute((_, value) => value);
        var runtime = model.Build().CreateRuntime();

        Assert.Equal(new[]
        {
            (sources.Definition.Id, ScopeRequirementReason.RelationSourceCoverage),
            (sources.Definition.Id, ScopeRequirementReason.ProjectedConsumerCoverage),
            (items.Definition.Id, ScopeRequirementReason.RelationTargetCoverage)
        }, Shape(runtime.GetScopeRequirements(projected.Definition)));
    }

    [Fact]
    public void Foreign_definitions_are_rejected()
    {
        var first = CreateRelationModel();
        var second = CreateRelationModel();

        Assert.Throws<ArgumentException>(() => first.Runtime.GetScopeRequirements(second.Derived.Definition));
        Assert.Throws<ArgumentException>(() => first.Runtime.GetScopeRequirements(second.Invariant.Definition));
    }

    [Fact]
    public void Scope_is_fluent_idempotent_and_distinguishes_sets_with_the_same_type()
    {
        var model = new ConsistencyModelBuilder();
        var first = model.Objects<Source>().Named("first").Key(x => x.Id);
        var second = model.Objects<Source>().Named("second").Key(x => x.Id);
        var scope = new ConsistencyScope();

        Assert.Same(scope, scope.Complete(first));
        Assert.Same(scope, scope.Complete(first));
        Assert.Single(scope.CompleteSets);
        Assert.Contains(first.Definition, scope.CompleteSets);
        Assert.DoesNotContain(second.Definition, scope.CompleteSets);
        Assert.Throws<ArgumentNullException>(() => scope.Complete<Source>(null!));
    }

    [Fact]
    public void Missing_scope_produces_machine_readable_ordered_gaps()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Named("sources").Key(x => x.Id);
        var items = model.Objects<Item>().Named("items").Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.SourceId);
        var derived = model.Derived(sources).Using(relation).Compute((_, rows) => rows.Count);
        var runtime = model.Build().CreateRuntime();

        Assert.Equal(new[]
        {
            new ConsistencyScopeGap(sources.Definition.Id, "sources", typeof(Source),
                ConsistencyScopeRequirementKind.RelationSourceCoverage),
            new ConsistencyScopeGap(items.Definition.Id, "items", typeof(Item),
                ConsistencyScopeRequirementKind.RelationTargetCoverage)
        }, runtime.GetScopeGaps(derived.Definition, null));

        var scope = new ConsistencyScope().Complete(sources).Complete(sources);
        Assert.Equal(ConsistencyScopeRequirementKind.RelationTargetCoverage,
            Assert.Single(runtime.GetScopeGaps(derived.Definition, scope)).RequirementKind);
    }

    [Fact]
    public void Scope_validation_rejects_every_foreign_set_even_when_no_requirements_exist()
    {
        var first = new ConsistencyModelBuilder();
        var source = first.Objects<Source>().Key(x => x.Id);
        var local = first.Derived(source).Compute(x => x.Value);
        var runtime = first.Build().CreateRuntime();
        var second = new ConsistencyModelBuilder();
        var foreign = second.Objects<Source>().Key(x => x.Id);
        _ = second.Build();

        var error = Assert.Throws<ArgumentException>(() =>
            runtime.GetScopeGaps(local.Definition, new ConsistencyScope().Complete(foreign)));
        Assert.Contains("another compiled model", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Requirements_do_not_depend_on_access_or_propagation_plan(bool scan, bool conservative)
    {
        var model = new ConsistencyModelBuilder();
        if (scan) model.UseScanPlansForTesting();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var items = model.Objects<Item>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.SourceId);
        var builder = model.Derived(sources).Using(relation);
        if (conservative) builder.PreferConservativePropagation();
        var count = builder.Compute((_, rows) => rows.Count);
        var runtime = model.Build().CreateRuntime();

        Assert.Equal(2, runtime.GetScopeRequirements(count.Definition).Count);
    }

    private static (ConsistencyRuntime Runtime, Derived<Source, int> Derived, Invariant<Source> Invariant)
        CreateRelationModel()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(x => x.Id);
        var items = model.Objects<Item>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Id == item.SourceId);
        var derived = model.Derived(sources).Using(relation).Compute((_, rows) => rows.Count);
        var invariant = model.Invariant(sources).Using(derived).Must((_, value) => value >= 0);
        return (model.Build().CreateRuntime(), derived, invariant);
    }

    private static (int SetId, ScopeRequirementReason Reason)[] Shape(
        IReadOnlyList<ScopeRequirement> requirements) =>
        requirements.Select(requirement => (requirement.Set.Id, requirement.Reason)).ToArray();

    private sealed class Source
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public int Value { get; init; }
        public Source? Parent { get; init; }
        public Address Address { get; init; }
    }

    private readonly record struct Address(string PostCode);

    private sealed class Item
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public Guid SourceId { get; init; }
    }

    private sealed class OtherItem
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public Guid SourceId { get; init; }
    }

    private sealed class Consumer
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required Source Source { get; init; }
    }
}
