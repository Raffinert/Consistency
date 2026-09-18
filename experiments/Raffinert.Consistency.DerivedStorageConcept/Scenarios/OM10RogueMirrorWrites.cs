namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM10RogueMirrorWrites
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.ChangeInvoicePrice(55m);
        ObjectMaterializeScenarioSupport.EvaluateAllLinkTargets(fixture);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();
        fixture.Link.PriceRate = 999m;
        fixture.Link.UnitRate = 888m;

        fixture.Runtime.Materialize(fixture.Link);

        ModelDAssertions.Require(fixture.Link.PriceRate == 5.5m && fixture.Link.UnitRate == 11m,
            "OM10 object Materialize restores every rogue configured mirror.");
        ModelDAssertions.Require(fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 0,
            "OM10 restoring rogue mirrors reuses Fresh logical values.");
    }
}
