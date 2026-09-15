# EF Core consistency

`Raffinert.Consistency.EntityFrameworkCore` hosts persistence policy around the core binding-plan protocol.
Core determines affected dependency consequences; the adapter decides which invariant states block SQL
and which derived values are persisted as mirrors.

## Stable-key workflow

Configure the exact object sets, enforced invariants, and persisted mirrors:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Map(fulfillments)
    .Enforce(availabilityInvariant)
    .Materialize(availableQuantity, x => x.AvailableQuantity);

var scope = new ConsistencyScope()
    .Complete(lines)
    .Complete(fulfillments);

await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope },
    cancellationToken);
```

`Validate` evaluates affected configured invariants but writes no mirrors.
`RecalculateAndValidate` (the default) also evaluates and writes affected configured derived values.
Only an explicitly `Enforce`d evaluation whose state is `Violated` blocks persistence. `Unknown`, `Dirty`,
`Invalid`, an unenforced violation, and `ScheduleRepair` policy data do not automatically block SQL.

The ordering is:

```text
validate authoritative scope -> snapshot tracked mutations -> prepare binding plan -> evaluate configured affected values
-> reject or write sink mirrors -> SaveChanges -> install exact runtime plan -> dispatch
```

The interceptor provides the same ordering for ordinary `SaveChanges`/`SaveChangesAsync`; it is a thin
host over the same coordinator.

## Authoritative consistency scope

`ConsistencyScope` is an application-supplied assertion that the runtime contains every object in each
declared object set within the consistency boundary for the operation. Raffinert does not query the
database to verify that assertion and never auto-loads missing graph state.

Cross-object persistence policies fail closed. A relation-backed `Enforce` requires both relation sets to
be complete. A relation-backed `Materialize` requires the same proof in `RecalculateAndValidate` mode.
Missing declarations produce `IncompleteConsistencyScopeException` before semantic planning, mirror
setters, or SQL. `Validate` mode does not require coverage solely for a configured materialization because
that mode does not calculate or write mirrors.

Source-local computations read only the known source, so they need no complete-set declaration:

```csharp
var positive = model.Derived(lines).Compute(line => line.Quantity >= 0);
var invariant = model.Invariant(lines).Using(positive).Must((_, value) => value);

var mappings = new ConsistencyEfCoreMappings().Map(lines).Enforce(invariant);
await db.SaveChangesConsistentlyAsync(runtime, mappings);
```

A relation aggregate requires both sides, even when only one side appears to supply the aggregate values:

```csharp
var scope = new ConsistencyScope()
    .Complete(orderLines)   // reverse routing from target-side changes
    .Complete(allocations); // all aggregate contributors
```

A projected dependency additionally requires the projected consumer set. If allocation computations
select an `OrderLine` and consume its relation-backed total, the scope must declare `orderLines`, the
relation target set, and `allocations`; otherwise reverse projection cannot prove that it found every
consumer.

`mappings.Map(allocations)` means “translate tracked `Allocation` changes into this object set.” It does
not mean all allocation rows are loaded and is never completeness proof. The host must seed and maintain
authoritative runtime coverage and state that fact with `scope.Complete(allocations)`. Whole-set coverage
is intentionally coarse in this version; key- or partition-scoped completeness is not implemented.

## Materialization contract

A materialized target is a persisted mirror, never an input to the Relations graph. Configuration rejects
keys, generated properties, relation/derived/invariant dependencies, and projected-selector members.
The source must be the same object instance tracked by the saving `DbContext`. Writes use EF value comparison
and skip equal values. If a mirror setter fails before SQL, earlier adapter-owned mirror writes are restored;
caller-owned POCO mutations are not rolled back.

## Transactions and generated keys

Convenience saves and the interceptor reject ambient or externally controlled transactions. They also
reject an added entity when its Relations identity is store-generated. Use the manual workflow:

```text
capture relationship evidence if needed
-> begin database transaction
-> first SaveChanges (obtain final keys and relationship fixup)
-> prepare and PlanDetailed with affected evaluations
-> if enforced violation: rollback and discard/reload the DbContext
-> otherwise write mirrors/outbox, DetectChanges, and SaveChanges again if needed
-> commit database transaction
-> Commit(plan)
-> Dispatch
```

The automatic scope gate belongs to the convenience-save/interceptor coordinator and therefore does not
wrap this low-level workflow. Before treating a manual `PlanDetailed` result as an authoritative
cross-object persistence decision, the application must independently establish the same complete runtime
coverage. `PlanDetailed` itself remains an EF-agnostic runtime primitive and does not query a database.

A database rollback does not restore EF's in-memory generated keys or tracking snapshots. Discard the
context, or clear and reload authoritative state; do not guess-reset keys and reuse the prepared plan.

## Failure and recovery boundaries

| Failure point | Database | Runtime | Required action |
|---|---|---|---|
| Planning, invariant, or mirror write | Not called | Unchanged | Correct mutations/configuration and retry |
| EF command | Failed/rolled back by EF | Unchanged | Reload/discard tracked state as appropriate |
| Runtime install after DB success | Durable | Pre-commit; synchronization exception | Reconcile/rebuild runtime; do not retry SQL blindly |
| Dispatch after DB/runtime commit | Durable | Committed | Retry resumable policy dispatch |

After an EF command failure, deterministic mirror values may remain on the still-mutated tracked graph.
Reload or discard that graph when the operation is abandoned. Planning rollback restores runtime-owned
state only, not caller-owned domain changes.

See the executable [SQLite consistency sample](../samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Program.cs).
