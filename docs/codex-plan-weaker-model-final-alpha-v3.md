# Codex Implementation Plan — Weaker-Model Final Alpha v3

Baseline: `main` at `c6143b00307f949500af5bfaae1a340a28a1e2d8`.

This plan is intentionally written for a weaker coding agent. Follow it mechanically. Do not redesign the library, do not invent a new public feature, and do not collapse several tasks into one large commit.

The previous Tasks 67–72 wave is **partially implemented and superseded by this file**.

## What is already implemented — DO NOT redo it

The current repository already has:

- `RelationRouteTrigger` carried through `RelationDelta` / `RelationImpact`;
- conservative right-side routing records the actual right-side trigger object;
- multi-item conservative provenance no longer guesses from all same-type objects;
- `RuntimeApplyResultAssert` compares the full public result shape much more deeply than the old count-only helper;
- `PreparedImpactPlan` no longer retains the planning-time rollback state;
- generated-key SQLite planning can write outbox payloads from durable identities in `plan.Result`;
- plan failure after the first generated-key `SaveChanges` is covered;
- local release-style build/test/pack/package-consumer verification has passed;
- `PreviewDetailed`, `PlanDetailed`, and `Commit(plan)` remain separate APIs with the documented binding/non-binding semantics.

Do not replace those pieces unless a task below explicitly says to edit them.

## Verified remaining gaps

The next agent must understand these exact gaps before editing code.

### Gap A — relation cause shape is still partially reconstructed after propagation

`RelationRouteTrigger` already contains:

```csharp
Left
Trigger
Kind
Precision
```

but `MutationCommit.CreateCauses(...)` still derives relation cause kind/precision separately by looking at `AddedPairs`, `RemovedPairs`, and the relation propagation plan.

That means the runtime records decision-time evidence and then ignores part of it when creating the public causal result.

### Gap B — upstream-derived causality is still inferred from final impact snapshots

`CreateCauses(...)` currently walks derived inputs after propagation and checks whether the upstream source appears in `DependencyPropagation.DerivedImpacts`.

The required alpha contract is stronger: when `ApplyInherited(...)` maps an upstream impacted source to a downstream source, that edge and its precision must be captured then and later rendered, not rediscovered.

### Gap C — `RuntimeForwardPatch` is currently only a renamed snapshot

Current shape is conceptually:

```text
RuntimeForwardPatch
    -> RuntimeStateSnapshot
```

and `Commit(plan)` still installs it through `RestoreState(...)`.

That is not the requested touched-state forward patch.

### Gap D — broad snapshot APIs still exist on hot planning paths

Current broad state copying includes:

- `IRelationRuntimeState.CaptureState()` copying all relation indexes and pair maps for each affected relation;
- `NavigationIndexRegistry.CaptureState()` copying every navigation index and all root registrations when navigation state is considered changed;
- `IDerivedRuntimeState.CaptureState()` copying the entire derived cache;
- `IInvariantRuntimeState.CaptureState()` copying the entire invariant-state dictionary;
- `DependencyGraphRuntime.CaptureState(...)` composing those broad cache snapshots.

Object-set and projection state already have touched-entry capture. Preserve those improvements.

### Gap E — mixed-model equivalence proof is still too small

`RuntimeApplyResultAssert` is good infrastructure, but adding a stronger assertion helper is not the same as proving a mixed graph.

There is still no single deterministic/randomized fixture that exercises all of:

- exact relation;
- conservative relation;
- source-only derived value;
- relation-backed derived value;
- derived chain;
- diamond fan-in;
- projected one-upstream dependency;
- projected two-upstream dependency;
- property change;
- collection change;
- object add/remove;
- projected-reference retarget;
- Dirty vs Invalid merge;
- `ScheduleRepair`;
- Preview vs Plan vs direct Commit vs `Commit(plan)`.

### Gap F — release candidate remote gate still has never run

