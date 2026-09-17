# Codex implementation plan — post-`ccfb24f` external-consumer-discovery gap closeout

Status: **ACTIVE FOLLOW-UP PLAN**

Verified remote baseline: `ccfb24fd485488d5680271545232e7ed6f26f031`

Underlying implementation commit: `e3d050fa0fe6a62422da1ce261f4183bf66e347e`

Verified CI: GitHub Actions run **280**, exact head SHA `ccfb24fd485488d5680271545232e7ed6f26f031`, conclusion **success**.

Tasks: **237–243**

This plan exists because the implementation that was previously reported as “fully implemented” is now actually pushed and CI is green, but remote review still found one concrete production-code hole plus a substantial set of unproven contract cases from Tasks 220–228.

Do **not** re-run or rewrite the entire previous roadmap. Preserve the good work already present. Close only the verified gaps below.

---

# 0. Remote verification summary

## 0.1 What is now genuinely present and should be preserved

Current `main` contains the structural-admission implementation and tests that were previously only local.

Verified useful changes include:

- `PreparedMutation.StructuralMutations` is now frozen instead of allocating a fresh concatenated array on every access;
- `MutationCollections.BuildStructuralMutations(...)` provides a shared validation-stage structural view;
- `CaptureDependencyPatchEntryCount(...)` now passes `prepared.StructuralMutations`;
- rollback/forward patch capture uses the canonical structural mutation view;
- relation touched-root capture includes `CoverageAdmission` on left and right sides;
- projection snapshot/final-state paths include `CoverageAdmission`;
- Core coverage-admission planning tests exist for relation rollback/install, projection rollback/install, and injected forward-patch install failure;
- EF tests exist for basic discovery, same-reference resolver dedupe, tracked Added preservation, SQL-failure retry, planning failure, cancellation, later open-world discovery, missing sibling reference, loaded optional null, detached return, policy activation, already-registered roots, unknown Modified/Deleted fail-closed, safe resolver supersets, basic batching, and duplicate rows;
- public API baselines include `DiscoverConsumers`;
- README / architecture / EF docs include the structural-admission and host-query boundaries;
- exact current `main` has green CI that builds, runs Core tests on .NET 8 and .NET 10, runs EF tests, runs samples, verifies formatting, packs, and smoke-tests packages.

Do **not** weaken or delete any of these behaviors to make later tests pass.

## 0.2 Confirmed production-code defect still present

The current `DependencyGraphRuntime.CaptureState(...)` signature correctly says `structuralMutations`, but its relation-right source collection is still incomplete.

Current code contains the equivalent of:

```csharp
var rights = ...
    .Concat(structuralMutations.Select(mutation => mutation switch
    {
        IAddedMutation added when ReferenceEquals(added.Set, node.Relation.RightSet) => added.Instance,
        ObjectRemoved removed when ReferenceEquals(removed.Set, node.Relation.RightSet) => removed.Instance,
        _ => null
    }).OfType<object>());
```

`CoverageAdmission` is missing from this switch.

This is not merely a naming issue. A right-side coverage admission is a structural membership mutation and can change relation baseline state for existing left sources. The dependency snapshot must include every affected left whose dependency state may be touched while planning/installing that structural baseline.

The previous source-local helper test does **not** prove this relation-right case.

## 0.3 Important contract cases still absent from the pushed test tree

Remote review did not find equivalent proof for several required cases, including at least:

```text
Different_instances_with_same_runtime_key_across_resolvers_fail_closed
All_required_references_loaded_and_tracked_pass
Evaluation_closure_uses_exact_ObjectSet_and_navigation_metadata
Supported_direct_navigation_with_exact_resolver_can_replace_navigation_complete_scope
Supported_and_unsupported_navigation_on_same_set_remain_fail_closed
Upstream_derived_navigation_requirement_is_preserved
All_active_navigation_obligations_require_exact_resolvers
Same_CLR_type_in_two_ObjectSets_does_not_cross_satisfy_resolver
RelationSourceCoverage_is_never_substituted_by_DiscoverConsumers
RelationTargetCoverage_is_never_substituted_by_DiscoverConsumers
ProjectedConsumerCoverage_is_never_substituted_by_DiscoverConsumers
Retargeted_away_consumer_is_excluded
Retargeted_in_registered_consumer_is_included
Tracked_Deleted_consumer_is_excluded
Two_changed_members_same_target_same_navigation_one_resolver_call
Source_and_Target_changes_one_call_per_navigation
Same_target_reference_is_deduplicated_within_batch
Wrong_exact_ObjectSet_mapping_fails
Missing_required_resolver_fails_before_sql
```

