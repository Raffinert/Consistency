using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM02DirectMaterialize
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        fixture.ChangeInvoicePrice(55m);
        fixture.Runtime.InnerRuntime.ResetDiagnostics();
        var value = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(value == 5.5m && fixture.Link.PriceRate == value,
            "EM2 direct Materialize evaluates then assigns.");
        ModelDAssertions.Require(fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 1,
            "EM2 direct Materialize computes exactly once when stale.");
        var assignments = fixture.Link.PriceRateAssignments;
        _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(
            fixture.Runtime.InnerRuntime.Diagnostics.DerivedFullRecomputations == 1 &&
            fixture.Link.PriceRateAssignments == assignments,
            "EM2 repeated Materialize neither recomputes nor performs an equal assignment.");

        var nullable = ModelDFixture.Create();
        _ = nullable.Runtime.Materialize(nullable.Model.NullablePriceRate, nullable.Link);
        nullable.Link.PurchaseOrderLine.Price = 0m;
        nullable.Base.Storage.Apply(Change.Property(
            nullable.Link.PurchaseOrderLine, x => x.Price, 10m, 0m));
        var nullValue = nullable.Runtime.Materialize(nullable.Model.NullablePriceRate, nullable.Link);
        ModelDAssertions.Require(nullValue is null && nullable.Link.NullablePriceRate is null,
            "EM2 nullable value comparison and assignment work.");

        var custom = ModelDFixture.Create();
        custom.Link.RateToken = new RateToken(6.004m);
        var tokenAssignments = custom.Link.RateTokenAssignments;
        var token = custom.Runtime.Materialize(custom.Model.RateToken, custom.Link);
        ModelDAssertions.Require(token.Equals(custom.Link.RateToken),
            "EM2 EqualityComparer<T>.Default honors custom value equality.");
        ModelDAssertions.Require(custom.Link.RateTokenAssignments == tokenAssignments,
            "EM2 custom-equal target avoids redundant assignment.");
    }
}
