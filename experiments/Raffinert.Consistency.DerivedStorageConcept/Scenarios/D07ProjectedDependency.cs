using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D07ProjectedDependency
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.FulfilledTarget, fixture.Line);
        fixture.Storage.Materialize(fixture.Model.RemainingTarget, fixture.Line);
        fixture.Storage.Materialize(fixture.Model.AllocationValidityTarget, fixture.Allocation);

        fixture.Fulfillment.Quantity = 7m;
        fixture.Storage.Apply(Change.Property(
            fixture.Model.Fulfillments, fixture.Fulfillment, x => x.Quantity, 4m, 7m));
        Harness.Require(fixture.Storage.Runtime.GetState(fixture.Model.AllocationValidity, fixture.Allocation) !=
                        DerivedValueState.Fresh,
            fixture, "D7 projected Allocation consumer must become stale.");
        var valid = fixture.Storage.Get(fixture.Model.AllocationValidityTarget, fixture.Allocation);
        Harness.Require(!valid, fixture,
            "D7 projected Get must route through current RemainingQuantity.");
        if (fixture.Semantics is ModelARuntimeAuthoritative)
        {
            Harness.Require(fixture.Line.RemainingQuantity == 6m && fixture.Allocation.IsValid,
                fixture, "D7 Model A leaves materialized mirrors unchanged after Get.");
        }
        else
        {
            Harness.Require(fixture.Line.RemainingQuantity == 3m && !fixture.Allocation.IsValid,
                fixture, "D7 Models B/C synchronize materialized upstream/downstream nodes.");
        }
    });
}
