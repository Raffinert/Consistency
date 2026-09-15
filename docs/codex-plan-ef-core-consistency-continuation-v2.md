# Codex Continuation Plan — EF Core Consistency v2

Baseline: `2c15f61ad1e0fdb305015aff703732d47ec3fc77`

Tasks: **109–115**

This plan supersedes the unfinished remainder of Tasks 100–108. It is written for a weaker coding agent. Follow it mechanically. Do not redesign the library while implementing it. Do not skip proof work because the current CI is green.

Current baseline facts:

- `a7c4e98154e1359ab08cdf81fa632f0852621ad4` added `PlannedDerivedEvaluationMode`, `PlannedDerivedEvaluation`, `PreparedImpactPlan.DerivedEvaluations`, and a four-argument `RelationRuntime.PlanDetailed(...)` overload.
- `2c15f61ad1e0fdb305015aff703732d47ec3fc77` exposed the same derived-evaluation mode through `RelationUnitOfWork.PlanDetailed(...)`.
- CI run `34980706207` / CI #169 completed successfully for `2c15f61...`.
- The EF Core package still contains only the existing `ChangeTrackerAdapter`, public API baselines, and project file. There is no `RelationEfCoreMappings`, no shared consistency coordinator, no consistent-save API, and no interceptor yet.
- The current `PlannedDerivedEvaluationTests` file contains only three test methods. It proves a basic source-derived path, one incremental-sum/composed-chain path, and `None` mode. It does **not** satisfy the complete proof matrix from Task 100.

Do not mark the original Tasks 100–108 roadmap complete. Do not proceed as if Task 100 is complete merely because the new API exists and CI passes.

The target product remains:

```text
Validate
    EF tracked changes
        -> Capture + Prepare
        -> binding plan
        -> evaluate configured affected invariants
        -> enforced violation? reject before SQL
        -> SaveChanges
        -> install exact runtime plan
        -> dispatch

RecalculateAndValidate
    EF tracked changes
        -> Capture + Prepare
        -> binding plan
        -> evaluate configured affected derived values
        -> evaluate configured affected invariants
        -> enforced violation? reject before SQL
        -> apply configured sink-only persisted mirrors
        -> SaveChanges once
        -> install exact runtime plan
        -> dispatch
```

The architectural boundary remains:

> Core determines dependency consequences. EF Core decides persistence policy.

---

# 0. Hard constraints

1. Preserve all currently shipped public API signatures.
2. Keep all new APIs from this wave in `PublicAPI.Unshipped.txt`.
3. Do not add EF concepts to `Raffinert.Relations` core.
4. Do not add `.Enforce()` or `.MaterializeTo(...)` to core handles.
5. Do not expose public `DefinitionId` properties merely for the EF adapter.
6. The EF package must reuse `Prepare` / binding `PlanDetailed` / `Commit(plan)`; no parallel dependency engine.
7. `PlannedDerivedEvaluationMode.Affected` and `PlannedInvariantEvaluationMode.Affected` must remain propagation-scoped. No global source scan.
8. `PlanDetailed` must restore runtime-owned state before returning or throwing.
9. `Commit(plan)` must not rerun derived computations or invariant predicates already captured by the plan.
10. EF materialization is **sink-only**. A target property cannot itself be a Relations dependency, object-set key, or projected selector member.
11. Do not build a generic fixpoint/write-back engine in this roadmap.
12. Do not auto-load missing database graph state.
13. Do not claim tracked EF state proves database-global invariants unless the application seeded the authoritative required scope.
14. Only explicitly configured enforced invariant violations reject persistence.
15. `Unknown`, `Dirty`, and `Invalid` are not automatically treated as `Violated`.
16. `ScheduleRepair` does not automatically become a transaction blocker.
17. Stable-key convenience APIs must reject unsupported ambient/external transactions before issuing SQL.
18. Store-generated Relations identities remain an explicit transaction workflow; do not fake pre-save validation using temporary keys.
19. Never fabricate a missing old navigation target as `null`.
20. Do not silently skip an affected materialization source that is not tracked by the current `DbContext` instance.
21. No DI-package dependency is required for this roadmap.
22. No package publishing, tag, GitHub release, or version bump.
23. One logical concern per commit.
24. Add a failing regression test before changing an existing semantic contract.

STOP before Task 110 until every Task 109 acceptance case is implemented and green on both .NET 8 and .NET 10.

---

