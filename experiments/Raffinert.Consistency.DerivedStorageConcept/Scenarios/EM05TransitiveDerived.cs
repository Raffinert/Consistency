using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM05TransitiveDerived
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.ChangeInvoicePrice(55m);

        var unit = fixture.Runtime.Evaluate(fixture.Model.UnitRate, fixture.Link);
        ModelDAssertions.Require(unit == 11m, "EM5 transitive Evaluate uses current upstream value.");
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.PriceRate, fixture.Link) == DerivedValueState.Fresh &&
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.UnitRate, fixture.Link) == DerivedValueState.Fresh,
            "EM5 transitive runtime nodes become Fresh.");
        ModelDAssertions.Require(fixture.Link.PriceRate == 6m && fixture.Link.UnitRate == 12m,
            "EM5 transitive Evaluate writes no mirrors.");

        var storedUnit = fixture.Runtime.Materialize(fixture.Model.UnitRate, fixture.Link);
        ModelDAssertions.Require(storedUnit == 11m && fixture.Link.UnitRate == 11m,
            "EM5 requested target is materialized.");
        ModelDAssertions.Require(fixture.Link.PriceRate == 6m,
            "EM5 T1 leaves materialized upstream closure untouched.");

        var splitGraphValue = fixture.Runtime.Evaluate(fixture.Model.DirectPropertyUnitRate, fixture.Link);
        ModelDAssertions.Require(splitGraphValue == 12m && splitGraphValue != storedUnit,
            "EM5 a direct mirror-property dependency observes stale storage and proves split-graph risk.");
    }
}
