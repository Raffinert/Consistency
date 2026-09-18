using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM07RepairSurvival
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

        ModelDAssertions.Require(application.Result.RepairRequests.Count == 1,
            "EM7 mutation retains one pending repair request.");
        var evaluated = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(evaluated == 7m && fixture.Link.PriceRate == 6m,
            "EM7 Evaluate refreshes only the logical PriceRate.");
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.UnitRate, fixture.Link) == DerivedValueState.Invalid,
            "EM7 downstream UnitRate remains Invalid.");
        ModelDAssertions.Require(application.Result.RepairRequests.Count == 1 && fixture.Model.Repairs.Count == 0,
            "EM7 Evaluate neither erases nor dispatches pending repair.");

        _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(fixture.Link.PriceRate == 7m && application.Result.RepairRequests.Count == 1,
            "EM7 Materialize changes only the requested mirror and preserves repair work.");
        application.Dispatch.Invoke();
        ModelDAssertions.Require(fixture.Model.Repairs.SequenceEqual([fixture.Link.Id]),
            "EM7 the original repair dispatch still invokes exactly once.");
    }
}
