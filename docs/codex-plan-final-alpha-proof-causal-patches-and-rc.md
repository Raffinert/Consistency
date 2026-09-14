# Codex Implementation Plan — Final Alpha Proof, Decision-Time Causality, Scoped Patches, and RC Gate

Baseline: `main` at `1741667acff8c9ab220838ccb47bce80d6726492`.

Verified implementation baseline: `ef18e01da7b70cb1b7c9074a9b1209b90c9cd226` passed CI run #96 successfully. The current documentation-only head has CI run #97 in progress at the time this roadmap was prepared.

Tasks 55–60 are **partially implemented and superseded by this plan**. Preserve what already landed; do not recreate it:

- summary results no longer expose causal `ImpactId`s;
- `ObjectAdded` causal origins can derive a durable identity from their final key before runtime registration;
- store-generated EF additions planned after the first transactional save retain final durable identities;
- object-set lifecycle rollback captures only touched entries;
- projection rollback captures only touched downstream projection sources;
- conservative precision is carried in derived propagation state instead of recursively recomputed from the final graph;
- a real SQLite outbox table now exists and one successful + one outbox-save-failure transaction are tested;
- packed CoreNet8/CoreNet10/EF consumers now exercise `PlanDetailed` / binding-plan commit;
- main implementation CI is green.

This verification also found that the first alpha still lacks proof in several areas:

1. `MutationCommit.CreateCauses(...)` still constructs relation/upstream/reaction causes after propagation;
2. `ResolveRelationOriginIds(...)` still attributes conservative relation origins using member-name / right-side-type heuristics rather than routing evidence captured at the decision point;
3. `PreparedImpactPlan` still stores `RuntimeStateSnapshot` pre/post objects rather than a purpose-built forward patch;
4. each affected relation still uses full `IRelationRuntimeState.CaptureState()`, which clones its complete indexes/pair maps;
5. any lifecycle/indexed-navigation change still calls whole `NavigationIndexRegistry.CaptureState()`;
6. dependency-state rollback is still snapshot-oriented and has not been proven proportional to touched nodes/sources;
7. the mixed-model randomized Preview/Plan/Commit equivalence and failure-injection gates from the prior roadmap did not land;
8. `PreparedImpactPlanningBenchmarks.Preview*` still performs a normal commit inside the benchmark method, so the measurement label is false;
9. there is still no committed prepared-planning benchmark result document;
10. the SQLite outbox proof does not yet cover multiple store-generated entities, post-save plan failure, rollback after planning, runtime-commit failure after DB durability, or resumable dispatch failure;
11. the current outbox success test hand-builds its payload from the domain entity ID instead of proving durable persistence can be produced solely from `plan.Result` identities/reasons;
12. `RuntimeApplyResult` XML still says it is produced by a committed mutation even though Preview/Plan return it;
13. the manual `release-candidate.yml` workflow still has zero `workflow_dispatch` runs.

Implement this wave in order:

```text
61  Finish decision-time source-scoped causal evidence and remove inference helpers
62  Replace remaining relation/navigation/dependency snapshots with touched-state journals + an explicit forward patch
63  Prove full mixed-model Preview / Plan / direct Commit / Commit(plan) equivalence and failure atomicity
64  Correct prepared-planning benchmarks and commit scaling evidence
65  Complete the transactional-outbox proof, including generated keys and failure/recovery boundaries
66  Freeze the first-alpha contract and execute the release-candidate verification gate
```

Do **not** add a new feature family in this roadmap. In particular: no hypothetical pre-mutation simulation, distributed runtime, nullable/key-based/nested projected selectors, generic `.Using(...)` arity 3..8, derived-valued relation predicates, additional query planners, or runtime auto-tuning.

---

# Global implementation rules

