# Codex implementation plan — post-`96c076d` structural-admission verification and closeout

Status: **ACTIVE FOLLOW-UP PLAN**

Baseline commit: `96c076d3702980784a6721228cc7c28ede28e065`

Tasks: **220–228**

This plan exists because commit `96c076d` fixes several production-code defects from `docs/codex-plan-external-consumer-discovery-structural-admission-finalization.md`, but it does **not** complete that roadmap. Treat `96c076d` as a partial implementation, not as release proof.

Do not mark Tasks 211–219 complete until every proof obligation below is implemented and the exact final SHA passes CI.

---

# Verified status of `96c076d`

## Correct changes that must be preserved

Commit `96c076d` correctly moves the implementation in the intended direction:

- `CoverageAdmission` remains separate from `IAddedMutation` / domain `ObjectAdded` semantics;
- pending-admission reference deduplication now explicitly checks `CoverageAdmission`;
- pending-admission duplicate-key detection now explicitly checks `CoverageAdmission`;
- relation patch capture includes `CoverageAdmission` on both left and right relation sets;
- projection change detection recognizes `CoverageAdmission`;
- projection state capture receives structural mutations instead of lifecycle-only mutations;
- projected final-state validation receives `prepared.StructuralMutations`;
- mutation validation includes coverage admissions in structural target membership;
- generic EF evaluation-closure validation now distinguishes unloaded `null` from loaded `null` using EF navigation metadata;
- one two-resolver duplicate-admission regression test was added;
- GitHub Actions run 277 for exact SHA `96c076d3702980784a6721228cc7c28ede28e065` completed successfully.

Do **not** revert any of those behaviors merely to make a new test easier.

## Concrete unfinished production-code finding

`ConsistencyRuntime.CaptureDependencyPatchEntryCount(PreparedMutation prepared)` still calls:

```csharp
_dependencyGraph.CaptureState(
    impact,
    prepared.LifecycleMutations,
    prepared.Changes);
```

This is lifecycle-only and contradicts the structural-membership rule introduced by the same commit.

For any prepared operation containing a `CoverageAdmission`, the benchmark/proof helper can measure a different dependency patch from the real prepared operation. This violates Task 212's explicit helper-parity requirement.

The correct input must use the same structural mutation set as the real planning/rollback/forward-patch path.

## Structural-mutation source of truth is still fragmented

`96c076d` added:

```csharp
internal IReadOnlyList<RuntimeMutation> StructuralMutations =>
    CoverageAdmissions.Cast<RuntimeMutation>().Concat(LifecycleMutations).ToArray();
```

but other code still reconstructs the same concept independently, including:

```text
MutationCommit.CapturePatchParts(...)
MutationValidation.ValidateMutations(...)
```

Do not leave three independently reconstructed definitions of “structural mutation”. The invariant must be defined once and reused where possible.

Also do not keep `StructuralMutations` as a computed property that allocates a new array on every access. Freeze it once during preparation/validation and treat it as immutable operation state.

## Test proof is still far below the active roadmap requirement

At baseline `96c076d`, `ExternalConsumerDiscoveryTests` contains only these three dedicated tests:

```text
Source_change_discovers_unloaded_consumers_and_materializes_all_rates_async
Complete_scope_skips_external_resolver
Same_unloaded_root_returned_by_two_navigation_resolvers_is_admitted_once
```

The active Tasks 211–219 require structural rollback/install tests, admission identity/key tests, evaluation-closure tests, policy/scope tests, ChangeTracker overlay tests, batching tests, resolver misuse tests, persistence-failure/retry/cancellation tests, manual-UoW proof, open-world repeat-operation proof, docs audit, public-API audit, and final exact-SHA release proof.

A green CI run does not substitute for those missing tests.

---

# Non-negotiable semantics

Keep these rules frozen for Tasks 220–228.

## Domain lifecycle and structural admission are different concepts

```text
Domain lifecycle mutations
    ObjectAdded
    ObjectRemoved

Structural membership mutations
    CoverageAdmission
    ObjectAdded
    ObjectRemoved
```

Use **structural mutations** for:

- object-set membership simulation;
- rollback snapshot scope;
- forward-patch scope;
- relation touched-root scope;
- navigation touched-root scope;
- projection snapshot scope;
- dependency-state snapshot scope;
- structural validation helpers;
- benchmark/proof helpers that claim to represent a prepared operation.

