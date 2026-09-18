namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D03RepeatedRead
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        fixture.Storage.Runtime.ResetDiagnostics();
        var first = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
        var computations = fixture.Storage.Runtime.Diagnostics.DerivedFullRecomputations;
        var assignments = fixture.Link.PriceRateAssignments;
        var second = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);

        Harness.Require(first == 5.5m && second == 5.5m, fixture, "D3 repeated reads must agree.");
        Harness.Require(fixture.Storage.Runtime.Diagnostics.DerivedFullRecomputations == computations,
            fixture, "D3 second Fresh read must not recompute.");
        Harness.Require(fixture.Link.PriceRateAssignments == assignments,
            fixture, "D3 second Fresh read must not assign redundantly.");
    });
}
