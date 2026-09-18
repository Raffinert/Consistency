using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM05CrossObjectScope
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        var lineFulfilled = fixture.Line.FulfilledQuantity;
        var lineRemaining = fixture.Line.RemainingQuantity;
        var allocationValid = fixture.Allocation.IsValid;
        var lineAssignments = fixture.Line.FulfilledQuantityAssignments + fixture.Line.RemainingQuantityAssignments;

        fixture.Base.Storage.Apply(MutationSet.Create(Change.Add(
            fixture.Model.Fulfillments,
            new Fulfillment { Id = 2, Quantity = 3m })));
        fixture.ChangeInvoicePrice(55m);
        fixture.Runtime.Materialize(fixture.Link);

        ModelDAssertions.Require(
            fixture.Line.FulfilledQuantity == lineFulfilled && fixture.Line.RemainingQuantity == lineRemaining &&
            fixture.Allocation.IsValid == allocationValid,
            "OM5 physical writes do not expand to downstream Line or Allocation objects.");
        ModelDAssertions.Require(
            fixture.Line.FulfilledQuantityAssignments + fixture.Line.RemainingQuantityAssignments == lineAssignments,
            "OM5 dependency traversal performs no cross-object mirror assignment.");
        ModelDAssertions.Require(fixture.Link.PriceRate == 5.5m,
            "OM5 projected/nested input evaluation still materializes the requested link.");
    }
}