Use **domain lifecycle only** for:

- `MutationOriginKind.ObjectAdded` / `ObjectRemoved`;
- business lifecycle policy semantics;
- semantic relation `AddedPairs` / `RemovedPairs` caused by actual domain membership changes;
- public causal explanation of actual domain additions/removals.

A `CoverageAdmission` establishes baseline knowledge. It must never become a semantic domain addition.

## Planning atomicity

Before durability:

```text
PlanDetailed / PrepareAndPlan
    must leave the live runtime exactly unchanged
```

That includes:

```text
object-set membership
relation membership
navigation indexes
projection indexes
dependency state
runtime.Version
public diagnostics
policy counters / causal state
```

After exact plan installation:

```text
all planned baseline state is installed exactly once
runtime.Version increments exactly once
```

## EF discovery contract

Still forbidden:

- automatic `Reference.Load()`;
- automatic `Include(...)` generation;
- lazy-loading assumptions;
- projected external consumer discovery;
- relation-predicate external consumer discovery;
- collection-navigation external consumer discovery;
- multi-hop external discovery;
- weakening the unknown existing Modified/Deleted fail-closed boundary.

Resolver completeness and evaluation closure remain host responsibilities.

---

# Mandatory weak-agent workflow

For every task below:

```text
1. Read the task completely before editing code.
2. Locate the exact existing production path named by the task.
3. Add the smallest focused regression test first.
4. Run only that test.
5. If the test is green before the intended production change, the test is insufficient; strengthen it.
6. Make the smallest production change needed.
7. Re-run the focused test.
8. Run the complete affected test project.
9. Do not perform unrelated cleanup.
10. Commit the task separately.
```

When a task is proof-only, do not change production code unless a newly added test exposes a real defect.

Never declare a task complete based only on code inspection.

---

# Task 220 — freeze one structural-mutation representation and fix helper parity

This task contains a known production defect and must be done first.

## 220.1 Freeze `PreparedMutation.StructuralMutations`

Do not keep this implementation:

```csharp
internal IReadOnlyList<RuntimeMutation> StructuralMutations =>
    CoverageAdmissions.Cast<RuntimeMutation>().Concat(LifecycleMutations).ToArray();
```

It allocates a new array on every access and makes it easier for callers to reconstruct the concept differently.

Prefer:

```text
constructor receives/finalizes lifecycle + coverage admissions
constructor builds structural mutation array once
property returns the same immutable/read-only instance thereafter
```

Possible shape:

```csharp
internal IReadOnlyList<RuntimeMutation> StructuralMutations { get; }
```

initialized exactly once in the constructor.

Do not expose it publicly.

## 220.2 Remove lifecycle-only dependency patch measurement

Fix:

```text
ConsistencyRuntime.CaptureDependencyPatchEntryCount
```

so it passes the same structural mutation set used by the real patch path.

Required focused test name or equivalent:

```text
Dependency_patch_entry_count_includes_coverage_admission_scope
```

The test must construct a prepared mutation with a coverage admission whose dependency snapshot contributes state that lifecycle-only input would omit.

Assertions must prove the helper matches the actual forward/rollback patch dependency scope; do not assert only that the value is non-zero.

## 220.3 Audit all prepared-operation helpers

Search for all uses of:

```text
prepared.LifecycleMutations
lifecycleMutations
coverageAdmissions.Cast<RuntimeMutation>().Concat(...)
Concat(coverageAdmissions)
```

Classify every occurrence as either:

```text
SEMANTIC DOMAIN LIFECYCLE
or
STRUCTURAL MEMBERSHIP
```

For each structural-membership occurrence, use the canonical structural view.

Explicitly inspect:

```text
CaptureDependencyPatchEntryCount
CaptureInstallRollbackJournal
ValidatePlanInstallForBenchmark
GetForwardPatchScopeCounts
CapturePatchParts
ValidateProjectedFinalState
DependencyGraph.CaptureState callers
ProjectionIndexRegistry.CaptureState callers
ProjectionIndexRegistry.ValidateFinalState callers
FinalSetMembershipView construction
```

If an occurrence intentionally remains lifecycle-only, add a one-line code comment only when the reason is non-obvious. Do not add explanatory comments everywhere.