1. Correctness, failure atomicity, and recoverability outrank explanation richness and micro-optimizations.
2. Original relation/computation/invariant expressions remain semantic authority.
3. `Invalid` dominates `Dirty`; propagation can never weaken severity.
4. Conservative precision is monotonic downstream and may never be upgraded to Exact.
5. Public causal output may claim only evidence captured when the runtime actually selected/routed that source.
6. Summary/basic execution must not allocate causal graphs, source-scoped causal IDs, or relation-origin evidence.
7. A binding plan is durability-sensitive: `Commit(plan)` must install exactly what was planned without semantic re-execution.
8. Planning/preview rollback state must scale with touched structures, not total runtime population.
9. Preview/Plan never dispatch application callbacks and never advance `Version`.
10. Core remains free of EF Core, transport, DI, logging, and persistence abstractions.
11. Preserve Prepare/Commit version binding, domain-drift checks, key immutability, exact-set projection ownership, and resumable dispatch.
12. Do not weaken the direct non-null projected-reference alpha contract.
13. Every public API change requires intentional `PublicAPI.Unshipped.txt` updates.
14. Core changes pass net8.0 + net10.0; EF adapter changes pass net10.0.
15. Do not publish packages, create tags, or create a GitHub release.
16. Keep commits small and bisectable. Do not land this roadmap as one large commit.

---

# Task 61 — Finish decision-time source-scoped causal evidence

## Goal

Make the causal result a serialization/projection of evidence produced by routing and propagation, not a second reasoning engine over final snapshots.

Current `ImpactId`, `UpstreamImpactId`, durable origins, and propagated conservative markers are useful foundations. Keep them. Remove the remaining post-hoc inference.

## 61A — execution-scoped causal context

Create an internal causal capture object only for `RuntimeImpactDetailLevel.Causal` executions.

Conceptual shape:

```text
CausalExecutionContext
  OriginByPreparedSignal
  EvidenceNode[(definition, source reference)]
  RelationRouteEvidence[(relation, affected-left/source)]
  UpstreamEdges
  ReactionEdges
```

The exact names are flexible. The important contract is:

- no context exists in Basic/Summary execution;
- evidence identity is `(definition, source reference)` for dependency nodes;
- relation routing evidence is keyed by the actual relation + affected left/source;
- the context is execution-local and is not retained by the runtime after the result/plan has been built.

## 61B — capture relation causes at routing time

Today `CreateCauses(...)` looks at final `RelationImpact` and calls `ResolveRelationOriginIds(...)`. Replace that.

When a relation delta / candidate route actually selects a left source, capture:

```text
relation
left source
cause kind
Exact | Conservative
origin IDs that actually caused this route
optional changed right source / membership pair identity when known
```

Required cause kinds remain:

```text
MembershipAdded
MembershipRemoved
RelatedItemChanged
ConservativeCandidate
```

For exact membership changes, origin provenance should come from the exact lifecycle/property signal that changed the pair.

For conservative old/new-key routing, carry the right-side mutation origin through candidate routing. Do not later infer all same-member instances of the right CLR type.

If exact origin attribution is impossible for a conservative fallback, emit an empty `OriginIds` set rather than a guessed superset.

### Acceptance

Delete `ResolveRelationOriginIds(...)` or reduce it to a trivial lookup of captured evidence. No member-name/type heuristic remains in public result construction.

## 61C — capture upstream edges while applying inheritance

`DerivedNode.ApplyInherited(...)` already sees:

```text
upstream input
upstream node
mapped upstream source set
resulting downstream sources
upstream severity
upstream conservative marker
```

That is the correct point to record an upstream evidence edge.

For every downstream source impact capture:

```text
DownstreamNodeId
UpstreamNodeId / source-scoped upstream identity
incoming severity
incoming precision
projection mapping when projected
```

Do not rediscover upstream relationships later by scanning `DerivedImpacts`.

The projected case must preserve different object identities:

```text
LinkValidity(link-42)
  <- AvailableQuantity(po-line-7)
```

## 61D — capture invariant reaction evidence when reaction occurs

Record a reaction edge only when reaction semantics actually transform the impact:

```text
Reaction
InputSeverity
OutputSeverity
```

Already-Invalid inherited input must not produce a fake `Dirty -> Invalid` escalation.

## 61E — public result builder becomes a projection

After this task `CreateSourceImpacts(...)` may still group/order snapshots, but it must not *derive* causes.

The public builder should only:

```text
assign stable current-result ImpactIds
map internal evidence -> public cause records
resolve upstream evidence-node IDs
create durable SourceIdentity
sort deterministically
```

No relation heuristics, recursive precision reasoning, or invariant severity inference should remain there.

## 61F — causal determinism tests

Add/strengthen tests for:

