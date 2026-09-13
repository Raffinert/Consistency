namespace Raffinert.Relations.Tests;

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
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var upstream = model.Derived(set)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(source => source.Rate);
        var composed = model.Derived(set).Using(upstream)
            .Impact(policy => policy.SourceMemberChanged(
                source => source.Quantity,
                (oldValue, newValue) => newValue < oldValue
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty))
            .Compute((source, rate) => source.Quantity * rate);
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Quantity = 10, Rate = 1 };
        runtime.Add(set, source);
        Assert.Equal(10, runtime.Get(composed, source));

        source.Rate = 2;
        runtime.Apply(Change.Property(set, source, value => value.Rate, 1, 2));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(composed, source));
    }

    [Fact]
    public void Classifier_failure_rolls_back_runtime_state_and_version()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(set)
            .Impact(policy => policy.SourceMemberChanged<int>(
                source => source.Quantity, (_, _) => throw new DomainException()))
            .Compute(source => source.Quantity);
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Quantity = 1 };
        runtime.Add(set, source);
        Assert.Equal(1, runtime.Get(value, source));
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

    private static Scenario CreateSourceOnly(int quantity)
    {
        var model = new RelationModelBuilder();
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
            .Compute(source => source.Quantity * source.Rate);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = new Source { Quantity = quantity, Rate = 1 };
        runtime.Add(set, source);
        Assert.Equal(quantity, runtime.Get(value, source));
        return new Scenario(compiled, runtime, set, value, source);
    }

    private sealed record Scenario(
        CompiledRelationModel RuntimeModel,
        RelationRuntime Runtime,
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