Some neighboring behavior is tested. That does not make these distinctions redundant. Add the exact missing proof or document the exact existing test that is truly equivalent.

---

# 1. Non-negotiable semantics

Keep these semantics frozen.

## 1.1 Domain lifecycle is not structural admission

```text
Domain lifecycle:
    ObjectAdded
    ObjectRemoved

Structural membership:
    CoverageAdmission
    ObjectAdded
    ObjectRemoved
```

`CoverageAdmission` means:

> an already-existing persisted object becomes known to the runtime as baseline state for this operation.

It does **not** mean:

> the domain object was added by this operation.

Therefore coverage admission must not, by itself, create:

```text
MutationOriginKind.ObjectAdded
semantic RelationMutationImpact.AddedPairs
business lifecycle-add callbacks as externally observable domain events
public causal evidence claiming a domain add
```

It **must** still update all runtime-owned structural baseline needed for correct future evaluation.

## 1.2 Structural baseline updates may require internal dependency rebasing

Do not confuse “no semantic domain add” with “do not update dependency state”.

If an admitted right-side relation member changes the correct relation-derived value for an existing left source, the runtime must internally establish the correct post-admission baseline.

Allowed:

```text
internal cache invalidation/rebase
internal dependency snapshot/restore
forward-patch installation of baseline state
```

Forbidden:

```text
faking ObjectAdded provenance
emitting semantic AddedPairs solely because coverage became known
pretending the admission was a user mutation
```

## 1.3 Planning atomicity remains absolute

Before durability, planning must leave live runtime state exactly unchanged, including:

```text
object-set membership
relation indexes
navigation indexes
projection indexes
derived state
invariant state
dependency dirty/invalid state
runtime.Version
diagnostics
policy counters
causal state
```

## 1.4 EF discovery boundaries remain frozen

Do not add:

```text
automatic Reference.Load()
automatic Include generation
lazy-loading assumptions
multi-hop discovery
collection-navigation discovery
relation-predicate discovery
projected external discovery
```

Resolver result completeness and evaluation closure remain host responsibilities.

---

# 2. Mandatory weak-agent execution discipline

For every task:

```text
1. git fetch origin
2. verify the current remote branch SHA
3. ensure git status --short is empty
4. add/strengthen the focused regression test first
5. run only that focused test
6. when the task describes a confirmed defect, verify the new test fails before the production fix
7. make the smallest production change
8. re-run the focused test
9. run the affected test project
10. commit
11. push
12. verify the pushed SHA exists remotely
13. record the SHA in the completion log
```

A task is **not complete** if the change exists only locally.

Do not report “fully implemented” until Task 243 succeeds.

Do not delete or weaken assertions to get green CI.

Do not replace exact state assertions with only `Assert.DoesNotThrow` / “save succeeds”.

---

# Task 237 — fix relation-right coverage-admission dependency snapshot and baseline rebasing

This is the first task because it contains a confirmed production-code defect.

## 237.1 Add the missing `CoverageAdmission` branch in relation-right dependency snapshot discovery

Target:

```text
src/Raffinert.Consistency/Dependencies/DependencyGraphRuntime.cs
DependencyGraphRuntime.CaptureState(...)
```

The relation-right structural source collection must treat:

```text
ObjectAdded
CoverageAdmission
ObjectRemoved
```

as structural membership inputs.

Do not solve this by changing `CoverageAdmission` to implement `IAddedMutation`. That would reintroduce the semantic bug fixed earlier.

The narrow expected shape is equivalent to:

```csharp
CoverageAdmission admission
    when ReferenceEquals(admission.Set, node.Relation.RightSet)
        => admission.Instance
```

but first prove the behavior with the tests below.

## 237.2 Add a relation-right dependency snapshot test that the existing helper test cannot accidentally satisfy