```text
same member changed on two right objects; only routed origin appears
same CLR type registered in multiple object sets; wrong set never leaks origin
exact add/remove pair provenance
conservative old-key + new-key routing provenance
conservative fallback with unknowable origin -> empty IDs, never guessed IDs
projected upstream source identity differs from downstream source
exact + conservative diamond -> downstream Conservative
same derived definition impacting two different source objects -> distinct upstream IDs
mutation order permutations -> identical causal graph ordering
summary/basic -> no causal capture allocations/state
```

Suggested commits:

```text
test: expose remaining post-hoc causal attribution gaps
refactor: capture relation routing evidence at decision time
refactor: capture upstream and reaction causal edges during propagation
refactor: render causal results from captured evidence
```

---

# Task 62 — Replace remaining broad snapshots with touched-state journals and an explicit forward patch

## Goal

Complete the work started by `CaptureEntriesState(...)` and projection source patches.

A binding plan should no longer conceptually be “pre snapshot + post snapshot.” It should be a compact forward patch plus only the inverse state needed for planning rollback / failed patch installation.

## 62A — introduce explicit internal patch/journal abstractions

Replace opaque `object PreState` / `object PostState` semantics with internal typed concepts such as:

```text
RuntimeRollbackJournal
RuntimeForwardPatch
```

`PreparedImpactPlan` remains immutable publicly, but internally stores the forward patch required by `Commit(plan)`.

The forward patch must be scoped to actual mutations and affected graph structures.

## 62B — relation touched-state patching

`IRelationRuntimeState.CaptureState()` currently copies complete:

```text
forward hash index
right -> key map
reverse left index
left -> key map
rightsByLeft
leftsByRight
```

for every affected relation.

Add a scoped journal/patch API driven by the exact execution inputs:

```text
touched left sources
touched right sources
old/new join-key buckets
exact pair buckets affected
conservative candidate buckets affected
```

A one-right key move in a relation containing 100k registered objects must not clone the relation's full dictionaries.

Keep a full-state capture helper only if useful for tests/debugging; binding plan/normal rollback must use scoped state.

## 62C — navigation touched-root patching

Currently any lifecycle mutation or indexed-navigation change may call whole `_navigation.CaptureState()`.

Add root/index-scoped capture/restore for:

```text
added/removed root
reference replacement
collection/reset root
nested navigation root refreshed by impact resolution
```

Only touched roots and affected navigation index buckets should be journaled.

## 62D — dependency touched-node/source patching

Review `_dependencyGraph.CaptureState(affectedRelations, changes)` and make its contract explicit.

The journal should include only:

```text
derived nodes that can execute in this wave
source entries changed in their runtime states
invariant entries affected
_previousDerived / _previousInvariants membership deltas needed for restoration
conservative-source markers / direct causal evidence when causal mode is active
```

Do not copy every cached source of an affected node if only one source is mutated, unless the node runtime genuinely cannot restore incrementally. If a specific aggregate implementation requires broader state, document and benchmark it rather than hiding it behind the generic patch.

## 62E — plan commit installation

`PlanDetailed(...)` should:

1. validate;
2. create rollback journal;
3. execute semantic mutation once;
4. capture immutable result + forward patch;
5. restore rollback journal in `finally`;
6. return plan.

`Commit(plan)` should:

1. validate plan/runtime/prepared/version/domain state;
2. capture only inverse touched state needed in case patch installation itself throws;
3. install forward patch;
4. increment version;
5. mark prepared + plan committed;
6. rollback touched inverse journal if installation fails.

It must not invoke classifiers, predicates, relation routing, dependency propagation, invariant reaction planning, or causal reconstruction.

## 62F — diagnostics for patch scope

Add internal/test-visible diagnostics (not necessarily public API) that allow tests/benchmarks to assert quantities such as:

```text
object-set entries captured
relation entries/buckets captured
navigation roots captured
projection sources captured
derived/invariant source states captured
```

This is more useful than relying only on wall-clock timings.

## Tests

At minimum:

```text
one source property change in 100k population captures O(affected) state
one relation right-key move does not copy full relation indexes
one projected link retarget captures one downstream projection source
one navigation reference replacement captures one/root-local navigation patch
add/remove lifecycle rollback restores exact registration/key state
plan failure during execution restores all touched structures
patch-install failure restores all touched structures
Commit(plan) equals planned post-state and never reruns semantic delegates
```

Suggested commits:

```text
refactor: introduce runtime rollback journals and forward patches
refactor: journal touched relation state
refactor: journal touched navigation and dependency state
test: assert binding plan patch scope
```

---

# Task 63 — Full mixed-model semantic equivalence and failure atomicity

## Goal

Replace the current source-only parity proof with a graph that exercises the actual product.

## 63A — semantic result comparator

Create a test helper that compares complete `RuntimeApplyResult` semantics:

```text
DetailLevel
ChangeImpact access/semantic counts
relation IDs/DefinitionKeys
added/removed pair object identities
AffectedLefts object identities
derived/invariant DefinitionKeys
source object identity + SourceIdentity + DurableSourceIdentity
severity
repair request definition/source/reason
immediate evaluation definition/source
MutationOrigins kind/source/member/collection item/kind
causal cause type
OriginIds
precision
classified severity/policy
UpstreamImpactId edges
reaction input/output severity
```

Normalize only ordering that the public API intentionally does not promise; otherwise require deterministic order.

## 63B — mixed exact/conservative/projected graph

Build a deterministic randomized scenario containing all of:

```text
source-only derived value with value-sensitive severity
exact relation-backed derived value
conservative relation-backed derived value
incremental aggregate where legal
same-source derived chain
same-source diamond
one projected upstream
shared two-upstream projected composition
ScheduleRepair invariant
reference retarget
left/right lifecycle add/remove
collection change/reset
Dirty + Invalid fan-in
```

Use fixed seeds and print seed + step + mutation trace on failure.

For each accepted mutation batch:

```text
PreviewDetailed(Summary) -> runtime fingerprint unchanged
PreviewDetailed(Causal)  -> runtime fingerprint unchanged
PlanDetailed(Causal)     -> runtime fingerprint unchanged
Commit(plan)             -> result semantically equals plan.Result
fresh oracle/runtime path using direct CommitDetailed -> same semantic state/result where delegates deterministic
```

## 63C — runtime fingerprint

Add an internal/test fingerprint or assertions covering:

```text
Version
object-set registrations/keys
relation materialized pairs/index counts
navigation/projection diagnostics
all derived/invariant states for test graph sources
LastRelationImpacts
runtime diagnostics counters
prepared/plan committed/dispatched flags
```

Preview and Plan must leave it unchanged.

## 63D — failure injection matrix

Inject deterministic exceptions from:

```text
value-sensitive source classifier
relation predicate / residual predicate
projection selector getter
incremental aggregate update path
invariant immediate evaluation / reaction planning path
forward patch installation
```

For each supported point prove the relevant operations preserve atomicity:

```text
PreviewDetailed
PlanDetailed
direct CommitDetailed
Commit(plan)
```

After failure:

```text
Version unchanged
runtime fingerprint unchanged
prepared mutation still usable when contract permits retry
plan committed flag unchanged when install fails
no callback dispatched
```

## 63E — mutation order / batch normalization

For equivalent MutationSets with different input order, verify normalized preparation and final causal/result semantics are stable.

Suggested commits:

```text
test: compare complete runtime apply semantics
test: randomize mixed preview plan commit parity
test: inject runtime planning and patch failures
```

---

# Task 64 — Correct benchmarks and prove scaling

## Goal

Make benchmark names true and produce committed evidence for the first alpha.

## 64A — fix `PreparedImpactPlanningBenchmarks`

Current `Preview(...)` calls `PreviewDetailed(...)` **and then commits the prepared mutation inside the timed method**. Split scenarios so:

```text
PreviewSummary/PreviewCausal
    measure preview only

PlanSummary/PlanCausal
    measure planning only

CommitSummary/CommitCausal
    measure direct commit on fresh prepared mutations

CommitPlannedSummary/CommitPlannedCausal
    measure only forward-patch installation when practical

PlanAndCommit*
    optional explicit end-to-end measurement, named as such
```

Do not mutate one benchmark's setup in a way that contaminates another benchmark iteration.

## 64B — benchmark realistic graph shapes

Add scenarios for:

```text
source-only scalar
exact relation membership change
conservative right key move
projected retarget
projected upstream change with fan-out 1 / 10 / 100
lifecycle add/remove
```

Population sizes:

```text
10k
100k
```

Keep touched fan-out constant where measuring population independence.

## 64C — patch scope counters

Record both timing/allocation and touched-state diagnostics from Task 62. This lets the benchmark prove structural complexity, not merely one machine's timing.

