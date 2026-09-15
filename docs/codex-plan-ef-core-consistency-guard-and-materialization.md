# Codex Implementation Plan — EF Core Consistency Guard and Derived Materialization

Baseline: `873bff99c343166a50830fe59d5c5d8a645b53d0`

Tasks: **100–108**

This plan is written for a weaker coding agent. Follow it mechanically. Do not redesign the library while implementing it.
Do not invent adjacent features because they appear convenient. Add a failing test before changing an established runtime
contract.

The previous pre-commit guard waves are complete. The current repository already proves the low-level binding-plan path:

```text
mutated domain / EF tracked state
        ↓
CaptureUnitOfWork
        ↓
Prepare
        ↓
PlanDetailed(..., PlannedInvariantEvaluationMode.Affected)
        ↓
affected invariant evaluations + exact forward patch
        ↓
caller may reject durability
        ↓
Commit(plan) installs exact planned runtime state without semantic rerun
```

The new goal is to turn that mechanism into a useful EF Core consistency integration with two explicit modes:

```text
Validate
    detect tracked changes
    -> plan affected consistency state
    -> evaluate configured enforced invariants
    -> violation: abort before SaveChanges
    -> valid: persist, install exact runtime plan, dispatch

RecalculateAndValidate
    detect tracked changes
    -> plan affected consistency state
    -> evaluate configured enforced invariants
    -> violation: abort before SaveChanges
    -> valid: write affected configured derived values into tracked persisted mirror properties
    -> persist user changes + derived mirror changes together
    -> install exact runtime plan, dispatch
```

The key architectural rule is:

> The core package owns dependency semantics and planned evaluation. The EF Core package owns persistence policy.

Core must never learn what `DbContext`, `SaveChanges`, a database transaction, an EF property, or an interceptor is.
The EF package must not reimplement dependency propagation or invariant source selection.

---

# 0. Why this integration exists

EF Core users repeatedly build `SaveChangesInterceptor` logic for consistency work and hit the same categories of problems:

- post-save entity state no longer tells them what was Added/Modified (`dotnet/efcore#32373`);
- navigation changes are not always represented by the intuitive `ReferenceEntry.IsModified` signal, including the open
  owned-reference-to-null bug (`dotnet/efcore#36368`);
- store-generated keys tempt users into recursive `SaveChanges` calls from `SavedChanges` (`dotnet/efcore#37165`);
- related-entity maintenance is difficult to place in the same transaction without loading extra entities or manually
  controlling transactions/savepoints (`dotnet/efcore#33845`).

Relevant public references:

- <https://github.com/dotnet/efcore/issues/32373>
- <https://github.com/dotnet/efcore/issues/36368>
- <https://github.com/dotnet/efcore/issues/37165>
- <https://github.com/dotnet/efcore/issues/33845>
- <https://learn.microsoft.com/ef/core/saving/transactions>

Raffinert.Relations should not become another general trigger framework. Its advantage is that the dependency graph already
knows which derived values and invariants are affected. The EF integration should consume that graph rather than ask users
to write another set of imperative `if entity X changed then update Y` handlers.

---

# 1. Non-negotiable architecture boundary

## Core (`Raffinert.Relations`) may contain

- relation/derived/invariant definitions;
- dependency analysis and affected-source selection;
- planned final-state evaluation;
- planned affected derived-value evaluation;
- planned affected invariant evaluation;
- binding forward patches;
- deterministic ordering and causal information;
- source/definition identity;
- generic runtime errors such as computation/invariant exceptions.

## Core must NOT contain

Do not add any of these to the core package:

```text
SaveChanges
DbContext
EntityEntry
EF property metadata
transaction/savepoint policy
RejectSave / RejectSaveChanges
MaterializeToEfProperty
EF-specific interceptor concepts
```

In particular, do **not** add:

```csharp
invariant.Enforce();
derived.MaterializeTo(x => x.SomeProperty);
```

to the core API.

`Enforce` means “block persistence” and is host policy, not dependency semantics. A violated invariant may mean HTTP 400,
message rejection, deferred repair, diagnostics, or EF save rejection depending on the host.

## EF Core package may contain

- `ChangeTracker` capture;
- EF entity/object-set mapping;
- invariant -> persistence rejection mapping;
- derived -> persisted property mapping;
- `SaveChanges` convenience orchestration;
- `SaveChangesInterceptor` integration;
- generated-key ordering rules;
- external transaction restrictions;
- EF-specific exceptions and reconciliation guidance.

