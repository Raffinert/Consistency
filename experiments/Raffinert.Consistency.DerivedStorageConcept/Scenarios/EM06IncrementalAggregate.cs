using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM06IncrementalAggregate
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        _ = fixture.Runtime.Materialize(fixture.Model.FulfilledQuantity, fixture.Line);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();
        var added = new Fulfillment { Id = 2, Quantity = 3m };
        fixture.Base.Storage.Apply(MutationSet.Create(Change.Add(fixture.Model.Fulfillments, added)));
        var increments = fixture.Runtime.InnerRuntime.Diagnostics.IncrementalDerivedUpdates;

        var evaluated = fixture.Runtime.Evaluate(fixture.Model.FulfilledQuantity, fixture.Line);
        ModelDAssertions.Require(evaluated == 7m && fixture.Line.FulfilledQuantity == 4m,
            "EM6 Evaluate returns Fresh incremental cache without property write.");
        ModelDAssertions.Require(fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 0,
            "EM6 Evaluate does not full-scan a Fresh incremental aggregate.");
        var materialized = fixture.Runtime.Materialize(fixture.Model.FulfilledQuantity, fixture.Line);
        ModelDAssertions.Require(materialized == 7m && fixture.Line.FulfilledQuantity == 7m,
            "EM6 Materialize writes the already-Fresh aggregate.");
        ModelDAssertions.Require(increments > 0 &&
                                 fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 0,
            "EM6 materialization preserves incremental execution evidence.");
    }
}