## 220.4 Validation-stage structural view

`ValidateMutations(...)` runs before `PreparedMutation` exists, so it may need a local structural collection. Do not independently redefine semantics ad hoc.

Prefer a narrow internal helper such as:

```text
BuildStructuralMutations(lifecycle, coverageAdmissions)
```

or an immutable validated-batch member reused by preparation.

The same helper/representation must define the ordering consistently.

Preserve ordering:

```text
CoverageAdmissions first or lifecycle first
```

only if existing runtime behavior depends on it. Otherwise choose one order and add a focused determinism test. Do not silently change ordering during this task.

Suggested commit:

```text
fix: unify structural mutation patch scope
```

---

# Task 221 — add missing Core structural-admission atomicity tests

Create:

```text
tests/Raffinert.Consistency.Tests/CoverageAdmissionPlanningTests.cs
```

Use internal test access. Do not expose new public API.

Create the smallest models possible. Do not involve EF Core in these tests.

## 221.1 Relation rollback — admitted right side

Required test:

```text
Coverage_admission_on_relation_right_is_invisible_after_planning
```

Arrange:

```text
registered Left L
unregistered persisted Right R
relation predicate L <-> R is true
prepared operation contains CoverageAdmission(R)
```

Capture before planning:

```text
runtime.Version
runtime registration state
relation lookup result
relation diagnostics / pair count where available
```

Call binding planning without committing.

Assert exact equality with pre-plan state.

The assertion must fail if relation membership leaked during planning.

## 221.2 Relation rollback — admitted left side

Required mirror test:

```text
Coverage_admission_on_relation_left_is_invisible_after_planning
```

Do not assume relation runtime implementation is symmetric without proving both sides.

## 221.3 Exact relation install

Required test:

```text
Coverage_admission_relation_baseline_installs_once_after_exact_plan_commit
```

Assert:

```text
not registered before commit
registered after commit
relation pair absent before commit
relation pair present after commit
runtime.Version += 1 exactly
no ObjectAdded MutationOrigin for admitted object
no semantic RelationMutationImpact.AddedPairs solely because object became known
```

## 221.4 Projection rollback

Required test:

```text
Coverage_admission_projection_state_is_invisible_after_planning
```

Use an internal projection scenario where admitting a downstream/root object updates reverse projection state.

Before planning capture at least:

```text
ReverseProjectionEntryCount
TargetCount
relevant projected lookup / propagation visibility
```

After planning but before commit all values must exactly match baseline.

## 221.5 Exact projection install

Required test:

```text
Coverage_admission_projection_state_installs_once_after_exact_plan_commit
```

Assert projection index state appears only after exact commit and does not duplicate on installation.

## 221.6 Forward-patch installation failure rollback

If there is an existing test hook:

```text
FailAfterNextForwardPatchApplyForTesting
```

add:

```text
Coverage_admission_forward_patch_install_failure_restores_relation_and_projection_state
```

Assert runtime registration, relation/projection state, diagnostics, and version all return to pre-install values.

Suggested commit:

```text
test: prove coverage admission runtime atomicity
```

---

# Task 222 — complete admission identity and duplicate-key proof

Extend `ExternalConsumerDiscoveryTests` or split into a narrowly named file if it becomes unwieldy.

The existing two-resolver test is not enough.

## 222.1 Strengthen existing same-reference test

For:

```text
Same_unloaded_root_returned_by_two_navigation_resolvers_is_admitted_once
```

also assert:

```text
runtime.Version == 1
admitted object is registered exactly once
no ObjectAdded MutationOrigin for the discovered root
materialized value correct
source resolver called once
target resolver called once
```

If no public direct registration-count API exists, prove single admission through stable observable behavior plus internal test access. Do not expose a new public API merely for this assertion.

## 222.2 Different CLR instances, same key

Required test:

```text
Different_instances_with_same_runtime_key_across_resolvers_fail_closed
```

Arrange:

```text
resolver A returns B1 key 42
resolver B returns different reference B2 key 42
```

Assert:

```text
InvalidOperationException before SQL
runtime.Version unchanged
neither B1 nor B2 structurally installed
original user mutation remains tracked
```

Do not reconcile the objects.

## 222.3 Tracked Added remains a domain add

