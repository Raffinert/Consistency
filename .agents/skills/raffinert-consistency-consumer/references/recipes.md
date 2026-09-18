# Raffinert.Consistency consumer recipes

These are downstream-application patterns for the **0.2 API line**.

Do not copy removed v1 API such as `Using`, `Compute`, `Incrementally`, runtime `Get`, or EF mappings `.Materialize(...)`.

---

## Recipe 1 — source-local derived mirror

Use when a persisted value depends only on properties of the same source object.

```csharp
var model = new ConsistencyModelBuilder();

var records = model.Objects<Record>()
    .Key(x => x.Id);

var total = model.Derived(records)
    .Select(x => x.Quantity * x.UnitValue)
    .MaterializeTo(x => x.Total)
    .Named("total");

var compiled = model.Build();

var mappings = new ConsistencyEfCoreMappings()
    .Map(records);
```

Typical save:

```csharp
await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    cancellationToken: cancellationToken);
```

No whole-set completeness is normally required merely for a source-local formula.

Useful runtime distinction:

```csharp
var logical = runtime.Evaluate(total, record); // does not write record.Total
runtime.Materialize(record);                  // synchronizes configured mirrors on record
```

---

## Recipe 2 — relation aggregate

Use when a root depends on all matching items.

```csharp
var containers = model.Objects<Container>()
    .Key(x => x.Id);

var contributions = model.Objects<Contribution>()
    .Key(x => x.Id);

var contributionsForContainer = model.Relation(containers, contributions)
    .Where((container, contribution) =>
        container.Id == contribution.ContainerId)
    .Named("contributions-for-container");

var usedCapacity = model.Derived(containers)
    .From(contributionsForContainer)
    .Sum(x => x.Amount)
    .Named("used-capacity");
```

Recognized operators:

```text
Sum
Count
LongCount
Any
```

Do not add `.Incrementally()`; recognized operators select the incremental plans automatically.

For authoritative EF materialization/enforcement involving the relation, both relation sets must be genuinely covered for the operation:

```csharp
var scope = new ConsistencyScope()
    .Complete(containers)
    .Complete(contributions);
```

`DiscoverConsumers` does not replace relation-source/target completeness.

---

## Recipe 3 — Dirty on additive, Invalid on subtractive

```csharp
var usedCapacity = model.Derived(containers)
    .From(contributionsForContainer)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Sum(x => x.Amount)
    .Named("used-capacity");
```

Use this shape when additions can safely postpone expensive work but removals/cancellations can make existing decisions unsafe.

Do not choose `Dirty` or `Invalid` based on performance preference alone; choose based on whether the stale value is safe to use.

---

## Recipe 4 — opaque calculator with explicit dependencies

Use when the calculator is normal application code and Raffinert cannot infer its reads.

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .Named("combined-value");
```

Bad:

```csharp
var combinedValue = model.Derived(associations)
    .Select(x => _calculator.Calculate(x));
```

when `_calculator.Calculate(x)` secretly reads modeled members Raffinert cannot infer.

Rule:

```text
every hidden modeled source-state input used by opaque code
    -> explicit DependsOn member path
```

Do not use `DependsOn` for external time, randomness, service calls, or database lookups.

---

## Recipe 5 — derived chain uses logical handles

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .MaterializeTo(x => x.CombinedValue)
    .Named("combined-value");

var normalizedValue = model.Derived(associations)
    .From(combinedValue)
    .Select((association, value) => Normalizer.Normalize(value))
    .Named("normalized-value");
```

Important: `normalizedValue` consumes the logical `combinedValue` handle, not `association.CombinedValue`.

This remains correct even if the mirror is stale:

```text
logical CombinedValue = current
association.CombinedValue = old mirror
Evaluate(normalizedValue) uses logical current value
```

---

## Recipe 6 — projected derived dependency

```csharp
var remainingCapacity = model.Derived(containers)
    .From(usedCapacity)
    .Select((container, used) => container.Capacity - used)
    .Named("remaining-capacity");

var assignmentValid = model.Derived(assignments)
    .From(x => x.Container, remainingCapacity)
    .Select((assignment, remaining) => assignment.Amount <= remaining)
    .Named("assignment-valid");
```

Meaning:

```text
Assignment -> Container -> logical RemainingCapacity
```

Projected-consumer completeness must be authoritative. Do not assume `DiscoverConsumers` proves projected completeness.

---

## Recipe 7 — mixed projected and local logical values

```csharp
var actualQuantity = model.Derived(links)
    .From(x => x.PurchaseOrderLine, remainingQuantity)
    .From(unitRate)
    .Select((link, remaining, rate) =>
        CalculateActualQuantity(link, remaining, rate))
    .Named("actual-quantity");
```

Chain `From` calls rather than flattening ownership into an ambiguous helper.

The first value belongs to the referenced PO line. The second belongs to the link itself.

---

## Recipe 8 — invariant that blocks persistence

```csharp
var remainingCapacity = model.Derived(containers)
    .From(usedCapacity)
    .Select((container, used) => container.Capacity - used);

var capacityValid = model.Invariant(containers)
    .From(remainingCapacity)
    .Must((container, remaining) => remaining >= 0)
    .Named("capacity-valid");

var mappings = new ConsistencyEfCoreMappings()
    .Map(containers)
    .Enforce(capacityValid);
```

Use `.Enforce(...)` only when violation must block SQL.

If temporary inconsistency is allowed and later repair is desired, use reaction/repair policy instead of enforcing every violation synchronously.