# Task 109 — Finish the affected-derived binding-plan proof before building EF features

## Why

The implementation at `2c15f61...` has the intended API shape, but the proof is incomplete. A weak agent must not infer correctness from the three existing tests.

The current implementation selects affected pairs from `DependencyPropagationResult.DerivedImpacts`, adds applicable newly-added sources, calls `GetValue(...)` while the planned state is installed, and captures the forward patch after evaluation. Preserve that design unless a new failing test exposes a defect.

## Primary files

- `tests/Raffinert.Relations.Tests/PlannedDerivedEvaluationTests.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs` only if a failing test requires a fix
- `src/Raffinert.Relations/Runtime/RelationRuntime.cs` only if a failing test requires a fix
- `src/Raffinert.Relations/Dependencies/DependencyGraphRuntime.cs` only if affected-source selection is proven wrong
- `src/Raffinert.Relations/PublicAPI.Unshipped.txt` only if a real public-surface correction is unavoidable

Do not add another planning API in this task.

## 109.1 Full relation-backed recomputation

Add a relation-backed derived value that is **not** incremental.

Example shape:

```csharp
var relation = model.Relation(parents, children)
    .Where((parent, child) => parent.Id == child.ParentId);

var total = model.Derived(parents)
    .Using(relation)
    .Compute((_, matches) => matches.Sum(x => x.Amount));
```

Prime the cache, mutate a related item, plan with `PlannedDerivedEvaluationMode.Affected`, and assert the final value and `Fresh` state.

Suggested test:

```text
Affected_full_recompute_relation_derived_returns_planned_final_value
```

## 109.2 Diamond ordering and de-duplication

Create:

```text
A
├─> B
├─> C
└────┐
B,C -> D
```

Use computation counters/event log.

Expected:

- each affected `(definition, source)` appears once;
- each computation is executed at most once for the planned evaluation wave when its cache is already available from an upstream request;
- D sees final B and C values;
- output ordering is deterministic across equivalent fresh runtimes.

Do not change `_derivedIds` ordering unless this test proves it is incorrect.

Suggested test:

```text
Affected_diamond_is_topological_deduplicated_and_deterministic
```

## 109.3 Projected upstream routing

Create two upstream owners and two downstream link/source objects. Change only one upstream owner.

Expected:

- only the downstream source that actually projects to the changed owner is evaluated;
- the unrelated projected consumer is not evaluated;
- `Source` and `SourceIdentity` identify the downstream source, not the upstream owner.

Suggested test:

```text
Affected_projected_upstream_change_evaluates_actual_downstream_source_only
```

## 109.4 Conservative relation propagation

Use `PreferConservativePropagation()` on a full-recompute relation consumer.

Expected:

- `DerivedEvaluations` may contain the safe affected superset selected by existing conservative propagation;
- unrelated sources outside that safe candidate set are not globally scanned;
- causal relation impact precision remains `Conservative`; planned derived evaluation must not rewrite causal precision semantics.

Suggested test:

```text
Affected_conservative_relation_evaluates_safe_superset_without_global_scan
```

## 109.5 Explicit no-global-scan proof

Create at least 100 unrelated registered sources and one touched source. Put counters in the derived computation.

Expected planning work:

```text
counter delta proportional to selected affected sources
not 100+ unrelated sources
```

Do not write a timing assertion. Use semantic counters.

Suggested test:

```text
Affected_mode_does_not_scan_unrelated_registered_sources
```

## 109.6 Summary/Causal parity

Run the same logical mutation in equivalent fresh runtimes:

```text
Summary + Affected derived
Causal + Affected derived
```

Compare:

```text
DerivedId
DefinitionKey
SourceIdentity
Value
State
```

Expected: exact parity.

Suggested test:

```text
Summary_and_causal_have_identical_planned_derived_values
```

## 109.7 Planning exception rollback

Use a derived computation that throws only for the planned final value.

Before planning capture:

```text
runtime.Version
cached value/state
runtime diagnostics counters that are expected to be journaled
relation membership / relevant runtime state
```

Expected after exception:

- same version;
- same pre-plan cache value/state;
- no retained forward patch;
- no policy dispatch;
- no partial relation/dependency state.

Suggested test:

```text
Affected_derived_exception_restores_runtime_state_atomically
```

## 109.8 Stale plan rejection must not rerun computation

Plan A with affected derived evaluation, commit independent mutation B so runtime version advances, then attempt `Commit(planA)`.

