# Codex implementation plan — external consumer discovery remote implementation recovery and proof

Status: **ACTIVE EXECUTION RECOVERY PLAN**

Remote baseline / current `main`: `9ef128bc3da88436fe7813869fdf5abc2db9c68c`

Supersedes completion claims for: Tasks **220–228** in `docs/codex-plan-external-consumer-discovery-post-96c076d-verification-and-closeout.md`

New execution tasks: **229–236**

---

# Why this plan exists

An implementation agent reported that:

```text
docs/codex-plan-external-consumer-discovery-post-96c076d-verification-and-closeout.md
```

was fully implemented.

Remote verification disproves that claim.

At verification time:

```text
main HEAD = 9ef128bc3da88436fe7813869fdf5abc2db9c68c
```

That SHA is only:

```text
docs: add post-96c076d structural admission closeout plan
```

There are no later commits on `main`, no implementation branch visible in the repository, and no pushed implementation SHA to inspect.

Expected proof artifacts from the plan are also absent on `main`, including tests such as:

```text
Coverage_admission_on_relation_right_is_invisible_after_planning
Dependency_patch_entry_count_includes_coverage_admission_scope
```

Therefore the previous agent completion report is invalid for repository state.

This plan is intentionally redundant and explicit. It is designed for an agent that may:

- edit files but forget to commit;
- commit but forget to push;
- run one test and claim the whole plan is complete;
- mark checklist items complete without adding the named tests;
- make tests pass by weakening assertions;
- silently skip tasks that look difficult;
- report local uncommitted state as repository implementation;
- confuse a green CI run for a docs-only commit with implementation proof.

None of those count as completion.

---

# Golden rule: remote SHA or it did not happen

For every task in this plan:

```text
1. Make the requested code/test/docs changes.
2. Run the required focused verification.
3. Commit the task.
4. Push the commit to the repository.
5. Record the pushed SHA in the task completion log.
6. Verify that GitHub can fetch that SHA.
```

A task is **NOT DONE** if any of the following is true:

```text
changes exist only in the working tree;
changes exist only in the local git repository;
commit has not been pushed;
pushed SHA is not reachable from the implementation branch;
required test names are absent from the pushed tree;
required command output was not actually run;
CI belongs to a different SHA;
CI belongs only to a docs commit;
```

Do not report “implemented” or “fully implemented” until Task 236 succeeds.

---

# Frozen semantics

Do not redesign the feature while executing this plan.

## Coverage admission is structural knowledge, not a domain add

Keep this distinction:

```text
Domain lifecycle
    ObjectAdded
    ObjectRemoved

Structural membership
    CoverageAdmission
    ObjectAdded
    ObjectRemoved
```

`CoverageAdmission` means:

> this persisted object becomes known to the runtime so existing persisted state can participate in evaluation.

It does **not** mean:

> this object was created by the current domain mutation.

Therefore a coverage admission must not, by itself, create:

```text
MutationOriginKind.ObjectAdded
semantic relation AddedPairs
business lifecycle add behavior
public explanation that the object was added
```

## Structural mutations must drive structural snapshots

Use the full structural mutation set for:

```text
object-set membership simulation
rollback snapshot scope
forward patch scope
relation touched-state capture
navigation touched-state capture
projection snapshot scope
dependency-state snapshot scope
projected final-state membership
structural validation
benchmark/proof helpers claiming to represent a prepared operation
```

Use domain lifecycle only for actual business lifecycle semantics.

## Planning must be atomic

Before durability:

```text
PlanDetailed / PrepareAndPlan
```

must leave the live runtime exactly as it was before planning.

That includes:

```text
object set membership
relation membership
navigation index state
projection index state
dependency state
runtime.Version
diagnostics
policy counters
causal state
```

After exact plan installation:

```text
planned structural baseline installed exactly once
semantic mutation effects installed exactly once
runtime.Version increments exactly once
```

## EF discovery boundaries stay frozen

Do not add:

```text
automatic Reference.Load()
automatic Include generation
lazy-loading assumptions
projected external consumer discovery
relation-predicate consumer discovery
collection-navigation consumer discovery
multi-hop consumer discovery
non-EF discovery providers
cross-process locking/serializability machinery
```

