using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM09SetterFailure
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();
        fixture.Link.ThrowOnNextPriceRateSet = true;

        var failed = Throws(() => fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link));
        var computations = fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations;
        ModelDAssertions.Require(failed && fixture.Link.PriceRate == 6m,
            "EM9 setter failure propagates and preserves the previous mirror.");
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.GetState(fixture.Model.PriceRate, fixture.Link) == DerivedValueState.Fresh,
            "EM9 logical freshness is independent from mirror synchronization failure.");
        ModelDAssertions.Require(computations == 1,
            "EM9 failed first materialization evaluated the stale logical value exactly once.");

        var retried = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(retried == 5.5m && fixture.Link.PriceRate == 5.5m,
            "EM9 retry assigns the already-Fresh value.");
        ModelDAssertions.Require(fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == computations,
            "EM9 retry does not recompute.");

        var normalized = ModelDFixture.Create();
        normalized.Link.NormalizePriceRate = true;
        normalized.ChangeInvoicePrice(55.55m);
        var normalizationDetected = Throws(
            () => normalized.Runtime.Materialize(normalized.Model.PriceRate, normalized.Link));
        ModelDAssertions.Require(normalizationDetected && normalized.Link.PriceRate == 6m,
            "EM9 read-back detects a normalizing setter and restores the previous mirror.");
        ModelDAssertions.Require(
            normalized.Runtime.InnerRuntime.GetState(normalized.Model.PriceRate, normalized.Link) ==
            DerivedValueState.Fresh,
            "EM9 normalizing setter does not roll back the independently Fresh logical value.");
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
