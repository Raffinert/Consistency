namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM02FreshCacheStaleMirrors
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.ChangeInvoicePrice(55m);
        ObjectMaterializeScenarioSupport.EvaluateAllLinkTargets(fixture);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();
        var priceAssignments = fixture.Link.PriceRateAssignments;
        var unitAssignments = fixture.Link.UnitRateAssignments;

        fixture.Runtime.Materialize(fixture.Link);

        ModelDAssertions.Require(fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 0,
            "OM2 source-wide Materialize reuses all Fresh logical caches.");
        ModelDAssertions.Require(fixture.Link.PriceRate == 5.5m && fixture.Link.UnitRate == 11m,
            "OM2 synchronizes stale mirrors after logical evaluation.");
        ModelDAssertions.Require(
            fixture.Link.PriceRateAssignments == priceAssignments + 1 &&
            fixture.Link.UnitRateAssignments == unitAssignments + 1,
            "OM2 each stale rate mirror is assigned once.");

        fixture.Runtime.Materialize(fixture.Link);
        ModelDAssertions.Require(
            fixture.Link.PriceRateAssignments == priceAssignments + 1 &&
            fixture.Link.UnitRateAssignments == unitAssignments + 1 &&
            fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 0,
            "OM2 repeated object Materialize avoids equal writes and recomputation.");
    }
}
