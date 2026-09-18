namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM04RuntimeOnlyIgnored
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.ChangeInvoicePrice(55m);
        var riskComputations = fixture.Link.RiskScoreComputations;

        fixture.Runtime.Materialize(fixture.Link);

        ModelDAssertions.Require(fixture.Link.RiskScoreComputations == riskComputations,
            "OM4 unrelated runtime-only RiskScore is not evaluated as a target.");
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.RiskScore, fixture.Link) !=
            Raffinert.Consistency.DerivedValueState.Fresh,
            "OM4 the stale runtime-only node remains untouched.");
        ModelDAssertions.Require(fixture.Link.AlternateUnitRate == 11m,
            "OM4 a runtime-only node is still evaluated when required by a materialized target.");
    }
}