## 64D — committed result documents

Add a reproducible document such as:

`benchmarks/PreparedPlanning-And-Patch-Results.md`

Include:

```text
commit SHA
BenchmarkDotNet version
runtime/OS/CPU
population/fan-out
mean
allocated bytes
patch/journal touched counts
```

Do not claim an SLA. State what the benchmark demonstrates structurally.

### Acceptance

There is no benchmark named Preview that includes a normal commit, and the repository contains a committed planning/patch result document.

Suggested commits:

```text
bench: separate preview plan and commit measurements
bench: add relation projection and lifecycle planning scenarios
docs: record prepared planning and patch benchmarks
```

---

# Task 65 — Complete the transactional outbox proof

## Goal

Turn the current useful two-test SQLite proof into a durability/recovery matrix that exercises the actual binding-plan contract.

## 65A — outbox payload must come from `plan.Result`

The successful test currently constructs payload with the domain entity ID directly.

Change the test model/helper so persisted outbox data is built exclusively from stable result data, for example:

```text
DefinitionKey
DurableSourceIdentity key parts
severity/reason
OriginId / origin durable identity where relevant
```

The outbox serializer/test helper must not need to reach back into a domain object to reconstruct identity.

## 65B — stable-key path

Prove:

```text
Capture
Prepare
PlanDetailed(Causal)
begin/use DB transaction
SaveChanges business
persist outbox from plan.Result
SaveChanges outbox
commit DB
Commit(plan)
Dispatch
```

Verify planned result and committed result are the same object/semantics as promised.

## 65C — multiple store-generated identities

Add at least two generated-key business rows starting with default key values.

Required sequence:

```text
Capture before save
begin transaction
first SaveChanges -> final IDs/fixup
Prepare
PlanDetailed(Causal)
persist one or more outbox rows built from final DurableSourceIdentity values
SaveChanges outbox
commit DB
Commit(plan)
Dispatch
```

Assert outbox contains final generated keys, never `0`/temporary values.

## 65D — failure/recovery matrix

Automate at least:

```text
first business SaveChanges fails -> DB rollback, runtime unchanged
post-save Prepare fails -> DB rollback, runtime unchanged
PlanDetailed fails -> DB rollback, runtime unchanged
outbox SaveChanges fails -> DB rollback, runtime unchanged
explicit DB rollback after successful plan -> runtime unchanged; stale plan rejected after domain/database restoration unless re-prepared
DB commit succeeds + Commit(plan) succeeds -> normal path
DB commit succeeds + forced Commit(plan) install failure -> DB/outbox durable, runtime unchanged/restored, recovery guidance points to rebuild/reconcile
Dispatch callback fails -> DB/runtime remain committed; dispatch can resume/retry according to existing contract
```

Do not retry the business DB command after the DB-success/runtime-failure case.

## 65E — synchronization failure metadata

Ensure the recovery exception/guidance contains enough context to operationally diagnose:

```text
runtime version before failed commit
DB operation succeeded flag
inner failure
recommendation to rebuild/reconcile runtime from authoritative DB/outbox state
```

No production outbox abstraction is required; keep the outbox schema test-only.

Suggested commits:

```text
test: persist durable impact identities from binding plans
test: cover generated-key transactional outbox
test: cover binding-plan outbox failure and recovery matrix
docs: finalize durable binding-plan transaction sequence
```

---

# Task 66 — Freeze the first-alpha contract and execute the RC gate

## Goal

After Tasks 61–65, stop changing semantics and prove the package/release surface without publishing.

## 66A — public API wording review

Review unshipped APIs, especially:

```text
PreparedImpactPlan
PlanDetailed
Commit(PreparedImpactPlan)
PreviewDetailed
RelationUnitOfWork.PlanDetailed
RuntimeApplyResult
SourceDependencyImpact.ImpactId
UpstreamDerivedCause.UpstreamImpactId
```

Fix XML wording:

- `RuntimeApplyResult` is stable result data for preview, planning, or committed execution—not only committed mutation data;
- `PreparedImpactPlan` stores a scoped forward patch, not a vague snapshot;
- `PreviewDetailed` is explicitly non-binding;
- `PlanDetailed` is binding and intended when durable external work depends on parity.

Do not rename APIs casually unless the final surface review shows a clear ambiguity.

