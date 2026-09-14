# Codex Mechanical Implementation Plan — Finish Alpha Proof Without Architectural Improvisation

Baseline: `main` at `4a3409e9dfac71efc6619632fceae143e9d9d498`.

This plan is intentionally written for an implementation agent that should **not invent architecture**. Follow the tasks in order. Do not skip acceptance tests because a nearby test looks similar. Do not collapse several tasks into one large commit. Do not mark a task complete because the code compiles.

The current code already has a useful alpha surface:

- `Prepare`, `PreviewDetailed`, `PlanDetailed`, direct `Commit`, binding `Commit(plan)`, and separate `Dispatch` exist;
- binding plans are version-bound and do not rerun semantic model code when installed;
- durable source identities exist for normal, removed, added, and post-save generated-key origins;
- object-set and projection rollback already use touched-entry capture;
- packed package consumers exercise binding plans;
- local release-style verification was recorded;
- benchmark methods now separate preview, planning, and planned-patch installation.

Do **not** reimplement those features.

The remaining work is narrower but important. Current verification found all of the following still true:

1. `MutationCommit.CreateCauses(...)` still reconstructs relation/upstream/reaction causes after propagation.
2. `CaptureRelationOriginIds(...)` is still an inference helper. For ambiguous conservative routing it now emits no origin instead of a wrong superset, which is safer but still not decision-time evidence.
3. `PreparedImpactPlan` still stores pre/post `RuntimeStateSnapshot` objects.
4. Every affected relation still uses full `IRelationRuntimeState.CaptureState()`, cloning complete relation dictionaries.
5. Any lifecycle or indexed-navigation change can still call whole `NavigationIndexRegistry.CaptureState()`.
6. Dependency rollback still snapshots complete state for selected nodes rather than source-scoped touched state.
7. There is no full mixed-model randomized proof that Preview, Plan, direct Commit, and `Commit(plan)` remain equivalent.
8. There is no broad failure-injection proof for planning/install/dispatch boundaries.
9. Corrected planning benchmarks exist, but no checked-in benchmark results prove scaling after the latest changes.
10. SQLite outbox coverage still lacks the complete generated-key/failure/recovery matrix.
11. The remote `release-candidate.yml` workflow has never been dispatched. Local verification is not a substitute for that gate.

Implement exactly these tasks:

```text
67  Replace post-hoc relation provenance with decision-time route evidence
68  Replace remaining broad snapshots with touched-state journals and an explicit forward patch
69  Add full mixed-model semantic parity and failure-atomicity tests
70  Run and record honest planning/patch scaling benchmarks
71  Complete SQLite transactional-outbox generated-key and recovery proof
72  Run the remote RC workflow and close the alpha roadmap only if it passes
```

Do not add new feature families. In particular, do not add hypothetical pre-mutation simulation, distributed execution, key-based/nested/nullable projected selectors, derived values inside relation predicates, generic `.Using(...)` arity 3–8, new planner families, logging/DI abstractions, or transport/outbox APIs in the core package.

---

# Agent operating rules

These rules are mandatory.

## Before each task

1. Pull/re-read current `main`.
2. Verify no newer commit already implements the exact task.
3. Read the files named in the task before editing them.
4. Add or modify tests **before** declaring the task complete.

## After each task

Run at least:

```bash
dotnet build Raffinert.Relations.sln -c Release

dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
```

If a task does not touch EF, the EF command may be run before the commit instead of after every micro-step, but it must pass before the task commit.

## Commit rules

- One concern per commit.
- Never commit a red test suite.
- Never use a commit message like `finish roadmap` when several acceptance items remain.
- Do not update CHANGELOG/RC docs to claim completion until the corresponding code/tests/remote workflow actually pass.
- Do not publish NuGet packages, create a tag, or create a GitHub Release.

---

# Task 67 — Replace post-hoc relation provenance with decision-time route evidence

## Goal

A public causal result must describe **what actually routed the impact**, not what can be guessed from the final relation state.