Do not weaken the fail-closed handling of previously unknown existing `Modified` or `Deleted` roots.

---

# Mandatory execution discipline

Every behavior-changing task must follow this exact order:

```text
A. Add or strengthen the focused regression test first.
B. Run only that test.
C. Confirm the test fails for the intended reason before the production fix.
D. If it is already green, strengthen the test; do not declare the defect absent.
E. Make the smallest production change.
F. Re-run the focused test until green.
G. Run the entire affected test project.
H. Commit.
I. Push.
J. Verify the pushed SHA exists remotely.
```

Proof-only tasks:

```text
add tests first
run them
change production code only if a test exposes a real defect
```

Do not perform opportunistic refactors.

Do not rename public APIs unless a task explicitly requires it.

Do not delete failing tests to obtain green CI.

Do not replace precise assertions with weaker “no exception” assertions.

---

# Task 229 — establish a remotely verifiable implementation line

This task exists because the previous agent claimed completion without any pushed implementation.

## 229.1 Start from the exact remote baseline

Before editing, run:

```bash
git fetch origin
git checkout main
git reset --hard origin/main
git status --short
git rev-parse HEAD
```

Required HEAD at task start:

```text
9ef128bc3da88436fe7813869fdf5abc2db9c68c
```

If remote `main` has advanced by the time this task is executed, stop using the literal SHA as the implementation base and record the actual remote HEAD. Do not silently build from stale local history.

Required `git status --short`:

```text
<empty>
```

## 229.2 Create one implementation branch

Use one branch for this recovery wave, for example:

```text
codex/external-consumer-discovery-closeout
```

Push it immediately before implementation:

```bash
git checkout -b codex/external-consumer-discovery-closeout
git push -u origin codex/external-consumer-discovery-closeout
```

Do not work only on an unpushed branch.

## 229.3 Add an execution log section to the existing closeout plan

At the bottom of:

```text
docs/codex-plan-external-consumer-discovery-post-96c076d-verification-and-closeout.md
```

add:

```markdown
## Remote execution log

- Task 220 / recovery Task 230: pending
- Task 221 / recovery Task 231: pending
- Task 222–223 / recovery Task 232: pending
- Task 224–225 / recovery Task 233: pending
- Task 226–227 / recovery Task 234: pending
- Task 228 / recovery Tasks 235–236: pending
```

Do not mark anything complete yet.

Suggested commit:

```text
chore: start external discovery closeout execution
```

Push it and record the SHA.

Acceptance:

- [ ] implementation branch exists remotely;
- [ ] branch is based on current remote main;
- [ ] working tree was clean before changes;
- [ ] execution log exists in pushed tree;
- [ ] pushed task SHA recorded.

---

# Task 230 — actually implement structural-mutation canonicalization and helper parity

This maps to Task 220 of the previous plan.

## 230.1 Freeze `PreparedMutation.StructuralMutations`

Current baseline behavior must not remain as a computed allocation:

```csharp
internal IReadOnlyList<RuntimeMutation> StructuralMutations =>
    CoverageAdmissions.Cast<RuntimeMutation>().Concat(LifecycleMutations).ToArray();
```

Change it so structural mutations are constructed once and stored once.

Required properties:

```text
same instance returned on repeated access
read-only from callers
not public
contains exactly coverage admissions + domain lifecycle mutations
stable deterministic ordering
```

Preferred shape:

```csharp
internal IReadOnlyList<RuntimeMutation> StructuralMutations { get; }
```

initialized in `PreparedMutation` construction or in one shared internal builder used by validation/preparation.

Do not expose a mutable `List<T>`.

## 230.2 Fix the known remaining production defect

Audit:

```text
ConsistencyRuntime.CaptureDependencyPatchEntryCount(PreparedMutation prepared)
```

It must use the same structural mutation input as real dependency snapshotting.

It must not use lifecycle-only input for a prepared operation containing coverage admissions.

Required test:

```text
Dependency_patch_entry_count_includes_coverage_admission_scope
```

The test must compare the helper’s dependency snapshot scope with the actual prepared forward/rollback patch scope.

Do not use a weak assertion such as:

```csharp
Assert.True(count > 0);
```

Instead prove equality/expected exact membership or exact count for a setup where omitting the coverage admission changes the answer.

