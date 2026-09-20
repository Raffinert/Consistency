# EF Core consistency

`Raffinert.Consistency.EntityFrameworkCore` hosts persistence policy around the core binding-plan protocol.
Core determines affected dependency consequences; the adapter decides which invariant states block SQL
and which derived values are persisted as mirrors.

## Injected runtime and one-call materialization

Register the compiled model and EF mappings with the application container:

```csharp
services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
services.AddRaffinertConsistency<AppDbContext>(compiledModel, mappings);
services.AddScoped<LinkService>();
```

Only one `AddRaffinertConsistency<TDbContext>` registration is supported per `IServiceCollection`.
This creates one scoped `ConsistencyRuntime` and EF session for each `AppDbContext`. `IConsistencyRuntime`
resolves to that same scoped concrete runtime instance bound to the EF consistency session/interceptor.
Mapped entities already tracked when the runtime is resolved, and entities tracked later by queries, are
admitted to the runtime baseline automatically. The runtime injected into an application service is the
same instance used by the save interceptor.

Resolve or inject that runtime before application code mutates consistency-relevant tracked state. First
binding rejects pending scalar, navigation, or collection changes only when the changed member is used by a
configured consistency key, dependency, invariant, relation, or projected selector. Mapped additions and
removals remain conservative because lifecycle changes cannot be admitted as a baseline. Querying clean
entities through the context before resolving the runtime remains supported. Ordinary mapped properties
unused by the consistency model, and pending changes on entities outside its mappings and dependency graph,
do not block first binding.

The application-facing service remains ordinary EF code:

```csharp
public sealed class LinkService(AppDbContext db, IConsistencyRuntime consistency)
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

Use `IConsistencyRuntime` for ordinary application code that only evaluates or materializes values. Inject
the concrete `ConsistencyRuntime` for advanced engine operations such as explicit mutation planning,
diagnostics, or manual runtime orchestration.

Use this rule:

```text
Need a materialized property before SaveChanges?  runtime.Materialize(entity)
Only saving?                                         db.SaveChanges / SaveChangesAsync
```

The explicit call prepares a binding plan, evaluates the affected logical graph, and writes only the
requested object's physical mirrors. It does not commit runtime state or dispatch repairs. SaveChanges
reuses an unchanged pending plan, rebuilds it if tracked semantic inputs changed, writes any remaining
persisted mirrors, and installs the plan only after SQL succeeds. Library-owned mirror writes are excluded
from the semantic mutation evidence used for that reuse decision.

Before SQL succeeds, committed runtime navigation and projection indexes remain at the durable baseline,
even though the pending plan evaluates current tracked relationships and may write requested mirrors.
A failed SQL save leaves those committed indexes unchanged and discards the pending plan; a retry prepares
a fresh plan. Baseline admission and query-fixup stabilization do not advance the runtime version or dispatch
repair/policy work.

Pending plans are valid only for the semantic runtime state and tracked baseline/coverage state against
which they were prepared. Clean tracking admission and relationship-fixup stabilization complete before the
first pending plan is created. Once that baseline is stable, `Materialize` followed by ordinary `SaveChanges`
reuses the plan when no further semantic mutation or mapped tracking occurs. Tracking another mapped baseline
object invalidates an older pending plan; the adapter restores its physical mirror writes, stabilizes the new
baseline, and prepares a fresh plan before SQL.

## Stable-key workflow

Configure the exact object sets, enforced invariants, and persisted mirrors:

```csharp
availableQuantity.MaterializeTo(x => x.AvailableQuantity);

var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Map(fulfillments)
    .Enforce(availabilityInvariant);

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

When an enforced violation rejects the save, `ConsistencyInvariantViolationException.Violations` contains only
the enforced violated evaluations and `RepairRequests` contains the matching structured requests for those
evaluations. A violated invariant without `RepairWhenViolated()` still rejects but contributes no repair request.
The exception is produced from the reversible plan: SQL is not executed, runtime version does not advance, and
the caller may inspect repair data without installing the rejected plan.

The allocation dogfood demonstrates an internal experimental rejected-plan query flow for application-owned repair.
The scoped EF session retains the rejected prepared plan and the tracked mutation fingerprint; application code
can obtain a read-only proposed-state view for `Evaluate`, `Related`, and invariant queries. The view represents
the final tracked object membership and current proposed CLR values, while the committed runtime remains at its
durable baseline. Preview detects runtime-plan drift and prepared-mutation drift. The EF rejected-preview integration
also detects changes visible to the adapter's relevant tracked-state fingerprint, including represented scalar,
navigation, add, and remove changes. The dependency-free core cannot detect arbitrary POCO mutations that were never
reported as changes; reading a current CLR value is not a validated snapshot guarantee. After a repair mutation, the
application obtains a fresh view/re-plans before making further consistency queries. The view does not materialize,
dispatch, persist, or select a replacement, and it is not a stable public API.

