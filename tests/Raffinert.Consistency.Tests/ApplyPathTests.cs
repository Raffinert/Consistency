namespace Raffinert.Consistency.Tests;

public sealed class ApplyPathTests
{
    [Fact]
    public void Basic_and_detailed_apply_have_equivalent_state_version_and_callback_semantics()
    {
        var basic = CreateScenario();
        var detailed = CreateScenario();

        Mutate(basic);
        Mutate(detailed);
        var basicImpact = basic.Runtime.Apply(Change.Property(
            basic.Sources, basic.Source, source => source.Amount, 1, 2));
        var application = detailed.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            detailed.Sources, detailed.Source, source => source.Amount, 1, 2)));

        Assert.Empty(detailed.Callbacks);
        application.Dispatch.Invoke();
        Assert.Equal(basicImpact, application.Result.ChangeImpact);
        Assert.Equal(basic.Runtime.Version, detailed.Runtime.Version);
        Assert.Equal(basic.Runtime.GetState(basic.Value, basic.Source),
            detailed.Runtime.GetState(detailed.Value, detailed.Source));
        Assert.Equal(basic.Runtime.GetState(basic.Invariant, basic.Source),
            detailed.Runtime.GetState(detailed.Invariant, detailed.Source));
        Assert.Equal(basic.Callbacks, detailed.Callbacks);
    }

    [Fact]
    public void Detailed_result_does_not_leak_or_change_after_a_later_wave()
    {
        var scenario = CreateScenario();
        Mutate(scenario);
        var first = scenario.Runtime.ApplyDetailed(MutationSet.Create(Change.Property(
            scenario.Sources, scenario.Source, source => source.Amount, 1, 2))).Result;

        var originalDerived = first.DerivedImpacts.ToArray();
        scenario.Runtime.Add(scenario.Sources, new Source { Amount = 3 });

        Assert.Equal(originalDerived, first.DerivedImpacts);
        Assert.Single(first.DerivedImpacts);
    }

    private static void Mutate(Scenario scenario) => scenario.Source.Amount = 2;

    private static Scenario CreateScenario()
    {
        var callbacks = new List<int>();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(sources)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(source => source.Amount);
        var invariant = model.Invariant(sources).From(value).Must((_, amount) => amount <= 1)
            .ScheduleRepairWith(source => callbacks.Add(source.Amount));
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Amount = 1 };
        runtime.Add(sources, source);
        Assert.Equal(1, runtime.Evaluate(value, source));
        Assert.True(runtime.Evaluate(invariant, source));
        callbacks.Clear();
        return new Scenario(runtime, sources, value, invariant, source, callbacks);
    }

    private sealed record Scenario(
        ConsistencyRuntime Runtime,
        ObjectSet<Source> Sources,
        Derived<Source, int> Value,
        Invariant<Source> Invariant,
        Source Source,
        List<int> Callbacks);

    private sealed class Source
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Amount { get; set; }
    }
}
