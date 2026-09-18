using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D09ComputationFailure
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
        fixture.Link.ThrowPriceRateComputation = true;
        fixture.ChangeInvoicePrice(55m);
        var failed = false;
        try
        {
            _ = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        Harness.Require(failed, fixture, "D9 computation failure must escape.");
        Harness.Require(fixture.Link.PriceRate == 6m, fixture,
            "D9 failed computation must leave materialized property unchanged.");
        Harness.Require(fixture.Storage.GetState(fixture.Model.PriceRateTarget, fixture.Link) !=
                        DerivedValueState.Fresh,
            fixture, "D9 failed computation must not claim Fresh.");

        var rollback = Fixture.Create(fixture.Semantics);
        rollback.Storage.Materialize(rollback.Model.PriceRateTarget, rollback.Link);
        rollback.Link.ThrowPriceRateComputation = true;
        rollback.Link.InvoiceLine.Price = 55m;
        var prepared = rollback.Storage.Runtime.Prepare(MutationSet.Create(
            Change.Property(rollback.Link.InvoiceLine, x => x.Price, 60m, 55m)));
        var planFailed = false;
        try
        {
            _ = rollback.Storage.Runtime.PlanDetailed(
                prepared,
                RuntimeImpactDetailLevel.Summary,
                PlannedInvariantEvaluationMode.None,
                PlannedDerivedEvaluationMode.Affected);
        }
        catch (InvalidOperationException)
        {
            planFailed = true;
        }
        Harness.Require(planFailed && rollback.Storage.Runtime.Version == 0,
            rollback, "D9 failed plan must roll runtime version/state back.");
        Harness.Require(rollback.Storage.Runtime.GetState(rollback.Model.PriceRate, rollback.Link) ==
                        DerivedValueState.Fresh && rollback.Link.PriceRate == 6m,
            rollback, "D9 rollback restores prior cache state and leaves property untouched.");
    });
}
