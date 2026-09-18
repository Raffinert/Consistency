namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM03RepeatedEvaluateUsesCache
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.ChangeInvoicePrice(55m);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();
        var first = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        var computations = fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations;
        var second = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(first == 5.5m && second == 5.5m,
            "EM3 Evaluate returns the same current value.");
        ModelDAssertions.Require(computations == 1 &&
                                 fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 1,
            "EM3 Evaluate is lazy/cache-aware, not force-recompute.");
        ModelDAssertions.Require(fixture.Link.PriceRate == 6m,
            "EM3 repeated Evaluate has no property side effect.");
    }
}