## 230.3 Eliminate ad-hoc structural reconstruction where a prepared operation already exists

Search all production code for:

```text
prepared.LifecycleMutations
coverageAdmissions.Cast<RuntimeMutation>()
Concat(lifecycleMutations)
Concat(coverageAdmissions)
```

Classify each occurrence as:

```text
DOMAIN_SEMANTIC
STRUCTURAL
```

Required audit targets:

```text
CaptureDependencyPatchEntryCount
CaptureInstallRollbackJournal
CaptureRollbackJournal
CaptureForwardPatch
CapturePatchParts
ValidatePlanInstallForBenchmark
GetForwardPatchScopeCounts
ValidateProjectedFinalState
DependencyGraph.CaptureState callers
ProjectionIndexRegistry.CaptureState callers
ProjectionIndexRegistry.ValidateFinalState callers
FinalSetMembershipView creation
```

Where a `PreparedMutation` exists and the concern is structural, use `prepared.StructuralMutations`.

Where only raw validated parts exist before `PreparedMutation`, use one shared internal `BuildStructuralMutations(...)` helper or one validated-batch property. Do not duplicate the membership definition.

## 230.4 Keep domain-semantic paths lifecycle-only

Do not accidentally switch these to structural admissions:

```text
ObjectAdded/ObjectRemoved mutation provenance
MutationOriginKind.ObjectAdded/ObjectRemoved
semantic relation added/removed deltas
business lifecycle callbacks/policies
```

Add a regression assertion if necessary to prove `CoverageAdmission` does not leak into those outputs.

## 230.5 Required focused tests

At minimum:

```text
Dependency_patch_entry_count_includes_coverage_admission_scope
Prepared_structural_mutations_are_frozen_and_stable
Coverage_admission_does_not_create_object_added_origin
Coverage_admission_does_not_create_semantic_relation_added_pair
```

If equivalent tests already exist under different names, do not duplicate unnecessarily; document exact names in the execution log.

Suggested commit:

```text
fix: canonicalize structural mutation state
```

Before pushing, run:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0
```

Push and record SHA.

Acceptance:

- [ ] canonical structural representation exists;
- [ ] no computed array allocation per property access;
- [ ] dependency patch measurement uses structural mutations;
- [ ] no semantic ObjectAdded leak;
- [ ] required focused tests pushed;
- [ ] Core tests green;
- [ ] task SHA pushed and recorded.

---

# Task 231 — prove Core planning/patch atomicity for coverage admissions

This maps to Task 221 of the previous plan.

Create or use:

```text
tests/Raffinert.Consistency.Tests/CoverageAdmissionPlanningTests.cs
```

Do not use EF Core for these tests.

## 231.1 Relation right-side planning rollback

Required test:

```text
Coverage_admission_on_relation_right_is_invisible_after_planning
```

Arrange:

```text
registered Left
unregistered persisted Right
relation predicate true for Left/Right
prepared mutation contains CoverageAdmission(Right)
```

Capture before planning:

```text
runtime.Version
runtime registration state for Right
relation lookup result
relation diagnostics/pair count
relevant dependency diagnostics if exposed
```

Call `PlanDetailed` or exact binding-plan API.

Do not commit.

Assert every captured value exactly equals pre-plan state.

## 231.2 Relation left-side planning rollback

Required test:

```text
Coverage_admission_on_relation_left_is_invisible_after_planning
```

Mirror the previous case. Do not assume internal symmetry.

## 231.3 Relation exact install

Required test:

```text
Coverage_admission_relation_baseline_installs_once_after_exact_plan_commit
```

Assert:

```text
not registered before commit
registered after commit
pair absent before commit
pair present after commit
runtime.Version increments exactly once
no ObjectAdded origin
no semantic relation AddedPairs solely due to admission
```

## 231.4 Projection planning rollback

Required test:

```text
Coverage_admission_projection_state_is_invisible_after_planning
```

Use a minimal projection scenario where admitting a downstream object would update reverse projection state.

Capture and compare at least:

```text
ReverseProjectionEntryCount
TargetCount
projected propagation visibility / lookup state
```

before and after planning without commit.

## 231.5 Projection exact install

Required test:

```text
Coverage_admission_projection_state_installs_once_after_exact_plan_commit
```

Assert the projection index appears exactly once after exact plan installation.

## 231.6 Forward-patch failure rollback

Use the existing internal fault hook if available:

```text
FailAfterNextForwardPatchApplyForTesting
```

Required test:

```text
Coverage_admission_forward_patch_install_failure_restores_structural_state
```

Capture before install:

```text
set membership
relation membership
projection state
dependency state diagnostics
runtime.Version
```

Inject failure after forward patch apply.

Assert every value is restored exactly.

Suggested commit:

```text
test: prove coverage admission planning atomicity
```

Required command:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0
```