---

# 2. Materialization semantic rule: persisted mirror, not new source of truth

The first version of automatic recalculation is intentionally **sink-only**.

Example:

```csharp
var receivedQuantity = model.Derived(poLines)
    .Using(receipts)
    .Compute((line, matches) => matches.Sum(x => x.Quantity))
    .Named("received-quantity");

var availableQuantity = model.Derived(poLines)
    .Using(receivedQuantity)
    .Compute((line, received) => line.OrderedQuantity - received)
    .Named("available-quantity");
```

The EF adapter may map:

```csharp
.Materialize(availableQuantity, x => x.AvailableQuantity)
```

but downstream Relations definitions must depend on the `availableQuantity` derived definition, **not** on
`PurchaseOrderLine.AvailableQuantity`.

The materialized CLR/EF property is a persistence mirror of the derived value. Writing that mirror must not create another
semantic propagation wave.

Why this restriction exists:

```text
without restriction:
Derived A
  -> write Property B
  -> Property B is itself a dependency
  -> recompute C
  -> possibly write another property
  -> need a generic mutation fixpoint engine, cycle detection, rollback of host object writes, and convergence semantics
```

That is a separate future feature. Do not implement it in Tasks 100–108.

The EF configuration validator must reject a materialization target that is used as a tracked dependency by any compiled
relation, derived value, invariant, projected selector, or object-set key in the same Relations model.

---

# 3. Public API direction

The exact namespaces must follow the existing package conventions. Use these names unless an existing type collision makes
one impossible.

## Core additions

Add the minimum generic planning surface required for materialization:

```csharp
public enum PlannedDerivedEvaluationMode
{
    None = 0,
    Affected = 1
}

public sealed record PlannedDerivedEvaluation(
    int DerivedId,
    object Source,
    object? Value,
    DerivedValueState State)
{
    public string? DefinitionKey { get; init; }
    public SourceIdentity? SourceIdentity { get; init; }
}
```

Extend `PreparedImpactPlan`:

```csharp
public IReadOnlyList<PlannedDerivedEvaluation> DerivedEvaluations { get; }
```

Add a non-breaking `PlanDetailed` overload instead of changing existing signatures:

```csharp
public PreparedImpactPlan PlanDetailed(
    PreparedMutation prepared,
    RuntimeImpactDetailLevel detailLevel,
    PlannedInvariantEvaluationMode invariantEvaluationMode,
    PlannedDerivedEvaluationMode derivedEvaluationMode);
```

Add the equivalent `RelationUnitOfWork.PlanDetailed(...)` overload in the EF package.

Keep the new surface in `PublicAPI.Unshipped.txt` for this roadmap.

Do not move any new API to Shipped in Tasks 100–108.

## EF additions

Target shape:

```csharp
public enum RelationEfCoreSaveBehavior
{
    Validate = 0,
    RecalculateAndValidate = 1
}

public sealed class RelationEfCoreConsistencyOptions
{
    public RelationEfCoreSaveBehavior SaveBehavior { get; init; }
        = RelationEfCoreSaveBehavior.RecalculateAndValidate;

    public RuntimeImpactDetailLevel DetailLevel { get; init; }
        = RuntimeImpactDetailLevel.Summary;
}
```

Configuration object:

```csharp
var mappings = new RelationEfCoreMappings()
    .Map(poLines)
    .Map(receipts)
    .Materialize(availableQuantity, x => x.AvailableQuantity)
    .Enforce(availabilityMustBeNonNegative);
```

Required methods conceptually:

```csharp
RelationEfCoreMappings Map<TEntity>(
    ObjectSet<TEntity> set,
    Func<EntityEntry<TEntity>, bool>? selector = null)
    where TEntity : class;

RelationEfCoreMappings Materialize<TSource, TValue>(
    Derived<TSource, TValue> derived,
    Expression<Func<TSource, TValue>> property)
    where TSource : class;

RelationEfCoreMappings Enforce<TSource>(Invariant<TSource> invariant)
    where TSource : class;
```

Do not require `.Named(...)` merely to use `.Enforce(...)` or `.Materialize(...)` in-process. The EF package is a first-party
adapter and may use core internals for handle identity.

Add:

```csharp
[assembly: InternalsVisibleTo("Raffinert.Relations.EntityFrameworkCore")]
```

