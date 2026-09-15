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

A materialized target is a persisted mirror, never an input to the consistency graph. Configuration rejects
keys, generated properties, relation/derived/invariant dependencies, and projected-selector members.
The source must be the same object instance tracked by the saving `DbContext`. Writes use EF value comparison
and skip equal values. If a mirror setter fails before SQL, earlier adapter-owned mirror writes are restored;
caller-owned POCO mutations are not rolled back.

## Transactions and generated keys

Convenience saves and the interceptor reject ambient or externally controlled transactions. They also
reject an added entity when its consistency identity is store-generated. Use the policy-aware manual workflow:

```csharp
var scope = new ConsistencyScope()
    .Complete(orderLines)
    .Complete(allocations);

var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var transaction = await db.Database.BeginTransactionAsync();

// Required when generated values must become final before planning.
await db.SaveChangesAsync();

var plan = work.PrepareAndPlan();
if (plan is not null)
    PersistDurablePolicyWork(db, plan.Result.GetDurablePolicyWork());

await db.SaveChangesAsync();
await transaction.CommitAsync();

work.CommitAfterDatabaseCommit();
work.Dispatch();
```

Capture validates mappings and scope and preserves relationship evidence before the first save.
`PrepareAndPlan` applies the same enforcement and materialization policy as convenience saves but never
executes SQL. For pending additions, `Complete(set)` asserts completeness of the planned final boundary:
current runtime coverage plus lifecycle changes captured by this work item. A stable application-key flow
can prepare before its first SQL command and may require only one database save.

The application owns transaction commit and rollback. After a planning or persistence failure, roll back
and discard/reload the context. Call `CommitAfterDatabaseCommit` only after durability, then dispatch.
The low-level `ChangeTrackerAdapter.CaptureUnitOfWork` and `ConsistencyUnitOfWork.PlanDetailed` remain
policy-agnostic runtime primitives; they do not apply `Enforce`, `Materialize`, or `ConsistencyScope`.

A database rollback does not restore EF's in-memory generated keys or tracking snapshots. Discard the
context, or clear and reload authoritative state; do not guess-reset keys and reuse the prepared plan.

## Failure and recovery boundaries

| Failure point | Database | Runtime | Required action |
|---|---|---|---|
| Scope failure at capture | Not called | Unchanged | Seed/maintain coverage and provide `ConsistencyScope` |
| Generated key not final at manual plan | Uncommitted/rollbackable | Unchanged | Save for final keys, or discard a faulted work item |
| Planning, invariant, or mirror write | Not durable | Unchanged | Roll back and discard/reload as appropriate |
| EF command | Failed/rolled back by EF | Unchanged | Reload/discard tracked state as appropriate |
| Runtime install after DB success | Durable | Pre-commit; synchronization exception | Reconcile/rebuild runtime; do not retry SQL blindly |
| Dispatch after DB/runtime commit | Durable | Committed | Retry resumable policy dispatch |

After an EF command failure, deterministic mirror values may remain on the still-mutated tracked graph.
Reload or discard that graph when the operation is abandoned. Planning rollback restores runtime-owned
state only, not caller-owned domain changes.

See the executable [SQLite consistency sample](../samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Program.cs).
