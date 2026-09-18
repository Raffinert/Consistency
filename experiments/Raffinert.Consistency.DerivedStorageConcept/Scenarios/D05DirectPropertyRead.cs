namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D05DirectPropertyRead
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        var ordinaryRead = fixture.Link.PriceRate;
        Harness.Require(ordinaryRead == 6m, fixture,
            "D5 public property read can silently consume stale state before synchronization.");

        fixture.Link.PriceRate = 999m;
        if (fixture.Semantics is ModelCPropertyBacked)
        {
            var rejected = false;
            try
            {
                _ = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Harness.Require(rejected, fixture, "D5 property-backed model must detect an external target write.");
        }
        else
        {
            var value = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
            Harness.Require(value == 5.5m, fixture, "D5 runtime value remains authoritative.");
            var expected = fixture.Semantics is ModelARuntimeAuthoritative ? 999m : 5.5m;
            Harness.Require(fixture.Link.PriceRate == expected, fixture,
                "D5 external mirror write follows the selected synchronization policy.");
        }
    });
}
