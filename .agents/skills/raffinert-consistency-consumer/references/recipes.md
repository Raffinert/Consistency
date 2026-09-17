# Raffinert.Consistency consumer recipes

These are downstream-application patterns. Adapt names and DI composition to the consumer repository.

All examples are deliberately domain-neutral. Do not copy internal Raffinert runtime/test types into application code.

---

## Recipe 1 — source-local derived mirror

Use when a persisted value depends only on properties of the same source object.

```csharp
var model = new ConsistencyModelBuilder();

var records = model.Objects<Record>()
    .Key(x => x.Id);

var total = model.Derived(records)
    .Compute(x => x.Quantity * x.UnitValue);

var compiled = model.Build();

var mappings = new ConsistencyEfCoreMappings()
    .Map(records)
    .Materialize(total, x => x.Total);
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
var containers = model.Objects<Container>()
    .Key(x => x.Id);

var contributions = model.Objects<Contribution>()
    .Key(x => x.Id);

var contributionsForContainer = model.Relation(containers, contributions)
    .Where((container, contribution) => container.Id == contribution.ContainerId);

var usedCapacity = model.Derived(containers)
    .Using(contributionsForContainer)
    .Incrementally()
    .Compute((container, matches) => matches.Sum(x => x.Amount));
```

For authoritative EF materialization or enforcement, both relation sets must be genuinely complete for the operation:

```csharp
var scope = new ConsistencyScope()
    .Complete(containers)
    .Complete(contributions);
```

Do not attempt to replace relation-source or relation-target completeness with `DiscoverConsumers`.

---

## Recipe 3 — direct-reference consumer discovery

Use when the derived root is an association and its inputs live on referenced objects that may change while some associations are unloaded.

Neutral domain:

```csharp
public sealed class Association
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }
    public required SourceItem Source { get; set; }

    public Guid TargetId { get; set; }
    public required TargetItem Target { get; set; }

    public decimal? CombinedValue { get; set; }
}
```

Declaration:

```csharp
var associations = model.Objects<Association>()
    .Key(x => x.Id);

var combinedValue = model.Derived(associations)
    .DependsOn(x => x.Source.Value)
    .DependsOn(x => x.Target.Value)
    .Compute(x => ValueCalculator.Calculate(
        x.Source.Value,
        x.Target.Value));
```

EF policy:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(associations)
    .Materialize(combinedValue, x => x.CombinedValue)
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

Why both Includes?

Because discovering a root through `Source` is not enough when the calculation also reads `Target.Value`; every discovered association must be evaluation-complete.

Acceptance test:

```text
DB:
    Association A -> Source S + Target 1
    Association B -> Source S + Target 2
    Association C -> Source S + Target 3

initial tracked/runtime:
    only Association A

mutation:
    Source S.Value changes

expected:
    one Source resolver call
    B/C discovered
    A/B/C combined values recomputed
    one runtime version increment
    persisted values correct in separate verification context
```

---

## Recipe 4 — opaque calculator with explicit dependencies

Use when the calculator is application code and cannot be analyzed safely as an expression.

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(x => x.Source.Value)
    .DependsOn(x => x.Target.Value)
    .Compute(x => ValueCalculator.Calculate(
        x.Source.Value,
        x.Target.Value));
```

Bad:

```csharp
var combinedValue = model.Derived(associations)
    .Compute(x => _calculator.Calculate(x));
```

when `_calculator.Calculate(x)` secretly reads values Raffinert cannot infer.

Good rule:

```text
every hidden source-state input used by opaque code
    -> explicit DependsOn member path
```

External services, current time, randomness, and database lookups are not repaired by `DependsOn`; keep derived calculations deterministic from modeled state.

---

## Recipe 5 — derived chain

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(x => x.Source.Value)
    .DependsOn(x => x.Target.Value)
    .Compute(x => ValueCalculator.Calculate(
        x.Source.Value,
        x.Target.Value));

var normalizedValue = model.Derived(associations)
    .Using(combinedValue)
    .Compute((association, value) => Normalizer.Normalize(value));
```