Expected:

- stale-plan rejection;
- no additional derived computation call;
- B remains authoritative runtime state;
- plan A remains uncommitted.

Suggested test:

```text
Stale_evaluated_derived_plan_is_rejected_without_reexecution
```

## 109.9 Domain drift rejection must not rerun computation

Plan against final domain member value X. Mutate that domain member again to Y before `Commit(plan)`.

Expected:

- domain-assumption validation rejects install;
- no derived recomputation;
- runtime unchanged;
- plan remains uncommitted.

Suggested test:

```text
Domain_drift_rejects_evaluated_derived_plan_without_reexecution
```

## 109.10 Newly added source

The current implementation explicitly adds derived definitions for `ObjectAdded` sources. Prove it.

Expected:

- newly added source receives planned derived evaluation if applicable;
- removed source is not emitted as a surviving evaluation source;
- source identity uses the final accepted key.

Suggested tests:

```text
Added_source_gets_planned_derived_evaluation
Removed_source_is_not_returned_as_surviving_derived_evaluation
```

## 109.11 EF unit-of-work overload forwarding

Add a focused EF adapter test proving:

```csharp
unit.PlanDetailed(runtime, detail, invariantMode, derivedMode)
```

forwards both modes and returns the same `DerivedEvaluations` semantics as direct runtime planning.

No high-level consistency feature is required yet.

## Task 109 acceptance

All of these must be true before Task 110:

```text
source-only path proven
full relation recompute proven
incremental Sum proven
chain proven
diamond proven
projected source routing proven
conservative routing proven
no-global-scan proven
None mode proven
Summary/Causal parity proven
planning exception rollback proven
stale-plan no-rerun proven
domain-drift no-rerun proven
added/removed lifecycle behavior proven
EF unit-of-work overload proven
```

Run:

```bash
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

Commit:

```text
test: complete planned derived evaluation proof matrix
```

If a defect is found, split test and fix commits.

---

# Task 110 — Add model inspection and EF consistency mappings without leaking persistence into core

## Goal

Create the EF configuration model needed for:

```text
ObjectSet -> EF tracked mutation capture
Invariant -> persistence rejection policy
Derived -> persisted mirror property
```

Do not duplicate dependency analysis in the EF package.

## Primary files

Core:

- `src/Raffinert.Relations/Properties/AssemblyInfo.cs`
- small internal model-inspection code in the most appropriate existing model/runtime file

EF:

- new `src/Raffinert.Relations.EntityFrameworkCore/RelationEfCoreMappings.cs`
- `src/Raffinert.Relations.EntityFrameworkCore/PublicAPI.Unshipped.txt`
- focused EF tests

## 110.1 Internals visibility

Add:

```csharp
[assembly: InternalsVisibleTo("Raffinert.Relations.EntityFrameworkCore")]
```

Do not expose new public definition IDs.

## 110.2 Add minimal persistence-agnostic member usage inspection

The EF adapter must reject a materialized target that participates in Relations semantics. Public diagnostics are string-oriented and are not a safe basis for this validation. Private runtime dictionaries are not accessible merely through `InternalsVisibleTo`.

Add a **small internal core inspection contract**, not an EF-specific contract.

Suggested shape:

```csharp
[Flags]
internal enum ModelMemberUsageKind
{
    None = 0,
    ObjectSetKey = 1,
    RelationDependency = 2,
    DerivedDependency = 4,
    InvariantDependency = 8,
    ProjectedSelector = 16
}

internal ModelMemberUsageKind GetMemberUsage(
    IObjectSetDefinition sourceSet,
    MemberInfo member);
```

The exact location/name may follow existing conventions, but it must remain internal and generic.

Inspection must use existing analyzed dependency/member metadata, not parse expression `ToString()` output.

Also provide internal ownership/ID resolution needed by the EF adapter, for example:

```csharp
internal int GetDerivedId(IDerivedDefinition definition);
internal int GetInvariantId(IInvariantDefinition definition);
internal bool OwnsObjectSet(IObjectSetDefinition definition);
```

Prefer a compact internal helper rather than several unrelated public APIs.

## 110.3 `RelationEfCoreMappings`

Public target shape:

```csharp
public sealed class RelationEfCoreMappings
{
    public RelationEfCoreMappings Map<TEntity>(
        ObjectSet<TEntity> set,
        Func<EntityEntry<TEntity>, bool>? selector = null)
        where TEntity : class;