Push and record SHA.

Acceptance:

- [ ] right relation rollback proven;
- [ ] left relation rollback proven;
- [ ] relation install proven;
- [ ] projection rollback proven;
- [ ] projection install proven;
- [ ] install-failure rollback proven;
- [ ] no semantic-add leakage proven;
- [ ] task SHA pushed and recorded.

---

# Task 232 — prove admission identity and EF evaluation closure

This maps to Tasks 222–223 of the previous plan.

Work in the EF Core test project.

## 232.1 Strengthen the existing same-root/two-resolver test

Test:

```text
Same_unloaded_root_returned_by_two_navigation_resolvers_is_admitted_once
```

Must assert all of:

```text
source resolver called once
target resolver called once
save succeeds
runtime.Version == 1
root is registered once
materialized derived value correct
no ObjectAdded mutation origin for the discovered persisted root
```

Do not use database row count as the only proof of one admission.

## 232.2 Different references with the same runtime key

Required test:

```text
Different_instances_with_same_runtime_key_across_resolvers_fail_closed
```

Arrange two tracked CLR instances representing the same runtime key through controlled resolver behavior.

Assert:

```text
InvalidOperationException
failure before durability
runtime.Version unchanged
no structural admission remains installed
original user mutation remains tracked
```

Do not merge/reconcile the instances.

## 232.3 Tracked Added root remains domain Added

Required test:

```text
Tracked_added_consumer_is_not_converted_to_coverage_admission
```

Assert:

```text
exactly one domain ObjectAdded semantic origin/effect
no duplicate structural admission
save succeeds
runtime version increments once
```

## 232.4 Already registered root is not re-admitted

Required test:

```text
Already_registered_resolver_result_is_not_admitted_again
```

Assert normal propagation still occurs.

## 232.5 Resolver duplicate rows / duplicate candidate reference

Required test:

```text
Resolver_duplicate_rows_same_reference_do_not_duplicate_admission
```

Use a realistic tracked query/harness. Do not bypass the normal discovery collector.

## 232.6 Missing sibling reference fails closure

Required test:

```text
Resolver_omitting_other_required_reference_fails_before_sql
```

Use:

```text
request navigation = Source
sibling evaluation navigation = Target
Derived = Source.UnitValue / Target.UnitValue
```

Resolver includes/loads `Source` but intentionally does not load `Target` for the discovered root.

Prove before calling save that:

```text
Target CLR value == null
Target NavigationEntry.IsLoaded == false
```

Assert failure before durability and runtime unchanged.

## 232.7 Loaded optional null is accepted

Required test:

```text
Loaded_optional_null_required_reference_is_accepted
```

Use a dependency/evaluator where a loaded optional null is a legitimate evaluated state.

Required precondition:

```text
navigation CLR value == null
NavigationEntry.IsLoaded == true
```

The closure guard must accept it.

## 232.8 Detached non-null reference fails

Required test:

```text
Non_null_detached_required_reference_fails_before_sql
```

Assert runtime and database unchanged.

## 232.9 Fully loaded/tracked closure succeeds

Required test:

```text
All_required_references_loaded_and_tracked_pass
```

## 232.10 Exact metadata isolation

Required test:

```text
Evaluation_closure_uses_exact_ObjectSet_and_navigation_metadata
```

Use same CLR type in two object sets or multiple navigations so type-only matching cannot accidentally satisfy the requirement.

Suggested commit:

```text
test: prove consumer admission identity and evaluation closure
```

Required command:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

Push and record SHA.

Acceptance:

- [ ] same reference dedupe proven;
- [ ] same key/different reference fail-closed proven;
- [ ] Added remains domain Added;
- [ ] already registered behavior proven;
- [ ] duplicate candidate behavior proven;
- [ ] unloaded sibling null rejected;
- [ ] loaded optional null accepted;
- [ ] detached non-null rejected;
- [ ] fully loaded closure accepted;
- [ ] exact mapping metadata proven;
- [ ] task SHA pushed and recorded.

---

# Task 233 — complete policy/scope, overlay, batching, and resolver-misuse proof

This maps to Tasks 224–225 of the previous plan.

This task is test-heavy by design.

Do not skip items because similar code “looks correct”.

## 233.1 Active-policy matrix

Required tests:

```text
Validate_ignores_materialization_only_discovery
RecalculateAndValidate_activates_materialization_discovery
Enforced_invariant_uses_discovery_in_Validate
Non_enforced_invariant_does_not_use_discovery
Unmaterialized_derived_definition_does_not_use_discovery
```

Each test must assert resolver call count in addition to outcome.

## 233.2 Authoritative-scope substitution matrix

Required tests:

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

For fail-closed cases assert:

```text
exception before resolver/database durability when appropriate
runtime.Version unchanged
no hidden Complete(set) added to make test pass
```

## 233.3 ChangeTracker overlay matrix

Required tests:

```text
Retargeted_away_consumer_is_excluded
Retargeted_in_registered_consumer_is_included
Tracked_Added_consumer_remains_domain_add
Tracked_Deleted_consumer_is_excluded
Unknown_existing_Modified_consumer_fails_closed
Unknown_existing_Deleted_consumer_fails_closed
```

Do not weaken unknown Modified/Deleted guards.

## 233.4 Batching matrix

Required tests:

```text
Multiple_changed_targets_same_navigation_one_resolver_call
Two_changed_members_same_target_same_navigation_one_resolver_call
Source_and_Target_changes_one_call_per_navigation
Same_target_reference_is_deduplicated_within_batch
Complete_scope_zero_resolver_calls
```

Assert exact call counts.

## 233.5 Resolver misuse matrix

Required tests:

```text
AsNoTracking_or_detached_return_fails
Wrong_exact_ObjectSet_mapping_fails
Different_instance_duplicate_runtime_key_fails
Safe_resolver_superset_is_filtered
Missing_required_resolver_fails_before_sql
Resolver_exception_propagates_before_sql_and_runtime_unchanged
```

For every failure case capture both:

```text
runtime.Version
relevant tracked entity state
```

and assert they remain correct after failure.

Suggested commit:

```text
test: prove consumer discovery policy scope overlay and guards
```

Required command:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

Push and record SHA.

Acceptance:

- [ ] all active-policy tests exist and pass;
- [ ] all scope-substitution tests exist and pass;
- [ ] all overlay tests exist and pass;
- [ ] all batching tests exist and pass;
- [ ] all resolver misuse tests exist and pass;
- [ ] no frozen boundary weakened;
- [ ] task SHA pushed and recorded.

---

# Task 234 — prove persistence failure, retry, cancellation, manual UoW, and open-world behavior

This maps to Tasks 226–227 of the previous plan.

## 234.1 SQL/provider failure after planning/materialization

Create a deterministic failure that happens after framework planning/materialization but before successful durability.

Required test:

```text
Discovery_sql_failure_restores_framework_materialization_and_leaves_runtime_uncommitted
```

Assert after failure:

```text
runtime.Version unchanged
no newly discovered root registered
relation state equals baseline
navigation state equals baseline
projection state equals baseline
dependency state/diagnostics equal baseline
framework-written mirror CLR values restored
mirror PropertyEntry.IsModified restored to prior state
original user mutation remains tracked
```

Do not simulate a failure before planning; the failure must exercise rollback of framework-prepared state.

## 234.2 Same-DbContext retry

Required test:

```text
Discovery_sql_failure_can_retry_with_same_DbContext
```

After the injected failure:

```text
remove fault
call the same consistency save path again on the same DbContext
```

Assert success and correct discovery/materialization.

## 234.3 Resolver/planning failure atomicity

Required test:

```text
Discovery_resolver_failure_leaves_runtime_and_framework_mirrors_unchanged
```

Assert no durability.

## 234.4 Async cancellation

