# Raffinert.Consistency consumer recipes

These are downstream-application patterns. Adapt names and DI composition to the consumer repository.

Do not copy internal Raffinert runtime/test types into application code.

---

## Recipe 1 — source-local derived mirror

Use when a persisted value depends only on properties of the same source object.

```csharp
var model = new ConsistencyModelBuilder();

var lines = model.Objects<InvoiceLine>()
    .Key(x => x.Id);

var lineAmount = model.Derived(lines)
    .Compute(x => x.Quantity * x.UnitPrice);

var compiled = model.Build();

var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Materialize(lineAmount, x => x.LineAmount);
```

Typical save:

```csharp
await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    cancellationToken: cancellationToken);
```

No whole-set completeness is normally required merely for a source-local formula.

Test:

```text
change Quantity
save consistently
assert mirror changed
verify persisted value from separate DbContext
```

---

## Recipe 2 — relation aggregate

Use when a root depends on all matching items.

```csharp
var orderLines = model.Objects<OrderLine>()
    .Key(x => x.Id);

var receipts = model.Objects<ReceiptLine>()
    .Key(x => x.Id);

var matchingReceipts = model.Relation(orderLines, receipts)
    .Where((line, receipt) =>
        line.PurchaseOrderNumber == receipt.PurchaseOrderNumber &&
        line.LineNumber == receipt.PurchaseOrderLineNumber);

var receivedQuantity = model.Derived(orderLines)
    .Using(matchingReceipts)
    .Incrementally()
    .Compute((line, matches) => matches.Sum(x => x.Quantity));
```

For authoritative EF materialization/enforcement, both relation sets must be genuinely complete for the operation:

```csharp
var scope = new ConsistencyScope()
    .Complete(orderLines)
    .Complete(receipts);
```

Do not attempt to replace relation-source/target completeness with `DiscoverConsumers`.

---

## Recipe 3 — PriceRate / direct-reference consumer discovery

Use when the derived root is an association/link and its inputs live on referenced objects that may change while some links are unloaded.

Domain:

```csharp
public sealed class PurchaseOrderInvoiceLine
{
    public Guid Id { get; set; }

    public Guid InvoiceLineId { get; set; }
    public required InvoiceLine InvoiceLine { get; set; }

    public Guid PurchaseOrderLineId { get; set; }
    public required PurchaseOrderLine PurchaseOrderLine { get; set; }

    public decimal? PriceRate { get; set; }
}
```

Declaration:

```csharp
var links = model.Objects<PurchaseOrderInvoiceLine>()
    .Key(x => x.Id);

var priceRate = model.Derived(links)
    .DependsOn(x => x.InvoiceLine.UnitPrice)
    .DependsOn(x => x.PurchaseOrderLine.UnitPrice)
    .Compute(x => PriceRateCalculator.Calculate(
        x.InvoiceLine.UnitPrice,
        x.PurchaseOrderLine.UnitPrice));
```

EF policy:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(links)
    .Materialize(priceRate, x => x.PriceRate)
    .DiscoverConsumers(
        links,
        x => x.InvoiceLine,
        (db, invoiceLines) =>
        {
            var ids = invoiceLines.Select(x => x.Id).ToArray();

            return db.Set<PurchaseOrderInvoiceLine>()
                .Where(x => ids.Contains(x.InvoiceLineId))
                .Include(x => x.InvoiceLine)
                .Include(x => x.PurchaseOrderLine);
        })
    .DiscoverConsumers(
        links,
        x => x.PurchaseOrderLine,
        (db, poLines) =>
        {
            var ids = poLines.Select(x => x.Id).ToArray();

            return db.Set<PurchaseOrderInvoiceLine>()
                .Where(x => ids.Contains(x.PurchaseOrderLineId))
                .Include(x => x.InvoiceLine)
                .Include(x => x.PurchaseOrderLine);
        });
```

Why both Includes?

Because discovery of a root by `PurchaseOrderLine` is not enough. `PriceRateCalculator` also reads `InvoiceLine.UnitPrice`; the discovered link must be evaluation-complete.

Acceptance test:

```text
DB:
    Link A -> PO1 + IL1
    Link B -> PO1 + IL2
    Link C -> PO1 + IL3

initial tracked/runtime:
    only Link A

mutation:
    PO1.UnitPrice changes

expected:
    one PO resolver call
    B/C discovered
    A/B/C rates recomputed
    one runtime version increment
    persisted values correct in separate verification context
```

---

## Recipe 4 — opaque calculator with explicit dependencies

Use when the calculator is application code and cannot be analyzed safely as an expression.

```csharp
var unitRate = model.Derived(links)
    .DependsOn(x => x.InvoiceLine.UnitPrice)
    .DependsOn(x => x.PurchaseOrderLine.UnitPrice)
    .Compute(x => UnitRateCalculator.Calculate(
        x.InvoiceLine.UnitPrice,
        x.PurchaseOrderLine.UnitPrice));
```

Bad:

```csharp
var unitRate = model.Derived(links)
    .Compute(x => _calculator.Calculate(x));
```

when `_calculator.Calculate(x)` secretly reads values Raffinert cannot infer.

Good rule:

```text
every hidden source-state input used by opaque code
    -> explicit DependsOn member path
```

External services/current time/randomness/database lookups are not repaired by `DependsOn`; keep derived calculations deterministic from modeled state.

---

## Recipe 5 — derived chain

```csharp
var priceRate = model.Derived(links)
    .DependsOn(x => x.InvoiceLine.UnitPrice)
    .DependsOn(x => x.PurchaseOrderLine.UnitPrice)
    .Compute(x => PriceRateCalculator.Calculate(
        x.InvoiceLine.UnitPrice,
        x.PurchaseOrderLine.UnitPrice));