    public RelationEfCoreMappings Enforce<TSource>(
        Invariant<TSource> invariant)
        where TSource : class;

    public RelationEfCoreMappings Materialize<TSource, TValue>(
        Derived<TSource, TValue> derived,
        Expression<Func<TSource, TValue>> property)
        where TSource : class;
}
```

Internally delegate object-set mapping to existing `RelationUnitOfWorkMappings`; do not create a second entity-to-set resolver.

`Enforce` and `Materialize` must work for unnamed handles because this is in-process configuration.

## 110.4 Materialization expression validation

At registration reject:

```text
not a direct instance property
static property
indexer
no setter
source type mismatch
TValue/property type incompatibility
duplicate target mapping
ambiguous duplicate derived mapping
```

At first use against a concrete `DbContext` + `RelationRuntime`, validate EF/model constraints:

```text
property is not mapped by EF
property is primary key
property is alternate key
property ValueGenerated != Never
property is database-computed/store-generated
property belongs to wrong EF entity type
Relations member usage != None
```

A concurrency token is allowed only if it is otherwise a normal writable non-generated property. Do not special-case it unless a test proves unsafe behavior.

## 110.5 Sink-only validation

Reject materialization target if internal model inspection reports any of:

```text
ObjectSetKey
RelationDependency
DerivedDependency
InvariantDependency
ProjectedSelector
```

Error message must explain that v1 materialized properties are persisted mirrors and cannot feed the Relations graph.

Do not implement fixpoint semantics.

## 110.6 Enforced invariant identity

On first validation against runtime, resolve each configured invariant handle to the runtime's local invariant ID using internal identity, not `DefinitionKey`.

Only evaluations for those exact IDs may block persistence.

Required tests:

```text
Enforce_accepts_unnamed_invariant
Enforce_rejects_handle_from_another_model_runtime
Materialize_accepts_normal_mapped_writable_property
Materialize_rejects_unmapped_property
Materialize_rejects_primary_key
Materialize_rejects_alternate_key
Materialize_rejects_store_generated_property
Materialize_rejects_object_set_key_member
Materialize_rejects_relation_dependency_member
Materialize_rejects_derived_dependency_member
Materialize_rejects_invariant_dependency_member
Materialize_rejects_projected_selector_member
Materialize_rejects_duplicate_target
Materialize_rejects_handle_from_another_model_runtime
```

Commit:

```text
feat: add ef core consistency mappings
```

---

# Task 111 — Replace fragile navigation change inference with an immutable EF mutation snapshot

## Goal

The current adapter still relies heavily on:

```csharp
entry.References.Where(x => x.IsModified)
entry.Collections.Where(x => x.IsModified)
```

That is not a sufficient foundation for a high-level consistency boundary.

Capture authoritative old/current relationship evidence **before SaveChanges** and build `MutationSet` from that snapshot.

Do not turn this into a general audit system.

## Primary files

- `src/Raffinert.Relations.EntityFrameworkCore/ChangeTrackerAdapter.cs`
- new internal snapshot helper file if it improves separation
- `tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreAdapterTests.cs`
- `tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreSqliteTests.cs`

## 111.1 Snapshot shape

Suggested internal model:

```csharp
internal sealed record EfTrackedMutationSnapshot(
    IReadOnlyList<EfTrackedEntitySnapshot> Entities,
    IReadOnlyList<EfTrackedReferenceSnapshot> References,
    IReadOnlyList<EfTrackedCollectionSnapshot> Collections);