`release-candidate.yml` exists, but there are still zero `workflow_dispatch` runs. Local verification is useful but does not replace the remote gate.

---

# Non-negotiable rules

1. Keep public behavior backward-compatible unless this plan explicitly changes it.
2. Do not add new public features.
3. Do not add generic `.Using(...)` arity 3..8 overload families.
4. Do not add hypothetical pre-mutation simulation.
5. Do not weaken exact object-set registration or projected-target validation.
6. `Invalid` must dominate `Dirty` everywhere.
7. Conservative precision may propagate downstream but may never become Exact again.
8. Summary/basic execution must not allocate causal evidence graphs.
9. `PlanDetailed` must not dispatch callbacks or advance runtime `Version`.
10. `Commit(plan)` must not rerun relation predicates, classifiers, derived computations, or dependency propagation.
11. Do not publish packages, create tags, or create a GitHub release.
12. After every task, run the exact validation commands listed in that task before starting the next one.
13. Make one logical commit per requested commit boundary. Do not squash the whole roadmap into one commit.

---

# Task 73 — Make relation and upstream causal output a direct projection of recorded evidence

## Goal

Remove the remaining second reasoning pass from `CreateCauses(...)`.

## Files to edit

Primary:

- `src/Raffinert.Relations/RelationImpact.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- `src/Raffinert.Relations/Dependencies/DependencyGraphRuntime.cs`
- `tests/Raffinert.Relations.Tests/CausalImpactTests.cs`

Only edit other files if compilation requires it.

## 73.1 — Use `RelationRouteTrigger.Kind` and `.Precision`

In `MutationCommit.CreateCauses(...)`, find the derived-definition relation-input branch.

Current behavior roughly does this:

```text
if added pair exists -> MembershipAdded
else if removed pair exists -> MembershipRemoved
else choose ConservativeCandidate or RelatedItemChanged

precision = based on relation propagation mode
```

Stop doing that.

Instead:

1. Select `impact.RouteTriggers` whose `Left` is the impacted derived source.
2. Group them by `(Kind, Precision)`.
3. Emit one `RelationDependencyCause` per group.
4. Set the public cause kind directly from `RelationRouteTrigger.Kind`.
5. Set public precision directly from `RelationRouteTrigger.Precision`.
6. Derive `OriginIds` only from the trigger objects in that specific group.
7. Do not inspect `AddedPairs` / `RemovedPairs` to decide cause kind.
8. Do not inspect `RelationPropagationPlan` to decide public cause precision.

Keep `AddedPairs` / `RemovedPairs` for public relation impact data. They are not causal authority anymore.

### Required helper shape

Replace the current broad trigger-origin mapper with a helper that takes the already-selected trigger group, for example:

```csharp
private static IReadOnlyList<int> MapTriggerOrigins(
    IRelationDefinition relation,
    IReadOnlyCollection<RelationRouteTrigger> triggers,
    IReadOnlyList<MutationOrigin> origins)
```

The helper may match prepared origins by:

- exact trigger-object reference;
- exact left-source reference when the trigger itself is the left source;
- relation dependency member name;
- lifecycle origin for that exact object.

It must NOT search unrelated same-type objects.

## 73.2 — Record upstream-derived edges during `ApplyInherited(...)`

Add an internal evidence record. Suggested shape:

```csharp
internal sealed record UpstreamPropagationEvidence(
    IDerivedDefinition Downstream,
    object DownstreamSource,
    IDerivedDefinition Upstream,
    object UpstreamSource,
    ImpactCausePrecision Precision);