After this task, `CreateCauses(...)` may transform captured internal evidence into public records, but it must not infer relation provenance from CLR type/member-name heuristics.

## Files to read first

- `src/Raffinert.Relations/RelationImpact.cs`
- `src/Raffinert.Relations/Runtime/RelationRuntimeState.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- `src/Raffinert.Relations/Dependencies/NavigationAndImpact.cs`
- `src/Raffinert.Relations/Dependencies/DependencyGraphRuntime.cs`
- `tests/Raffinert.Relations.Tests/CausalImpactTests.cs`

## 67.1 Add route-trigger data to `RelationDelta`

In `RelationImpact.cs`, keep existing added/removed pair data and affected-left behavior. Add internal route-trigger information without changing public APIs.

Required conceptual shape:

```csharp
internal sealed record RelationRouteTrigger(
    object Left,
    object Trigger,
    RelationImpactCauseKind Kind,
    ImpactCausePrecision Precision);
```

`RelationDelta` must be able to retain multiple triggers for the same left source. Do not collapse by left only.

Add an internal collection such as:

```text
RouteTriggers: (left, trigger object, cause kind, precision)
```

Do not put origin IDs here. `RelationRuntimeState` does not know mutation-origin numbering. It only knows which runtime object caused/routed the left source.

## 67.2 Record trigger objects where routing actually happens

Modify `RelationRuntimeState<TLeft,TRight>` methods. Do not reconstruct this later.

### Exact membership addition/removal

When `AddPair(left, right, delta)` records a new pair, also record:

```text
left = left
trigger = right
kind = MembershipAdded
precision = Exact
```

When `RemovePair(left, right, delta)` or `RemoveRight` removes a materialized pair, record:

```text
left = left
trigger = right
kind = MembershipRemoved
precision = Exact
```

A left-side predicate/key mutation can also be causal. Therefore later origin mapping must also include mutation origins whose source is the affected left itself. Do not try to encode mutation IDs inside relation state.

### Conservative candidate routing

Replace every bare `delta.Affect(left)` performed because of a specific right-side object with a route-aware operation.

Examples:

- `AddRight(right)` conservative candidate -> trigger is `right`;
- `RemoveRight(right)` conservative candidate -> trigger is `right`;
- `ReindexRight(right)` old candidates -> trigger is `right`;
- `ReindexRight(right)` new candidates -> trigger is `right`;
- `RefreshMembership(... rights ...)` conservative candidate -> trigger is each concrete `right`;
- `RefreshMembership(... lefts ...)` direct affected left -> trigger is the same `left` object.

Precision for these conservative candidate routes is `Conservative`.

**Do not** use `relation.RightSet.ObjectType.IsInstanceOfType(origin.Source)` as a replacement. The point of this task is to stop guessing by type.

## 67.3 Carry route triggers into `RelationImpact`

`RelationImpact` must copy the current wave's route triggers from `RelationDelta` into an internal read-only property.

Do not expose a new public API unless absolutely required. This is execution evidence used to build the already-existing public cause records.

## 67.4 Map trigger objects to normalized mutation origins

In `MutationCommit.cs`, delete `CaptureRelationOriginIds(...)` after the new route evidence is used everywhere.

Add a helper with semantics equivalent to:

```text
For relation cause on affected left L:
  start with normalized origins whose Source reference == L
  add normalized origins whose Source reference == each recorded Trigger for L
  retain only origins that are relevant to that relation dependency
  preserve multiple real origins
  sort/distinct OriginId