Create a Core test with:

```text
Left source set
Right relation-item set
relation Left <-> Right
relation-derived value on Left
one registered Left
one unregistered Right that matches Left
```

Prime the derived state for the Left before planning.

Required focused test:

```text
Coverage_admission_on_relation_right_captures_affected_left_dependency_state
```

The test must prove that a right-side `CoverageAdmission` causes the dependency snapshot to include the existing affected Left.

Do not merely assert `DependencyEntries > 0`; construct the scenario so omission of the admitted Right changes the exact expected dependency snapshot scope.

## 237.3 Prove planning rollback with primed derived state

Required test:

```text
Coverage_admission_on_relation_right_planning_restores_primed_derived_state
```

Sequence:

```text
1. register Left only
2. compute/cache the relation-derived value for Left
3. prepare CoverageAdmission(Right)
4. PlanDetailed without commit
5. assert runtime relation membership equals pre-plan baseline
6. assert derived value/state equals pre-plan baseline
7. assert dependency dirty/invalid state equals pre-plan baseline
8. assert runtime.Version unchanged
```

The test must fail if planning leaks dependency-state changes even when relation membership itself is restored.

## 237.4 Prove exact install establishes the new structural baseline without semantic add output

Required test:

```text
Coverage_admission_on_relation_right_exact_install_rebases_derived_without_semantic_add
```

After exact plan commit:

```text
Right is registered
relation baseline contains the pair
relation-derived value for Left reflects the admitted baseline
runtime.Version increments once
no MutationOriginKind.ObjectAdded for Right
no semantic RelationMutationImpact.AddedPairs caused solely by admission
```

If current architecture cannot update the relation-derived baseline without producing a semantic delta, introduce a narrow **internal structural rebase path**. Do not route the admission through normal domain `CommitAdd`.

## 237.5 Mirror invariant-state proof

If a relation-derived value feeds an invariant, add:

```text
Coverage_admission_on_relation_right_planning_restores_primed_invariant_state
```

This should prove rollback of invariant evaluation/cache state as well as the derived cache.

Suggested commit:

```text
fix: capture relation-right admission dependency state
```

Required before push:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0
```

---

# Task 238 — finish admission identity and evaluation-closure proof

This task is primarily tests. Change production code only when a focused test demonstrates a defect.

## 238.1 Different resolver instances with the same runtime key

The Core duplicate-admission test is not equivalent to the EF discovery boundary.

Required EF test:

```text
Different_instances_with_same_runtime_key_across_resolvers_fail_closed
```

Arrange two discovery resolvers so they return different CLR instances for the same consumer key during one operation.

Assert all of:

```text
InvalidOperationException before SQL
runtime.Version unchanged
no structural admission installed
no materialized mirror persisted
original user mutation remains tracked
```

Do not reconcile/merge the two instances.

## 238.2 Fully loaded evaluation closure positive proof

Required test:

```text
All_required_references_loaded_and_tracked_pass
```

The discovered root must have every required direct reference:

```text
non-null where required
tracked in the same DbContext
NavigationEntry.IsLoaded == true when appropriate
```

Assert discovery, evaluation, materialization, database durability, and runtime installation all succeed.

This must be a dedicated positive closure test, not just inferred from the broad happy-path test.

## 238.3 Exact ObjectSet + navigation metadata matching

Required test:

```text
Evaluation_closure_uses_exact_ObjectSet_and_navigation_metadata
```

Use either:

```text
same CLR type mapped to two different ObjectSets
```

or another setup where type-only matching would incorrectly satisfy closure.

Prove the closure validator matches the exact compiled object-set/navigation descriptor and fails closed for the wrong one.

## 238.4 Keep existing null semantics unchanged

Preserve:

```text
unloaded null => fail
loaded optional null => accepted
non-null detached => fail
```

Do not regress those tests while implementing exact metadata matching.

Suggested commit:

```text
test: complete discovery identity and evaluation closure proof
```

Required before push:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --filter "FullyQualifiedName~ExternalConsumerDiscoveryTests"
```

Then run the full EF test project.

---

# Task 239 — finish active-policy and authoritative-scope proof

Existing tests cover policy activation basics. They do not prove the resolver can replace **only** the exact supported navigation-consumer coverage obligation and nothing broader.