var unitRate = model.Derived(links)
    .Using(priceRate)
    .Compute((link, rate) => UnitRateCalculator.FromPriceRate(rate));
```

Do not repeat the original property dependencies in every downstream calculation unless that downstream calculation truly reads them directly.

Let the dependency DAG propagate through `priceRate`.

---

## Recipe 6 — invariant that blocks persistence

```csharp
var remaining = model.Derived(lines)
    .Using(received)
    .Compute((line, receivedQuantity) =>
        line.OrderedQuantity - receivedQuantity);

var quantityValid = model.Invariant(lines)
    .Using(remaining)
    .Must((line, remainingQuantity) => remainingQuantity >= 0);

var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Enforce(quantityValid);
```

Use `.Enforce(...)` only when violation must block SQL.

If the domain permits temporary inconsistency and wants later repair, model policy/repair instead of making every violation a persistence blocker.

---

## Recipe 7 — materialized mirror is sink-only

Good:

```text
Quantity + UnitPrice
    ↓
Derived Total
    ↓
TotalMirror persisted
```

Bad:

```text
Quantity + UnitPrice
    ↓
TotalMirror persisted
    ↓
other Raffinert computation reads TotalMirror
```

The mirror is persistence/output state, not the dependency graph's semantic input.

If another computation needs total, depend on the `Derived` handle:

```csharp
var tax = model.Derived(lines)
    .Using(total)
    .Compute((line, totalValue) => totalValue * line.TaxRate);
```

---

## Recipe 8 — Validate vs RecalculateAndValidate

Validation-only save:

```csharp
await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    new ConsistencySaveOptions
    {
        SaveBehavior = ConsistencySaveBehavior.Validate,
        Scope = scope
    },
    cancellationToken);
```

Meaning:

```text
enforced affected invariants are evaluated
materialized mirrors are not recalculated/written
```

Default `RecalculateAndValidate`:

```text
enforced affected invariants evaluated
configured affected materializations recalculated and written
```

A `DiscoverConsumers` resolver needed only by a materialization should not run in `Validate` mode.

---

## Recipe 9 — targeted discovery is not completeness

Suppose DB has one million associations and PO line 42 has three consumers.

A discovery resolver may load exactly those three consumers:

```csharp
.Where(x => poLineIds.Contains(x.PurchaseOrderLineId))
```

After that, do **not** claim:

```csharp
scope.Complete(associations);
```

The operation has targeted consumer coverage for the changed PO lines. It does not have whole-set coverage.

---

## Recipe 10 — safe resolver superset

This is allowed when convenient:

```csharp
mappings.DiscoverConsumers(
    links,
    x => x.PurchaseOrderLine,
    (db, poLines) =>
    {
        var organizationIds = poLines.Select(x => x.OrganizationId).Distinct().ToArray();

        return db.Set<PurchaseOrderInvoiceLine>()
            .Where(x => organizationIds.Contains(x.OrganizationId))
            .Include(x => x.InvoiceLine)
            .Include(x => x.PurchaseOrderLine);
    });
```

provided the result is an authoritative **superset** and current tracked navigation state can filter it safely.

Under-fetch is never safe.

---

## Recipe 11 — retargeting overlay

Database state:

```text
Link X -> PO1
```

Tracked current state before save:

```text
Link X -> PO2
```

If PO1 changes, a DB query may still return X. Current tracked state must exclude it from PO1's effective consumers.

If PO2 changes, X may not yet appear in the DB query for PO2. If X is already tracked/known, current tracked state must include it.

Do not write custom reconciliation around the resolver unless the application has a scenario Raffinert does not support.

---

## Recipe 12 — closed-world scope

Use only when the application can genuinely establish authoritative whole-set coverage.

```csharp
var allLinks = await db.Links
    .Include(x => x.Source)
    .Include(x => x.Target)
    .ToListAsync(cancellationToken);

var runtime = compiled.CreateRuntime(seed =>
    seed.Add(links, allLinks));

var scope = new ConsistencyScope()
    .Complete(links);
```

This is appropriate for small bounded aggregates/import batches/test fixtures.

It is usually not appropriate to load a huge application table merely to make Raffinert work.

Prefer targeted `DiscoverConsumers` for eligible direct-reference reverse consumers.

---

## Recipe 13 — manual transaction/outbox

```csharp
var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

// Optional first save only if semantic generated values must become final.
await db.SaveChangesAsync(cancellationToken);

var plan = work.PrepareAndPlan();

if (plan is not null)
{
    var durableWork = plan.Result.GetDurablePolicyWork();
    AddOutboxRows(db, durableWork);
}

await db.SaveChangesAsync(cancellationToken);
await tx.CommitAsync(cancellationToken);

work.CommitAfterDatabaseCommit();
work.Dispatch();
```

Do not install the runtime plan before DB commit.

---

## Recipe 14 — migration from manual orchestration

Before:

```text
ChangeTracker.DetectChanges
collect changed PO IDs
collect changed invoice IDs
query affected links
union tracked links
load endpoints
calculate PriceRate
write mirror
```

After:

```text
DependsOn(Link.InvoiceLine.UnitPrice)
DependsOn(Link.PurchaseOrderLine.UnitPrice)
Materialize(PriceRate)
DiscoverConsumers(Link.InvoiceLine)
DiscoverConsumers(Link.PurchaseOrderLine)
SaveChangesConsistently
```

Migration rule:

1. preserve old behavior;
2. add Raffinert model/mappings;
3. add parity tests covering unloaded consumers;
4. prove persisted outcomes;
5. remove duplicated old orchestration.

Do not delete the manual service before parity is demonstrated.