```

Relevance rules:

- `ObjectAdded` / `ObjectRemoved` is relevant when its source is the exact left or exact trigger object.
- `SourceMemberChanged` is relevant only when the origin member participates in that relation's analyzed dependency paths.
- `CollectionChanged` follows the same exact source/member rule.
- Never include an origin merely because it has the same CLR type as a trigger.

If there is no captured trigger matching an origin, emit no origin ID. Missing evidence is better than invented evidence.

## 67.5 Keep upstream cause links source-scoped

Do not regress the existing `ImpactId` / `UpstreamImpactId` link.

Upstream cause precision must come from propagated `ConservativeSources`; do not reintroduce recursive `IsConservative(...)` graph walking.

## 67.6 Tests to add first

Add these tests to `CausalImpactTests.cs` using these names or equally explicit names:

```text
Conservative_relation_cause_links_only_the_right_object_that_routed_that_left
Two_conservative_right_changes_can_produce_different_origin_sets_per_left
Exact_membership_add_carries_exact_trigger_origin
Exact_membership_remove_carries_exact_trigger_origin
Left_side_relation_change_carries_left_origin
Relation_cause_never_uses_same_type_unrelated_origin
Ambiguous_batch_no_longer_requires_origin_omission_when_route_trigger_is_known
```

The important regression scenario is:

```text
Source A matches Item A
Source B matches Item B
Item A.Code changes
Item B.Code changes
```

Each affected left must point only at the item mutation(s) that actually routed that left. The current implementation may emit no origins in an ambiguous conservative batch; the new implementation should have enough route evidence to be precise.

## 67 acceptance checklist

- [ ] `CaptureRelationOriginIds(...)` is deleted.
- [ ] No relation origin mapping uses source CLR type as a causal heuristic.
- [ ] Conservative routes keep the exact trigger object(s) per affected left.
- [ ] Exact add/remove pairs preserve exact trigger objects.
- [ ] Existing source-scoped `UpstreamImpactId` behavior still passes.
- [ ] Summary mode still builds no public causal IDs/causes.
- [ ] net8 + net10 core tests pass.

Suggested commits:

```text
test: pin decision-time relation provenance
refactor: carry relation route triggers through impact deltas
fix: build relation causes from captured route evidence
```

---

# Task 68 — Replace remaining broad snapshots with touched-state journals and an explicit forward patch

## Goal

`PlanDetailed` and rollback must scale with what one mutation wave touched, not with the total population inside an affected relation/navigation graph.

Do this in three substeps. Do **not** rewrite every runtime component at once.

## Files to read first

- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- `src/Raffinert.Relations/Runtime/RelationRuntimeState.cs`
- `src/Raffinert.Relations/Dependencies/NavigationAndImpact.cs`
- `src/Raffinert.Relations/Dependencies/DependencyGraphRuntime.cs`
- `src/Raffinert.Relations/Dependencies/ProjectionIndexRegistry.cs`
- `src/Raffinert.Relations/Runtime/ObjectSetRuntime.cs`
- `src/Raffinert.Relations/Policies/PolicyActions.cs`

Keep the already-scoped object-set/projection implementation. Do not replace working touched-entry code with whole snapshots.

## 68.1 Introduce internal patch terminology

Create internal types, preferably in a new file such as:

`src/Raffinert.Relations/Runtime/RuntimeStatePatch.cs`

Use clear separation:

```text
RuntimeRollbackJournal  = enough PRE-state to undo a failed/reversible execution
RuntimeForwardPatch     = enough POST-state to install a completed binding plan
```

Do not keep the name `RuntimeStateSnapshot` for the final binding-plan representation.

`PreparedImpactPlan` should eventually hold one `RuntimeForwardPatch`, not general pre/post snapshots.

The plan itself does **not** need the rollback journal after `PlanDetailed` has restored the runtime.

## 68.2 Relation state: capture only touched keys/sources/pairs

Current `IRelationRuntimeState.CaptureState()` copies all of:

```text
_index
_keys
_leftIndex
_leftKeys
_rightsByLeft
_leftsByRight
```

Do not use that method for normal mutation rollback/planning after this task.

Add scoped relation journal/patch methods. The exact method names may differ, but the responsibilities must be separate:

```csharp
object CaptureTouchedBefore(...);
object CaptureTouchedAfter(...);
void RestoreTouched(object state);
```

The touched scope must be derived from the actual mutation wave:

- changed/lifecycle left roots;
- changed/lifecycle right roots;
- old + new hash/index keys for those roots;
- left/right objects appearing in the wave's `RelationDelta` added/removed pairs;
- conservative candidate lefts actually routed for changed right objects.

Do not clone unrelated buckets.

### Implementation guidance

For each mutable relation dictionary, journal the first old value for a touched key/object and capture the final value for the same key/object after execution.

For bucket dictionaries (`_index`, `_leftIndex`, `_rightsByLeft`, `_leftsByRight`) copy only the touched bucket contents.

For scalar diagnostics, store the previous/final `_predicateEvaluationCount` value directly.

Do not attempt to serialize these patches; they are internal in-process state.

## 68.3 Navigation indexes: journal touched owners and reverse targets

Current `NavigationIndexRegistry.CaptureState()` clones all indexes and registrations.

Add touched-state capture based on the roots already known to `MutationCommit.CaptureState(...)` / execution planning.

For each affected root:

1. capture its old registration membership;
2. capture the old forward entry for each navigation owner that may be removed/refreshed;
3. capture reverse buckets for old targets;
4. after execution capture the same owner plus newly discovered owners/targets;
5. restore only those owners/buckets during rollback or patch installation.

Both scalar `NavigationIndex` and `CollectionNavigationIndex` must be covered.

Do not clear/rebuild `_registrations` for every set during restore.

Add tests with a large number of unrelated registrations and one changed root. The test should assert unrelated owners survive unchanged.

## 68.4 Dependency graph: source-scoped state, not whole node cache state

Current `DependencyGraphRuntime.CaptureState(...)` selects affected nodes, but `DerivedNode.CaptureState()` delegates to `State.CaptureState()` and can still copy all cached source state inside that node.

Add source-scoped capture to derived/invariant runtime state implementations used by the graph.

The capture set should include only sources that can be touched in the wave:

- directly affected sources;
- relation-routed sources;
- projected downstream sources;
- downstream DAG fan-out sources;
- previous-wave sources that are being cleared.

For each node also retain its current `InvalidSources`, `DirtySources`, `ConservativeSources`, and direct evidence only as needed for touched sources/current wave bookkeeping.

Do not capture cache entries for unrelated sources in the same derived definition.

## 68.5 Convert `PlanDetailed` to one forward patch

Current shape conceptually is:

```text
execute
capture PRE RuntimeStateSnapshot
capture POST RuntimeStateSnapshot
restore PRE
PreparedImpactPlan stores PRE + POST
```

Target shape:

```text
begin rollback journal
execute once
build RuntimeForwardPatch from touched final state
restore using rollback journal
PreparedImpactPlan stores RuntimeForwardPatch only
```

Then `Commit(plan)` must:

1. validate runtime ownership;
2. validate not already committed;
3. validate base version;
4. validate prepared domain state/projected final state as today;
5. install `RuntimeForwardPatch`;
6. increment version;
7. mark prepared + plan committed;
8. return the exact stored `RuntimeApplyResult` instance.

If patch installation throws halfway through, use an install rollback journal for the same touched entries. Do not require the original Plan-time PRE snapshot to stay stored forever.

## 68.6 Tests

Add focused tests before converting all components:

```text
Plan_of_one_relation_change_does_not_capture_unrelated_relation_population
Plan_of_one_navigation_change_does_not_restore_unrelated_navigation_roots
Plan_of_one_derived_source_does_not_copy_unrelated_cached_sources
Commit_plan_installs_exact_planned_touched_state
Commit_plan_install_failure_restores_preinstall_touched_state
Stale_plan_does_not_install_any_patch_entry
Plan_object_does_not_retain_general_runtime_pre_snapshot
```

If internal patch-size diagnostics are useful, expose **internal** counts to tests/benchmarks only. Do not add a public diagnostics API just for this roadmap.

## 68 acceptance checklist

- [ ] normal planning path does not call full relation `CaptureState()`;
- [ ] normal planning path does not call whole `NavigationIndexRegistry.CaptureState()`;
- [ ] dependency cache rollback is source-scoped for affected nodes;
- [ ] `PreparedImpactPlan` no longer stores both general PRE and POST snapshots;
- [ ] `Commit(plan)` still does zero predicate/classifier/propagation reexecution;
- [ ] install failure is atomic for touched runtime state;
- [ ] unrelated runtime populations survive preview/plan/failed commit unchanged;
- [ ] all core tests pass on net8/net10.

Suggested commits:

```text
refactor: introduce runtime rollback journal and forward patch
perf: journal touched relation state
perf: journal touched navigation state
perf: scope dependency rollback to affected sources
refactor: store compact forward patch in prepared impact plans
```

---

# Task 69 — Full mixed-model parity and failure-atomicity proof

## Goal

Prove with one realistic graph that all supported execution surfaces agree semantically.

Do not use only a single scalar source-derived value.

## New test file

Create:

`tests/Raffinert.Relations.Tests/PreparedImpactMixedModelTests.cs`

## 69.1 Build one deterministic mixed model

Use stable IDs so four independent scenario copies can receive the same logical mutation sequence.

Include all of these primitives in the same model:

1. keyed object sets;
2. one exact relation;
3. one `PreferConservativePropagation()` relation;
4. source-only derived value;
5. relation-backed derived aggregate;
6. upstream derived chain;
7. diamond dependency fan-in;
8. one projected upstream direct-reference dependency;
9. one two-upstream projected composition;
10. one invariant using `ScheduleRepair` or equivalent repair-producing reaction;
11. value-sensitive `SourceMemberChanged` severity (`Dirty` for additive, `Invalid` for subtractive);
12. at least one collection dependency/change;
13. add/remove lifecycle operations;
14. projected-reference retargeting.

A procurement-shaped model is preferred because the repository already uses that language, but keep the test self-contained.

## 69.2 Run four independent runtimes

For each logical mutation wave, create the same mutation against four cloned scenarios:

```text
A = direct CommitDetailed
B = PreviewDetailed, then direct CommitDetailed
C = PlanDetailed, then Commit(plan)
D = ApplyDetailed
```

Use both Summary and Causal detail in separate runs.

Do not reuse domain object instances across scenarios.

## 69.3 Compare complete semantic result data

Write one comparison helper that normalizes by stable definition keys and durable source identities.

Compare:

- `ChangeImpact`;
- relation definition key + added/removed durable pairs + affected sources;
- derived definition key + source durable identity + severity;
- invariant definition key + source durable identity + severity;
- repair request identity/reason;
- immediate evaluation requests;
- causal mutation origins;
- cause types;
- cause precision;
- direct member policy/severity;
- relation cause kind + origin IDs after normalizing origin identity;
- upstream links by definition/source identity, not raw integer assignment alone.

Do not write a helper that only compares counts.

## 69.4 Randomized deterministic waves

Use fixed seeds, for example `0..19`, with at least 50 waves per seed.

Choose operations from:

```text
scalar increase
scalar decrease
right item change exact relation
right item change conservative relation
collection add/remove/reset
object add
object remove when legal
projected reference retarget
multiple changes to same member in one batch (normalized chain)
combined two-origin batch
```

Every generated wave must be legal for all scenario copies.

On failure print seed + wave + operation description.

## 69.5 Failure injection tests

Add deterministic tests for these boundaries:

```text
classifier throws during Preview -> runtime unchanged
classifier throws during Plan -> runtime unchanged
relation predicate throws during Plan -> runtime unchanged
projection selector/validation failure -> runtime unchanged
stale plan Commit -> runtime unchanged
prepared domain drift before Commit(plan) -> runtime unchanged
second Commit(plan) -> rejected, no state change
dispatch callback throws -> committed runtime remains committed and retry behavior matches existing resumable contract
```

Do not swallow user exceptions. Assert exception type/message where stable.

## 69 acceptance checklist

- [ ] mixed graph includes every primitive listed above;
- [ ] comparator checks semantic content, not counts only;
- [ ] deterministic randomized suite passes on net8 and net10;
- [ ] Preview leaves runtime version/state unchanged;
- [ ] Plan leaves runtime version/state unchanged;
- [ ] direct commit and binding-plan commit end in equivalent runtime state;
- [ ] failures before commit leave runtime unchanged;
- [ ] dispatch failure does not roll back an already committed runtime.

Suggested commits:

```text
test: add mixed prepared impact parity fixture
test: add randomized preview plan commit equivalence
test: inject planning install and dispatch failures
```

---

# Task 70 — Run and record honest planning/patch scaling benchmarks

## Goal

The benchmark code now separates Preview, Plan, and planned-patch installation. Prove the latest journal/patch implementation actually scales with touched state.

## Files

- `benchmarks/Raffinert.Relations.Benchmarks/PreparedImpactPlanningBenchmarks.cs`
- add `benchmarks/PreparedImpactPlanning-Results.md`

## 70.1 Keep existing simple microbenchmarks

Keep distinct measurements for:

```text
CommitSummary
CommitCausal
PreviewSummary
PreviewCausal
PlanSummary
PlanCausal
CommitPlannedSummary
CommitPlannedCausal
```

Do not put a commit inside `Preview*` or `Plan*` timing.

## 70.2 Add population-scaling scenarios

Add a separate benchmark class for a **single touched source** with different unrelated populations:

```text
1,000 sources
10,000 sources
100,000 sources
```

Measure at least:

1. one scalar derived source change;
2. one exact relation right-side change with small fan-out;
3. one conservative relation right-side change with small candidate fan-out;
4. one navigation-root refresh;
5. one projected-reference retarget;
6. one lifecycle add/remove.

The scenario must keep touched fan-out roughly constant while total population grows.

## 70.3 Record environment and results

Run BenchmarkDotNet Release benchmarks and commit a result summary containing:

- date;
- OS;
- .NET runtime;
- CPU if available;
- exact git commit;
- mean time;
- allocated bytes;
- ratios from 1k -> 10k -> 100k.

Do not cherry-pick only favorable numbers.

## 70.4 Acceptance interpretation

The target is not identical nanoseconds. The important requirement is that planning/patch-install allocation for one touched source does not scale approximately linearly with total unrelated population.

If a 100x population increase produces a near-100x allocation increase in a fixed-fan-out scenario, Task 68 is incomplete. Investigate before documenting success.

## 70 acceptance checklist

- [ ] preview measures preview only;
- [ ] planning measures planning only;
- [ ] patch install measures `Commit(plan)` only;
- [ ] fixed-fan-out population benchmarks exist;
- [ ] result markdown is committed;
- [ ] no obvious O(total unrelated population) allocation remains in Plan/Commit(plan).

Suggested commits:

```text
bench: add fixed-fanout prepared plan scaling cases
bench: record final alpha planning and patch results
```

---

# Task 71 — Complete SQLite transactional-outbox generated-key and recovery proof

## Goal

Prove the recommended durability workflow, including generated identities and failure boundaries, using a real SQLite transaction and a real outbox table.

## File

`tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreSqliteTests.cs`

Keep the existing stable-key outbox tests. Add the missing cases below.

## 71.1 Multiple store-generated additions

Add a test with at least two newly added entities whose database keys are store-generated.

Required sequence:

```text
CaptureUnitOfWork BEFORE first SaveChanges
Begin explicit transaction
SaveChanges -> database assigns keys
unit.Prepare(runtime)
plan = unit.PlanDetailed(runtime, Causal)
create outbox rows ONLY from plan.Result durable identities/data
SaveChanges outbox
commit DB transaction
unit.Commit(runtime)  // must install the binding plan
unit.Dispatch(runtime)
```

Assertions:

- generated keys are non-default;
- every persisted outbox source identity comes from `plan.Result`, not direct reads of `entity.Id` when constructing payload;
- runtime version advances once;
- runtime contains/reflects both generated-key entities correctly;
- outbox rows and business rows are in the same committed transaction.

## 71.2 Plan failure after first SaveChanges rolls DB back

Inside an explicit transaction:

1. first `SaveChanges` succeeds;
2. `PlanDetailed` throws intentionally from a classifier/predicate/validation hook;
3. roll back transaction;
4. verify business rows did not persist;
5. verify runtime version/state did not advance;
6. verify no outbox rows persisted.

Use a deterministic test-only failure trigger. Do not rely on random database failure.

## 71.3 Outbox SaveChanges failure

Keep/extend the existing unique-payload failure test.

Also verify the planned object was not accidentally marked committed and can either be discarded/replanned according to current contract.

## 71.4 Database succeeds but `Commit(plan)` cannot install

Model the documented recovery boundary deliberately:

1. produce a valid binding plan and durable outbox rows;
2. commit the DB transaction;
3. make the in-memory plan stale before installing it (for example advance runtime version with another legal committed mutation);
4. assert `Commit(plan)` rejects installation;
5. assert DB business row + outbox remain durable;
6. assert documentation/recovery expectation is rebuild/reconcile runtime from authoritative DB/outbox, not retry semantic planning against unknown state.

This is not a success path. It proves the documented failure boundary is real and detectable.

## 71.5 Dispatch failure is retryable after durable commit

Use a repair/policy callback that fails on first dispatch and succeeds on second attempt if the existing dispatch contract supports retry.

Assertions:

- DB and outbox stay committed;
- runtime stays committed;
- semantic work is not rerun;
- successful retry does not duplicate already completed callback actions beyond the existing resumability contract.

If current dispatch semantics intentionally do not support retry of the same handle, document the exact supported recovery behavior instead of inventing a new public API in this task.

## 71 acceptance checklist

- [ ] stable-key outbox success still passes;
- [ ] multiple generated-key success exists;
- [ ] payload/identity comes from `plan.Result`;
- [ ] plan failure rolls back first business save;
- [ ] outbox save failure rolls back business save;
- [ ] DB-success/runtime-install-failure boundary is tested;
- [ ] dispatch failure/recovery behavior is tested and documented;
- [ ] EF test suite passes on net10.

Suggested commits:

```text
test: prove generated-key binding plan outbox atomicity
test: cover post-save plan and runtime install failures
test: prove post-commit dispatch recovery behavior
```

---

# Task 72 — Run remote RC workflow and close the alpha roadmap only if it passes

## Goal

Do not call the alpha RC-proven until the repository's actual `workflow_dispatch` release-candidate job has passed on the final implementation head.

## 72.1 Final local gate

Run:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Then run fresh-cache packed consumers exactly like `.github/workflows/release-candidate.yml`.

## 72.2 Dispatch GitHub workflow

Preferred command if `gh` is available:

```bash
gh workflow run release-candidate.yml --ref main
```

Then locate the run and wait for completion:

```bash
gh run list --workflow release-candidate.yml --limit 5
gh run watch <run-id> --exit-status
```

If the agent environment cannot dispatch workflows, **do not mark Task 72 complete**. Leave an explicit blocking note saying remote RC dispatch still requires a human/tool with Actions write permission.

## 72.3 Verify artifact

The successful run must upload `release-candidate-packages` containing:

```text
2 .nupkg
2 .snupkg
```

Do not publish them.

## 72.4 Update docs only after success

Update `docs/release-candidate-verification.md` with:

- final verified commit SHA;
- remote workflow run ID/link;
- conclusion `success`;
- test counts;
- package artifact name;
- explicit statement that packages were not published.

Update `docs/roadmaps/README.md` so Tasks 67–72 move from Active to Completed only after the remote workflow passes.

If the remote workflow fails, fix the actual failure, rerun it, and document the successful rerun. Do not edit docs to call a failed run acceptable.

## 72 acceptance checklist

- [ ] final local release gate passes;
- [ ] manual `workflow_dispatch` run exists on final implementation head;
- [ ] remote run conclusion is success;
- [ ] RC package artifact exists;
- [ ] verification doc records real run ID/link;
- [ ] no publish/tag/release performed;
- [ ] roadmap is marked completed only after all above conditions.

Suggested commits after the remote run:

```text
docs: record successful first alpha rc verification
docs: close mechanical alpha finish roadmap
```

---

# Required final regression matrix

The agent must explicitly verify all of these before claiming the roadmap complete.

| Area | Required proof |
| --- | --- |
| Registration | unregistered/removed/wrong-set source queries still reject |
| Dirty/Invalid | `Invalid` dominates `Dirty`; value-sensitive source rules still work |
| Exact relation | membership add/remove cause precision and origins are exact |
| Conservative relation | affected left receives only the actual right-side route origins |
| Ambiguous batch | no guessed same-type origin superset |
| DAG | chain + diamond propagate once with correct severity |
| Projected dependency | one-upstream and two-upstream projected composition still work |
| Projection integrity | missing target / illegal removal / retarget validation still works |
| Lifecycle | add/remove remains exception atomic |
| Preview | no version/state/dispatch mutation |
| Plan | no version/state/dispatch mutation; result bound to exact patch |
| Commit(plan) | no semantic reexecution; one version increment |
| Failed install | touched runtime state rolls back |
| Causal mode | durable origins, source-scoped impact IDs, monotonic conservative precision |
| Summary mode | no causes/impact IDs built |
| Outbox | stable-key + generated-key transactional cases |
| DB failure | business + outbox transaction rolls back, runtime unchanged |
| DB success/runtime failure | detectable, durable outbox remains, rebuild/reconcile path documented |
| Dispatch failure | committed state remains committed; supported retry/recovery proven |
| Performance | one touched source does not allocate proportional to unrelated 100k population |
| Packaging | Core net8/net10 + EF net10 packed consumers pass |
| RC | actual remote `workflow_dispatch` run succeeds |

---

# Recommended commit sequence

Do not combine these into one giant commit.

```text
1.  test: pin decision-time relation provenance
2.  refactor: carry relation route triggers through impact deltas
3.  fix: build relation causes from captured route evidence
4.  refactor: introduce runtime rollback journal and forward patch
5.  perf: journal touched relation state
6.  perf: journal touched navigation state
7.  perf: scope dependency rollback to affected sources
8.  refactor: store compact forward patch in prepared impact plans
9.  test: add mixed prepared impact parity fixture
10. test: add randomized preview plan commit equivalence
11. test: inject planning install and dispatch failures
12. bench: add fixed-fanout prepared plan scaling cases
13. bench: record final alpha planning and patch results
14. test: prove generated-key binding plan outbox atomicity
15. test: cover post-save plan and runtime install failures
16. test: prove post-commit dispatch recovery behavior
17. docs: update alpha contract after implementation proof
18. docs: record successful first alpha rc verification
19. docs: close mechanical alpha finish roadmap
```

If one of these commits becomes unnecessary because current `main` already contains the exact behavior, document that in the active roadmap completion notes instead of recreating it.

---

# Definition of done

This roadmap is done only when **all** of the following are true at the same final commit (or documentation-only child commit):

1. relation causal provenance comes from recorded route triggers, not inference by type/member heuristics;
2. binding plans retain a scoped forward patch rather than broad pre/post runtime snapshots;
3. relation, navigation, projection, object-set, and dependency rollback/install are touched-state scoped;
4. mixed-model deterministic randomized parity tests pass on net8/net10;
5. failure injection proves preview/plan/install/dispatch boundaries;
6. benchmark results show no obvious population-proportional cost for fixed-fan-out plan/install work;
7. SQLite outbox proof covers stable keys, multiple generated keys, DB rollback, runtime-install failure, and dispatch recovery;
8. packed consumers pass;
9. the remote manual release-candidate workflow has actually run and succeeded;
10. no package/tag/release has been published by this roadmap.

If any item is false, keep this roadmap active.