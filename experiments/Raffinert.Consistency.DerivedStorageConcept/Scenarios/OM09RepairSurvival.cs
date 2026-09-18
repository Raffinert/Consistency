using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM09RepairSurvival
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.Model.Repairs.Clear();
        fixture.Link.InvoiceLine.Price = 70m;
        var application = fixture.Runtime.InnerRuntime.ApplyDetailed(
            MutationSet.Create(Change.Property(fixture.Link.InvoiceLine, x => x.Price, 60m, 70m)),
            RuntimeImpactDetailLevel.Causal);

        fixture.Runtime.Materialize(fixture.Link);

        ModelDAssertions.Require(fixture.Link.PriceRate == 7m && fixture.Link.UnitRate == 14m,
            "OM9 source-wide materialization synchronizes the configured link mirrors.");
        ModelDAssertions.Require(application.Result.RepairRequests.Count == 1 && fixture.Model.Repairs.Count == 0,
            "OM9 materialization neither consumes nor dispatches the pending repair.");
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.LinkValidity, fixture.Link) ==
            DerivedValueState.Invalid,
            "OM9 unrelated LinkValidity remains Invalid.");
        application.Dispatch.Invoke();
        ModelDAssertions.Require(fixture.Model.Repairs.SequenceEqual([fixture.Link.Id]),
            "OM9 normal dispatch invokes the preserved repair exactly once.");
    }
}