Required test:

```text
Discovery_cancellation_before_durability_leaves_runtime_uncommitted
```

Cancel during discovery/query/planning, not after successful save.

Assert framework materializations are not left poisoned.

## 234.5 Manual policy-aware UoW

Required test:

```text
Manual_unit_of_work_discovers_during_prepare_and_installs_exact_plan_after_durability
```

Prove sequence:

```text
CaptureConsistencyUnitOfWork
PrepareAndPlan
    -> resolver/discovery happens
    -> live runtime remains unchanged
caller performs durability
CommitAfterDatabaseCommit
    -> exact structural admission + semantic effects installed once
Dispatch remains separate
```

Assert runtime version increments exactly once.

## 234.6 Open-world repeat operation

Required test:

```text
Later_open_world_save_runs_consumer_resolver_again
```

After one successful operation, run a later operation against the same navigation without `Complete(set)`.

Assert resolver is called again.

Previous discovery does not prove that the database can never acquire new consumers.

Suggested commit:

```text
test: prove consumer discovery failure and workflow atomicity
```

Required command:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

Push and record SHA.

Acceptance:

- [ ] SQL failure rollback proven;
- [ ] same-context retry proven;
- [ ] resolver/planning failure atomicity proven;
- [ ] cancellation proven;
- [ ] manual UoW exact install proven;
- [ ] open-world repeat discovery proven;
- [ ] task SHA pushed and recorded.

---

# Task 235 — documentation, roadmap, and public API audit

Do this only after behavioral tasks are green.

## 235.1 Audit product documentation

Review:

```text
README.md
docs/architecture.md
docs/anemic-model-dependency-maintenance-example.md
samples/Raffinert.Consistency.DependencyMaintenanceSample
```

Required statements, if relevant to those documents:

```text
CoverageAdmission is structural baseline knowledge, not ObjectAdded.
DiscoverConsumers is operation-scoped targeted consumer coverage.
DiscoverConsumers does not make an ObjectSet globally complete.
Resolver completeness is a host assertion.
Resolver evaluation closure is host-owned and must load required references.
Raffinert.Consistency does not invent Includes or Reference.Load calls.
Database/query concurrency and isolation remain host/database responsibilities.
Unsupported dependency shapes remain fail-closed and require authoritative Complete(set) coverage.
Prior discovery does not close an open-world database for future saves.
```

Do not claim support for projected/relation/multi-hop external discovery.

## 235.2 Keep production-shaped dogfood

The dependency-maintenance sample must still include a case equivalent to:

```text
three persisted consumers share one source
only one consumer initially loaded/known
mutate the source value
one resolver call for that navigation
all consumer mirrors become correct
```

Do not preload all consumers.

## 235.3 Public API audit

Compare public API against the baseline before this recovery wave.

Do not expose:

```text
CoverageAdmission
StructuralMutations
ExternalConsumerRequest
resolver metadata wrappers
rollback/forward patch types
internal coverage proof types
```

If any public member was added only to make tests easier, remove it and use internals-visible-to-tests/internal access instead.

## 235.4 Update roadmaps honestly

Update:

```text
docs/codex-plan-external-consumer-discovery-post-96c076d-verification-and-closeout.md
```

Execution log entries must contain actual pushed SHAs.

Do **not** write “complete” yet for the entire roadmap.

Mark behavioral tasks as implemented/proven only if their exact pushed commits exist.

Suggested commit:

```text
docs: align external discovery contracts with proven behavior
```

Push and record SHA.

Acceptance:

- [ ] docs describe only proven behavior;
- [ ] dogfood remains production-shaped;
- [ ] no accidental public API expansion;
- [ ] execution log contains real pushed SHAs;
- [ ] task SHA pushed and recorded.

---

# Task 236 — exact-SHA release proof and only then completion

This is the only task allowed to mark the wave complete.

## 236.1 Ensure clean pushed tree

Run:

```bash
git status --short
git log --oneline --decorate -12
git rev-parse HEAD
git rev-parse origin/codex/external-consumer-discovery-closeout
```

Required:

```text
working tree clean
local HEAD == pushed branch HEAD
```

Record the exact final implementation SHA as:

```text
FINAL_SHA=<full 40-character SHA>
```