Rejected-preview validation deliberately remains authority-aware. It performs persistence-policy validation and
external consumer discovery before comparing fingerprints, both when the preview is created and before each read.
A tracked-state-only fingerprint is not sufficient: a consumer row can appear in the database without changing the
already tracked graph, yet that row changes the proposed evaluation closure. Final save also repeats authoritative
validation and discovery; preview validation is not a substitute for the persistence boundary.

Repeating authority discovery before preview reads strengthens stale-state detection, but it does not establish an
atomic database snapshot or eliminate external races. Another transaction can change authoritative database state
after validation completes. Final `SaveChanges` planning and enforcement remains the durability boundary that must
revalidate authoritative consistency requirements.

Tracked navigation capture uses one stable entry snapshot plus lazy, metadata-aware indexes for current and
original principal keys and foreign keys. It does not issue database queries or lazy-load navigations. This removes
the former tracked-entry rescan per reference/collection while preserving composite and nullable keys,
dependent/principal navigation direction, Added/Deleted evidence, ambiguity failures, and collection-reset
deduplication. In the allocation dogfood, the unchanged 10,000-allocation rejected-preview workload fell from
8,952.954 ms rejection plus 22,400.720 ms repair reads and 90,287,397,064 allocated bytes to 40.342 ms plus
96.279 ms and 384,347,784 bytes after seventh-pass capture hardening. The six validations (creation plus five reads)
are retained because they are now
practical and provide the strongest stale-state guarantee.

Optional composite foreign keys use EF relationship null semantics: if any component is null, no relationship is
resolved or indexed. Complete relationship tuples are compared with each principal key property's EF
`GetKeyValueComparer()`, including structural array equality and explicitly configured comparers for converted key
types. Policy-aware capture reuses that same tracked snapshot for generated-FK fixup evidence, locating intended
principals by entity reference without a per-property scan of the change tracker.

Relevant tracked scalar, navigation, add, and remove changes make a retained preview stale. Properties outside the
compiled consistency semantics are filtered out and do not. Arbitrary unreported mutations in dependency-free core
POCOs remain outside the guarantee because core has no general mutation observer.

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
var positive = model.Derived(lines).Select(line => line.Quantity >= 0);
var invariant = model.Invariant(lines).From(positive).Must((_, value) => value);

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

Ordinary navigation traversal also requires consumer coverage:

```csharp
var price = model.Derived(lines).Select(line => line.Product.Price);
var scope = new ConsistencyScope().Complete(lines);
```

When `Product.Price` changes, reverse navigation must discover every consuming line. The terminal product
does not need completeness merely for that path; the risk is a missing line owner, so `lines` receives
`NavigationConsumerCoverage`.

For an eligible direct reference-navigation dependency, `DiscoverConsumers` is an operation-scoped, targeted
alternative to completing the consumer set. It does not make that object set complete, and the resolver remains
responsible for returning a complete query result and loading every reference needed for evaluation. Raffinert does
not generate queries or `Include` paths; unsupported navigation shapes remain fail-closed and require
`ConsistencyScope.Complete(set)`. Discovered existing roots are recorded as structural `CoverageAdmission`, not
domain `ObjectAdded` mutations. Database/query isolation and concurrency remain host responsibilities, and database
changes invisible to EF or Raffinert require later reconciliation, rebuild, or publication.

## Materialization contract

A materialized target is a persisted mirror, never an input to the consistency graph. Configuration rejects
keys, generated properties, relation/derived/invariant dependencies, and projected-selector members.
The source must be the same object instance tracked by the saving `DbContext`. Writes use EF value comparison
and skip equal values. If a mirror setter fails before SQL, earlier adapter-owned mirror writes are restored;
caller-owned POCO mutations are not rolled back.

The declaration places the descriptor in Core:

```csharp
var priceRate = model.Derived(links)
    .DependsOn(x => x.InvoiceLine.Price, x => x.PurchaseOrderLine.Price)
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

Policy-aware save/capture validation automatically consumes compiled Core descriptors for mapped source sets.

## Transactions and generated keys

Convenience saves and the interceptor reject ambient or externally controlled transactions. All convenience
paths, including `SaveChangesAndApply`, fail closed when a semantic value generated by INSERT or UPDATE is not
final before SQL. Use the policy-aware manual workflow:

```csharp
var scope = new ConsistencyScope()
    .Complete(orderLines)
    .Complete(allocations);

