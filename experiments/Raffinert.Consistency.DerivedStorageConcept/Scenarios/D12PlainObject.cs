namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D12PlainObject
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        var rate = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
        Harness.Require(rate == 5.5m, fixture,
            "D12 plain-object Get must not require an EF context.");
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        Harness.Require(fixture.Link.PriceRate == 5.5m, fixture,
            "D12 explicit synchronization must work for detached/plain objects.");
    });
}
