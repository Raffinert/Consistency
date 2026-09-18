using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D04DerivedChain
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.PrimeAll();
        var directBefore = fixture.Storage.Get(fixture.Model.DirectPropertyUnitRate, fixture.Link);
        fixture.ChangeInvoicePrice(55m);

        var runtimeOnlyUnit = fixture.Storage.Get(fixture.Model.RuntimeOnlyUnitRate, fixture.Link);
        Harness.Require(runtimeOnlyUnit == 11m, fixture,
            "D4 downstream runtime-only Get must use current PriceRate.");
        if (fixture.Semantics is ModelARuntimeAuthoritative)
            Harness.Require(fixture.Link.PriceRate == 6m, fixture,
                "D4 Model A upstream mirror remains stale after downstream Get.");
        else
            Harness.Require(fixture.Link.PriceRate == 5.5m, fixture,
                "D4 Models B/C synchronize a recomputed materialized upstream during downstream Get.");

        var materializedUnit = fixture.Storage.Get(fixture.Model.UnitRateTarget, fixture.Link);
        Harness.Require(materializedUnit == 11m, fixture, "D4 materialized UnitRate must be current.");
        var expectedUnitProperty = fixture.Semantics is ModelARuntimeAuthoritative ? 12m : 11m;
        Harness.Require(fixture.Link.UnitRate == expectedUnitProperty, fixture,
            "D4 UnitRate target follows the selected Get policy.");

        var fromRuntimePrice = fixture.Storage.Get(fixture.Model.AlternateUnitRateTarget, fixture.Link);
        Harness.Require(fromRuntimePrice == 11m, fixture,
            "D4 runtime-only PriceRate can feed a materialized UnitRate.");

        var directAfterMaterialization = fixture.Storage.Get(fixture.Model.DirectPropertyUnitRate, fixture.Link);
        if (fixture.Semantics is ModelARuntimeAuthoritative)
            Harness.Require(directAfterMaterialization == directBefore, fixture,
                "D4 direct property dependency silently retains stale state when mirror assignment is not reported.");
        else
            Harness.Require(directAfterMaterialization == directBefore, fixture,
                "D4 materializer assignment is not a runtime mutation, so direct property dependency stays stale.");

        var oldProperty = fixture.Link.PriceRate;
        fixture.Storage.Runtime.Apply(Change.Property(
            fixture.Model.Links, fixture.Link, x => x.PriceRate, oldProperty, oldProperty));
        Harness.Require(fixture.Storage.Runtime.GetState(fixture.Model.DirectPropertyUnitRate, fixture.Link) !=
                        DerivedValueState.Fresh,
            fixture, "D4 reporting target assignment creates a second dependency edge.");
    });
}
