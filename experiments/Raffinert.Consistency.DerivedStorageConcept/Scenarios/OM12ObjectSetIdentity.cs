using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM12ObjectSetIdentity
{
    public static void Run()
    {
        var builder = new ConsistencyModelBuilder();
        var firstSet = builder.Objects<DualSetSource>().Named("dual-first").Key(x => x.Id);
        var secondSet = builder.Objects<DualSetSource>().Named("dual-second").Key(x => x.Id);
        var first = builder.Derived(firstSet).Compute(x => x.Input * 2m).Named("first-value");
        var second = builder.Derived(secondSet).Compute(x => x.Input * 3m).Named("second-value");
        var firstOnly = new DualSetSource { Id = 1, Input = 4m };
        var secondOnly = new DualSetSource { Id = 2, Input = 5m };
        var shared = new DualSetSource { Id = 3, Input = 6m };
        var runtime = builder.Build().CreateRuntime(seed =>
        {
            seed.Add(firstSet, [firstOnly, shared]);
            seed.Add(secondSet, [secondOnly, shared]);
        });
        var adapter = new ExplicitEvaluateMaterializeRuntime(runtime);
        adapter.Register(firstSet,
            new MaterializedValue<DualSetSource, decimal>(
                "first-value", first, x => x.FirstMirror, (x, value) => x.FirstMirror = value), 0);
        adapter.Register(secondSet,
            new MaterializedValue<DualSetSource, decimal>(
                "second-value", second, x => x.SecondMirror, (x, value) => x.SecondMirror = value), 0);
        adapter.RegisterSource(firstSet, firstOnly);
        adapter.RegisterSource(secondSet, secondOnly);
        adapter.RegisterSource(firstSet, shared);
        adapter.RegisterSource(secondSet, shared);

        adapter.Materialize(firstOnly);
        ModelDAssertions.Require(firstOnly.FirstMirror == 8m && firstOnly.SecondMirror == 0m,
            "OM12 same CLR type does not leak mappings from another object-set identity.");
        adapter.Materialize(secondOnly);
        ModelDAssertions.Require(secondOnly.FirstMirror == 0m && secondOnly.SecondMirror == 15m,
            "OM12 each exact object-set membership selects only its mappings.");
        adapter.Materialize(shared);
        ModelDAssertions.Require(shared.FirstMirror == 12m && shared.SecondMirror == 18m,
            "OM12 one instance in two sets intentionally receives the union of both exact-set mappings.");
    }

    private sealed class DualSetSource
    {
        public int Id { get; init; }
        public decimal Input { get; init; }
        public decimal FirstMirror { get; set; }
        public decimal SecondMirror { get; set; }
    }
}