```

Do not make this public.

In `DependencyGraphRuntime.DerivedNode.ApplyInherited(...)` (or the smallest nearby layer that already has all necessary information):

1. When an upstream impacted source maps to a downstream source, capture the actual pair.
2. Exact upstream source -> downstream edge has Exact precision unless the upstream source is already conservative.
3. Conservative upstream source -> downstream edge is Conservative.
4. Store the evidence only when `captureCausalEvidence == true`.
5. Do not allocate/populate it for Summary/basic execution.

Add the evidence to `DependencyPropagationResult` or a child structure carried by it.

Then change `CreateCauses(...)` so `UpstreamDerivedCause` comes from this evidence instead of re-running `input.Project(source)` and searching final snapshots.

## 73.3 — Record invariant upstream edges at the propagation point

Do the same for invariant propagation.

Suggested internal record:

```csharp
internal sealed record InvariantUpstreamEvidence(
    IInvariantDefinition Invariant,
    object InvariantSource,
    IDerivedDefinition Upstream,
    object UpstreamSource,
    ImpactCausePrecision Precision);
```

If the existing upstream evidence type can safely represent both derived and invariant downstream nodes, use one type instead of two. Do not create complexity only for naming.

`InvariantReactionCause` may still be derived from the actual reaction escalation because that reaction is itself the decision point. Do not infer a Dirty->Invalid reaction if the inherited input was already Invalid.

## 73.4 — Tests to add before/with the fix

Add these exact behavioral tests to `CausalImpactTests.cs` (names may differ slightly but intent must be obvious):

```text
Exact_added_and_removed_relation_causes_use_route_trigger_kind
Conservative_relation_cause_uses_recorded_conservative_precision
Two_changes_on_same_right_object_only_include_relation_dependency_origins
Two_right_objects_in_same_wave_keep_distinct_origin_ids_per_left
Projected_upstream_cause_uses_recorded_upstream_source_edge
Conservative_upstream_precision_remains_conservative_through_two_derived_levels
Invariant_upstream_cause_links_to_the_actual_upstream_impact
Summary_detail_does_not_allocate_or_expose_upstream_evidence
```

The two-level conservative test is mandatory.

## STOP condition for Task 73

Do not continue if any causal test fails or if `CreateCauses(...)` still decides relation cause kind from pair lists or precision from propagation mode.

## Validation commands

```bash
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0
dotnet format Raffinert.Relations.sln --verify-no-changes
```

## Commit boundary

Commit 1:

```text
refactor: project causal results from recorded route evidence
```

Commit 2:

```text
refactor: record upstream causal edges during propagation
```

---

# Task 74 — Replace full relation snapshots with touched relation state

## Goal

A mutation affecting one relation root must not clone every index and every pair entry of that relation just to support preview/planning rollback or `Commit(plan)`.

Do only relation state in this task. Do not touch navigation or dependency caches yet.

## Files to edit

- `src/Raffinert.Relations/Runtime/RelationRuntimeState.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- relation-focused tests under `tests/Raffinert.Relations.Tests`

## 74.1 — Add touched-state API to `IRelationRuntimeState`

Keep the existing full `CaptureState()` temporarily for migration/debug tests, but stop using it from the normal prepared-mutation capture path after this task.

Add internal APIs conceptually like:

```csharp
object CaptureTouchedState(
    IReadOnlyCollection<object> touchedLefts,
    IReadOnlyCollection<object> touchedRights);

void RestoreTouchedState(object state);
```

If you prefer two method names for rollback/forward patch, that is fine, but the stored representation should be identical and usable for either pre-state or post-state.

## 74.2 — Define exactly what a touched relation state contains

For `RelationRuntimeState<TLeft, TRight>`, capture only state that can be changed by the supplied left/right roots.

The touched-state record must include enough information to restore:

- `_leftKeys` entries for touched lefts;
- `_keys` entries for touched rights;
- hash buckets for every old/current key of touched lefts/rights;
- `_rightsByLeft` entries for touched lefts;
- `_leftsByRight` entries for touched rights;
- counterpart pair-map entries reached from the touched left/right pair sets when needed to keep both directions consistent;
- `_predicateEvaluationCount` scalar.

Important: when capturing one hash bucket, copy the complete bucket contents. That bucket may contain unrelated objects sharing the same composite key. This is acceptable because cost is proportional to the touched key bucket, not the whole relation.

