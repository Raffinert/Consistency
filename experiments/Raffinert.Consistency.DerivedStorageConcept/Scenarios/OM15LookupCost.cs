using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM15LookupCost
{
    public static void Run()
    {
        const int unrelatedSetCount = 40;
        var builder = new ConsistencyModelBuilder();
        var sets = new List<ObjectSet<LookupSource>>();
        var definitions = new List<Derived<LookupSource, decimal>>();
        var sources = new List<LookupSource>();
        for (var index = 0; index < unrelatedSetCount; index++)
        {
            var set = builder.Objects<LookupSource>().Named($"lookup-{index}").Key(x => x.Id);
            sets.Add(set);
            definitions.Add(builder.Derived(set).Compute(x => x.Input * 2m).Named($"value-{index}"));
            sources.Add(new LookupSource { Id = index + 1, Input = index + 1 });
        }
        var runtime = builder.Build().CreateRuntime(seed =>
        {
            for (var index = 0; index < sets.Count; index++)
                seed.Add(sets[index], [sources[index]]);
        });
        var adapter = new ExplicitEvaluateMaterializeRuntime(runtime);
        for (var index = 0; index < sets.Count; index++)
        {
            var target = new MaterializedValue<LookupSource, decimal>(
                $"value-{index}", definitions[index], x => x.Mirror, (x, value) => x.Mirror = value);
            adapter.Register(sets[index], target, 0);
            adapter.RegisterSource(sets[index], sources[index]);
        }

        adapter.ResetMaterializationDiagnostics();
        adapter.Materialize(sources[17]);

        ModelDAssertions.Require(sources[17].Mirror == sources[17].Input * 2m,
            "OM15 resolves the applicable target.");
        ModelDAssertions.Require(
            adapter.MaterializationDiagnostics.ObjectLookups == 1 &&
            adapter.MaterializationDiagnostics.ObjectTargetsVisited == 1,
            "OM15 identity index visits k=1 target, not all 40 compiled definitions.");
        ModelDAssertions.Require(sources.Where((_, index) => index != 17).All(x => x.Mirror == 0m),
            "OM15 unrelated source-set targets remain untouched.");
    }

    private sealed class LookupSource
    {
        public int Id { get; init; }
        public decimal Input { get; init; }
        public decimal Mirror { get; set; }
    }
}