Do not repeat the original property dependencies in every downstream calculation unless that downstream calculation truly reads them directly.

Let the dependency DAG propagate through `combinedValue`.

---

## Recipe 6 — invariant that blocks persistence

```csharp
var remainingCapacity = model.Derived(containers)
    .Using(usedCapacity)
    .Compute((container, used) => container.Capacity - used);

var capacityValid = model.Invariant(containers)
    .Using(remainingCapacity)
    .Must((container, remaining) => remaining >= 0);

var mappings = new ConsistencyEfCoreMappings()
    .Map(containers)
    .Enforce(capacityValid);
```

Use `.Enforce(...)` only when violation must block SQL.

If the application permits temporary inconsistency and wants later repair, model policy/repair instead of making every violation a persistence blocker.

---

## Recipe 7 — materialized mirror is sink-only

Good:

```text
Quantity + UnitValue
    ↓
Derived Total
    ↓
Total mirror persisted
```

Bad:

```text
Quantity + UnitValue
    ↓
Total mirror persisted
    ↓
other Raffinert computation reads the persisted mirror
```

The mirror is persistence/output state, not the dependency graph's semantic input.

If another computation needs total, depend on the `Derived` handle:

```csharp
var adjustedTotal = model.Derived(records)
    .Using(total)
    .Compute((record, totalValue) => totalValue * record.Multiplier);
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

Suppose the database contains many associations and one changed `SourceItem` has only three consumers.

A discovery resolver may load exactly those three consumers:

```csharp
.Where(x => sourceIds.Contains(x.SourceId))
```

After that, do **not** claim:

```csharp
scope.Complete(associations);
```

The operation has targeted consumer coverage for the requested sources. It does not have whole-set coverage.

---

## Recipe 10 — safe resolver superset

A resolver may return a safe authoritative superset when convenient:

```csharp
mappings.DiscoverConsumers(
    associations,
    x => x.Source,
    (db, sources) =>
    {
        var partitionIds = sources.Select(x => x.PartitionId).Distinct().ToArray();

        return db.Set<Association>()
            .Where(x => partitionIds.Contains(x.PartitionId))
            .Include(x => x.Source)
            .Include(x => x.Target);
    });
```

This is valid only when the result is an authoritative **superset** for all requested targets and current tracked navigation state can filter it safely.

Under-fetch is never safe.

---

## Recipe 11 — retargeting overlay

Database state:

```text
Association X -> Source A
```

Tracked current state before save:

```text
Association X -> Source B
```

If Source A changes, a database query may still return X. Current tracked state must exclude it from Source A's effective consumers.

If Source B changes, X may not yet appear in the database query for Source B. If X is already tracked/known, current tracked state must include it.

Do not write custom reconciliation around the resolver unless the application has a scenario Raffinert does not support.

---

## Recipe 12 — closed-world scope

Use only when the application can genuinely establish authoritative whole-set coverage.

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

This is appropriate for small bounded aggregates, complete import batches, and test fixtures.

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

Do not install the runtime plan before database commit.

---

## Recipe 14 — migration from manual orchestration

Before:

```text
ChangeTracker.DetectChanges
collect changed referenced-object IDs
query affected associations
union tracked associations
load missing references
calculate derived value
write persisted mirror
```

After:

```text
DependsOn(Association.Source.Value)
DependsOn(Association.Target.Value)
Materialize(CombinedValue)
DiscoverConsumers(Association.Source)
DiscoverConsumers(Association.Target)
SaveChangesConsistently
```

Migration rule:

1. preserve old behavior;
2. add Raffinert model/mappings;
3. add parity tests covering unloaded consumers;
4. prove persisted outcomes;
5. remove duplicated old orchestration.

Do not delete the manual service before parity is demonstrated.