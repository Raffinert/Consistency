using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D08RepairSurvival
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.PrimeAll();
        fixture.Model.Repairs.Clear();
        fixture.Link.InvoiceLine.Price = 70m;
        var application = fixture.Storage.Runtime.ApplyDetailed(
            MutationSet.Create(Change.Property(fixture.Link.InvoiceLine, x => x.Price, 60m, 70m)),
            RuntimeImpactDetailLevel.Causal);
        Harness.Require(application.Result.RepairRequests.Count == 1,
            fixture, "D8 mutation must retain one pending repair request.");

        var rate = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
        Harness.Require(rate == 7m &&
                        fixture.Storage.GetState(fixture.Model.PriceRateTarget, fixture.Link) == DerivedValueState.Fresh,
            fixture, "D8 PriceRate can become Fresh independently.");
        Harness.Require(application.Result.RepairRequests.Count == 1 && fixture.Model.Repairs.Count == 0,
            fixture, "D8 read must neither erase nor dispatch pending repair.");
        Harness.Require(fixture.Storage.Runtime.GetState(fixture.Model.UnitRate, fixture.Link) ==
                        DerivedValueState.Invalid,
            fixture, "D8 downstream UnitRate remains Invalid after only PriceRate is read.");

        _ = fixture.Storage.Get(fixture.Model.UnitRateTarget, fixture.Link);
        Harness.Require(application.Result.RepairRequests.Count == 1,
            fixture, "D8 downstream recomputation must not erase committed policy work.");
        application.Dispatch.Invoke();
        Harness.Require(fixture.Model.Repairs.SequenceEqual([fixture.Link.Id]),
            fixture, "D8 pending repair dispatch survives recomputation.");
    });
}