---

## Recipe 9 — materialized mirror is sink-only

Declare the mirror in Core:

```csharp
var total = model.Derived(records)
    .Select(x => x.Quantity * x.UnitValue)
    .MaterializeTo(x => x.Total)
    .Named("total");
```

Good graph:

```text
Quantity + UnitValue
        ↓
logical Total
        ↓
Total property mirror
```

Bad graph:

```text
logical Total
        ↓
Total property mirror
        ↓
other derived computation reads mirror
```

If another computation needs total:

```csharp
var adjustedTotal = model.Derived(records)
    .From(total)
    .Select((record, totalValue) => totalValue * record.Multiplier);
```

Do not use removed EF mapping `.Materialize(total, x => x.Total)`.

---

## Recipe 10 — targeted vs object materialization

Logical read only:

```csharp
var totalValue = runtime.Evaluate(total, record);
```

Synchronize one known mirror:

```csharp
var totalValue = runtime.Materialize(total, record);
```

Synchronize all configured mirrors on the object:

```csharp
runtime.Materialize(record);
```

Object materialization does **not** mean:

```text
repair everything reachable from record
materialize dependency objects
materialize downstream objects
```

It only synchronizes configured representations physically located on the requested source object.

---

## Recipe 11 — direct-reference consumer discovery

Use when an association/root reads referenced objects and some roots may be unloaded.

```csharp
var associations = model.Objects<Association>()
    .Key(x => x.Id);

var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .MaterializeTo(x => x.CombinedValue)
    .Named("combined-value");

var mappings = new ConsistencyEfCoreMappings()
    .Map(associations)
    .DiscoverConsumers(
        associations,
        x => x.Source,
        (db, sources) =>
        {
            var ids = sources.Select(x => x.Id).ToArray();

            return db.Set<Association>()
                .Where(x => ids.Contains(x.SourceId))
                .Include(x => x.Source)
                .Include(x => x.Target);
        })
    .DiscoverConsumers(
        associations,
        x => x.Target,
        (db, targets) =>
        {
            var ids = targets.Select(x => x.Id).ToArray();

            return db.Set<Association>()
                .Where(x => ids.Contains(x.TargetId))
                .Include(x => x.Source)
                .Include(x => x.Target);
        });
```

Why load both references? Because every discovered root must be evaluation-complete for all active formulas.

The resolver must be tracked and authoritative for the requested targets.

---

## Recipe 12 — targeted discovery is not whole-set completeness

A resolver may load exactly the roots affected by requested targets.

After that, do **not** claim:

```csharp
scope.Complete(associations);
```

unless the operation really has authoritative coverage of the entire set.

Targeted discovery proves coverage for the discovery obligation, not all rows in the set.

---

## Recipe 13 — safe resolver superset

A resolver may return an authoritative superset:

```csharp
mappings.DiscoverConsumers(
    associations,
    x => x.Source,
    (db, sources) =>
    {
        var partitionIds = sources
            .Select(x => x.PartitionId)
            .Distinct()
            .ToArray();

        return db.Set<Association>()
            .Where(x => partitionIds.Contains(x.PartitionId))
            .Include(x => x.Source)
            .Include(x => x.Target);
    });
```

Superset is safe if it cannot omit an authoritative consumer for any requested target. Under-fetch is unsafe.

---

## Recipe 14 — retargeting overlay

Database state:

```text
Association X -> Source A
```

Tracked current state:

```text
Association X -> Source B
```

If A changes, a database query may still return X; current tracked state must exclude it from A's effective consumers.

If B changes, X may not yet appear in the database query for B; if X is already tracked/known, current tracked state must include it.

Do not build custom reconciliation around `DiscoverConsumers` unless the application has a genuinely unsupported scenario.

---

## Recipe 15 — closed-world scope

Use only when whole-set coverage is genuinely authoritative.

```csharp
var allAssociations = await db.Associations
    .Include(x => x.Source)
    .Include(x => x.Target)
    .ToListAsync(cancellationToken);

var runtime = compiled.CreateRuntime(seed =>
    seed.Add(associations, allAssociations));

var scope = new ConsistencyScope()
    .Complete(associations);
```

Good for bounded aggregates, complete import batches, and test fixtures.

Do not load a huge table merely to make Raffinert work if targeted discovery or a better consistency partition is possible.

---

## Recipe 16 — Validate vs RecalculateAndValidate

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
affected enforced invariants evaluated
configured materialized mirrors not written
```

Default `RecalculateAndValidate`:

```text
affected enforced invariants evaluated
affected Core MaterializeTo mirrors synchronized and persisted
```

A discovery resolver needed only by a materialization should not run in `Validate` mode.

---

## Recipe 17 — manual transaction/outbox

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

Do not install the runtime plan or dispatch repair before database commit.

---

## Recipe 18 — migration from manual orchestration

Before:

```text
ChangeTracker.DetectChanges
collect changed referenced-object IDs
query affected roots
union tracked roots
load missing references
calculate derived value
write persisted mirror
```

After:

```text
DependsOn(Source.Value)
DependsOn(Target.Value)
Select(calculator)
MaterializeTo(root mirror)
DiscoverConsumers(Source)
DiscoverConsumers(Target)
SaveChangesConsistently
```

Migration rule:

1. preserve old behavior;
2. declare the Raffinert graph;
3. add parity tests including unloaded consumers;
4. prove persisted outcomes from a separate DbContext;
5. remove duplicated old orchestration only after parity is demonstrated.