in the core package rather than adding public numeric IDs to `Derived<TSource,TValue>` or `Invariant<TSource>` just for the
adapter.

Convenience save API target:

```csharp
context.SaveChangesConsistently(runtime, mappings, options);
await context.SaveChangesConsistentlyAsync(runtime, mappings, options, cancellationToken);
```

Interceptor target:

```csharp
new RelationConsistencySaveChangesInterceptor(runtime, mappings, options)
```

The explicit save API and interceptor must share one internal coordinator. Do not implement the behavior twice.

---

# 4. Hard constraints for the implementing agent

1. Preserve every currently shipped public API signature.
2. Keep all Tasks 100–108 public additions in `PublicAPI.Unshipped.txt`.
3. Core remains EF-agnostic.
4. EF code must reuse `Prepare` / `PlanDetailed` / `Commit(plan)`; do not create a parallel consistency engine.
5. Do not scan every derived source or every invariant source when `Affected` is requested.
6. `PlanDetailed` remains reversible: return or exception leaves runtime version/state unchanged.
7. `Commit(plan)` must not rerun semantic code captured by the plan.
8. Planned derived evaluation may execute a requested affected computation during planning; committing the plan must not
   execute it again.
9. A plan may contain violated invariants as data. Only EF `.Enforce(...)` mappings decide which violations reject save.
10. Existing `ScheduleRepair` / immediate policy reactions do not automatically become persistence blockers.
11. Auto-recalculation only writes configured sink-only materialized mirror properties.
12. Do not implement arbitrary repair actions before save.
13. Do not auto-query/load missing database graph state in this roadmap.
14. Do not claim database-global invariant proof unless the runtime has been seeded with the authoritative scope required by
    those definitions.
15. If a materialized source cannot be safely written through the current tracked EF entity instance, fail explicitly. Do
    not silently skip it and do not attach an arbitrary runtime object graph in v1.
16. The interceptor must not commit runtime state before an externally controlled transaction is durable.
17. Therefore the interceptor/convenience path must reject unsupported external/ambient transaction use and direct callers
    to the existing manual unit-of-work workflow.
18. Store-generated-key additions remain a separate explicit manual transaction workflow unless Task 106 proves a safe
    convenience path. Do not fake pre-save validation using temporary/default keys.
19. Never represent unavailable old navigation history as real `null`.
20. Do not publish packages, create tags, create releases, or bump release versions in this roadmap.
21. One logical concern per commit. Run the task-specific tests before proceeding.

---

# Task 100 — Add affected derived evaluation to binding plans in core

## Goal

Allow a host integration to obtain the **actual final value** of an affected derived computation while the reversible
planned final runtime state is installed.

This is generic core functionality. It must not mention EF.

## Primary files

- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- `src/Raffinert.Relations/Runtime/RelationRuntime.cs`
- `src/Raffinert.Relations/Dependencies/DependencyGraphRuntime.cs`
- `src/Raffinert.Relations/Derived/DerivedRuntimeState.cs`
- `src/Raffinert.Relations/PublicAPI.Unshipped.txt`
- new/focused tests under `tests/Raffinert.Relations.Tests/`

## Required behavior

When:

```csharp
runtime.PlanDetailed(
    prepared,
    RuntimeImpactDetailLevel.Summary,
    PlannedInvariantEvaluationMode.None,
    PlannedDerivedEvaluationMode.Affected)
```

is requested:

1. execute the normal reversible binding-plan mutation once;
2. use the already computed dependency impact/touch information to select affected derived `(definition, source)` pairs;
3. do not globally enumerate every registered source;
4. evaluate selected derived values while planned final relation/navigation/dependency state is installed;
5. capture `DerivedId`, `DefinitionKey`, `Source`, `SourceIdentity`, actual `Value`, and resulting `State`;
6. retain the evaluated cache state in the forward patch;
7. restore pre-plan runtime state before returning;
8. later `Commit(plan)` installs the captured state/value without rerunning the computation.

Ordering must be deterministic:

```text
DerivedId
then durable/source identity
then stable encounter order only as final fallback
```

Evaluation must respect dependency topological order so composed values see already-correct upstream planned values.

## Required test matrix

Create `PlannedDerivedEvaluationTests.cs` or the nearest focused file.

Must cover:

```text
Affected source-only derived returns planned final value
Affected relation-backed derived returns planned final value
Affected incremental Sum returns exact final value
Affected composed chain returns downstream final value
Affected diamond returns one deterministic evaluation per definition/source
Projected upstream change evaluates the actual downstream source
Conservative relation propagation evaluates safe affected superset and labels impact precision as before
Unrelated source is not evaluated
None mode performs no eager derived evaluation
Summary and Causal produce identical DerivedEvaluations values
Planning exception restores runtime state
Commit(plan) does not rerun derived computation
Stale plan rejection does not rerun derived computation
Domain drift rejection does not install or rerun evaluation
```

Use counters that throw on an unexpected second computation for the no-rerun cases.

## Acceptance

- no global scan;
- no EF references in core;
- existing planned invariant behavior unchanged;
- new API remains Unshipped;
- net8 and net10 core suites pass.

## Commit

```text
feat: add affected derived evaluation to binding plans
```

Run:

```bash
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0
```

---

# Task 101 — Add the EF consistency mapping model and validate configuration

## Goal

Introduce persistence policy configuration without changing core domain semantics.

## Primary files

- new `src/Raffinert.Relations.EntityFrameworkCore/RelationEfCoreMappings.cs`
- `src/Raffinert.Relations/Properties/AssemblyInfo.cs`
- `src/Raffinert.Relations.EntityFrameworkCore/PublicAPI.Unshipped.txt`
- focused EF tests

## Mapping responsibilities

`RelationEfCoreMappings` owns three categories:

```text
ObjectSet mapping      -> existing ChangeTracker mutation capture
Enforced invariant     -> a violated planned evaluation blocks EF persistence
Materialized derived   -> a planned derived value is copied to a persisted CLR property
```

Reuse/delegate to the existing `RelationUnitOfWorkMappings` for object-set resolution instead of duplicating that logic.

## Materialization validation

At configuration validation / first use reject all of these:

```text
target expression is not a direct writable property
property source type differs from derived source type
property value type is incompatible with derived TValue
property is an EF primary/alternate key member
property is store-generated/computed by EF/database
same property has two materialization mappings
same derived/property mapping registered twice ambiguously
property is a Relations dependency anywhere in the compiled model
property participates in a projected selector
property participates in an object-set key
```

The final two groups enforce the sink-only rule.

Use core internals through `InternalsVisibleTo`; do not add public `DefinitionId` properties to handles solely for this.

## Enforce validation

`Enforce(invariant)` stores the exact invariant definition identity. It must work without `.Named(...)`.

At plan filtering time, only violations for configured enforced invariants block save.

Example:

```text
Invariant A = Violated, configured Enforce -> reject
Invariant B = Violated, not configured Enforce -> do not reject merely because plan.HasInvariantViolations is true
Invariant C = ScheduleRepair and violated, not Enforce -> preserve existing post-commit policy semantics
```

## Required tests

```text
Enforce_accepts_unnamed_invariant_handle
Materialize_accepts_direct_writable_scalar_property
Materialize_rejects_key_property
Materialize_rejects_store_generated_property
Materialize_rejects_dependency_source_property
Materialize_rejects_projected_selector_member
Materialize_rejects_duplicate_target_mapping
Only_explicitly_enforced_violation_blocks_persistence_policy
Unenforced_violated_invariant_remains_plan_data
```

## Commit

```text
feat: add ef core consistency mappings
```

---

# Task 102 — Harden EF mutation capture for real navigation/change-tracker edge cases

## Goal

Do not build the new high-level integration on the assumption that `NavigationEntry.IsModified` is a complete change
signal.

The current adapter should continue using EF's original/current FK metadata where it is authoritative, but must also cover
relationship transitions represented by added/deleted dependents and owned entities.

## Primary files

- `src/Raffinert.Relations.EntityFrameworkCore/ChangeTrackerAdapter.cs`
- `tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreAdapterTests.cs`
- `tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreSqliteTests.cs`

## Required work

Refactor capture into an internal immutable snapshot step before building `MutationSet`.

Suggested internal shape (do not make public unless required):

```csharp
internal sealed record EfTrackedMutationSnapshot(...);
```

It must preserve enough information for the adapter to survive later EF state reset:

```text
entity reference
entity state
mapped object set if any
modified scalar original/current values
modified FK original/current values
reference old/current target where unambiguous
collection reset/member evidence
added/deleted dependent evidence required to reconstruct a navigation transition
```

