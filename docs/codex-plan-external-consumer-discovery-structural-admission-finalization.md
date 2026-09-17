# Codex implementation plan — external consumer discovery structural-admission finalization

Status: **ACTIVE COMPLETION PLAN**

Baseline commit: `1d4487fec60da7cf24567ce74efcaa7b8027500a`

Tasks: **211–219**

This plan supersedes the active status of Tasks 202–210.

Commit `1d4487f` correctly started separating `CoverageAdmission` from domain `ObjectAdded`, but the refactor is incomplete. It fixes the semantic type distinction while leaving several call sites and proof obligations on the old lifecycle-only assumptions. Do not mark external consumer discovery release-ready until this plan is complete.

---

# What `1d4487f` got right

Preserve these changes unless a task below explicitly says otherwise:

- `CoverageAdmission` no longer implements `IAddedMutation`;
- `PreparedMutation` carries `CoverageAdmissions` separately from domain `LifecycleMutations`;
- mutation provenance is still created only for real `ObjectAdded`, `ObjectRemoved`, property, and collection mutations;
- `CommitCoverageAdmission(...)` establishes object-set/navigation/projection/relation baseline state without merging returned relation deltas into semantic `relationDeltas`;
- discovered roots therefore no longer intentionally become `MutationOriginKind.ObjectAdded`;
- dependency-state snapshotting has started to include coverage admissions;
- the exact GitHub Actions CI run for `1d4487fec60da7cf24567ce74efcaa7b8027500a` is green.

The direction is correct. Do not revert to `CoverageAdmission : IAddedMutation`.

---

# Blocking review findings

Treat every finding below as a required fix, not a suggestion.

## Finding A — relation rollback/forward patch scope does not include admitted roots

`CapturePatchParts(...)` now builds:

```csharp
var structuralMutations = coverageAdmissions
    .Cast<RuntimeMutation>()
    .Concat(lifecycleMutations)
    .ToArray();
```

but the relation touched-root loop still handles only:

```csharp
IAddedMutation
ObjectRemoved
```

and has no `CoverageAdmission` cases.

At the same time `CommitCoverageAdmission(...)` executes:

```csharp
relation.AddRight(admission.Instance)
relation.AddLeft(admission.Instance)
```

Therefore planning can mutate relation baseline membership for an admitted persisted root without capturing that root in the rollback/forward relation patch scope.

This can violate the binding-plan invariant:

```text
PlanDetailed before SQL
    must leave live runtime exactly unchanged
```

Possible symptoms:

- relation membership survives planning rollback;
- `Related(...)` / reverse relation state changes before SQL;
- a later operation observes a consumer that was never committed;
- forward patch installation after SQL does not exactly represent the planned relation baseline;
- rollback after an install failure cannot restore the exact pre-plan relation state.

This is the highest-priority correctness bug after `1d4487f`.

## Finding B — projection rollback/final-state plumbing still ignores coverage admissions

The refactor updated `ProjectionIndexRegistry` so it *can* understand `CoverageAdmission` when supplied, but callers still do not supply it consistently.

Current problems include:

```csharp
projectionChanged = structuralMutations.Any(mutation => mutation switch
{
    IAddedMutation ...
    ObjectRemoved ...
    _ => false
});
```

so `CoverageAdmission` does not make `projectionChanged` true.

Then patch capture still calls:

```csharp
_projections.CaptureState(lifecycleMutations, changes)
```

instead of the combined structural mutation set.

And projected final-state validation still uses only:

```csharp
prepared.LifecycleMutations
```

rather than `CoverageAdmissions + LifecycleMutations`.

Meanwhile `CommitCoverageAdmission(...)` calls:

```csharp
_projections.AddRoot(...)
```

so the live projection index can be mutated during planning without a corresponding rollback snapshot.

This plan does **not** add projected external consumer discovery. The fix is only about preserving atomic runtime semantics for the internal structural-admission primitive.

