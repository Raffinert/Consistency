using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM08ComputationFailure
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        fixture.Model.Repairs.Clear();
        fixture.Link.ThrowPriceRateComputation = true;
        fixture.Link.InvoiceLine.Price = 70m;
        var application = fixture.Runtime.InnerRuntime.ApplyDetailed(
            MutationSet.Create(Change.Property(fixture.Link.InvoiceLine, x => x.Price, 60m, 70m)),
            RuntimeImpactDetailLevel.Causal);

        var evaluateFailed = Throws(() => fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link));
        ModelDAssertions.Require(evaluateFailed, "EM8 Evaluate propagates computation failure.");
        ModelDAssertions.Require(fixture.Link.PriceRate == 6m,
            "EM8 failed Evaluate leaves the mirror untouched.");
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.PriceRate, fixture.Link) != DerivedValueState.Fresh,
            "EM8 failed Evaluate does not publish a new Fresh value.");
        ModelDAssertions.Require(application.Result.RepairRequests.Count == 1 && fixture.Model.Repairs.Count == 0,
            "EM8 failed Evaluate preserves pending consequences.");

        var materializeFailed = Throws(() => fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link));
        ModelDAssertions.Require(materializeFailed && fixture.Link.PriceRate == 6m,
            "EM8 Materialize uses the same evaluation path and cannot assign after evaluation failure.");
        ModelDAssertions.Require(application.Result.RepairRequests.Count == 1,
            "EM8 failed Materialize also preserves pending consequences.");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