Do not store the snapshot as a replacement persistence model. It exists only for one save/unit of work.

## Regression cases

Add an owned optional reference scenario corresponding to the shape of `dotnet/efcore#36368`:

```text
owner.Owned != null
owner.Owned = null
DetectChanges
capture Relations unit of work
```

Required result: the relevant Relations navigation dependency is reported even if `ReferenceEntry.IsModified` is false.

Also prove:

```text
null -> owned instance
principal retarget A -> B
collection add/remove/reset
added dependent
removed dependent
SavedChanges no longer needed to rediscover what changed because snapshot was captured before save
ambiguous old principal still throws rather than fabricating null
```

Do not broaden this task into generic EF auditing.

## Commit

```text
fix: harden ef change snapshot for relationship transitions
```

Run EF tests on net10.

---

# Task 103 — Implement one internal EF consistency coordinator

## Goal

Create one orchestration component used by both convenience save methods and the interceptor.

Do not expose this internal type unless later tasks prove a user-facing session API is necessary.

## Primary files

- new internal file under `src/Raffinert.Relations.EntityFrameworkCore/`
- `ChangeTrackerAdapter.cs` only for reusable capture helpers
- focused tests

## Coordinator input

```text
DbContext
RelationRuntime
RelationEfCoreMappings
RelationEfCoreConsistencyOptions
```

## Stable-key prepare flow

For a normal supported save:

```text
DetectChanges
Capture immutable EF mutation snapshot / RelationUnitOfWork
Prepare(runtime)
PlanDetailed(
    requested detail,
    invariant mode = Affected when any Enforce mappings exist,
    derived mode = Affected only for RecalculateAndValidate with materialization mappings)
Filter InvariantEvaluations to configured Enforce definitions
if any enforced state == Violated -> throw RelationInvariantViolationException before EF SaveChanges
if RecalculateAndValidate -> apply materialized mirror writes from DerivedEvaluations
return pending { unit, binding plan, materialization metadata }
```

Do not treat `Unknown`, `Dirty`, or `Invalid` as `Violated` unless an existing invariant predicate evaluation explicitly
produced `Violated`. Preserve the current domain modeling rule.

## Materialization write rules

For every configured materialized derived definition present in `plan.DerivedEvaluations`:

1. source must be the same instance tracked by the current `DbContext`;
2. source entry must not be `Detached`;
3. read current target property value;
4. compare using EF property's configured/value comparer where practical; otherwise use `EqualityComparer<TValue>.Default`;
5. only assign when value actually differs;
6. assignment must make EF see the property as modified normally;
7. do not manually add a Relations `PropertyChange` for the materialized sink property because configuration validation has
   already proven it is not a Relations dependency;
8. do not run another binding plan solely because a sink mirror changed.

If the affected materialized source is not tracked by this context, throw a dedicated clear exception before database
write. Do not silently skip and do not auto-attach arbitrary graphs in v1.

## Exception types

Add structured EF-specific exceptions, names may be adjusted only for existing naming consistency:

```csharp
RelationInvariantViolationException
RelationMaterializationSourceNotTrackedException
RelationUnsupportedTransactionException
RelationStoreGeneratedKeyRequiresManualWorkflowException
```

`RelationInvariantViolationException` should expose immutable violation records containing at minimum:

```text
InvariantId
DefinitionKey
Source
SourceIdentity
State
```

Do not expose a live mutable plan through the exception.

## Required tests

```text
Validate_mode_rejects_enforced_violation_before_save
Validate_mode_does_not_write_materialized_properties
Recalculate_mode_writes_only_changed_materialized_value
Recalculate_mode_does_not_write_unaffected_materialization
Unenforced_violation_does_not_reject
ScheduleRepair_without_Enforce_does_not_reject
Materialization_source_must_be_tracked_same_instance
Violation_does_not_apply_materialization_write
Planning_exception_does_not_apply_materialization_write
```

## Commit

```text
feat: coordinate ef consistency planning and materialization
```

---

# Task 104 — Add explicit `SaveChangesConsistently` APIs for the safe stable-key path

## Goal

Give applications a straightforward opt-in API without requiring an interceptor.

## Public API target

