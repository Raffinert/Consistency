using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM01PriceAndUnitRate
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        _ = fixture.Runtime.Evaluate(fixture.Model.DirectPropertyUnitRate, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        var lineAssignments = fixture.Line.FulfilledQuantityAssignments + fixture.Line.RemainingQuantityAssignments;
        var riskComputations = fixture.Link.RiskScoreComputations;
        var repairs = fixture.Model.Repairs.Count;

        fixture.Runtime.Materialize(fixture.Link);

        ModelDAssertions.Require(fixture.Link.PriceRate == 5.5m && fixture.Link.UnitRate == 11m,
            "OM1 object Materialize synchronizes PriceRate and transitive UnitRate.");
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.PriceRate, fixture.Link) == DerivedValueState.Fresh &&
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.UnitRate, fixture.Link) == DerivedValueState.Fresh,
            "OM1 all required logical rate nodes are Fresh.");
        ModelDAssertions.Require(
            fixture.Line.FulfilledQuantityAssignments + fixture.Line.RemainingQuantityAssignments == lineAssignments,
            "OM1 does not write an unrelated source object.");
        ModelDAssertions.Require(fixture.Link.RiskScoreComputations == riskComputations,
            "OM1 does not evaluate an unrelated runtime-only definition.");
        ModelDAssertions.Require(
            fixture.Runtime.Evaluate(fixture.Model.DirectPropertyUnitRate, fixture.Link) == 12m,
            "OM1 suppressed mirror writes avoid feedback but leave direct mirror-property dependencies split/stale.");
        ModelDAssertions.Require(fixture.Model.Repairs.Count == repairs,
            "OM1 consumes or dispatches no additional repair action.");

        var targeted = ModelDFixture.Create();
        targeted.Prime();
        targeted.ChangeInvoicePrice(55m);
        _ = targeted.Runtime.Materialize(targeted.Model.UnitRate, targeted.Link);
        ModelDAssertions.Require(targeted.Link.UnitRate == 11m && targeted.Link.PriceRate == 6m,
            "OM1 targeted T1 Materialize still writes only UnitRate.");
    }
}