Required test:

```text
Tracked_added_consumer_is_not_converted_to_coverage_admission
```

Assert:

```text
one ObjectAdded origin exists for the Added entity
no duplicate admission behavior
save succeeds
runtime version increments once
```

## 222.4 Already registered root

Required test:

```text
Already_registered_resolver_result_is_not_admitted_again
```

Assert normal propagation still occurs while no structural re-admission is created.

## 222.5 Same request duplicate rows

Required test:

```text
Resolver_duplicate_rows_same_reference_do_not_duplicate_admission
```

If EF query shape naturally identity-resolves duplicates, use a controlled resolver/test harness that still exercises candidate de-duplication without bypassing normal tracking.

Suggested commit:

```text
test: prove consumer admission identity rules
```

---

# Task 223 — prove EF evaluation closure completely

The production code was changed in `96c076d`; now prove the exact contract.

Use an association with:

```text
Source reference
Target reference
Derived = Source.UnitValue / Target.UnitValue
```

## 223.1 Missing sibling reference

Required test:

```text
Resolver_omitting_other_required_reference_fails_before_sql
```

Discovery request must be caused by `Source`.

Resolver query intentionally loads `Source` but does **not** load `Target`.

For the discovered root ensure:

```text
Target CLR value == null
Target NavigationEntry.IsLoaded == false
```

Assert failure before SQL and runtime unchanged.

This test is mandatory because validating only the request navigation would miss the defect.

## 223.2 Loaded optional null

Required test:

```text
Loaded_optional_null_required_reference_is_accepted
```

Use an actually optional EF reference and ensure:

```text
CLR value == null
NavigationEntry.IsLoaded == true
```

The closure validator must accept this state.

Do not weaken the formula semantics just to avoid the null; use a test dependency/evaluator that can legitimately tolerate optional null.

## 223.3 Detached non-null reference

Required test:

```text
Non_null_detached_required_reference_fails_before_sql
```

Assert runtime and database unchanged.

## 223.4 Fully loaded closure

Required test:

```text
All_required_references_loaded_and_tracked_pass
```

## 223.5 Metadata matching

Required test:

```text
Evaluation_closure_uses_exact_ObjectSet_and_navigation_metadata
```

Use same CLR type in two object sets or two navigation descriptors so incorrect type-only metadata matching cannot pass accidentally.

Suggested commit:

```text
test: prove discovered-root evaluation closure
```

---

# Task 224 — complete active-policy and authoritative-scope matrix

This is proof-first. Do not change design unless a test fails.

Required tests:

```text
Validate_ignores_materialization_only_discovery
RecalculateAndValidate_activates_materialization_discovery
Enforced_invariant_uses_discovery_in_Validate
Non_enforced_invariant_does_not_use_discovery
Unmaterialized_derived_definition_does_not_use_discovery
Supported_direct_navigation_with_exact_resolver_can_replace_navigation_complete_scope
Supported_and_unsupported_navigation_on_same_set_remain_fail_closed
Upstream_derived_navigation_requirement_is_preserved
All_active_navigation_obligations_require_exact_resolvers
Same_CLR_type_in_two_ObjectSets_does_not_cross_satisfy_resolver
RelationSourceCoverage_is_never_substituted_by_DiscoverConsumers
RelationTargetCoverage_is_never_substituted_by_DiscoverConsumers
ProjectedConsumerCoverage_is_never_substituted_by_DiscoverConsumers
```

For every fail-closed test assert all three:

```text
resolver query was not executed when capability is known insufficient
no SQL durability operation occurred
runtime.Version unchanged
```

Do not satisfy tests by adding `.Complete(rootSet)` unless that is the behavior under test.

Suggested commit:

```text
test: complete discovery policy and scope proof
```

---

# Task 225 — complete ChangeTracker overlay, batching, and resolver-misuse matrix

## 225.1 ChangeTracker overlay

Required tests:

```text
Retargeted_away_consumer_is_excluded
Retargeted_in_registered_consumer_is_included
Tracked_Added_consumer_remains_domain_add
Tracked_Deleted_consumer_is_excluded
Unknown_existing_Modified_consumer_fails_closed
Unknown_existing_Deleted_consumer_fails_closed
```

Assertions must include tracked state and runtime state, not DB rows only.

