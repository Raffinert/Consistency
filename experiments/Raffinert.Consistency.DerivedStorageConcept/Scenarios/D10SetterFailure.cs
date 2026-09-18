using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D10SetterFailure
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        fixture.Link.ThrowOnNextPriceRateSet = true;

        if (fixture.Semantics is ModelARuntimeAuthoritative)
        {
            Harness.Require(fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link) == 5.5m,
                fixture, "D10 Model A Get does not invoke a materializer.");
            Harness.Require(fixture.Link.PriceRate == 6m, fixture,
                "D10 Model A mirror remains old until boundary.");
            return;
        }

        var failed = false;
        try
        {
            _ = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        Harness.Require(failed && fixture.Link.PriceRate == 6m,
            fixture, "D10 setter failure must preserve prior property value.");
        Harness.Require(fixture.Storage.GetState(fixture.Model.PriceRateTarget, fixture.Link) ==
                        DerivedValueState.Invalid,
            fixture, "D10 synchronization failure must override raw Fresh state with Invalid.");
        Harness.Require(fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link) == 5.5m &&
                        fixture.Link.PriceRate == 5.5m,
            fixture, "D10 retry must repair synchronization and return current value.");
    });
}