Do not clone all `_index`, `_leftIndex`, `_rightsByLeft`, or `_leftsByRight` dictionaries.

## 74.3 — Compute touched relation roots in `MutationCommit.CaptureState(...)`

For every affected relation, build:

```text
touched lefts =
  semantic lefts
  + left reindex roots
  + lifecycle left instances
  + left endpoints from relation deltas if already available at post-capture time

touched rights =
  semantic rights
  + right reindex roots
  + lifecycle right instances
  + right endpoints from relation deltas if already available at post-capture time
```

For the pre-execution capture, use only information available before mutation execution.

For the post-execution forward patch capture, include endpoints created/removed by the committed relation delta if required.

If the current `CaptureState(...)` signature does not have the relation delta for post capture, extend the post-capture path rather than falling back to full snapshot.

## 74.4 — Required tests

Add tests that create a large relation with many unrelated entries, mutate one root, Preview/Plan, then verify:

1. runtime semantic state is restored after preview;
2. `Commit(plan)` installs exactly the planned relation state;
3. an unrelated relation bucket remains unchanged;
4. a shared-key bucket containing unrelated members remains unchanged except for the touched member;
5. failure during relation mutation restores both directions of the exact pair map.

Add an internal diagnostic/test hook if necessary to count entries in the captured touched-state object. Do not expose a new public API only for tests.

Required scaling assertion:

```text
captured relation patch entry count for 1 touched root
must not grow approximately 10x when relation population grows 10k -> 100k
unless the touched key bucket itself grows 10x.
```

## STOP condition for Task 74

`MutationCommit.CaptureState(...)` must no longer call `IRelationRuntimeState.CaptureState()` on the normal prepared-mutation path.

## Validation

Run both core TFMs and formatting.

## Commit boundary

Commit 3:

```text
refactor: capture touched relation state for prepared execution
```

Commit 4:

```text
test: prove relation patch scope and rollback
```

---

# Task 75 — Replace whole navigation snapshots with touched root/owner state

## Goal

A lifecycle mutation or one indexed navigation change must not clone every navigation index and every root registration.

## Files to edit

- `src/Raffinert.Relations/Dependencies/NavigationAndImpact.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- navigation-focused tests

## 75.1 — Add owner-level state to `INavigationIndex`

Add internal methods conceptually like:

```csharp
object CaptureOwnersState(IEnumerable<object> owners);
void RestoreOwnersState(object state);
```

For scalar `NavigationIndex`, per owner capture:

- whether owner exists in `_forward`;
- target reference;
- exact `ReferenceCount`.

For collection navigation, capture:

- whether owner exists;
- exact item set;
- exact `ReferenceCount`.

Restore must update both `_forward` and `_reverse` consistently.

Do not restore an owner by repeatedly calling public-ish `RemoveOwner` until count reaches zero. Implement a private exact remove/replace helper so the saved reference count is restored deterministically.

## 75.2 — Add touched-root state to `NavigationIndexRegistry`

Create registry capture API taking the root registrations that may change.

For each touched `(set, root)` capture:

- whether `_registrations[set]` contains the root;
- its `NavigationMembership[]`;
- all `(index, owner)` entries referenced by the old registration;
- all `(index, owner)` entries that the post-state registration can introduce.

For the post-state forward patch, capture after execution using the actual registration/memberships.

Do not clone `_indexes` or all `_registrations`.

## 75.3 — Integrate in `MutationCommit`

Replace:

```csharp
_navigation.CaptureState()
```

with touched capture based on:

- lifecycle roots;
- `navigationRoots` already computed by `DependencyGraphRuntime.ResolveNavigationRoots(...)`;
- changed owners for directly indexed navigation members.

## Required tests

```text
Preview_of_one_navigation_retarget_restores_exact_old_reverse_mapping
Plan_commit_installs_only_touched_navigation_owner
Shared_navigation_owner_reference_count_restores_exactly
Collection_navigation_preview_restores_items_and_reverse_owners
Navigation_patch_size_does_not_scale_with_unrelated_runtime_population
```

## STOP condition for Task 75

Normal Preview/Plan/Commit(plan) execution must not call `NavigationIndexRegistry.CaptureState()`.

## Commit boundary

Commit 5:

```text
refactor: journal touched navigation roots and owners
```

Commit 6:

```text
test: prove navigation patch scope and reference counts
```

---

# Task 76 — Replace whole derived/invariant cache snapshots with source-entry patches

## Goal

Planning one source must not copy all derived and invariant cached entries.

## Files to edit

- `src/Raffinert.Relations/Derived/DerivedRuntimeState.cs`
- `src/Raffinert.Relations/Derived/InvariantRuntimeState.cs`
- `src/Raffinert.Relations/Dependencies/DependencyGraphRuntime.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- dependency/derived tests