```csharp
public static int SaveChangesConsistently(
    this DbContext context,
    RelationRuntime runtime,
    RelationEfCoreMappings mappings,
    RelationEfCoreConsistencyOptions? options = null);

public static Task<int> SaveChangesConsistentlyAsync(
    this DbContext context,
    RelationRuntime runtime,
    RelationEfCoreMappings mappings,
    RelationEfCoreConsistencyOptions? options = null,
    CancellationToken cancellationToken = default);
```

If preserving `acceptAllChangesOnSuccess: false` is practical without ambiguity, add explicit overloads mirroring EF Core;
otherwise keep v1 simple and document that this convenience path uses normal EF acceptance. Do not invent a confusing
optional boolean in the middle of existing arguments.

## Required sequence

```text
coordinator.PrepareBeforeSave
  -> may reject before SQL
  -> may apply sink materializations
context.SaveChanges
  -> if DB fails, runtime remains unchanged
unit.Commit(runtime) / Commit(plan)
  -> exact plan install
unit.Dispatch(runtime)
```

If runtime commit fails after database success, preserve the existing `RelationRuntimeSynchronizationException` contract.
Do not blindly retry the database write.

## Unsupported cases in this convenience API

Before any database command, detect and reject:

```text
an externally controlled DbTransaction / CurrentTransaction
an ambient System.Transactions.Transaction
mapped Added entities whose Relations object-set identity depends on a store-generated key not yet final
```

The exception message must point users to the existing manual captured-unit workflow.

Do not pretend `SavedChanges` means an outer transaction is durable.

## SQLite acceptance tests

Use real SQLite for the durability boundary:

```text
violating enforced invariant -> zero DB write, runtime unchanged
valid update -> DB write, runtime advances once, exact plan installed
recalculated materialized column persists in same SaveChanges
DB failure after valid plan -> runtime unchanged
runtime commit failure after DB success -> synchronization exception, DB durable
external transaction -> convenience API rejects before DB write
store-generated mapped addition -> convenience API rejects before DB write
```

## Commit

```text
feat: add consistent save convenience api
```

---

# Task 105 — Add `SaveChangesInterceptor` integration for ordinary saves

## Goal

Allow normal application code:

```csharp
await context.SaveChangesAsync();
```

while retaining the same safe semantics as Task 104 for the supported subset.

## Public type

```csharp
public sealed class RelationConsistencySaveChangesInterceptor : SaveChangesInterceptor
```

Constructor receives:

```text
RelationRuntime
RelationEfCoreMappings
RelationEfCoreConsistencyOptions
```

Do not add DI framework dependencies to the package merely for registration sugar in this task.

## Interceptor lifecycle

`SavingChanges` / `SavingChangesAsync`:

```text
reject unsupported external/ambient transaction
reject unsupported store-generated mapped additions
run shared coordinator prepare
throw before SQL on enforced violation
store pending unit/plan for this DbContext
```

`SavedChanges` / `SavedChangesAsync`:

```text
retrieve pending unit
commit exact binding plan to runtime
remove/complete pending state
then dispatch
```

`SaveChangesFailed` / `SaveChangesFailedAsync` and cancellation path:

```text
discard pending binding plan reference
runtime remains unchanged
```

Do not attempt to roll back the caller's original object mutations. Materialized sink values may remain on tracked objects
after a database failure because they are deterministic consequences of the still-present user changes; document this
explicitly.

## State management

The interceptor may be reused across DbContexts. Do not store one global `_pending` field.

Use a per-context structure such as `ConditionalWeakTable<DbContext, PendingConsistencySave>` or another leak-safe
reference-keyed mechanism. Guard against overlapping/reentrant saves on the same context with a clear exception.

`RelationRuntime` remains not thread-safe; do not add fake internal locking that implies cross-request safety.

## Required tests

```text
Interceptor_validates_before_sql
Interceptor_recalculates_before_sql
Interceptor_commits_runtime_only_after_successful_save
Interceptor_database_failure_discards_pending_plan
Interceptor_runtime_commit_failure_uses_sync_exception
Interceptor_rejects_explicit_transaction
Interceptor_rejects_ambient_transaction
Interceptor_rejects_store_generated_mapped_addition
Interceptor_state_is_isolated_per_DbContext
Interceptor_detects_reentrant_same_context_save
Sync_and_async_paths_have_same_semantics
```

## Commit

```text
feat: add relations savechanges interceptor
```

---

# Task 106 — Integrate store-generated-key workflows without pretending they are pre-save-only

## Goal

