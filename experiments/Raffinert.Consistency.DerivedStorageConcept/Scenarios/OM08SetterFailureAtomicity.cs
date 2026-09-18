using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM08SetterFailureAtomicity
{
    public static void Run()
    {
        var f1 = ModelDFixture.Create();
        f1.Prime();
        f1.ChangeInvoicePrice(55m);
        f1.Link.ThrowOnNextUnitRateSet = true;
        _ = f1.Runtime.Materialize(f1.Model.PriceRate, f1.Link);
        var f1Failed = ObjectMaterializeScenarioSupport.Throws<InvalidOperationException>(
            () => f1.Runtime.Materialize(f1.Model.UnitRate, f1.Link));
        ModelDAssertions.Require(f1Failed && f1.Link.PriceRate == 5.5m && f1.Link.UnitRate == 12m,
            "OM8 F1 separate targeted calls leave an earlier target materialized.");

        var f2 = ModelDFixture.Create();
        f2.Prime();
        f2.ChangeInvoicePrice(55m);
        f2.Link.ThrowOnNextUnitRateSet = true;
        var f2Failed = ObjectMaterializeScenarioSupport.Throws<InvalidOperationException>(
            () => f2.Runtime.Materialize(f2.Link));
        ModelDAssertions.Require(f2Failed && f2.Link.PriceRate == 6m && f2.Link.UnitRate == 12m,
            "OM8 F2 rolls back all physical writes on a multi-target setter failure.");
        ModelDAssertions.Require(
            f2.Runtime.InnerRuntime.GetState(f2.Model.PriceRate, f2.Link) == DerivedValueState.Fresh &&
            f2.Runtime.InnerRuntime.GetState(f2.Model.UnitRate, f2.Link) == DerivedValueState.Fresh,
            "OM8 F2 does not roll back independently successful logical evaluation.");

        f2.Runtime.InnerRuntime.ResetDiagnostics();
        f2.Runtime.Materialize(f2.Link);
        ModelDAssertions.Require(f2.Link.PriceRate == 5.5m && f2.Link.UnitRate == 11m &&
                                 f2.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 0,
            "OM8 retry reuses Fresh values and completes every target write.");
    }
}
