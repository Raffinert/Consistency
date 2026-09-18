using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM01EvaluateThenMaterialize
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();

        var evaluated = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        var computations = fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations;
        ModelDAssertions.Require(evaluated == 5.5m, "EM1 Evaluate returns current logical value.");
        ModelDAssertions.Require(fixture.Runtime.InnerRuntime.GetState(fixture.Model.PriceRate, fixture.Link) ==
                                 DerivedValueState.Fresh,
            "EM1 Evaluate transitions stale PriceRate to Fresh.");
        ModelDAssertions.Require(fixture.Link.PriceRate == 6m,
            "EM1 Evaluate does not assign the mirror.");

        var materialized = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(materialized == 5.5m && fixture.Link.PriceRate == 5.5m,
            "EM1 Materialize returns and stores the same value.");
        ModelDAssertions.Require(fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == computations,
            "EM1 Materialize reuses the Fresh logical value.");
    }
}
