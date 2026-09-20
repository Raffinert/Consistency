namespace Raffinert.Consistency.Tests;

public sealed class SourceMemberImpactTests
{
    [Theory]
    [InlineData(10, 12, DerivedValueState.Dirty)]
    [InlineData(10, 8, DerivedValueState.Invalid)]
    public void Source_member_classifier_supports_asymmetric_severity(
        int oldValue, int newValue, DerivedValueState expected)
    {
        var scenario = CreateSourceOnly(oldValue);
        scenario.Source.Quantity = newValue;

        scenario.Runtime.Apply(Change.Property(
            scenario.Set, scenario.Source, source => source.Quantity, oldValue, newValue));

        Assert.Equal(expected, scenario.Runtime.GetState(scenario.Value, scenario.Source));
    }

    [Fact]
    public void Multiple_changes_use_net_transition_and_strongest_member_severity()
    {
        var scenario = CreateSourceOnly(10);
        scenario.Source.Quantity = 8;
        scenario.Source.Rate = 2;

        scenario.Runtime.Apply(ChangeSet.Create(
            Change.Property(scenario.Set, scenario.Source, source => source.Quantity, 10, 12),
            Change.Property(scenario.Set, scenario.Source, source => source.Quantity, 12, 8),
            Change.Property(scenario.Set, scenario.Source, source => source.Rate, 1, 2)));

        Assert.Equal(DerivedValueState.Invalid, scenario.Runtime.GetState(scenario.Value, scenario.Source));
    }

    [Fact]
    public void Unrelated_rule_does_not_manufacture_a_dependency_and_tracked_fallback_is_used()
    {
        var scenario = CreateSourceOnly(10);
        scenario.Source.Note = "changed";
        scenario.Runtime.Apply(Change.Property(
            scenario.Set, scenario.Source, source => source.Note, "", "changed"));
        Assert.Equal(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.Value, scenario.Source));

        scenario.Source.Rate = 2;
        scenario.Runtime.Apply(Change.Property(
            scenario.Set, scenario.Source, source => source.Rate, 1, 2));
        Assert.Equal(DerivedValueState.Dirty, scenario.Runtime.GetState(scenario.Value, scenario.Source));
    }

    [Fact]
    public void Composed_derived_uses_local_member_rule_and_preserves_upstream_invalid()
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var upstream = model.Derived(set)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(source => source.Rate);
        var composed = model.Derived(set).From(upstream)
            .Impact(policy => policy.SourceMemberChanged(
                source => source.Quantity,
                (oldValue, newValue) => newValue < oldValue
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty))
            .Select((source, rate) => source.Quantity * rate);
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Quantity = 10, Rate = 1 };
        runtime.Add(set, source);
        Assert.Equal(10, runtime.Evaluate(composed, source));

        source.Rate = 2;
        runtime.Apply(Change.Property(set, source, value => value.Rate, 1, 2));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(composed, source));
    }

    [Fact]
    public void Classifier_failure_rolls_back_runtime_state_and_version()
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(set)
            .Impact(policy => policy.SourceMemberChanged<int>(
                source => source.Quantity, (_, _) => throw new DomainException()))
            .Select(source => source.Quantity);
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Quantity = 1 };
        runtime.Add(set, source);
        Assert.Equal(1, runtime.Evaluate(value, source));
        var version = runtime.Version;
        source.Quantity = 2;

        Assert.Throws<DomainException>(() => runtime.Apply(
            Change.Property(set, source, value => value.Quantity, 1, 2)));
        Assert.Equal(version, runtime.Version);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(value, source));
    }

    [Fact]
    public void Diagnostics_describe_conditional_source_policy_without_delegate_details()
    {
        var scenario = CreateSourceOnly(10);
        var diagnostics = scenario.RuntimeModel.Diagnostics.DerivedValues.Single();

        Assert.True(diagnostics.HasConditionalSourcePolicy);
        Assert.Equal(2, diagnostics.SourceMemberRuleCount);
        Assert.Equal([nameof(Source.Quantity), nameof(Source.Note)], diagnostics.SourceMemberRuleNames);
        Assert.Equal(DependencySeverity.Dirty, diagnostics.SourceChangedSeverity);
        Assert.True(scenario.RuntimeModel.Diagnostics.Invariants.Count == 0);
    }

    [Fact]
    public void Diagnostics_describe_conditional_relation_item_policy_members()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(source => source.Id);
        var items = model.Objects<DerivedItemRecord>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        _ = model.Derived(sources).From(relation)
            .Impact(policy => policy.ItemMemberChanged(
                item => item.Quantity, (_, _) => DependencySeverity.Invalid))
            .Select((_, matches) => matches.Sum(item => item.Quantity));

        var diagnostics = model.Build().Diagnostics.DerivedValues.Single();

        Assert.Equal(1, diagnostics.ItemMemberRuleCount);
        Assert.Equal([nameof(DerivedItemRecord.Quantity)], diagnostics.ItemMemberRuleNames);
    }

    private static Scenario CreateSourceOnly(int quantity)
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(set)
            .Impact(policy => policy
                .SourceChanged(DependencySeverity.Dirty)
                .SourceMemberChanged(
                    source => source.Quantity,
                    (oldValue, newValue) => newValue < oldValue
                        ? DependencySeverity.Invalid
                        : DependencySeverity.Dirty)
                .SourceMemberChanged(source => source.Note, (_, _) => DependencySeverity.Invalid))
            .Select(source => source.Quantity * source.Rate);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = new Source { Quantity = quantity, Rate = 1 };
        runtime.Add(set, source);
        Assert.Equal(quantity, runtime.Evaluate(value, source));
        return new Scenario(compiled, runtime, set, value, source);
    }

    private sealed record Scenario(
        CompiledConsistencyModel RuntimeModel,
        ConsistencyRuntime Runtime,
        ObjectSet<Source> Set,
        Derived<Source, int> Value,
        Source Source);

    private sealed class Source
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Quantity { get; set; }
        public int Rate { get; set; }
        public string Note { get; set; } = "";
    }

    private sealed class DomainException : Exception;
}
