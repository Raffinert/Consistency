# EF Core consistency

`Raffinert.Consistency.EntityFrameworkCore` hosts persistence policy around the core binding-plan protocol.
Core determines affected dependency consequences; the adapter decides which invariant states block SQL
and which derived values are persisted as mirrors.

## Stable-key workflow

Configure the exact object sets, enforced invariants, and persisted mirrors:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Enforce(availabilityInvariant)
    .Materialize(availableQuantity, x => x.AvailableQuantity);

await db.SaveChangesConsistentlyAsync(runtime, mappings, cancellationToken: cancellationToken);
```

`Validate` evaluates affected configured invariants but writes no mirrors.
`RecalculateAndValidate` (the default) also evaluates and writes affected configured derived values.
Only an explicitly `Enforce`d evaluation whose state is `Violated` blocks persistence. `Unknown`, `Dirty`,
`Invalid`, an unenforced violation, and `ScheduleRepair` policy data do not automatically block SQL.

The ordering is:

```text
snapshot tracked mutations -> prepare binding plan -> evaluate configured affected values
-> reject or write sink mirrors -> SaveChanges -> install exact runtime plan -> dispatch
```

The interceptor provides the same ordering for ordinary `SaveChanges`/`SaveChangesAsync`; it is a thin
host over the same coordinator.

## Scope and materialization

The adapter does not query or auto-load missing database graph state. Database-global correctness requires
the application to seed and maintain the authoritative scope needed by the model. A tracked-only subset
does not by itself prove a database-global invariant.

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