## 225.2 Batching

Required tests:

```text
Multiple_changed_targets_same_navigation_one_resolver_call
Two_changed_members_same_target_same_navigation_one_resolver_call
Source_and_Target_changes_one_call_per_navigation
Same_target_reference_is_deduplicated_within_batch
Complete_scope_zero_resolver_calls
```

Count resolver invocations explicitly.

Also assert all expected consumers are materialized/evaluated correctly so “one call” is not achieved by dropping targets.

## 225.3 Resolver misuse

Required tests:

```text
AsNoTracking_or_detached_return_fails
Wrong_exact_ObjectSet_mapping_fails
Different_instance_duplicate_runtime_key_fails
Safe_resolver_superset_is_filtered
Missing_required_resolver_fails_before_sql
Resolver_exception_propagates_before_sql_and_runtime_unchanged
```

For the safe superset case, make the resolver return at least one tracked root that does not currently point to any requested target. It must be filtered and not admitted.

Suggested commit:

```text
test: complete discovery overlay batching and guard matrix
```

---

# Task 226 — prove persistence failure, retry, cancellation, manual UoW, and open-world behavior

Do not claim atomicity until these tests exist.

## 226.1 Convenience-save SQL failure

Inject deterministic persistence failure after planning/materialization and before successful DB commit.

Required async test:

```text
Discovery_SQL_failure_restores_runtime_and_framework_materialization
```

Assert:

```text
runtime.Version unchanged
no discovered root registered
relation state equals baseline
projection state equals baseline
navigation state equals baseline
Raffinert-written mirror CLR values restored
mirror PropertyEntry.IsModified restored
original user mutation remains present
```

If a sync path has materially different code, add sync coverage too.

## 226.2 Same-context retry

After the failure above, disable the injected failure and retry with the **same DbContext**.

Required test:

```text
Discovery_SQL_failure_can_retry_with_same_DbContext
```

Assert resolver runs again as required and final state is correct.

## 226.3 Resolver/planning failure

Required test:

```text
Discovery_planning_failure_does_not_install_structural_state
```

## 226.4 Cancellation

Required async test:

```text
Discovery_cancellation_before_durability_leaves_runtime_and_materialization_unchanged
```

Cancellation must occur during/after discovery work, not before the operation starts, otherwise the test proves nothing about rollback.

## 226.5 Manual policy-aware unit of work

Required test:

```text
Manual_unit_of_work_installs_discovered_admission_only_after_database_commit
```

Exact sequence:

```text
CaptureConsistencyUnitOfWork
PrepareAndPlan
assert live runtime unchanged
perform caller-owned DB durability step
CommitAfterDatabaseCommit
assert exact forward state installed once
Dispatch remains separate
```

Assert `runtime.Version` increments exactly once.

Do not invent rollback semantics for arbitrary external SQL transactions beyond the documented manual-UoW contract.

## 226.6 Open-world repeat operation

Required test:

```text
Later_open_world_save_reruns_consumer_resolver
```

After one successful discovered save, create/add another persisted consumer externally/in a separate context, then perform a later relevant mutation using the original runtime without `Complete(set)`.

Assert the resolver runs again and the newly persisted consumer is discovered.

Previous discovery is runtime knowledge, not durable set completeness.

Suggested commit:

```text
test: prove discovery durability rollback and retry contracts
```

---

# Task 227 — documentation, sample, roadmap, and public API audit

Only after Tasks 220–226 are green.

Review:

```text
README.md
docs/architecture.md
docs/anemic-model-dependency-maintenance-example.md
docs/codex-plan-external-consumer-discovery-structural-admission-finalization.md
docs/roadmaps/README.md
samples/Raffinert.Consistency.DependencyMaintenanceSample
```

Required statements:

```text
CoverageAdmission is structural baseline knowledge, not ObjectAdded.
DiscoverConsumers is operation-scoped targeted consumer coverage.
DiscoverConsumers does not make an ObjectSet complete.
Resolver result completeness is a host assertion.
Resolvers must load the references needed for evaluation closure.
Raffinert does not invent Includes or auto-load references.
Unsupported dependency shapes remain fail-closed.
Database/query isolation and concurrency remain host/database responsibilities.
Invisible external DB mutations require later discovery/reconciliation/rebuild/publication.
```

