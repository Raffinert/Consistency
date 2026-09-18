using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D06RelationAggregate
{
    public static void Run() => Harness.ForEachPolicy(fixture =>
    {
        fixture.Storage.Materialize(fixture.Model.FulfilledTarget, fixture.Line);
        fixture.Storage.Runtime.ResetDiagnostics();

        var added = new Fulfillment { Id = 2, Quantity = 3m };
        fixture.Storage.Apply(MutationSet.Create(Change.Add(fixture.Model.Fulfillments, added)));
        Harness.Require(fixture.Storage.Runtime.GetState(fixture.Model.FulfilledQuantity, fixture.Line) ==
                        DerivedValueState.Fresh,
            fixture, "D6 recognized membership add should incrementally retain Fresh state.");
        Harness.Require(fixture.Storage.Runtime.Diagnostics.IncrementalDerivedUpdates > 0,
            fixture, "D6 membership add must use the existing incremental plan.");
        var propertyAfterIncrement = fixture.Semantics is ModelCPropertyBacked ? 7m : 4m;
        Harness.Require(fixture.Line.FulfilledQuantity == propertyAfterIncrement, fixture,
            "D6 only property-backed C writes a Fresh incremental update directly to storage.");

        var addedValue = fixture.Storage.Get(fixture.Model.FulfilledTarget, fixture.Line);
        Harness.Require(addedValue == 7m, fixture, "D6 aggregate after membership add.");
        var propertyAfterGet = fixture.Semantics is ModelARuntimeAuthoritative ? propertyAfterIncrement : 7m;
        Harness.Require(fixture.Line.FulfilledQuantity == propertyAfterGet, fixture,
            "D6 Get synchronization after membership add.");

        added.Quantity = 5m;
        fixture.Storage.Apply(Change.Property(
            fixture.Model.Fulfillments, added, x => x.Quantity, 3m, 5m));
        Harness.Require(fixture.Storage.Runtime.GetState(fixture.Model.FulfilledQuantity, fixture.Line) ==
                        DerivedValueState.Invalid,
            fixture, "D6 item change policy should mark aggregate Invalid.");
        Harness.Require(fixture.Storage.Get(fixture.Model.FulfilledTarget, fixture.Line) == 9m,
            fixture, "D6 Invalid aggregate must recompute to 9.");

        fixture.Storage.Apply(MutationSet.Create(Change.Remove(fixture.Model.Fulfillments, added)));
        Harness.Require(fixture.Storage.Runtime.GetState(fixture.Model.FulfilledQuantity, fixture.Line) ==
                        DerivedValueState.Invalid,
            fixture, "D6 membership removal policy should mark aggregate Invalid.");
        Harness.Require(fixture.Storage.Get(fixture.Model.FulfilledTarget, fixture.Line) == 4m,
            fixture, "D6 aggregate after membership removal.");
    });
}