## Finding C — pending coverage-admission deduplication still relies on `IAddedMutation`

`ExternalConsumerDiscovery.Collect(...)` still contains checks shaped like:

```csharp
admissions.Any(value => value is IAddedMutation ...)
admissions.OfType<IAddedMutation>()
```

After `1d4487f`, a `CoverageAdmission` is no longer an `IAddedMutation`, so those checks no longer see pending discovered roots.

A single persisted root can legitimately be returned by two resolver requests in the same save, for example:

```text
Association.Source.UnitValue changed
Association.Target.UnitValue changed

Source resolver returns Association B
Target resolver also returns Association B
```

Expected:

```text
one pending CoverageAdmission for B
```

Current risk:

```text
two CoverageAdmissions for B
    -> duplicate simulation/key failure
    or duplicated structural work
```

Reference-identity dedupe and key-identity conflict detection must explicitly understand both domain `ObjectAdded` and structural `CoverageAdmission`.

## Finding D — generic EF evaluation closure still treats unloaded null as valid

The request navigation itself has an EF `IsLoaded` guard, but `ValidateEvaluationClosure(...)` still effectively does:

```csharp
var target = MemberReader.Read(descriptor.Navigation, root);
if (target is null)
    continue;
```

For an active formula such as:

```text
Association.Source.UnitValue / Association.Target.UnitValue
```

this is unsafe when the source resolver includes `Source` but forgets `Target`:

```text
Target CLR value = null
Target NavigationEntry.IsLoaded = false
```

That is not a legitimate null. It means evaluation closure is unknown.

Required distinction:

```text
non-null reference + tracked in same DbContext -> valid
null + NavigationEntry.IsLoaded == true       -> valid loaded optional null
null + NavigationEntry.IsLoaded == false      -> fail before SQL
non-null reference + detached                 -> fail before SQL
```

No automatic `Load()` and no generated `Include(...)`.

## Finding E — mutation validation still has split-brain structural-target logic

`ValidateMutations(...)` correctly creates separate arrays:

```text
lifecycle
coverageAdmissions
```

but `lifecycleTargets` is still built from `lifecycle` only, even though its switch contains an unreachable `CoverageAdmission` branch.

Do not leave ad-hoc variants such as:

```text
lifecycle
coverageAdmissions
structuralMutations
lifecycleTargets
```

with different membership rules in different call sites.

Create one exact internal structural-membership view and use it wherever the concern is **set membership / snapshot scope**, while continuing to use domain lifecycle only for **semantic origins and domain-add/remove behavior**.

## Finding F — `1d4487f` added zero regression tests

The commit changed seven production files and no test files.

The dedicated external discovery suite still contains only the two tests introduced by `730de44`:

```text
Source_change_discovers_unloaded_consumers_and_materializes_all_rates_async
Complete_scope_skips_external_resolver
```

Green CI therefore proves only that the existing suite did not catch the incomplete structural refactor.

Tasks below must add RED tests first for every blocking defect.

---

# Frozen boundaries for Tasks 211–219

Still out of scope:

- multi-hop external discovery;
- collection-navigation external discovery;
- relation-predicate consumer discovery;
- projected-consumer discovery;
- generated resolver queries;
- automatic `Include(...)` generation;
- automatic `Reference.Load()` / lazy loading;
- partitioned/key-scoped `ConsistencyScope`;
- non-EF discovery providers;
- CDC/reconciliation/background loading;
- cross-process serializability/locking;
- automatic proof that a host resolver did not under-fetch;
- reconstruction of previously unknown existing Modified/Deleted roots;
- public `CoverageAdmission` or public structural-mutation APIs.

Keep the public v1 API unchanged:

```csharp
public ConsistencyEfCoreMappings DiscoverConsumers<TRoot, TTarget>(
    ObjectSet<TRoot> roots,
    Expression<Func<TRoot, TTarget?>> navigation,
    Func<DbContext, IReadOnlyCollection<TTarget>, IQueryable<TRoot>> query)
    where TRoot : class
    where TTarget : class;
```

