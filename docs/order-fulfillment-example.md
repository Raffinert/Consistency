# Order fulfillment and allocation-validity flow

The compiling [order-fulfillment sample](../samples/Raffinert.Consistency.OrderFulfillmentSample/Program.cs)
models the complete business-value chain:

```text
Fulfillment membership/value -> FulfilledQuantity -> RemainingQuantity
OrderLine.UnitRate -----------------------------------> UnitRate
RemainingQuantity + UnitRate -------------------------> AllocationValidity
AllocationValidity -----------------------------------> repair invariant
```

The model declares correctness rules once. An ordered-quantity increase produces `Dirty`, while a
decrease produces `Invalid`; fulfillment cancellation flows through exact membership removal and
invalidation; and a unit-rate change invalidates allocation validity. Callers report ordinary mutations
and never invoke a manual invalidation or repair chain.

The decrease scenario opts into `RuntimeImpactDetailLevel.Causal` and renders:

```text
remaining-quantity -> Invalid
  because OrderedQuantity changed (member-specific conditional policy) [Exact]
allocation-validity -> Invalid
  because upstream remaining-quantity [Exact]
allocation-validity-invariant -> Invalid
  because upstream allocation-validity [Exact]
```

Definitions and the source set are named, so `RuntimeApplyResult.GetDurablePolicyWork()` can strictly project
repair requests to data-only identities when moved to an outbox or background queue. Run the executable proof with:

```powershell
dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample -c Release
```

For EF Core capture/save/commit/dispatch ordering, see the existing end-to-end adapter tests and
[architecture.md](architecture.md).
