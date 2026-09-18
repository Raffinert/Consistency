namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D02PersistenceWithoutRead
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        Harness.Require(fixture.Link.PriceRate == 6m, fixture,
            "D2 no-read path must leave the old property until its synchronization boundary.");
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        Harness.Require(fixture.Link.PriceRate == 5.5m, fixture,
            "D2 explicit persistence/materialization boundary must synchronize PriceRate.");
    });
}