```

Names are flexible; semantics are not.

Preserve enough evidence for one unit of work:

```text
entity instance
EntityState
resolved Relations object set when configured
scalar member original/current values
FK original/current values
reference navigation old/current targets when unambiguous
collection/root evidence sufficient to issue CollectionReset
added/deleted dependent evidence
```

## 111.2 Dependent-to-principal reference

For a dependent reference navigation, use FK original/current values to resolve old/current principal identity. Do not require `ReferenceEntry.IsModified == true` when FK evidence proves the relationship changed.

If the old principal cannot be resolved unambiguously from tracked authoritative information, preserve the existing fail-fast rule. Do not fabricate `null`.

## 111.3 Principal-to-dependent / owned optional reference

Cover the real problematic shape:

```text
owner.Owned != null
owner.Owned = null
owned dependent becomes Deleted
ReferenceEntry.IsModified may be false
```

For principal-to-dependent one-to-one/owned navigation, reconstruct the old target from tracked dependent entries and FK original values when exactly one authoritative match exists.

Also support:

```text
null -> new owned dependent
old dependent -> replacement dependent
```

Do not infer an old target from arbitrary database queries.

## 111.4 Collection reset evidence

Issue a collection reset when any of the following proves membership changed:

```text
CollectionEntry.IsModified
added/deleted dependent with FK linking it to owner
relationship FK retarget into/out of owner
```

Deduplicate resets by `(owner reference, MemberInfo)`.

Do not try to preserve ordering/multiplicity deltas beyond existing `CollectionReset` semantics.

## Required regression tests

Use relational SQLite where relationship tracking behavior matters.

```text
Owned_optional_reference_nonnull_to_null_is_captured_even_if_IsModified_false
Owned_optional_reference_null_to_instance_is_captured
One_to_one_replacement_captures_real_old_and_new_target
Dependent_reference_retarget_A_to_B_uses_original_FK_history
Ambiguous_old_principal_throws
Collection_add_emits_reset_when_relationship_evidence_changes
Collection_remove_emits_reset
Dependent_added_and_removed_evidence_survives_until_unit_capture
Snapshot_captured_before_save_does_not_need_SavedChanges_state_to_rediscover_change
```

Ensure existing adapter tests remain green.

Commit:

```text
fix: harden ef tracked mutation snapshot
```

---

# Task 112 — Implement one shared consistency coordinator and the stable-key convenience API

## Goal

Build the high-level feature exactly once, then let both explicit save methods and the interceptor reuse it.

## Public types

Add:

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

Add EF-specific exceptions:

```text
RelationInvariantViolationException
RelationMaterializationSourceNotTrackedException
RelationUnsupportedTransactionException
RelationStoreGeneratedKeyRequiresManualWorkflowException
```

Keep them in the EF package.

## 112.1 Internal coordinator

Create one internal coordinator. Suggested input:

```text
DbContext
RelationRuntime
RelationEfCoreMappings
RelationEfCoreConsistencyOptions
```

Suggested pending result:

```csharp
internal sealed record PendingConsistencySave(
    RelationUnitOfWork Unit,
    PreparedImpactPlan? Plan,
    IReadOnlyList<AppliedMaterialization> Materializations);
```

Do not expose the live binding plan through public exceptions.

## 112.2 Stable-key pre-save planning

Coordinator sequence:

```text
context.ChangeTracker.DetectChanges()
capture immutable EF mutation snapshot / RelationUnitOfWork
validate mappings against context + runtime
reject unsupported transaction/key cases
unit.Prepare(runtime)
unit.PlanDetailed(
    detailLevel,
    invariantMode = Affected only when Enforce mappings exist,
    derivedMode = Affected only when RecalculateAndValidate and Materialize mappings exist)
filter planned invariant evaluations by exact configured invariant IDs
if any enforced evaluation.State == Violated:
    throw before materialization and before SQL
if RecalculateAndValidate:
    apply configured affected materialized mirror values