## Sample acceptance case

Keep one production-shaped case:

```text
three persisted consumers share one Source/PO-like line
one consumer initially loaded/registered
mutate only shared source value
one resolver call
all three materialized mirrors become correct
```

Do not preload all consumers.

## Public API audit

Compare public API before and after the final implementation wave.

There must be no newly public:

```text
CoverageAdmission
StructuralMutations
ExternalConsumerDescriptor
ExternalConsumerRequest
resolver metadata wrappers
rollback/forward patch handles
internal structural proof helpers
```

If a test seems to require public exposure, use `InternalsVisibleTo` / existing internal test access instead.

## Roadmap status

Do not mark the previous plan complete yet. Update it only after Task 228 exact-SHA proof succeeds.

Suggested commit:

```text
docs: finalize external consumer discovery contract
```

---

# Task 228 — exact release proof and closeout

No production edits are allowed in this task. If a command fails, fix the failure in a separate commit and restart Task 228 from the top.

## 228.1 Required local commands

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

Do not skip a command.

## 228.2 Exact CI SHA

After pushing the final implementation/docs commit:

1. record its full SHA;
2. wait for the GitHub Actions run for that exact SHA;
3. require `status = completed`;
4. require `conclusion = success`;
5. do not substitute a CI run for an earlier SHA;
6. record the exact SHA and workflow run URL in the old structural-admission finalization plan and `docs/roadmaps/README.md`.

## 228.3 Final closeout checklist

Only then mark Tasks 211–219 and 220–228 complete.

Required final state:

- [ ] `CoverageAdmission` is not an `IAddedMutation`.
- [ ] one canonical structural-mutation representation is used for structural scope.
- [ ] dependency patch helper parity includes coverage admissions.
- [ ] relation planning rollback is proven for admitted left and right roots.
- [ ] projection planning rollback is proven.
- [ ] exact forward installation is proven.
- [ ] install-failure rollback is proven.
- [ ] coverage admission creates no `ObjectAdded` origin.
- [ ] coverage admission creates no semantic relation `AddedPairs` by itself.
- [ ] same-reference multi-resolver discovery admits once.
- [ ] same-key/different-reference discovery fails closed.
- [ ] tracked Added remains domain Added.
- [ ] already-registered roots are not re-admitted.
- [ ] unloaded required null reference fails before SQL.
- [ ] loaded optional null is accepted.
- [ ] detached required reference fails before SQL.
- [ ] active-policy matrix is proven.
- [ ] authoritative-scope substitution matrix is proven.
- [ ] ChangeTracker overlay matrix is proven.
- [ ] batching matrix is proven.
- [ ] resolver misuse matrix is proven.
- [ ] SQL-failure runtime rollback is proven.
- [ ] framework materialization rollback is proven.
- [ ] same-DbContext retry is proven.
- [ ] cancellation is proven.
- [ ] manual-UoW exact installation is proven.
- [ ] later open-world save re-runs discovery.
- [ ] docs state the narrow v1 guarantees without overclaiming.
- [ ] no accidental public API expansion.
- [ ] all required local commands pass.
- [ ] GitHub Actions succeeds for the exact final SHA.

---

# Anti-shortcut rules

A weak agent must **not** make the plan green by:

- reintroducing `CoverageAdmission : IAddedMutation`;
- converting structural admission into domain `ObjectAdded` provenance;
- filtering relation `AddedPairs` only at rendering time while semantic propagation still sees them;
- adding `Complete(rootSet)` to open-world discovery tests;
- preloading all consumers before the mutation;
- manually calling resolver logic from test code instead of going through the EF save/UoW integration;
- using `AsNoTracking()` results and attaching them manually just to bypass resolver guards;
- auto-loading missing references;
- generating Includes;
- weakening unknown Modified/Deleted guards;
- silently reconciling different CLR instances with the same key;
- asserting only database state when runtime/tracked state is part of the contract;
- removing rollback assertions because CI is green;
- exposing internal structural types publicly for test convenience;
- skipping RED-first proof for known defects;
- marking the old roadmap complete before exact-SHA CI proof.

---

# Definition of done

This follow-up is complete only when the implementation, automated tests, docs, samples, local verification, and exact-SHA CI together prove the contract.

`96c076d` is the **baseline**, not the completion SHA.