var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var transaction = await db.Database.BeginTransactionAsync();

// Required when INSERT/UPDATE generated values must become final before planning.
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

The captured evidence keeps original scalar and FK/navigation state even when EF later replaces a temporary generated
principal key and fixes up the dependent FK. Planning accepts that new-value transition only when it can prove
the same tracked relationship and matching final key component(s). Arbitrary caller changes after capture
remain invalid under strict new-value validation. For a generated UPDATE semantic input, capture retains its
pre-SQL value and synthesizes the transition to the final provider value only after the tracked database
operation succeeds. Any store-generated property used as a set key, relation/derived/invariant dependency,
or projected selector must be final before planning; unused generated properties do not force two saves.
Store-generated UPDATE of an existing Raffinert identity is unsupported.

Tracked nested navigation targets do not require their own Raffinert object-set mapping merely to route
scalar dependency changes. Additions and removals still require `Map(...)`, because lifecycle changes belong
to a specific object set. The adapter never auto-loads an untracked target.

## Store-side referential actions

Scope completeness proves data coverage in the runtime; it does not prove mutation coverage for SQL-side
effects. Every authoritative/call-and-save path inspects actual tracked deletions before planning or SQL. It
rejects EF-declared database `Cascade` and `SetNull` paths, including transitive cascade paths, when they can
reach consistency-relevant rows that may be absent from the tracker. The dedicated exception identifies the
deleted type, affected type, action, and entity-type path. Unrelated cascades and owned state that is not an
independent Raffinert object set remain supported.

For consistency-managed aggregates, prefer `ClientCascade` / `ClientSetNull` and explicitly track all affected
dependents. `Restrict` / `NoAction` are appropriate when domain rules require explicit deletion. With a client
behavior, missing dependents cause the relational command to fail instead of succeeding with an invisible
runtime side effect. Database `Cascade` is not universally prohibited; it is rejected only when a reachable
store-side effect can touch Raffinert-managed state.

Automatic EF evidence includes tracked lifecycle/scalar/reference/collection mutations, proven generated FK
fixup, and proven same-entry store-generated INSERT/UPDATE values. Database triggers that mutate other rows,
raw SQL, `ExecuteUpdate` / `ExecuteDelete`, bulk libraries that bypass `ChangeTracker`, other application
processes or pods, and manual DBA/data-fix writes are outside automatic synchronization. The application must
emit exact mutations using authoritative old/new evidence or rebuild/reseed/reconcile the runtime before using
it again. EF trigger metadata alone does not describe arbitrary trigger side effects.

The application owns transaction commit and rollback. After a planning or persistence failure, roll back
and discard/reload the context. Call `CommitAfterDatabaseCommit` only after durability, then dispatch.
The low-level `ChangeTrackerAdapter.CaptureUnitOfWork` and `ConsistencyUnitOfWork.PlanDetailed` remain
policy-agnostic runtime primitives; they do not apply `Enforce`, `Materialize`, or `ConsistencyScope`. A caller
that owns SQL through those primitives also owns the external/store-side-effect boundary.

A database rollback does not restore EF's in-memory generated keys or tracking snapshots. Discard the
context, or clear and reload authoritative state; do not guess-reset keys and reuse the prepared plan.

## Failure and recovery boundaries

| Failure point | Database | Runtime | Required action |
|---|---|---|---|
| Scope failure at capture | Not called | Unchanged | Seed/maintain coverage and provide `ConsistencyScope` |
| Generated semantic value not final at manual plan | Uncommitted/rollbackable | Unchanged | Save for final INSERT/UPDATE values, or discard a faulted work item |
| Planning, invariant, or mirror write | Not durable | Unchanged | Roll back and discard/reload as appropriate |
| EF command | Failed/rolled back by EF | Unchanged | Reload/discard tracked state as appropriate |
| Runtime install after DB success | Durable | Pre-commit; synchronization exception | Reconcile/rebuild runtime; do not retry SQL blindly |
| Dispatch after DB/runtime commit | Durable | Committed | Retry resumable policy dispatch |

After an EF command failure, deterministic mirror values may remain on the still-mutated tracked graph.
Reload or discard that graph when the operation is abandoned. Planning rollback restores runtime-owned
state only, not caller-owned domain changes.

See the executable [SQLite consistency sample](../samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Program.cs).