Add or identify exact equivalent tests for every item below.

## 239.1 Positive substitution case

Required:

```text
Supported_direct_navigation_with_exact_resolver_can_replace_navigation_complete_scope
```

Prove an exact `DiscoverConsumers` resolver satisfies only the supported direct-navigation consumer-coverage requirement for that path.

## 239.2 Supported + unsupported navigation on the same consumer set

Required:

```text
Supported_and_unsupported_navigation_on_same_set_remain_fail_closed
```

One supported direct reference has a resolver.

A second active unsupported navigation obligation on the same object set has no legal resolver path.

Assert:

```text
IncompleteConsistencyScopeException before SQL
runtime.Version unchanged
resolver for the supported path does not magically complete the set
```

## 239.3 Upstream-derived navigation obligation

Required:

```text
Upstream_derived_navigation_requirement_is_preserved
```

Prove discovery on a downstream direct reference does not erase a separate upstream dependency coverage requirement.

## 239.4 Multiple active direct-navigation obligations

Required:

```text
All_active_navigation_obligations_require_exact_resolvers
```

If two active navigation obligations exist, registering only one resolver must still fail closed.

## 239.5 Exact ObjectSet identity

Required:

```text
Same_CLR_type_in_two_ObjectSets_does_not_cross_satisfy_resolver
```

A resolver registered for ObjectSet A must not satisfy the coverage requirement for ObjectSet B merely because both use the same CLR type.

## 239.6 Discovery must never substitute relation/projected completeness

Required tests:

```text
RelationSourceCoverage_is_never_substituted_by_DiscoverConsumers
RelationTargetCoverage_is_never_substituted_by_DiscoverConsumers
ProjectedConsumerCoverage_is_never_substituted_by_DiscoverConsumers
```

For each fail-closed test assert:

```text
expected requirement kind remains in exception gaps
no SQL
runtime.Version unchanged
no framework materialization side effects
```

Suggested commit:

```text
test: complete discovery scope substitution boundaries
```

---

# Task 240 — finish ChangeTracker overlay, batching, and resolver guard matrix

Do not implement new discovery features. This task proves the existing supported feature under EF state overlays.

## 240.1 Retargeted away consumer must be excluded

Required:

```text
Retargeted_away_consumer_is_excluded
```

Arrange a tracked consumer that matched the changed target in the database/original state but whose tracked navigation/FK is now retargeted away.

The resolver SQL may return it, but the operation overlay must exclude it from admission/evaluation for the old target.

Assert no incorrect materialization and no incorrect runtime admission caused by stale database membership.

## 240.2 Retargeted in registered consumer must be included

Required:

```text
Retargeted_in_registered_consumer_is_included
```

Arrange a tracked/registered consumer moved to the changed target in the current EF state even if database membership still reflects the old target.

Current tracked state must win.

## 240.3 Tracked Deleted known consumer must be excluded

Required:

```text
Tracked_Deleted_consumer_is_excluded
```

This is different from the existing **unknown existing Deleted consumer fails closed** case.

For a consumer already known to the runtime and marked Deleted in the DbContext, discovery must not re-admit/re-evaluate it as a surviving consumer.

## 240.4 Batching — two changed members on one target

Required:

```text
Two_changed_members_same_target_same_navigation_one_resolver_call
```

Two relevant member changes on the same target must produce one resolver invocation for that navigation batch.

## 240.5 Batching — source and target navigation changes

Required:

```text
Source_and_Target_changes_one_call_per_navigation
```

Assert:

```text
source resolver == 1 call
target resolver == 1 call
no duplicate admission
one runtime commit/version increment
```

## 240.6 Batch target-reference dedupe

Required:

```text
Same_target_reference_is_deduplicated_within_batch
```

The target collection passed into a resolver must contain a reference only once even if multiple mutation paths nominate it.

Capture resolver input count explicitly.

## 240.7 Complete scope means zero discovery calls for every covered active path

Strengthen the existing complete-scope proof if needed so a case with both Source and Target direct-navigation resolvers demonstrates:

```text
source calls == 0
target calls == 0
```

## 240.8 Wrong ObjectSet resolver mapping

Required:

```text
Wrong_exact_ObjectSet_mapping_fails
```

Same CLR type is not enough. A resolver attached to the wrong compiled ObjectSet must not satisfy the request.

## 240.9 Missing required resolver

Required:

```text
Missing_required_resolver_fails_before_sql
```

An active supported navigation requirement without either complete scope or the exact resolver must fail before SQL durability.

## 240.10 Existing resolver exception proof may be reused only if truly equivalent

The existing `Discovery_planning_failure_does_not_install_structural_state` can satisfy the “resolver throws before SQL/runtime unchanged” requirement **only if** it asserts all required state:

```text
runtime.Version unchanged
no admission installed
no SQL durability
framework-written mirror restored/not applied
original user mutation remains tracked
```

If any assertion is missing, strengthen that existing test rather than adding a duplicate.

Suggested commit:

```text
test: complete discovery overlay batching and resolver guards
```

---

# Task 241 — deepen durability rollback and manual-UoW discovery proof

The existing same-DbContext retry test is useful but does not prove rollback of every runtime subsystem touched by a structurally admitted graph.

## 241.1 Rich SQL-failure rollback scenario

Create one deterministic persistence-failure case where discovery/admission touches more than a source-local derived property.

The scenario should exercise as many relevant runtime subsystems as practical:

```text
structural object-set admission
navigation index
projection index and/or relation baseline
dependency state
framework materialization
```

Required test:

```text
Discovery_SQL_failure_restores_runtime_and_framework_materialization
```

After injected SQL failure assert:

```text
runtime.Version unchanged
admitted object not registered
relation state equals baseline when exercised
projection state equals baseline when exercised
navigation state equals baseline when exercised
derived/invariant dependency state equals baseline
framework-written mirror CLR value restored
mirror PropertyEntry.IsModified restored
original user mutation remains tracked
```

## 241.2 Same-context retry must reuse the same DbContext after the rich failure

Keep or strengthen:

```text
Discovery_SQL_failure_can_retry_with_same_DbContext
```

The second attempt must:

```text
run discovery again as required
succeed using the same context
install structural state exactly once
persist the final correct mirror
increment runtime.Version exactly once total
```

## 241.3 Cancellation after discovery but before durability

Strengthen cancellation proof to include the same structural state dimensions used above when possible.

Required properties after cancellation:

```text
no runtime install
no durable SQL
no leaked mirror mutation
original user change remains retryable
```

## 241.4 Manual persistence UoW must explicitly exercise external discovery

Existing manual UoW tests prove generated-key and ordering semantics but do not prove `DiscoverConsumers` through the manual sequence.

Add a dedicated test, for example:

```text
Manual_UoW_discovery_plan_is_live_state_neutral_until_database_commit
```

Required sequence:

```text
CaptureConsistencyUnitOfWork
PrepareAndPlan
assert resolver has run
assert live runtime still unchanged
assert discovered root still not installed in live runtime
perform caller-owned DB durability step
CommitAfterDatabaseCommit
assert exact planned structural state installed once
Dispatch
```

Also prove stale-plan protection still works if runtime version changes between planning and `CommitAfterDatabaseCommit`.

## 241.5 Preserve later open-world discovery

Keep the existing later-open-world save test. Extend only if needed to prove a newly inserted consumer is found after a previous successful operation even though earlier admissions remain registered.

Suggested commit:

```text
test: prove discovery durability and manual uow atomicity
```

---

# Task 242 — audit docs, roadmap state, and test-to-requirement mapping

Do this only after Tasks 237–241 are green.

## 242.1 Update release-candidate verification truthfully

`docs/release-candidate-verification.md` currently records `e3d050f` as local-only and says remote CI is pending.

That statement became stale after remote `main` `ccfb24f` passed CI run 280.

Do not merely replace the old paragraph immediately. After the new fixes, record the **new final SHA** and the exact successful CI run for that SHA.

## 242.2 Update the active roadmap pointer

`docs/roadmaps/README.md` must point to this plan as the active closeout plan until Task 243 completes.

Mark Tasks 220–228 as implemented/partially verified only after the final gap wave is complete.

Do not claim the older wave was complete at `e3d050f` if this follow-up changed production behavior.