## 236.2 Run the complete local verification matrix

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

Do not skip commands because CI will run later.

Do not rerun only failing subsets and report the whole matrix green.

## 236.3 Verify required test inventory in the pushed tree

Search the pushed source for every required test from Tasks 230–234.

At minimum confirm these anchor tests exist:

```text
Dependency_patch_entry_count_includes_coverage_admission_scope
Coverage_admission_on_relation_right_is_invisible_after_planning
Coverage_admission_on_relation_left_is_invisible_after_planning
Coverage_admission_projection_state_is_invisible_after_planning
Different_instances_with_same_runtime_key_across_resolvers_fail_closed
Resolver_omitting_other_required_reference_fails_before_sql
Loaded_optional_null_required_reference_is_accepted
Unknown_existing_Modified_consumer_fails_closed
Multiple_changed_targets_same_navigation_one_resolver_call
AsNoTracking_or_detached_return_fails
Discovery_sql_failure_can_retry_with_same_DbContext
Manual_unit_of_work_discovers_during_prepare_and_installs_exact_plan_after_durability
Later_open_world_save_runs_consumer_resolver_again
```

Equivalent names are allowed only if the execution log maps each required behavior to an exact existing test method.

“Covered indirectly” without naming a concrete test does not count.

## 236.4 Push final SHA before CI verification

If any local command modified files, commit the legitimate changes and rerun the relevant verification until the working tree is clean.

Push:

```bash
git push
```

Re-read:

```bash
git rev-parse HEAD
git rev-parse origin/codex/external-consumer-discovery-closeout
```

They must match.

## 236.5 Verify GitHub Actions for the exact final SHA

Inspect CI for `FINAL_SHA`.

Required:

```text
workflow head_sha == FINAL_SHA
status == completed
conclusion == success
```

A green run for an earlier SHA does not count.

A green run for `9ef128b` does not count.

A green docs-only run does not count.

## 236.6 Merge/push to main only after proof

After exact-SHA CI succeeds, integrate the implementation branch according to repository workflow.

If direct main update is used, ensure the final implementation commits are reachable from `main`.

Then verify:

```bash
git fetch origin
git rev-parse origin/main
```

and confirm the implementation/proof commits are ancestors of `origin/main`.

## 236.7 Close roadmap with exact evidence

Only now update the roadmap/status docs to say complete.

Record:

```text
final implementation SHA
final main SHA if different
exact CI run URL/id
local verification matrix completed
public API audit result
```

Suggested final commit if a closeout-doc commit is necessary:

```text
docs: close external consumer discovery hardening roadmap
```

If a docs closeout commit is created after the proven implementation SHA, run CI for that closeout SHA too before calling `main` green.

---

# Required final test inventory

The wave cannot close without dedicated automated proof for every category below.

## Structural admission

- [ ] coverage admission separate from ObjectAdded semantics;
- [ ] canonical/frozen structural mutation representation;
- [ ] dependency snapshot helper includes admissions;
- [ ] right relation planning rollback;
- [ ] left relation planning rollback;
- [ ] relation exact install;
- [ ] projection planning rollback;
- [ ] projection exact install;
- [ ] forward-patch failure rollback;
- [ ] no semantic relation AddedPairs from baseline admission;
- [ ] no ObjectAdded origin from baseline admission;
- [ ] runtime version increments exactly once after install.

## Admission identity

- [ ] same reference from multiple resolvers admitted once;
- [ ] same key/different reference fails closed;
- [ ] tracked Added remains domain Added;
- [ ] already registered root not re-admitted;
- [ ] duplicate resolver candidate reference does not duplicate admission.

## Evaluation closure

- [ ] unloaded sibling null fails;
- [ ] loaded optional null succeeds;
- [ ] detached non-null fails;
- [ ] fully loaded/tracked succeeds;
- [ ] exact ObjectSet/navigation metadata isolation proven.

## Policy and scope

- [ ] Validate ignores materialization-only discovery;
- [ ] RecalculateAndValidate activates it;
- [ ] enforced invariant activates discovery in Validate;
- [ ] non-enforced invariant does not;
- [ ] unmaterialized derived does not;
- [ ] exact direct-navigation resolver can satisfy supported navigation coverage;
- [ ] unsupported sibling navigation stays fail-closed;
- [ ] upstream requirement preserved;
- [ ] same CLR type/different ObjectSet isolation;
- [ ] relation source/target coverage not substituted;
- [ ] projected coverage not substituted.