---

# Mandatory implementation discipline for weak agents

For every task that changes behavior:

```text
1. Add a focused test that fails on baseline 1d4487f for the intended reason.
2. Run only that focused test and record the failure reason locally.
3. Make the smallest production change.
4. Re-run focused test until green.
5. Run the full affected test project.
6. Commit the task separately.
7. Do not perform opportunistic cleanup/refactors.
```

If the test is already green on `1d4487f`, the test is not proving the claimed defect. Strengthen the assertion instead of declaring success.

---

# Task 211 — prove the structural-admission regressions before fixing them

Add Core-level regression coverage first. Use internal test access; do not expose new public API.

Prefer a dedicated file such as:

```text
tests/Raffinert.Consistency.Tests/CoverageAdmissionPlanningTests.cs
```

or an existing narrowly related runtime planning test file if one already exists.

Create minimal deterministic models for relation and projection state.

## 211.1 Relation baseline rollback test

Required test name or equivalent:

```text
Coverage_admission_relation_baseline_is_not_visible_after_planning_before_commit
```

Arrange:

```text
Left set L
Right set R
relation L <-> R with a predicate that is true for admitted R and an already-registered L
runtime initially contains L but not R
mutation batch contains CoverageAdmission(R)
```

Plan with the binding-plan API (`PlanDetailed`/equivalent), but do not commit the plan.

Assert after planning returns:

```text
runtime.Version unchanged
R is not registered in live runtime
relation does not expose the new L-R pair
relation diagnostics/materialized-pair count equal pre-plan baseline
```

This test must be RED on `1d4487f` if relation rollback scope is incomplete.

Add the mirror case with the admitted object on the relation left side if relation runtime state is asymmetric enough to require separate proof.

## 211.2 Exact commit-install test

Plan the same admission, then commit the exact prepared plan.

Assert:

```text
R becomes registered only after commit
relation baseline pair exists after commit
runtime.Version increments exactly once
public RelationMutationImpact.AddedPairs does NOT report the baseline pair solely because R became known
MutationOrigins has no ObjectAdded for R
```

## 211.3 Projection rollback test

Create a projected upstream dependency where the admitted object is a downstream projection consumer.

This is an **internal runtime atomicity test**, not new external projected discovery support.

Before planning capture:

```text
runtime.Diagnostics.ReverseProjectionEntryCount
runtime.Diagnostics.TargetCount
```

After `PlanDetailed` but before commit assert both counts are unchanged and admitted downstream is not visible through projected propagation.

After exact plan commit assert projection index state is installed once.

## 211.4 Duplicate pending admission EF test

Extend `ExternalConsumerDiscoveryTests`:

```text
Same_unloaded_root_returned_by_two_navigation_resolvers_is_admitted_once
```

Arrange an unknown Association B that matches both a changed Source target and a changed Target target in one save.

Assert:

```text
source resolver called once
target resolver called once
save succeeds
B registered once
runtime version increments once
materialized result correct
```

This must detect the stale `IAddedMutation`-based pending-admission checks.

Suggested commit:

```text
test: expose structural coverage admission regressions
```

---

# Task 212 — introduce one structural-mutation view and finish patch atomicity

Do not scatter more `.Concat(...)` logic.

Create one internal representation/helper on `PreparedMutation` or immediately adjacent runtime code that means exactly:

```text
Structural mutations
    = CoverageAdmissions
    + ObjectAdded/ObjectRemoved domain lifecycle mutations
```

Possible shape (name not frozen):

```csharp
internal IReadOnlyList<RuntimeMutation> StructuralMutations { get; }
```

or a helper that enumerates them without exposing a mutable list.

The important point is semantic reuse, not the exact member name.

Use **structural mutations** for:

