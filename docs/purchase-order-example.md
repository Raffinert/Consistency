# Purchase order, invoice, and goods receipt flow

The motivating workflow is a purchase-order line whose received quantity is derived from matching,
non-cancelled goods receipts. A quantity update or cancellation must affect exactly that PO line and may
invalidate persisted decisions immediately.

## Model

```csharp
var model = new RelationModelBuilder();
var poLines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
var receipts = model.Objects<GoodsReceipt>().Key(x => x.Id);

var matchingReceipts = model.Relation(poLines, receipts).Where((line, receipt) =>
    line.OrderNumber == receipt.OrderNumber &&
    line.ItemNumber == receipt.ItemNumber &&
    !receipt.Cancelled);

var receivedQuantity = model.Derived(poLines)
    .Using(matchingReceipts)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Incrementally()
    .Compute((line, matches) => matches.Sum(receipt => receipt.Quantity));

var quantityInvariant = model.Invariant(poLines)
    .Using(receivedQuantity)
    .Must((line, received) => received <= line.OrderedQuantity)
    .ScheduleRepairWith(repairQueue.Enqueue);

var runtime = model.Build().CreateRuntime();
```

The relation uses the full predicate for correctness. Exact propagation records which receipt belongs to
which line. The incremental sum uses membership and quantity deltas only for an already-fresh cache.

## Explicit mutation batch

Register one initial operation as a single runtime transaction:

```csharp
runtime.Apply(MutationSet.Create(
    Change.Add(poLines, line),
    Change.Add(receipts, receipt)));

decimal received = runtime.Get(receivedQuantity, line);
bool withinOrderedQuantity = runtime.Evaluate(quantityInvariant, line);
```

After changing a receipt quantity, request structured post-commit work:

```csharp
decimal oldQuantity = receipt.Quantity;
receipt.Quantity = 12m;

RuntimeApplyResult result = runtime.ApplyDetailed(MutationSet.Create(
    Change.Property(receipts, receipt, x => x.Quantity, oldQuantity, receipt.Quantity)));

foreach (var request in result.RepairRequests)
    outbox.Add(request.InvariantId, request.Source, request.Reason);

result.DispatchPolicies(); // optional in-process scheduler
```

With `ItemChanged(Invalid)`, the old received quantity cannot be treated as usable before recomputation.
Other PO lines remain fresh. Cancelling a receipt follows the same pattern:

```csharp
receipt.Cancelled = true;
runtime.Apply(Change.Property(
    receipts, receipt, x => x.Cancelled, false, true));
```

The membership removal immediately invalidates only the formerly matched PO line.

## EF Core adapter

Map tracked entity types once:

```csharp
var mappings = new RelationUnitOfWorkMappings()
    .Map(poLines)
    .Map(receipts);

receipt.Quantity = 12m;
await dbContext.SaveChangesAndApplyAsync(runtime, mappings);
```

The helper detects and captures changes, prepares the runtime batch, saves the database, commits runtime
state, and dispatches callbacks. For an explicit transaction, place runtime commit after database commit:

```csharp
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(dbContext.ChangeTracker, mappings);
unit.Prepare(runtime);

await dbContext.SaveChangesAsync();
await transaction.CommitAsync();

unit.Commit(runtime);
unit.Dispatch(runtime);
```

Discard `unit` on save failure or rollback. See [architecture.md](architecture.md) for the underlying
mutation and materialization contracts.