Make the new mappings usable with the repository's already documented generated-key manual transaction sequence.
Do **not** force generated keys through Task 104/105 convenience paths.

## Required supported manual flow

Document and test this sequence:

```text
Capture EF unit/session before first save
Begin explicit DB transaction
First SaveChanges
    -> database assigns final keys and EF relationship fixup occurs
Prepare(runtime) only now
PlanDetailed(... Affected invariants + Affected derived values)
Filter configured enforced invariant violations
if violation:
    rollback DB transaction
    do not Commit(plan)
    explicitly reconcile/discard EF tracking state
else:
    apply configured sink materialized values
    second SaveChanges if any materialized/outbox changes exist
    commit DB transaction
    Commit(plan)
    Dispatch
```

The first save may already have executed SQL, but a violating plan must still prevent database **durability** by rolling back
the explicit transaction.

## Important context-state rule

A rollback after the first generated-key save does not magically restore EF's in-memory tracking state. Tests and docs must
show explicit reconciliation. Do not hide this with guessed key resets.

Acceptable guidance:

```text
discard the DbContext / clear and reload authoritative state before retry
```

if that is the safest behavior.

## Required SQLite tests

```text
Generated_key_invalid_plan_rolls_back_database_and_does_not_advance_runtime
Generated_key_valid_plan_materializes_then_commits_database_and_runtime
Generated_key_violation_requires_context_reconciliation_after_rollback
Two_generated_entities_keep_distinct_final_source_identities
Generated_key_plan_commit_does_not_rerun invariant or derived computations
```

## No new magical API requirement

If the existing `RelationUnitOfWork` plus a small internal/shared consistency helper can express this safely, use it.
Do not add a large transaction abstraction merely to shorten the sample.

## Commit

```text
test: prove generated key consistency workflow
```

If a small reusable public helper is genuinely required by failing tests, split the API addition into a separate commit and
explain it in the plan completion notes.

---

# Task 107 — Add a real EF Core consistency sample and document scope/recovery

## Goal

Make the feature understandable without reading architecture internals.

Prefer a new focused sample if extending the current purchase-order console sample would make it confusing:

```text
samples/Raffinert.Relations.EntityFrameworkCore.ConsistencySample
```

Use SQLite, not EF InMemory.

## Required sample domain

Keep it small but demonstrate a real dependency chain:

```text
PurchaseOrderLine.OrderedQuantity
GoodsReceiptLine.Quantity
        ↓
ReceivedQuantity (derived)
        ↓
AvailableQuantity (derived)
        ↓
materialized PurchaseOrderLine.AvailableQuantity column
        ↓
Availability invariant
```

Downstream invariant must depend on the `Derived` handle, not on the materialized property, proving the sink-only model.

## Required scenarios

### Scenario A — Validate rejects

```text
mutate quantity so enforced invariant becomes Violated
SaveChangesConsistently / interceptor path
expect RelationInvariantViolationException
query fresh DbContext
DB unchanged
runtime unchanged
```

### Scenario B — Recalculate and persist

```text
valid receipt change
ReceivedQuantity recalculates
AvailableQuantity recalculates
AvailableQuantity property mirror changes automatically
one supported SaveChanges persists user + mirror changes
runtime plan installs after DB success
```

### Scenario C — violation is not always blocking

Configure an invariant with repair semantics but do not `.Enforce(...)` it.
Show that it remains impact/policy data and does not automatically block persistence.

### Scenario D — generated identity manual transaction

Show the explicit two-phase generated-key workflow and rollback/reconciliation note.

## Documentation changes

Update:

- root `README.md` with a concise EF consistency section;
- `docs/architecture.md` with the persistence-policy boundary;
- add a focused `docs/ef-core-consistency.md` with full workflow tables.

The docs must explicitly state:

```text
Core does not mutate domain objects.
EF materialization is adapter policy.
Materialized targets are sink-only persisted mirrors in v1.
Validation is only as authoritative as the Relations runtime scope supplied by the application.
The integration does not auto-load missing database graph state.
Rejected planning does not roll back caller object mutations.
External transactions and store-generated keys require the documented manual workflow.
Runtime failure after DB durability requires rebuild/reconciliation, not DB retry.
```

## Commit

```text
sample: demonstrate ef core consistency guard and materialization
```

and separately:

```text
docs: document ef core consistency boundary
```

---

# Task 108 — Prove scaling, failure boundaries, and finish the roadmap

## Goal