- object-set simulation;
- set membership validation;
- rollback/forward patch scope;
- relation touched-left/touched-right capture;
- navigation snapshot scope;
- projection snapshot scope;
- projected final-state validation;
- dependency-state snapshot scope;
- any internal patch-size/proof helper that measures the prepared operation.

Continue using **domain lifecycle only** for:

- `MutationOriginKind.ObjectAdded/ObjectRemoved`;
- semantic relation add/remove deltas;
- business lifecycle policy behavior;
- public causal explanation of domain additions/removals.

## Required relation patch changes

In `CapturePatchParts(...)`, when collecting touched relation roots, add explicit structural handling:

```text
CoverageAdmission on relation.LeftSet  -> touchedLefts.Add(instance)
CoverageAdmission on relation.RightSet -> touchedRights.Add(instance)
```

Do not merge admission baseline deltas into semantic `relationDeltas`.

`CommitCoverageAdmission(...)` may still call `AddLeft/AddRight` to establish final baseline membership, but returned deltas must remain discarded.

## Required projection patch changes

`CoverageAdmission` on a projection downstream set must make projection snapshotting active.

Pass the combined structural mutation set to:

```text
ProjectionIndexRegistry.CaptureState(...)
ProjectionIndexRegistry.ValidateFinalState(...)
FinalSetMembershipView
```

Do not add external projected-consumer discovery.

## Required validation cleanup

Replace the current unreachable `CoverageAdmission` arm inside a `lifecycle.Select(...)` with an actual structural target collection.

Conceptually:

```text
structuralTargets = domain lifecycle targets + coverage admission targets
```

Use `structuralTargets` only for structural membership validation.

## Required internal helper parity

Audit and fix helpers such as:

```text
CaptureDependencyPatchEntryCount
CaptureInstallRollbackJournal
ValidatePlanInstallForBenchmark
```

so they do not silently measure/validate a lifecycle-only subset of a prepared operation that contains admissions.

Required tests from Task 211 must turn green without weakening assertions.

Suggested commit:

```text
fix: make coverage admission patch state atomic
```

---

# Task 213 — fix pending-admission identity and key deduplication

In `ExternalConsumerDiscovery.Collect(...)`, stop using `IAddedMutation` as a proxy for all pending membership additions.

Create narrow internal helpers that distinguish:

```text
pending domain ObjectAdded
pending CoverageAdmission
already registered runtime root
```

Required semantics:

## Same CLR instance returned twice

If the same root reference is returned by multiple resolver requests in the same save:

```text
first request  -> create one CoverageAdmission
second request -> recognize pending admission and skip
```

No duplicate simulation and no duplicate key error.

## Different CLR instance with same runtime key

Fail closed:

```text
pending admission B1 key 42
resolver later returns B2 key 42, B2 != B1 by reference
    -> duplicate runtime identity error
```

Do not silently reconcile instances.

## Domain Added remains domain Added

A ChangeTracker `Added` root is a real domain addition and must not be converted into `CoverageAdmission`.

If the resolver also returns the same tracked Added root, do not create an admission for it.

## Already registered root

Skip admission, but it can still participate in affected-root propagation through normal runtime indexes.

Required tests:

```text
Same_unloaded_root_returned_by_two_navigation_resolvers_is_admitted_once
Different_instance_same_key_across_resolvers_fails_closed
Tracked_added_root_is_not_coverage_admitted
Already_registered_root_is_not_admitted_again
```

Suggested commit:

```text
fix: deduplicate pending consumer admissions explicitly
```

---

# Task 214 — complete EF evaluation-closure proof

Refactor closure validation so every active direct-navigation descriptor on the admitted root uses validated EF navigation metadata.

Do not read CLR `null` and assume closure.

For every required descriptor:

1. resolve the corresponding validated resolver/navigation metadata for exact `RootSet + Navigation`;
2. obtain the EF `NavigationEntry` for that root;
3. inspect current CLR/reference value;
4. apply the exact rules below.