context.ChangeTracker.DetectChanges() again
return pending unit/plan
```

The second `DetectChanges()` is mandatory. Do not assume a `SavingChanges` interceptor assignment will be rediscovered automatically after EF's initial detect-changes phase.

## 112.3 Materialization rules

For each configured derived definition:

- consume only matching `plan.DerivedEvaluations`;
- require `State == Fresh`; anything else is a planning defect and must fail before SQL;
- source object must be the same instance tracked by this `DbContext`;
- entry state must not be `Detached`;
- target value must be type-compatible;
- compare using EF `ValueComparer` when available, otherwise default typed equality;
- write only when different;
- do not create a Relations `PropertyChange` for the sink property;
- do not run another binding plan just because the mirror changed.

## 112.4 Adapter-owned materialization rollback on write failure

Materialization writes are adapter-owned, unlike the caller's original domain changes. Avoid leaving a half-applied set of mirror writes when a property setter/value conversion fails before SQL.

Before applying writes capture, per target:

```text
source reference
property metadata
original current CLR/EF value
original IsModified flag
```

Apply writes in deterministic order.

If a write throws:

```text
restore already-applied mirror values in reverse order
restore their IsModified flags
rethrow the original failure (wrap only if rollback itself also fails)
no SQL
runtime remains unchanged
```

Do **not** attempt to rollback the caller's original business mutations.

## 112.5 Explicit consistent-save API

Add:

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

Supported v1 flow:

```text
coordinator prepare
-> possible pre-SQL rejection/materialization
-> context.SaveChanges
-> unit.Commit(runtime) installs exact retained plan
-> unit.Dispatch(runtime)
```

If DB fails: do not commit runtime plan.

If runtime commit fails after DB success: wrap with existing `RelationRuntimeSynchronizationException`; database must not be retried blindly.

If dispatch fails: DB + runtime are already committed; preserve existing resumable callback semantics.

## 112.6 Unsupported transaction cases

Before any SQL reject:

```text
context.Database.CurrentTransaction != null
System.Transactions.Transaction.Current != null
```

The exception must direct callers to the documented manual transaction/unit-of-work flow.

Do not attempt to infer outer transaction durability from `SavedChanges`.

## 112.7 Store-generated Relations identity detection

For each mapped `Added` entity, cross-reference the Relations object-set `KeyMembers` with EF properties.

If any Relations key member is:

```text
PropertyEntry.IsTemporary == true
or EF metadata ValueGenerated != Never and final value is not yet assigned authoritatively
```

reject the convenience path before SQL with `RelationStoreGeneratedKeyRequiresManualWorkflowException`.

Do not reject unrelated generated columns that are not part of the Relations object-set identity.

## Required SQLite tests

```text
Validate_rejects_enforced_violation_before_SQL
Validate_does_not_materialize
Recalculate_writes_and_persists_only_affected_mirror
Recalculate_skips_equal_value_using_EF_comparer
Unenforced_violation_does_not_block
ScheduleRepair_without_Enforce_does_not_block
Unknown_is_not_treated_as_violation
Materialization_source_must_be_same_tracked_instance
Materialization_setter_failure_rolls_back_adapter_owned_mirror_writes
Database_failure_after_valid_plan_leaves_runtime_unchanged
Runtime_commit_failure_after_DB_success_throws_synchronization_exception
Dispatch_failure_occurs_after_DB_and_runtime_commit
Explicit_transaction_is_rejected_before_SQL
Ambient_transaction_is_rejected_before_SQL
Store_generated_Relations_key_addition_is_rejected_before_SQL
Generated_nonkey_column_does_not_force_manual_workflow
```

Commit in two logical steps if necessary:

```text
feat: coordinate ef consistency planning and materialization
feat: add consistent save api
```

---

# Task 113 — Add `SaveChangesInterceptor` as a thin host over the shared coordinator

## Goal

Enable:

```csharp
await context.SaveChangesAsync();
```

with the same supported semantics as `SaveChangesConsistentlyAsync`.

Do not duplicate planning/materialization code.

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

No DI-package dependency in this task.

## Lifecycle

### SavingChanges / SavingChangesAsync

```text
if pending save already exists for same context -> reject reentrancy
run shared coordinator pre-save phase
store PendingConsistencySave by DbContext reference
```

An enforced violation must throw before SQL.

### SavedChanges / SavedChangesAsync

```text
retrieve pending
commit exact retained plan/runtime unit
remove pending state
then dispatch
```

If runtime commit fails after database success, remove pending state and throw `RelationRuntimeSynchronizationException`.

### SaveChangesFailed / async equivalent

```text
remove pending state
never Commit(plan)
runtime unchanged
```

### cancellation

Ensure pending state is cleared on async cancellation/failure paths covered by EF interception callbacks.

## Per-context state

Do not use one `_pending` field.

Use a leak-safe reference-keyed structure such as:

```csharp
ConditionalWeakTable<DbContext, PendingConsistencySave>
```

or an equivalent existing pattern.

Guard overlapping/reentrant save on the same context with a clear exception.

Do not add locks that suggest `RelationRuntime` became thread-safe.

## Materialization and detect-changes rule

The shared coordinator already performs the second `ChangeTracker.DetectChanges()` after mirror writes. The interceptor must not reimplement or omit that step.

## Required tests

```text
Interceptor_validates_before_SQL
Interceptor_recalculates_and_SQL_sees_materialized_property
Interceptor_runtime_commit_occurs_only_after_saved_changes
Interceptor_DB_failure_discards_plan
Interceptor_runtime_commit_failure_uses_sync_exception
Interceptor_explicit_transaction_rejected
Interceptor_ambient_transaction_rejected
Interceptor_store_generated_Relations_key_rejected
Interceptor_state_is_isolated_per_DbContext
Interceptor_reentrant_same_context_save_rejected
Interceptor_sync_async_semantic_parity
Interceptor_failure_does_not_leave_pending_entry_for_next_save
```

Commit:

```text
feat: add relations savechanges interceptor
```

---

# Task 114 — Prove generated-key/manual-transaction consistency and recovery boundaries

## Goal

Do not force store-generated identities through the pre-SQL convenience/interceptor path. Prove and document the correct explicit transaction sequence.

## Required manual workflow

```text
Capture EF unit/snapshot before first SaveChanges if old relationship evidence is needed
Begin explicit transaction
First SaveChanges
    -> database assigns final generated keys
    -> EF relationship fixup completes