## 242.3 Create a requirement-to-test audit table

Add a short table to this plan or a dedicated verification doc mapping every contract item below to an exact pushed test method:

```text
relation-right admission dependency snapshot
relation-right planning rollback
relation-right exact install without semantic add
duplicate key across resolver instances
fully loaded evaluation closure
exact ObjectSet/navigation closure
policy activation
supported direct-navigation substitution
unsupported obligation fail-closed
relation/projected coverage non-substitution
retargeted away
retargeted in
known tracked Deleted exclusion
unknown Modified/Deleted fail-closed
batching per navigation
target input dedupe
wrong ObjectSet resolver
missing resolver
safe superset filtering
AsNoTracking/detached failure
SQL failure rollback
same-context retry
cancellation
manual UoW discovery
later open-world discovery
```

If an existing test is used as equivalent proof, write its exact method name and one sentence explaining why it is equivalent.

“No test needed” is not acceptable for these entries.

## 242.4 Public API audit

Run public API verification and ensure Task 237–241 do not accidentally expose new public types/members merely to support tests.

Use internal test hooks / `InternalsVisibleTo` where appropriate.

Suggested commit:

```text
docs: record external discovery closeout proof
```

---

# Task 243 — exact final-SHA release proof

This is the only task after which the agent may say “fully implemented”.

## 243.1 Local clean-tree gate

Start from a clean tree:

```bash
git status --short
```

Expected:

```text
<empty>
```

Run at minimum:

```bash
dotnet build -c Release

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0

dotnet format --verify-no-changes
```

Run the same samples/package smoke tests expected by repository CI.

Do not substitute a filtered test run for the full project runs.

## 243.2 Push before claiming completion

After all task commits:

```bash
git push
```

Then verify remotely:

```text
remote branch/main contains the final SHA
required test methods are visible in the remote tree
```

## 243.3 Exact-SHA CI gate

Wait for/check the GitHub Actions run whose:

```text
head_sha == final pushed SHA
```

Required:

```text
status = completed
conclusion = success
```

A green run for `ccfb24f` is historical proof only after new fixes exist. It cannot certify a later SHA.

## 243.4 Completion report format

The agent’s final report must contain exactly these sections:

```text
Final SHA
Remote branch/main SHA
Task 237 commit SHA
Task 238 commit SHA
Task 239 commit SHA
Task 240 commit SHA
Task 241 commit SHA
Task 242 commit SHA
Core net8 test result
Core net10 test result
EF net10 test result
Build result
Formatting result
Sample/package smoke result
GitHub Actions run id
GitHub Actions head SHA
GitHub Actions conclusion
Requirement-to-test mapping location
git status --short result
```

If any field is missing, do not use the phrase “fully implemented”.

---

# 3. Anti-shortcut checklist

Before closing this plan, explicitly verify all of these are false:

```text
[ ] CoverageAdmission was made to implement IAddedMutation
[ ] relation-right dependency snapshot still ignores CoverageAdmission
[ ] a test only checks relation rollback but not primed dependency state
[ ] a resolver for one ObjectSet satisfies another ObjectSet by CLR type
[ ] a direct-navigation resolver is treated as full ObjectSet completeness
[ ] relation source/target completeness is replaced by DiscoverConsumers
[ ] projected consumer completeness is replaced by DiscoverConsumers
[ ] EF current-state retargeting is ignored in favor of stale DB query rows
[ ] known tracked Deleted consumers are rediscovered as live
[ ] resolver input contains duplicate target references
[ ] AsNoTracking results are admitted
[ ] detached required references are accepted
[ ] unloaded null is confused with loaded optional null
[ ] failed SQL leaves structural runtime state installed
[ ] failed SQL leaves framework mirror IsModified state changed
[ ] manual UoW installs discovery state before DB durability
[ ] completion is reported before push
[ ] CI belongs to a different SHA
```

---

# 4. Intended final state

When Tasks 237–243 are complete, the claim should be supportable that:

> External consumer discovery for the supported direct-reference-navigation case is operation-scoped, structurally atomic, exact-ObjectSet-aware, ChangeTracker-overlay-aware, correctly batched, fail-closed outside its supported coverage boundary, retry-safe across persistence failure, compatible with manual persistence units of work, and does not confuse structural baseline admission with domain lifecycle addition.

