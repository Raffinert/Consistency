namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM03PartialStaleness
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.ChangeInvoicePrice(55m);
        _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Model.UnitFromRuntimePrice, fixture.Link);
        fixture.Link.AlternateUnitRate = 999m;
        var priceAssignments = fixture.Link.PriceRateAssignments;
        var alternateAssignments = fixture.Link.AlternateUnitRateAssignments;
        var unitComputations = fixture.Link.UnitRateComputations;

        fixture.Runtime.Materialize(fixture.Link);

        ModelDAssertions.Require(fixture.Link.PriceRateAssignments == priceAssignments,
            "OM3 equal Fresh PriceRate is neither recomputed nor reassigned.");
        ModelDAssertions.Require(
            fixture.Link.UnitRate == 11m && fixture.Link.UnitRateComputations == unitComputations + 1,
            "OM3 stale UnitRate recomputes and writes once.");
        ModelDAssertions.Require(
            fixture.Link.AlternateUnitRate == 11m &&
            fixture.Link.AlternateUnitRateAssignments == alternateAssignments + 1,
            "OM3 Fresh AlternateRate overwrites a rogue mirror without recomputation.");
    }
}