```text
value != null
    -> referenced entity must be tracked by this DbContext and not Detached

value == null && NavigationEntry.IsLoaded
    -> valid loaded null

value == null && !NavigationEntry.IsLoaded
    -> fail before SQL: evaluation closure incomplete
```

Do not auto-load.

Do not require a resolver registration merely to evaluate a navigation that is already covered by `Complete(set)` if existing active-policy semantics do not require it. Reuse the existing validated metadata model rather than inventing strings.

Required tests:

```text
Resolver_omitting_other_required_reference_fails_before_sql
Loaded_optional_null_required_reference_is_accepted
Non_null_detached_required_reference_fails_before_sql
All_required_references_loaded_and_tracked_pass
```

At least one test must use:

```text
request navigation = Source
evaluation-only sibling navigation = Target
```

so the bug cannot be hidden by validating only the request navigation.

Suggested commit:

```text
fix: require complete EF evaluation closure for discovered roots
```

---

# Task 215 — finish active-policy and authoritative-scope proof matrix

Do not change the design unless a test exposes a real defect.

Add dedicated tests proving the hardening already attempted in `730de44`.

Required policy tests:

```text
Validate_ignores_materialization_only_discovery
RecalculateAndValidate_activates_materialization_discovery
Enforced_invariant_uses_discovery_in_Validate
Non_enforced_invariant_does_not_use_discovery
Unmaterialized_derived_definition_does_not_use_discovery
```

Required scope-substitution tests:

```text
Supported_direct_navigation_with_exact_resolver_can_replace_navigation_complete_scope
Supported_and_unsupported_navigation_on_same_set_remain_fail_closed
Upstream_derived_navigation_requirement_is_preserved
All_active_navigation_obligations_require_exact_resolvers
Same_CLR_type_in_two_ObjectSets_does_not_cross_satisfy_resolver
RelationSourceCoverage_is_never_substituted_by_DiscoverConsumers
RelationTargetCoverage_is_never_substituted_by_DiscoverConsumers
ProjectedConsumerCoverage_is_never_substituted_by_DiscoverConsumers
```

For fail-closed tests assert:

```text
exception occurs before resolver query when capability is insufficient
no SQL attempted
runtime.Version unchanged
```

Suggested commit:

```text
test: prove discovery policy and scope substitution boundaries
```

---

# Task 216 — prove ChangeTracker overlay, batching, and resolver misuse

Extend `ExternalConsumerDiscoveryTests` with the full effective-consumer matrix.

## Overlay

```text
Retargeted_away_consumer_is_excluded
Retargeted_in_registered_consumer_is_included
Tracked_Added_consumer_remains_domain_add
Tracked_Deleted_consumer_is_excluded
Unknown_existing_Modified_consumer_fails_closed
Unknown_existing_Deleted_consumer_fails_closed
```

Do not weaken the unknown Modified/Deleted v1 boundary.

## Batching

```text
Multiple_changed_targets_same_navigation_one_resolver_call
Two_changed_members_same_target_same_navigation_one_resolver_call
Source_and_Target_changes_one_call_per_navigation
Same_target_reference_is_deduplicated_within_batch
Complete_scope_zero_resolver_calls
```

## Resolver misuse

```text
AsNoTracking_or_detached_return_fails
Wrong_exact_ObjectSet_mapping_fails
Different_instance_duplicate_runtime_key_fails
Safe_resolver_superset_is_filtered
Missing_required_resolver_fails_before_sql
Resolver_exception_propagates_before_sql_and_runtime_unchanged
```

Assertions must cover runtime + tracked state + database where meaningful, not database only.

Suggested commit:

```text
test: prove discovery overlay batching and resolver guards
```

---

# Task 217 — persistence failure, retry, cancellation, and manual-UoW proof

The implementation already contains materialization rollback code from `730de44`; now prove it.

## Convenience save SQL failure

Create a deterministic provider/database failure after planning/materialization but before successful SQL completion.

Assert:

```text
runtime.Version unchanged
no discovered root registered in runtime
relation/navigation/projection runtime state equals pre-save state
Raffinert-written mirror CLR values restored
mirror PropertyEntry.IsModified restored
original user mutation remains present
```

Then remove the injected failure and retry **with the same DbContext**.

Assert retry succeeds and discovery is performed again as required.

Provide sync and async coverage if the same fault harness can support both.

## Resolver/planning failure

Assert:

```text
no SQL
no runtime admission
no relation/projection structural leak
runtime.Version unchanged
```

## Cancellation

Cancel async discovery/query before persistence completes.

Assert no runtime patch is installed and framework materializations are not left poisoned.

## Manual policy-aware UoW

Prove:

```text
CaptureConsistencyUnitOfWork
PrepareAndPlan performs discovery
live runtime unchanged after planning
caller performs database durability step
CommitAfterDatabaseCommit installs exact admission + semantic mutation once
Dispatch remains separate
runtime.Version increments exactly once
```

Do not invent automatic rollback for arbitrary external SQL failure in the manual workflow. Document the existing single-use/abandon contract if necessary.

## Open-world repeat operation

After one successful operation, run a later save against the same navigation without `Complete(set)`.

Assert the resolver executes again. Prior discovery is runtime knowledge, not a durable proof that the database has no newly created consumers.

Suggested commit:

```text
test: prove discovery persistence atomicity and retry workflows
```

---

# Task 218 — docs and dogfood contract audit

Do not add new product claims.

Review and update only where required:

```text
README.md
docs/architecture.md
docs/anemic-model-dependency-maintenance-example.md
samples/Raffinert.Consistency.DependencyMaintenanceSample
```

Required wording:

```text
CoverageAdmission is structural baseline knowledge, not ObjectAdded.
DiscoverConsumers provides operation-scoped targeted consumer coverage; it does not make an ObjectSet complete.
Resolver completeness remains a host assertion.
Resolver evaluation closure is host-owned and must load required references.
Raffinert never invents Includes or queries.
Database/query concurrency and isolation remain host/database responsibilities.
Invisible DB mutations still require reconciliation/rebuild/publication.
Unsupported dependency shapes remain fail-closed and require Complete(set).
```

Keep the production-shaped sample acceptance case:

```text
three persisted consumers share one Source/PO line
only one consumer initially loaded/registered
mutate only shared source price/value
one resolver call
all three derived mirrors become correct
```

Do not claim the sample proves:

- detection of resolver under-fetch;
- cross-process serializability;
- unsupported projected/relation discovery.

Suggested commit:

```text
docs: finalize external consumer discovery guarantees
```

---

# Task 219 — release proof and roadmap closeout

Do not close this roadmap based on a green incremental CI run alone.

## Required local commands

Run exactly:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj \
  -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj \
  -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Do not skip a command and still mark the plan complete.

## Public API gate

Expected public API remains only the existing `DiscoverConsumers<TRoot,TTarget>(...)` addition from the earlier wave.

Do not expose:

```text
CoverageAdmission
StructuralMutations
ExternalConsumerDescriptor
ExternalConsumerRequest
resolver metadata wrappers
rollback handles
coverage proof internals
```

## Remote proof

After the final implementation commit:

1. push it;
2. inspect the GitHub Actions run for that exact SHA;
3. require conclusion `success`;
4. record the exact proof SHA in this document and `docs/roadmaps/README.md`;
5. only then mark Tasks 211–219 complete.

---

# Required final test inventory

Before closeout, dedicated automated tests must prove all categories below.

## Structural admission semantics

- no ObjectAdded origin for coverage admission;
- no semantic relation AddedPairs solely from baseline admission;
- no policy request solely from baseline admission;
- relation baseline state invisible after planning rollback;
- projection baseline state invisible after planning rollback;
- exact relation/projection baseline installed after commit;
- runtime version increments once.

## Admission identity