Prove the feature remains incremental and does not turn every `SaveChanges` into full-model validation/recalculation.

## Benchmark additions

Add focused BenchmarkDotNet scenarios for stable-key planning:

```text
1 affected source among 10k registered sources
1 affected source among 100k registered sources
1 materialized derived definition among several unrelated derived definitions
1 enforced invariant among several unrelated invariants
Validate mode
RecalculateAndValidate mode
Summary detail
Causal detail
```

The important interpretation is not absolute microseconds. Record whether allocations/work scale with the affected wave
rather than the complete Relations population.

It is acceptable that EF `ChangeTracker` enumeration has cost proportional to tracked EF entries. Do not misrepresent that
as a Relations dependency-graph regression. Separate:

```text
EF capture cost
Relations plan/evaluation cost
materialization write cost
```

Write results to:

```text
benchmarks/EfCoreConsistency-Results.md
```

Include date, environment, commit SHA, mean, allocated bytes, and interpretation.

## Final failure matrix

Add/confirm tests for:

```text
relation predicate throws during plan -> no SQL, runtime unchanged
planned derived computation throws -> no SQL, no materialization, runtime unchanged
invariant predicate throws -> no SQL, runtime unchanged
materialized property setter throws -> no SQL, runtime unchanged
EF database command fails -> runtime unchanged
runtime forward-patch install fails after DB success -> DB durable + synchronization exception
post-commit policy dispatch fails -> DB/runtime committed; callback failure does not imply rollback
stale runtime plan -> save path does not execute DB after stale condition is known where validation occurs pre-save
```

For any boundary where DB has already succeeded, state recovery behavior explicitly rather than pretending atomicity can be
restored after the fact.

## Final commands

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Run the executable EF consistency sample in Release as an additional gate.

## Roadmap completion checklist

Do not mark Tasks 100–108 complete unless all are true:

```text
Core contains no EF/persistence policy concepts.
PlannedDerivedEvaluationMode.Affected exists and performs no global source scan.
Commit(plan) does not rerun planned derived computations.
RelationEfCoreMappings supports Map + Enforce + Materialize.
Enforce works for unnamed invariant handles.
Materialize target is validated as sink-only and cannot be a Relations dependency/key/projected selector.
Validate mode never writes materialized properties.
RecalculateAndValidate writes only affected configured mirror values.
Only explicitly Enforced violated invariants reject persistence.
Owned-reference-to-null capture regression is covered.
Stable-key convenience API uses SQLite durability tests.
Interceptor rejects unsupported external/ambient transactions.
Interceptor rejects mapped store-generated additions before SQL.
Generated-key manual transaction path has SQLite rollback/commit tests.
No path claims rejected planning rolls back the caller's object graph.
No path claims tracked-only runtime state proves database-global invariants.
EF consistency sample runs successfully.
EfCoreConsistency benchmark results are recorded.
Full build/test/format/pack passes.
New public APIs remain Unshipped for this roadmap.
```

---

# Planned commit boundaries

Prefer these commits. Do not collapse everything into one large change:

```text
1.  feat: add affected derived evaluation to binding plans
2.  feat: add ef core consistency mappings
3.  fix: harden ef change snapshot for relationship transitions
4.  feat: coordinate ef consistency planning and materialization
5.  feat: add consistent save convenience api
6.  feat: add relations savechanges interceptor
7.  test: prove generated key consistency workflow
8.  sample: demonstrate ef core consistency guard and materialization
9.  docs: document ef core consistency boundary
10. bench: record ef core consistency scaling
11. test: complete ef consistency failure matrix
12. docs: record tasks 100-108 completion
```

The final completion commit is allowed only after all acceptance commands pass.

---

# Explicit non-goals for Tasks 100–108

Do not implement any of the following in this roadmap:

- arbitrary before-save repair callbacks;
- a generic business-rule engine;
- auto-loading missing relations from the database;
- automatic `ExecuteUpdate` for untracked materialization sources;
- cross-process distributed consistency;
- generic fixpoint write-back semantics;
- materialized properties that feed back into the Relations dependency graph;
- automatic rollback of caller-owned POCO mutations;
- transparent support for arbitrary ambient/external transactions in the interceptor;
- pretending generated-key validation happens before the first SQL command;
- package publication or stable API graduation.

These may be evaluated only after the v1 EF consistency model is proven useful and its failure boundaries are measured.
