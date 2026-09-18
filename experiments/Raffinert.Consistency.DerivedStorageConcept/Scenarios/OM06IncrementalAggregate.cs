using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM06IncrementalAggregate
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.Runtime.InnerRuntime.ResetDiagnostics();
        fixture.Base.Storage.Apply(MutationSet.Create(Change.Add(
            fixture.Model.Fulfillments,
            new Fulfillment { Id = 2, Quantity = 3m })));
        var increments = fixture.Runtime.InnerRuntime.Diagnostics.IncrementalDerivedUpdates;
        _ = fixture.Runtime.Evaluate(fixture.Model.RemainingQuantity, fixture.Line);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();

        fixture.Runtime.Materialize(fixture.Line);

        ModelDAssertions.Require(
            fixture.Line.FulfilledQuantity == 7m && fixture.Line.RemainingQuantity == 3m,
            "OM6 synchronizes both applicable Line targets.");
        ModelDAssertions.Require(increments > 0 &&
                                 fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 0,
            "OM6 writes the Fresh incremental aggregate without a relation scan/recomputation.");
    }
}