## 76.1 — Add per-source state APIs

Extend `IDerivedRuntimeState` internally with conceptually:

```csharp
object CaptureSourcesState(IEnumerable<object> sources);
void RestoreSourcesState(object state);
```

For every source store:

- whether a cache entry existed;
- cached value when present;
- `DerivedValueState` when present.

Also capture diagnostic counters once per touched derived runtime state:

- `FullRecomputationCount`;
- `IncrementalUpdateCount`.

Do not copy the whole cache dictionary.

Extend `IInvariantRuntimeState` the same way:

- source entry existed/not;
- exact `InvariantEvaluationState`.

## 76.2 — Determine touched sources before execution

Do not guess by scanning all registered sources.

Build a helper inside `DependencyGraphRuntime` that computes the source closure for the current wave from:

- directly changed source roots;
- relation affected lefts;
- navigation-resolved roots;
- projected reverse indexes;
- downstream derived DAG edges;
- invariants consuming touched derived nodes.

Name it clearly, for example:

```csharp
DependencyTouchSet ResolveTouchSet(...)
```

It may contain:

```text
Derived -> set of source references
Invariant -> set of source references
```

Use this same touch set for pre-state rollback capture and to know what post-state entries belong in the binding forward patch.

Do not traverse every registered source.

## 76.3 — Transient dependency-node wave state

`DerivedNode.InvalidSources`, `DirtySources`, `ConservativeSources`, direct evidence, and invariant equivalents are per-wave collections.

It is acceptable to snapshot/restore those complete per-wave collections because their size is bounded by impacted work, not by total runtime population.

It is NOT acceptable to copy complete derived/invariant source caches.

## 76.4 — Required tests

```text
Preview_restores_cached_fresh_value_for_touched_source
Preview_does_not_modify_cached_state_of_unrelated_source
Plan_commit_installs_dirty_state_only_for_planned_sources
Incremental_derived_value_is_identical_after_plan_commit
Invariant_state_is_identical_after_plan_commit
Derived_patch_size_is_population_independent_for_one_touched_source
Invariant_patch_size_is_population_independent_for_one_touched_source
```

Include at least one incremental relation-backed derived value, not only a source-only computed property.

## STOP condition for Task 76

Normal prepared execution must not call full `IDerivedRuntimeState.CaptureState()` or full `IInvariantRuntimeState.CaptureState()`.

## Commit boundary

Commit 7:

```text
refactor: patch derived and invariant state by touched source
```

Commit 8:

```text
test: prove dependency cache patch scope
```

---

# Task 77 — Replace `RuntimeStateSnapshot`-backed binding plans with a real composite forward patch

## Goal

After Tasks 74–76, assemble the subsystem patches into one explicit binding patch and remove the misleading wrapper-over-snapshot implementation.

## Files to edit

- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- `src/Raffinert.Relations/Runtime/RelationRuntime.cs`
- `src/Raffinert.Relations/Policies/PolicyActions.cs`
- tests for prepared planning/commit

