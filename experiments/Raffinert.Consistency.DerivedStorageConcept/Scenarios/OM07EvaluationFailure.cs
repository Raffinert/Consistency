using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM07EvaluationFailure
{
    public static void Run()
    {
        var o1 = ModelDFixture.Create();
        o1.Prime();
        o1.ChangeInvoicePrice(55m);
        o1.Link.ThrowUnitRateComputation = true;
        _ = o1.Runtime.Materialize(o1.Model.PriceRate, o1.Link);
        var o1Failed = ObjectMaterializeScenarioSupport.Throws<InvalidOperationException>(
            () => o1.Runtime.Materialize(o1.Model.UnitRate, o1.Link));
        ModelDAssertions.Require(o1Failed && o1.Link.PriceRate == 5.5m && o1.Link.UnitRate == 12m,
            "OM7 O1 targeted sequence exposes a partial physical update before later evaluation failure.");

        var o2 = ModelDFixture.Create();
        o2.Prime();
        o2.ChangeInvoicePrice(55m);
        o2.Link.ThrowUnitRateComputation = true;
        var o2Failed = ObjectMaterializeScenarioSupport.Throws<InvalidOperationException>(
            () => o2.Runtime.Materialize(o2.Link));
        ModelDAssertions.Require(o2Failed && o2.Link.PriceRate == 6m && o2.Link.UnitRate == 12m,
            "OM7 O2 evaluation failure occurs before every physical write.");
        ModelDAssertions.Require(
            o2.Runtime.InnerRuntime.GetState(o2.Model.PriceRate, o2.Link) == DerivedValueState.Fresh &&
            o2.Runtime.InnerRuntime.GetState(o2.Model.UnitRate, o2.Link) != DerivedValueState.Fresh,
            "OM7 logical nodes evaluated before failure retain normal cache semantics.");
    }
}
