using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM11NoTargetsAndInvalidSources
{
    public static void Run()
    {
        var builder = new ConsistencyModelBuilder();
        var probes = builder.Objects<RuntimeOnlyProbe>().Named("runtime-only-probes").Key(x => x.Id);
        var runtimeOnly = builder.Derived(probes)
            .DependsOn(x => x.Input)
            .Compute(x => RuntimeOnlyProbe.Calculate(x))
            .Named("runtime-only");
        var source = new RuntimeOnlyProbe { Id = 1, Input = 3m };
        var runtime = builder.Build().CreateRuntime(seed => seed.Add(probes, [source]));
        var adapter = new ExplicitEvaluateMaterializeRuntime(runtime);
        adapter.RegisterSource(probes, source);

        adapter.Materialize(source);
        ModelDAssertions.Require(source.Computations == 0 &&
                                 runtime.GetState(runtimeOnly, source) != DerivedValueState.Fresh,
            "OM11 N1 is a no-op and does not evaluate runtime-only definitions.");

        ModelDAssertions.Require(
            ObjectMaterializeScenarioSupport.Throws<ArgumentNullException>(
                () => adapter.Materialize<RuntimeOnlyProbe>(null!)),
            "OM11 null source is rejected.");
        ModelDAssertions.Require(
            ObjectMaterializeScenarioSupport.Throws<ArgumentException>(
                () => adapter.Materialize(new InvoiceLine { Id = 9 })),
            "OM11 a CLR type unknown to the model is rejected.");
        ModelDAssertions.Require(
            ObjectMaterializeScenarioSupport.Throws<InvalidOperationException>(
                () => adapter.Materialize(new RuntimeOnlyProbe { Id = 2 })),
            "OM11 a known-type unregistered source is rejected.");

        var fixture = ModelDFixture.Create();
        fixture.Runtime.InnerRuntime.Remove(fixture.Model.Links, fixture.Link);
        ModelDAssertions.Require(
            ObjectMaterializeScenarioSupport.Throws<InvalidOperationException>(
                () => fixture.Runtime.Materialize(fixture.Link)),
            "OM11 a removed source is rejected by runtime registration validation.");
        var foreign = ModelDFixture.Create();
        ModelDAssertions.Require(
            ObjectMaterializeScenarioSupport.Throws<InvalidOperationException>(
                () => fixture.Runtime.Materialize(foreign.Link)),
            "OM11 a source from another runtime/model is rejected.");
    }

    private sealed class RuntimeOnlyProbe
    {
        public int Id { get; init; }
        public decimal Input { get; init; }
        public int Computations { get; private set; }

        public static decimal Calculate(RuntimeOnlyProbe probe)
        {
            probe.Computations++;
            return probe.Input * 2m;
        }
    }
}