## 66B — package consumers

Keep CoreNet8/CoreNet10/EF packed-package consumers on the binding-plan surface.

Strengthen the EF consumer enough to prove the documented ordering rather than merely compiling:

```text
Capture / Prepare
SaveChanges as appropriate for its key strategy
PlanDetailed
Commit via the unit/plan
Dispatch
```

## 66C — run manual release-candidate workflow

The repository currently has zero `workflow_dispatch` runs. Trigger `.github/workflows/release-candidate.yml` if the agent/tooling has permission.

Verify the run actually executes and passes:

```text
Release build
core net8 tests
core net10 tests
EF net10 tests
format verification
pack
README/CHANGELOG package payload checks
packed CoreNet8/CoreNet10/EfNet10 runs
nupkg + snupkg artifact upload
```

If the agent cannot dispatch workflows, record that explicitly in the roadmap status and do **not** claim the RC workflow has been proven.

## 66D — final docs truth pass

README, architecture, CHANGELOG, XML docs, and roadmap must agree on:

```text
runtime not thread-safe
Dirty vs Invalid
Exact vs Conservative
projected reference restrictions
bootstrap/recovery
Capture/Prepare timing for stable vs store-generated keys
Preview is non-binding
Plan is binding
DB durability happens before runtime Commit(plan)
Dispatch is post-runtime-commit and resumable
DB-success/runtime-failure requires rebuild/reconcile
known alpha limitations
```

## 66E — final gate

Before marking this roadmap complete:

```text
latest main CI green
manual RC workflow green (or explicitly unexecuted if tooling truly cannot dispatch)
full mixed-model parity/failure suite green
prepared planning/patch benchmark results committed
SQLite outbox generated-key + failure matrix green
packed consumers green
PublicAPI.Unshipped files intentional
no publish secrets/workflows introduced
no release/tag created
```

Suggested commits:

```text
docs: clarify result and binding plan contracts
build: strengthen binding plan package consumers
docs: record release candidate verification
docs: close first alpha proof roadmap
```

---

# Recommended commit sequence

Keep this wave bisectable. A reasonable sequence is:

```text
1  test: expose relation causal routing attribution gaps
2  refactor: capture relation causal evidence during routing
3  refactor: capture upstream and reaction causal edges during propagation
4  refactor: remove causal inference from result construction
5  refactor: introduce runtime rollback journal and forward patch
6  refactor: journal touched relation state
7  refactor: journal touched navigation and dependency state
8  test: assert scoped plan patch state
9  test: compare complete runtime apply semantics
10 test: randomize mixed preview plan commit parity
11 test: inject planning and patch installation failures
12 bench: separate preview plan and commit measurements
13 bench: add relation projection lifecycle scaling cases
14 docs: record prepared planning and patch benchmark results
15 test: persist outbox from durable planned identities
16 test: cover generated-key outbox transaction
17 test: cover outbox failure and recovery matrix
18 docs: finalize transactional binding-plan workflow
19 docs: clarify first-alpha public contracts
20 build: verify packed binding-plan consumers
21 docs: record release-candidate verification and close roadmap
```

Do not squash the causal, patching, test, benchmark, and release-gate work into one commit.

---

# Definition of done

This roadmap is complete only when the repository can prove this sequence end to end:

```text
ordinary domain mutation
        ↓
Capture / Prepare at correct persistence boundary
        ↓
PlanDetailed exactly once
        ↓
causal result backed entirely by decision-time evidence
        ↓
persist business + durable impact/outbox rows atomically
        ↓
DB transaction durable
        ↓
Commit(plan) by installing a scoped forward patch
        ↓
Dispatch resumably
```

and can prove structurally and with tests that:

```text
no public cause is guessed after propagation
no conservative path becomes Exact
summary/basic mode pays no causal graph cost
small mutations do not clone full object sets, relation indexes, navigation registry, or projection registry
Preview/Plan are non-mutating and exception-atomic
Commit(plan) installs exactly the planned state without semantic delegate re-execution
mixed exact/conservative/projected graphs preserve complete result parity
benchmark names measure what they claim
outbox payload can be produced from stable plan result identities alone
generated-key outbox transactions are proven
recovery after DB-success/runtime-failure is explicit
packed packages exercise the binding-plan surface
manual RC verification has actually run before alpha-ready is claimed
```