## ChangeTracker overlay and batching

- [ ] retarget-away excluded;
- [ ] retarget-in included;
- [ ] Added handled as domain add;
- [ ] Deleted excluded;
- [ ] unknown existing Modified fails closed;
- [ ] unknown existing Deleted fails closed;
- [ ] multiple targets batch into one call per navigation;
- [ ] multiple changed members do not duplicate navigation call;
- [ ] Source+Target changes yield one call per navigation;
- [ ] target references deduplicated;
- [ ] Complete(set) yields zero resolver calls.

## Resolver guards

- [ ] AsNoTracking/detached fails;
- [ ] wrong ObjectSet mapping fails;
- [ ] duplicate runtime key fails;
- [ ] safe resolver superset filtered;
- [ ] missing required resolver fails before durability;
- [ ] resolver exception propagates with runtime unchanged.

## Failure and workflows

- [ ] SQL/provider failure leaves runtime unchanged;
- [ ] framework mirror CLR values restored;
- [ ] mirror IsModified restored;
- [ ] original user mutation preserved;
- [ ] same-DbContext retry succeeds;
- [ ] resolver/planning failure atomicity;
- [ ] async cancellation atomicity;
- [ ] manual UoW prepare/discover/commit workflow;
- [ ] exact plan installed once;
- [ ] later open-world save re-runs resolver.

## Release proof

- [ ] all required local commands green;
- [ ] working tree clean;
- [ ] all task commits pushed;
- [ ] exact final SHA recorded;
- [ ] CI success belongs to exact final SHA;
- [ ] implementation reachable from `main`;
- [ ] no accidental public API expansion;
- [ ] roadmap closeout records real evidence.

---

# Anti-shortcut rules

The following are explicitly forbidden ways to make this plan appear complete:

```text
reporting uncommitted working-tree changes;
reporting local commits that were not pushed;
claiming a task is complete because code was inspected;
reusing CI from 96c076d or 9ef128b;
adding Complete(set) to an open-world discovery test;
preloading every consumer before the mutation;
manually calling resolver logic from the test instead of normal save/UoW flow;
reintroducing CoverageAdmission : IAddedMutation;
turning CoverageAdmission into ObjectAdded provenance;
hiding relation AddedPairs only in rendering while still propagating semantic add deltas;
auto-calling Reference.Load();
generating Include expressions;
weakening unknown Modified/Deleted guards;
reconciling different CLR references with the same runtime key;
deleting or skipping difficult required tests;
renaming a test to the required name without proving the required condition;
asserting only database final state when runtime/tracker atomicity is the contract;
adding public APIs only so tests can inspect internals;
marking a roadmap complete before exact-SHA CI succeeds.
```

If a required test is difficult to write, solve the testability problem with internal test access or a focused internal harness. Do not weaken the contract.

---

# Required completion report format

When the agent believes Tasks 229–236 are complete, the report must contain exactly this evidence structure:

```text
Implementation branch:
  <branch>

Task 229 SHA:
  <sha>

Task 230 SHA:
  <sha>

Task 231 SHA:
  <sha>

Task 232 SHA:
  <sha>

Task 233 SHA:
  <sha>

Task 234 SHA:
  <sha>

Task 235 SHA:
  <sha>

Final implementation/proof SHA:
  <sha>

Main SHA containing implementation:
  <sha>

Exact successful CI run:
  <run id/url>
  head_sha=<same final/main SHA being claimed green>
  conclusion=success

Local verification:
  restore: PASS
  build Release: PASS
  Core net8: PASS
  Core net10: PASS
  EF Core net10: PASS
  DependencyMaintenanceSample: PASS
  OrderFulfillmentSample: PASS
  EntityFrameworkCore.Sample: PASS
  dotnet format --verify-no-changes: PASS

Required test inventory:
  <list every required behavior -> exact test method>

Public API audit:
  PASS / explanation

git status --short:
  <empty>
```

Any completion report missing pushed SHAs or exact-SHA CI evidence is incomplete by definition.
