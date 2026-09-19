# Raffinert.Consistency consumer recipes

These recipes show current declaration, runtime, and EF Core patterns for downstream applications.

---

## Recipe 0 — injected EF runtime and one-call materialization

Register the compiled model and mappings once per application:

```csharp
services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
services.AddRaffinertConsistency<AppDbContext>(compiledModel, mappings);
services.AddScoped<LinkService>();
```

Register the Raffinert EF integration once per service collection. Resolve the scoped runtime before
mutating mapped tracked entities. It may bind after a clean query, but binding is intentionally rejected if
mapped tracked state is already dirty.

Keep the service constructor limited to the context and scoped runtime:

```csharp
public sealed class LinkService(AppDbContext db, ConsistencyRuntime consistency)
{
    public async Task ChangeAsync(long id, decimal value, CancellationToken cancellationToken)
    {
        var link = await db.Links
            .Include(x => x.Left)
            .Include(x => x.Right)
            .SingleAsync(x => x.Id == id, cancellationToken);

        link.Left.Value = value;
        consistency.Materialize(link);
        Use(link.Ratio, link.NormalizedRatio);
        await db.SaveChangesAsync(cancellationToken);
    }
}
```

Use `Materialize(entity)` only when a mirror is needed before saving. If the service only persists the
mutation, call ordinary `SaveChanges`/`SaveChangesAsync`. The integration admits mapped tracked entities,
reuses an unchanged pending plan, rebuilds after intervening changes, and commits runtime state only after
SQL succeeds. Pending navigation/projection changes are visible to plan evaluation without rebasing the
committed runtime baseline; failed SQL leaves that baseline unchanged and retry builds a fresh plan.

---

## Recipe 1 — source-local materialized value

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

Logical read:

```csharp
var value = runtime.Evaluate(total, record);
```

Synchronize physical mirrors on the object:

```csharp
runtime.Materialize(record);
```

---

## Recipe 2 — relation aggregate

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

Recognized relation aggregates:

```text
Sum
Count
LongCount
Any
```

For authoritative EF work involving the relation, establish real coverage of both participating sets.

---

## Recipe 3 — additive vs subtractive impact

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

Choose severity from correctness semantics: whether stale state may remain temporarily usable or must be treated as unsafe.

---

## Recipe 4 — opaque calculator with explicit dependencies

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .Named("combined-value");
```

Every hidden modeled member read in opaque application code must have a corresponding tracked dependency.

Keep calculators deterministic from modeled state.

---

## Recipe 5 — derived chain through logical handles

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

`normalizedValue` receives the logical `combinedValue`. Its correctness does not depend on whether the physical mirror has already been synchronized.

---

## Recipe 6 — projected derived dependency

```csharp
var remainingCapacity = model.Derived(containers)
    .From(usedCapacity)
    .Select((container, used) => container.Capacity - used)
    .Named("remaining-capacity");

var assignmentValid = model.Derived(assignments)
    .From(x => x.Container, remainingCapacity)
    .Select((assignment, remaining) =>
        assignment.Amount <= remaining)
    .Named("assignment-valid");
```

The selector identifies which upstream owner supplies the logical value for each assignment.

---

## Recipe 7 — mixed projected and local logical inputs

```csharp
var actualQuantity = model.Derived(links)
    .From(x => x.PurchaseOrderLine, remainingQuantity)
    .From(unitRate)
    .Select((link, remaining, rate) =>
        CalculateActualQuantity(link, remaining, rate))
    .Named("actual-quantity");
```

Chained `From` calls preserve ownership: one value belongs to the referenced PO line, the other belongs to the link.

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

Use `Enforce` only when violation must block persistence.

---

## Recipe 9 — physical mirror is sink-only

```csharp
var total = model.Derived(records)
    .Select(x => x.Quantity * x.UnitValue)
    .MaterializeTo(x => x.Total)
    .Named("total");
```

Correct graph:

```text
Quantity + UnitValue
        ↓
logical Total
        ↓
Total mirror
```

A downstream value consumes the logical handle:

```csharp
var adjustedTotal = model.Derived(records)
    .From(total)
    .Select((record, totalValue) =>
        totalValue * record.Multiplier);
```

---

## Recipe 10 — logical evaluation and materialization

Logical value only:

```csharp
var totalValue = runtime.Evaluate(total, record);
```

Synchronize one definition's representation:

```csharp
var totalValue = runtime.Materialize(total, record);
```

Synchronize every configured representation on one source object:

```csharp
runtime.Materialize(record);
```

Object materialization writes only representations physically located on the requested object and does not execute repair.

---

## Recipe 11 — direct-reference consumer discovery

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

Every discovered root must be evaluation-complete for the active formulas.

---

## Recipe 12 — targeted discovery is not whole-set completeness

A resolver may load exactly the roots affected by requested targets. That proves the discovery obligation for those targets; it does not prove every object in the set is represented.

Declare:

```csharp
scope.Complete(associations);
```

only when the operation genuinely has authoritative coverage of the whole set.

---

## Recipe 13 — safe discovery superset

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

An authoritative superset is acceptable. Under-fetch is not.

---

## Recipe 14 — retargeting overlay

Database relationship:

```text
Association X -> Source A
```

Current tracked relationship:

```text
Association X -> Source B
```

Consistency planning must honor current tracked state. A database discovery query may still reflect the stored relationship, so the tracked overlay determines effective consumers for the pending operation.

---

## Recipe 15 — closed-world scope

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

This is suitable for bounded aggregates, complete batches, and test fixtures where whole-set authority is real.

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

Validation mode evaluates affected enforced invariants without synchronizing configured mirrors.

The default recalculation-and-validation flow synchronizes affected `MaterializeTo` representations before persistence.

---

## Recipe 17 — manual transaction and durable policy work

```csharp
var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

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

The first save is appropriate only when semantic store-generated values must become final before planning.

---

## Recipe 18 — replacing manual maintenance orchestration

Typical manual flow:

```text
collect changed endpoint IDs
query affected roots
union tracked roots
load required references
recalculate
write mirror
```

Declarative flow:

```text
DependsOn / From declarations
Select / semantic aggregate
MaterializeTo
DiscoverConsumers where needed
consistent save boundary
```

Keep parity tests until affected roots, logical calculations, persisted outcomes, and unloaded-consumer behavior are proven equivalent.
