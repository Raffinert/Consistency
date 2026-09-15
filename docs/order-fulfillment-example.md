# Purchase order, goods receipt, and link-validity flow

The compiling [purchase-order sample](../samples/Raffinert.Consistency.PurchaseOrderSample/Program.cs)
models the complete business-value chain:

```text
GoodsReceipt membership/value -> ReceivedQuantity -> AvailableQuantity
PurchaseOrderLine.PriceRate ---------------------------> UnitRate
AvailableQuantity + UnitRate --------------------------> LinkValidity
LinkValidity ------------------------------------------> repair invariant
```

The model declares correctness rules once. An ordered-quantity increase produces `Dirty`, while a
decrease produces `Invalid`; receipt cancellation flows through exact membership removal; and a rate
change invalidates the captured link decision. Callers report ordinary mutations and never invoke a
manual invalidation or rematching chain.

The decrease scenario opts into `RuntimeImpactDetailLevel.Causal` and renders:

```text
available-quantity -> Invalid
  because OrderedQuantity changed (member-specific conditional policy) [Exact]
link-validity -> Invalid
  because upstream available-quantity [Exact]
link-validity-invariant -> Invalid
  because upstream link-validity [Exact]
```

Definitions and the source set are named, so `RuntimeApplyResult.GetDurablePolicyWork()` can strictly project
repair requests to data-only identities when moved to an outbox or background queue. Run the executable proof with:

```powershell
dotnet run --project samples/Raffinert.Consistency.PurchaseOrderSample -c Release
```

For EF Core capture/save/commit/dispatch ordering, see the existing end-to-end adapter tests and
[architecture.md](architecture.md).