Prepare(runtime) only after final Relations identity exists
PlanDetailed(
    invariant mode = Affected for configured Enforce mappings,
    derived mode = Affected for configured Materialize mappings)
filter enforced invariant violations
if violated:
    rollback DB transaction
    do not Commit(plan)
    discard/reconcile DbContext tracking state before retry
else:
    apply configured sink-only materialized mirrors
    DetectChanges
    second SaveChanges only if mirror/outbox work exists
    persist durable policy work if requested
    commit DB transaction
    Commit(plan)
    Dispatch
```

If existing `RelationUnitOfWork` plus internal mapping/coordinator helpers are sufficient, do not add a large public transaction abstraction.

## Critical rollback rule

Database rollback does not restore EF's in-memory state after the first save/generated-key assignment.

Documentation and tests must make this explicit:

```text
rollback DB transaction
-> discard DbContext or Clear/reload authoritative state
-> do not guess-reset generated keys and retry the same prepared plan
```

## SQLite tests

```text
Generated_key_invalid_plan_rolls_back_database_and_does_not_advance_runtime
Generated_key_valid_plan_materializes_then_commits_database_and_runtime
Generated_key_violation_requires_context_reconciliation_after_rollback
Two_generated_entities_get_distinct_final_SourceIdentity_values
Generated_key_plan_commit_does_not_rerun_derived_or_invariant_logic
Generated_key_outbox_uses_plan_result_final_durable_identities
Database_commit_then_runtime_install_failure_requires_runtime_rebuild_not_DB_retry
Dispatch_failure_after_DB_and_runtime_commit_preserves_durable_outbox_and_allows_retry
```

Commit:

```text
test: prove generated key consistency workflow
```

---

# Task 115 — Add the real sample, documentation, benchmarks, and close only after the full gate

## Goal

Finish the feature as something a user can understand and trust, not only an API surface.

## 115.1 SQLite sample

Prefer a focused project:

```text
samples/Raffinert.Relations.EntityFrameworkCore.ConsistencySample
```

Domain:

```text
PurchaseOrderLine.OrderedQuantity
GoodsReceiptLine.Quantity
        ↓
ReceivedQuantity derived
        ↓
AvailableQuantity derived
        ↓
EF persisted mirror PurchaseOrderLine.AvailableQuantity
        ↓