## 77.1 — Create explicit internal patch records

Use a shape similar to:

```csharp
private sealed record RuntimeRollbackJournal(
    IReadOnlyDictionary<IObjectSetDefinition, object> Sets,
    IReadOnlyDictionary<IRelationDefinition, object> Relations,
    object? Navigation,
    object? Projections,
    object Dependencies,
    RuntimeScalarState Scalars);

private sealed record RuntimeForwardPatch(
    IReadOnlyDictionary<IObjectSetDefinition, object> Sets,
    IReadOnlyDictionary<IRelationDefinition, object> Relations,
    object? Navigation,
    object? Projections,
    object Dependencies,
    RuntimeScalarState Scalars);
```

The exact names may differ. The important constraint is:

```text
RuntimeForwardPatch MUST NOT contain RuntimeStateSnapshot.
RuntimeRollbackJournal MUST NOT contain RuntimeStateSnapshot.
```

Move scalar diagnostic/last-wave fields into an explicit scalar record rather than relying on generic restore of the old snapshot object.

## 77.2 — Separate capture and apply methods

Do not keep one generic `RestoreState(...)` that hides whether the object is a pre-state journal or post-state patch.

Use explicit methods, for example:

```csharp
CaptureRollbackJournal(...)
CaptureForwardPatch(...)
RestoreRollbackJournal(...)
ApplyForwardPatch(...)
```

`PreviewDetailed`:

```text
capture rollback journal
execute
produce result
restore rollback journal
```

`PlanDetailed`:

```text
capture rollback journal
execute
capture forward patch
produce plan
restore rollback journal
```

`Commit(plan)`:

```text
validate plan/runtime/domain/version
capture install rollback journal for touched scope
apply forward patch
advance version + mark committed
on exception -> restore install rollback journal
```

No semantics rerun during forward-patch application.

## 77.3 — Delete or quarantine old generic snapshot path

Once all normal paths use journals/patches:

- delete `RuntimeStateSnapshot` if no longer needed;
- or keep a clearly named `CaptureFullStateForTestsOnly` debug path if a test truly needs it.

Do not leave normal planning calling broad full-state capture under a new name.

## Required tests

```text
Binding_plan_does_not_reexecute_predicate_classifier_or_compute
Commit_plan_failure_restores_runtime_to_pre_install_state
Preview_failure_restores_runtime_state_and_diagnostics
Plan_failure_restores_runtime_state_and_diagnostics
Plan_object_does_not_retain_pre_state_journal
Forward_patch_contains_only_touched_subsystem_entries
```

The no-reexecution test must use counters/throw-on-second-call delegates.

## STOP condition for Task 77

Search the core source for:

```text
RuntimeForwardPatch(RuntimeStateSnapshot
RuntimeRollbackJournal(RuntimeStateSnapshot
```

There must be zero matches.

## Commit boundary

Commit 9:

```text
refactor: compose explicit runtime rollback journals and forward patches
```

Commit 10:

```text
test: prove binding patch install atomicity and no reexecution
```

---

# Task 78 — Add the missing mixed-model parity and failure matrix

## Goal

Prove the entire execution contract on one realistic graph. Do not add another helper-only commit.

## New test file

Create:

```text
tests/Raffinert.Relations.Tests/MixedModelPreparedExecutionTests.cs
```

## Required model

Build one model containing all of these:

1. `Parent` object set.
2. `Item` object set.
3. `Link` object set projecting to `Parent`.
4. one exact relation `Parent -> Item`;
5. one conservative relation (may use another item set if clearer);
6. one source-only derived value;
7. one relation-backed derived aggregate;
8. one derived chain A -> B -> C;
9. one diamond A -> B/C -> D;
10. one projected upstream derived on `Link`;
11. one projected two-upstream derived on `Link`;
12. one invariant with `ScheduleRepair`;
13. one value-sensitive member classifier producing Dirty for one direction and Invalid for the other.

