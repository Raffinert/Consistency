using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D01PriceRate
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        Harness.Require(fixture.Storage.GetState(fixture.Model.PriceRateTarget, fixture.Link) != DerivedValueState.Fresh,
            fixture, "D1 PriceRate should be stale after input mutation.");

        var rate = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
        Harness.Require(rate == 5.5m, fixture, "D1 Get must return logical 5.5.");
        Harness.Require(fixture.Storage.GetState(fixture.Model.PriceRateTarget, fixture.Link) == DerivedValueState.Fresh,
            fixture, "D1 successful Get must make the node Fresh.");
        var expectedProperty = fixture.Semantics is ModelARuntimeAuthoritative ? 6m : 5.5m;
        Harness.Require(fixture.Link.PriceRate == expectedProperty, fixture,
            $"D1 property should be {expectedProperty} after Get.");
    });
}