- same reference from two resolvers admitted once;
- same key/different reference fails closed;
- domain Added remains domain Added;
- already registered root not admitted again.

## Evaluation closure

- missing sibling Include/unloaded null fails;
- loaded optional null accepted;
- detached required target fails;
- fully loaded closure succeeds.

## Policy/scope

- Validate vs RecalculateAndValidate behavior;
- invariant activation/inactivity;
- unsupported sibling/upstream navigation remains fail-closed;
- same CLR type/different ObjectSet isolation;
- relation/projected scope requirements never substituted.

## Overlay/batching

- retarget-away;
- retarget-in;
- Added;
- Deleted;
- unknown Modified/Deleted fail closed;
- multi-target batching;
- multi-member batching;
- one call per navigation;
- Complete(set) zero calls;
- superset filtering.

## Failure/workflow

- resolver failure atomicity;
- planning failure atomicity;
- SQL failure structural/runtime rollback;
- framework mirror rollback;
- same-context retry;
- async cancellation;
- manual UoW exact plan install;
- later open-world save re-runs resolver.

---

# Anti-shortcut rules for weak agents

Do not make tests green by any of the following:

- reintroducing `CoverageAdmission : IAddedMutation`;
- treating coverage admission as a business `ObjectAdded` in provenance;
- hiding baseline relation `AddedPairs` only in result rendering while still feeding them into dependency propagation;
- adding `ConsistencyScope.Complete(rootSet)` to an open-world discovery test;
- preloading all consumers before the mutation;
- manually invoking resolver code from tests/handlers;
- auto-calling `Reference.Load()`;
- generating Includes;
- weakening unknown Modified/Deleted guards;
- ignoring duplicate admissions by key without checking reference identity;
- reconciling different CLR instances with the same runtime key;
- skipping projection/relation rollback tests because projected external discovery is out of scope;
- mutating live runtime before SQL to simplify installation;
- swallowing resolver/query/database exceptions;
- weakening assertions from runtime + EF tracked state + DB to DB only;
- creating new public APIs to expose structural internals;
- claiming green CI proves contracts that still have no dedicated tests.

If a required red test cannot be written without changing public API, stop and document the obstacle instead of exposing internals.

---

# Final acceptance checklist

Tasks 211–219 are complete only when all are true:

- [ ] `CoverageAdmission` remains structurally separate from `ObjectAdded`.
- [ ] relation touched-state capture includes coverage admissions on both left and right sets.
- [ ] planning rollback leaves no admitted relation membership in live runtime.
- [ ] projection state capture/validation receives the full structural mutation set.
- [ ] planning rollback leaves no admitted projection state in live runtime.
- [ ] exact forward patch installs relation/projection baseline once after durability.
- [ ] coverage admission creates no ObjectAdded mutation origin.
- [ ] coverage admission creates no semantic relation AddedPairs by itself.
- [ ] pending admissions are deduplicated explicitly, without `IAddedMutation` assumptions.
- [ ] same key/different reference remains fail-closed.
- [ ] generic evaluation closure distinguishes unloaded null from loaded null.
- [ ] no automatic loading/query generation was added.
- [ ] active-policy and scope substitution matrix is proven.
- [ ] ChangeTracker overlay and batching matrix is proven.
- [ ] resolver misuse matrix is proven.
- [ ] SQL failure restores framework-written mirrors and structural runtime state remains unchanged.
- [ ] same-DbContext retry is proven.
- [ ] cancellation and manual-UoW paths are proven.
- [ ] later open-world operations re-run discovery.
- [ ] dogfood remains production-shaped with one shared-source mutation and one resolver call.
- [ ] no accidental public API expansion.
- [ ] all required local verification commands pass.
- [ ] GitHub Actions is green for the exact final implementation SHA.
- [ ] Tasks 202–210 are recorded as partially implemented/superseded by this wave.
- [ ] Tasks 211–219 are closed with the exact proof SHA recorded.