Do not use `.AllowIncompleteDependencies()` unless the test is specifically testing incomplete dependency opt-out. The main mixed model must be fully analyzable.

## Execution modes to compare

For each generated test case, create equivalent independently seeded runtimes and execute through:

```text
A  CommitDetailed(prepared, Causal)
B  PreviewDetailed(prepared, Causal), then CommitDetailed(prepared, Causal)
C  PlanDetailed(prepared, Causal), then Commit(plan)
D  ApplyDetailed(mutationSet, Causal)
```

Use `RuntimeApplyResultAssert.Equivalent` to compare the detailed results.

Also compare observable runtime state after commit:

- relation membership;
- derived values/states;
- invariant states;
- runtime version;
- diagnostics relevant to semantic execution (normalize expected differences only if preview intentionally reruns semantics before later normal commit; binding-plan commit must not add semantic executions).

## Mutation cases

At minimum include deterministic cases for:

```text
parent scalar increase
parent scalar decrease
item relation-key change
item add
item remove
collection add/remove/reset
link projected-reference retarget
upstream parent removal + link removal in same batch
multiple conservative right changes in one batch
Dirty + Invalid fan-in in one batch
ScheduleRepair request
```

Then add a randomized loop with a fixed seed, at least 100 valid mutation waves.

When generation produces an invalid mutation batch, either generate again or explicitly assert the same validation failure across modes. Do not silently skip failures that only happen in one mode.

## Failure injection matrix

Add deliberate failures at:

```text
relation predicate
value-sensitive classifier
derived computation/incremental update
invariant evaluation
policy dispatch
forward patch installation (use internal test hook only if necessary)
```

For pre-commit failures assert:

```text
Version unchanged
runtime-owned relation/index/cache/invariant state restored
PreparedMutation not committed
no callback dispatched
```

For dispatch failure assert committed runtime stays committed and dispatch can be retried according to existing resumable-dispatch contract.

## Commit boundary

Commit 11:

```text
test: prove mixed model prepared execution equivalence
```

Commit 12:

```text
test: prove prepared execution failure atomicity matrix
```

---

# Task 79 — Record scaling evidence, finish outbox recovery boundaries, and run the remote RC gate

This task has three independent parts. Do them in this exact order.

## 79A — Benchmark the actual patch work

Update benchmarks so these operations are measured separately:

```text
CommitDetailed Summary
CommitDetailed Causal
Preview Summary
Preview Causal
Plan Summary
Plan Causal
Commit(prebuilt Summary plan)
Commit(prebuilt Causal plan)
```

Add population/fan-out scenarios:

```text
relation population: 10k, 100k
unrelated navigation population: 10k, 100k
derived cached sources: 10k, 100k
projected fan-out: 1, 10, 100
```

The mutation should touch one logical source/root.

Record results in:

```text
benchmarks/PreparedImpactPlanning-Results.md
```

The document must include environment, date, commit SHA, mean time, allocated bytes, and short interpretation.

Required interpretation rule:

```text
If one-source Plan/Preview allocation grows close to 10x when unrelated population grows 10x,
do not call the patch work complete. Find the remaining broad copy first.
```

## 79B — Finish SQLite outbox recovery tests

The current tests already cover generated-key payload creation and one plan-failure rollback. Add the missing boundaries:

```text
Database_commit_succeeds_then_runtime_plan_install_failure_requires_runtime_rebuild
Dispatch_failure_after_database_and_runtime_commit_keeps_outbox_and_allows_dispatch_retry
Two_generated_entities_and_two_outbox_rows_use_only_plan_result_durable_identities
```

For the DB-success/runtime-install-failure test:

1. commit DB business + outbox transaction;
2. force `Commit(plan)` install failure with an internal test hook or controlled corrupted runtime precondition;
3. assert DB/outbox remain committed;
4. assert documentation/recovery path is rebuild/reconcile runtime from authoritative DB/outbox;
5. create a newly seeded runtime from DB state and prove it is healthy.