Availability invariant using the Derived handle
```

The invariant must depend on the derived value, not the persisted mirror property.

Scenarios:

```text
A Validate mode rejects enforced violation, DB/runtime unchanged
B RecalculateAndValidate updates mirror and persists one supported stable-key save
C violated but unenforced repair-style invariant does not block persistence
D generated-key case uses explicit transaction/manual workflow
E DB failure leaves runtime unchanged
F runtime install failure after DB success explains rebuild/reconciliation boundary
```

## 115.2 Documentation

Add/update:

```text
README.md
docs/architecture.md
docs/ef-core-consistency.md
```

README should show the small happy path only.

Focused docs must state:

```text
Core remains persistence agnostic.
Enforce is EF adapter policy.
Materialized targets are sink-only persisted mirrors in v1.
Only affected configured derived/invariant definitions are eagerly evaluated.
No database graph auto-loading occurs.
Correctness is bounded by the authoritative runtime scope supplied by the application.
Rejected planning does not rollback caller-owned POCO mutations.
Adapter-owned partial mirror writes are rolled back if materialization itself fails before SQL.
DB failure may leave deterministic mirror values on the still-mutated tracked graph; reload/discard as appropriate.
External/ambient transactions are unsupported by convenience/interceptor paths.
Store-generated Relations identities use the documented explicit transaction flow.
DB durability followed by runtime failure requires runtime reconciliation/rebuild, not DB retry.
```

## 115.3 Benchmark separation

Add BenchmarkDotNet scenarios that separately report:

```text
EF capture cost
Relations PlanDetailed affected-evaluation cost
materialization write cost
```

Population:

```text
10k registered Relations sources
100k registered Relations sources
one touched source
several unrelated derived definitions
several unrelated invariants
Validate Summary
Validate Causal
RecalculateAndValidate Summary
RecalculateAndValidate Causal
```

Do not claim EF `ChangeTracker` enumeration is O(affected-wave). It may scale with tracked EF entries. The Relations-specific plan/evaluation portion must remain proportional to the affected graph.

Write:

```text
benchmarks/EfCoreConsistency-Results.md
```

Include date, environment, commit SHA, mean, allocated bytes, and interpretation.

## 115.4 Final failure matrix

Prove:

```text
relation predicate throws during planning -> no SQL, runtime unchanged
planned derived computation throws -> no SQL, no mirror writes, runtime unchanged
invariant predicate throws -> no SQL, no mirror writes, runtime unchanged
mirror setter throws -> previously applied adapter mirror writes restored, no SQL, runtime unchanged
EF command fails -> runtime unchanged
stale runtime version detected pre-save -> no SQL
runtime forward patch install fails after DB success -> DB durable + synchronization exception
post-commit dispatch fails -> DB/runtime committed; no fake rollback
```

## 115.5 Final commands

Run exactly:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
dotnet run --project samples/Raffinert.Relations.EntityFrameworkCore.ConsistencySample/Raffinert.Relations.EntityFrameworkCore.ConsistencySample.csproj -c Release
```

If the final sample project name differs, use the actual committed path.

## Completion checklist

Do not mark Tasks 109–115 complete unless every statement is true:

```text
Task 109 full proof matrix exists.
Core contains no EF persistence-policy concepts.
New core API remains Unshipped.
RelationEfCoreMappings exists with Map + Enforce + Materialize.
Unnamed invariant/derived handles work in-process.
Materialization targets are proven sink-only by analyzed MemberInfo usage, not expression strings.
Owned-reference-to-null regression is covered.
Validate mode performs no mirror write.
RecalculateAndValidate writes only affected configured mirrors.
Second DetectChanges occurs after mirror writes before SQL.
Only explicitly Enforced Violated states block persistence.
Unenforced ScheduleRepair remains policy data.
Materialization source must be the same tracked instance.
Adapter-owned mirror writes are rolled back if materialization itself throws before SQL.
Stable-key convenience API has real SQLite durability tests.
Interceptor is a thin wrapper over the same coordinator.
Interceptor pending state is isolated per DbContext and cleared on failure.
External/ambient transactions are rejected before SQL by convenience/interceptor paths.
Store-generated Relations identities are rejected before SQL by convenience/interceptor paths.
Generated-key explicit transaction workflow is covered by SQLite tests.
No documentation claims planning rollback restores caller POCO mutations.
No documentation claims tracked-only scope proves database-global invariants.
EF consistency sample runs successfully.
Benchmark results separate EF capture from Relations affected-plan work.
Full failure matrix passes.
Build/test/format/pack/sample gate passes.
No package publish/tag/release occurred.
```

## Planned commit boundaries

Prefer:

```text
1.  test: complete planned derived evaluation proof matrix
2.  feat: add core model member inspection for adapters
3.  feat: add ef core consistency mappings
4.  fix: harden ef tracked mutation snapshot
5.  feat: coordinate ef consistency planning and materialization
6.  feat: add consistent save api
7.  feat: add relations savechanges interceptor
8.  test: prove generated key consistency workflow
9.  sample: demonstrate ef core consistency integration
10. docs: document ef core consistency boundary
11. bench: record ef core consistency scaling
12. test: complete ef core consistency failure matrix
13. docs: record ef core consistency v2 completion
```

The final completion commit is allowed only after the full gate passes.

---

# Explicit non-goals

Do not implement in Tasks 109–115:

- arbitrary before-save repair callbacks;
- generic trigger framework;
- generic fixpoint/write-back engine;
- materialized mirrors feeding back into Relations dependencies;
- database auto-loading of missing relation scope;
- transparent support for external/ambient transactions in the interceptor;
- automatic `ExecuteUpdate` of untracked affected entities;
- automatic rollback of caller-owned object mutations;
- distributed transactions;
- package publication or stable API graduation.