Anything weaker means the plan is still active.

---

# 5. Completion log and requirement-to-test audit

Implementation checkpoints:

| Task | Commit |
|---|---|
| 237 | `cbe5387e1318be66548c50b99aaffbc4c23c91f1` |
| 238 | `eba8ec9d43a9f26d04fabb77cd80659945219008` |
| 239 | `159e3d2ce13e96f106c13345aa21fd188b01008d` |
| 240 | `fcd5eb1960b4a5c46a955334405f980b3b12fe41` |
| 241 | `ed383a919ca0a41fe75a2c00a59de265de4cbf5a` |

All tests below are in `CoverageAdmissionPlanningTests` or `ExternalConsumerDiscoveryTests`.

| Contract item | Exact test method / equivalent proof |
|---|---|
| relation-right admission dependency snapshot | `Coverage_admission_on_relation_right_captures_affected_left_dependency_state` |
| relation-right planning rollback | `Coverage_admission_on_relation_right_planning_restores_primed_derived_state` and `Coverage_admission_on_relation_right_planning_restores_primed_invariant_state` |
| relation-right exact install without semantic add | `Coverage_admission_on_relation_right_exact_install_rebases_derived_without_semantic_add` |
| duplicate key across resolver instances | `Different_instances_with_same_runtime_key_across_resolvers_fail_closed` |
| fully loaded evaluation closure | `All_required_references_loaded_and_tracked_pass` |
| exact ObjectSet/navigation closure | `Evaluation_closure_uses_exact_ObjectSet_and_navigation_metadata` |
| policy activation | `Validate_ignores_materialization_only_discovery`, `RecalculateAndValidate_activates_materialization_discovery`, and `Enforced_invariant_uses_discovery_in_Validate` cover each active save policy |
| supported direct-navigation substitution | `Supported_direct_navigation_with_exact_resolver_can_replace_navigation_complete_scope` |
| unsupported obligation fail-closed | `Supported_and_unsupported_navigation_on_same_set_remain_fail_closed`, `Upstream_derived_navigation_requirement_is_preserved`, and `All_active_navigation_obligations_require_exact_resolvers` |
| relation/projected coverage non-substitution | `RelationSourceCoverage_is_never_substituted_by_DiscoverConsumers`, `RelationTargetCoverage_is_never_substituted_by_DiscoverConsumers`, and `ProjectedConsumerCoverage_is_never_substituted_by_DiscoverConsumers` |
| retargeted away | `Retargeted_away_consumer_is_excluded` |
| retargeted in | `Retargeted_in_registered_consumer_is_included` |
| known tracked Deleted exclusion | `Tracked_Deleted_consumer_is_excluded` |
| unknown Modified/Deleted fail-closed | `Unknown_existing_Modified_consumer_fails_closed` and `Unknown_existing_Deleted_consumer_fails_closed` |
| batching per navigation | `Two_changed_members_same_target_same_navigation_one_resolver_call` and `Source_and_Target_changes_one_call_per_navigation` |
| target input dedupe | `Same_target_reference_is_deduplicated_within_batch` |
| wrong ObjectSet resolver | `Wrong_exact_ObjectSet_mapping_fails` and `Same_CLR_type_in_two_ObjectSets_does_not_cross_satisfy_resolver` |
| missing resolver | `Missing_required_resolver_fails_before_sql` |
| safe superset filtering | `Safe_resolver_superset_is_filtered` |
| AsNoTracking/detached failure | `AsNoTracking_or_detached_return_fails` and `Non_null_detached_required_reference_fails_before_sql` |
| SQL failure rollback | `Discovery_SQL_failure_restores_runtime_and_framework_materialization` |
| same-context retry | `Discovery_SQL_failure_can_retry_with_same_DbContext` |
| cancellation | `Discovery_cancellation_before_durability_leaves_runtime_and_materialization_unchanged` |
| manual UoW discovery | `Manual_UoW_discovery_plan_is_live_state_neutral_until_database_commit` and `Manual_UoW_discovery_rejects_stale_plan_after_database_commit` |
| later open-world discovery | `Later_open_world_save_reruns_consumer_resolver` |