Do not attempt to roll back an already committed database transaction from runtime code.

## 79C — Execute the remote release-candidate workflow

The repository has `.github/workflows/release-candidate.yml` with `workflow_dispatch`.

The previous agent incorrectly stopped because `gh` was not installed. That is not an implementation completion criterion.

If the available GitHub integration in the execution environment can dispatch workflows, use it.

If the agent truly has no API/action capable of dispatching a workflow:

1. do NOT mark this task complete;
2. update `docs/release-candidate-verification.md` with the exact blocker;
3. leave the roadmap active;
4. do not claim first-alpha remote RC verification.

If dispatch succeeds:

1. wait for the workflow result using the available GitHub API/tooling;
2. require all steps green;
3. record run ID, head SHA, date, package artifact name in `docs/release-candidate-verification.md`;
4. only then mark this roadmap complete in `docs/roadmaps/README.md`.

Do not publish the artifact.

## Final validation commands before remote RC

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Run packed consumer smoke tests from a clean package cache as already implemented by CI/RC workflow.

## Commit boundaries

Commit 13:

```text
bench: record touched patch planning scaling
```

Commit 14:

```text
test: prove outbox runtime recovery boundaries
```

Commit 15:

```text
docs: record remote alpha rc verification
```

Commit 15 is allowed only after a successful remote RC workflow. If remote dispatch is impossible, do not create a fake success commit.

---

# Required implementation order

Do not reorder these tasks:

```text
73  causal evidence projection
74  relation touched-state patch
75  navigation touched-state patch
76  derived/invariant touched-source patch
77  compose real rollback journal + forward patch
78  mixed-model parity + failure matrix
79  scaling + outbox recovery + remote RC
```

Reason:

```text
causal truth
  before
state-patch refactor
  before
large equivalence proof
  before
performance claims
  before
release-candidate claim
```

---

# Quick grep checklist for the agent

Before declaring Tasks 73–79 complete, run searches manually or with repository search.

The following must be true:

```text
CreateCauses relation branch does not derive Kind from AddedPairs/RemovedPairs
CreateCauses relation branch does not derive Precision from PropagationPlan
CreateCauses upstream branch uses recorded propagation evidence
normal prepared path does not call IRelationRuntimeState.CaptureState()
normal prepared path does not call NavigationIndexRegistry.CaptureState()
normal prepared path does not call IDerivedRuntimeState.CaptureState()
normal prepared path does not call IInvariantRuntimeState.CaptureState()
RuntimeForwardPatch does not wrap RuntimeStateSnapshot
RuntimeRollbackJournal does not wrap RuntimeStateSnapshot
MixedModelPreparedExecutionTests exists and executes >=100 fixed-seed valid waves
PreparedImpactPlanning-Results.md exists with 10k/100k population data
SQLite tests include DB-success/runtime-failure and dispatch-retry cases
workflow_dispatch has at least one successful release-candidate run before roadmap completion
```

If any line above is false, the roadmap is not complete.

---

# Final definition of done

The first alpha engineering gate is complete only when all of these are true:

1. causal relation/upstream output is a direct projection of evidence recorded during routing/propagation;
2. Preview/Plan rollback work is proportional to touched state rather than total runtime population;
3. binding plans contain a real touched-state forward patch, not a renamed global snapshot;
4. `Commit(plan)` installs without semantic reexecution;
5. mixed-model deterministic + randomized equivalence passes across all four execution modes;
6. failure atomicity is proven for semantic execution and patch installation;
7. generated-key outbox integration is proven from `plan.Result` durable identities;
8. DB-success/runtime-failure recovery is documented and tested by rebuilding from authoritative state;
9. benchmark evidence shows expected scaling;
10. the manual GitHub release-candidate workflow has actually run successfully;
11. no package was published and no release/tag was created.

Until item 10 is true, documentation must say local verification is green but remote RC is still pending.