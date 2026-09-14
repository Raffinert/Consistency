# Codex Implementation Plan — Binding Impact Plans, Causal Proof, and EF Transactional Hardening

Baseline: `main` at `574cedd3267db73ffef9034e9bbbfbc01d9ba18d`.

Baseline CI: run #81 completed successfully. Build, core tests on .NET 8/.NET 10, EF Core tests, formatting, packing, and packed-package consumers are green.

The Tasks 41–47 wave is substantially implemented: prepared impact preview exists, preview uses the same mutation execution core as commit, projection ownership indexes are structurally shared, projected final-state validation no longer clones every object set, an explicit EF transaction pattern is documented, release-candidate workflow scaffolding exists, and main is green.

This verification also found several acceptance gaps that are important enough to close before expanding the feature set or publishing the first alpha:

- causal relation/upstream evidence is still reconstructed in `MutationCommit.CreateCauses(...)` instead of being captured at propagation decisions;
- projected upstream precision still calls `IsConservative(...)` with the downstream source rather than the actual projected upstream source;
- conservative relation-origin inference still uses member names/type heuristics and can over-attribute same-member mutations from unrelated right-side objects;
- `PreviewDetailed(...)` executes user-defined mutation semantics and then normal `Commit(...)` executes them again, so a stateful/non-deterministic source-member classifier can make an outbox preview disagree with the later runtime commit;
- EF store-generated identity works only for the tested one-row case: `Prepare` validates lifecycle keys immediately, so two new identity rows at key `0` collide before the first `SaveChanges`;
- EF reference-navigation changes currently report `oldValue = null`, which is not a truthful old value for value-sensitive policies;
- rollback/preview snapshots still copy whole touched object sets/navigation/projection registries for some lifecycle/reference changes;
- current randomized preview/commit parity covers a simple source-only derived value, not the full graph; the parity helper compares mostly counts/severity rather than the complete semantic result;
- `PreparedImpactPlanningBenchmarks.Preview*` currently measures preview **plus a commit**, so it is not a clean preview-cost measurement;
- the transactional-outbox SQLite test does not actually persist an outbox row in the same transaction;
- `RuntimeApplyResult` XML documentation still says it is produced by a committed mutation even though preview returns it;
- the manual release-candidate workflow exists but has never been run (`workflow_dispatch` run count is zero).

Implement this wave in order:

```text
48  Capture a real source-scoped causal evidence graph
49  Add a binding prepared impact plan so durable preview and runtime commit cannot diverge
50  Harden EF capture/prepare semantics for store-generated keys and reference changes
51  Replace broad rollback snapshots with touched-state journals/patches
52  Prove full preview/plan/commit equivalence with mixed-model randomized and failure tests
53  Prove the real same-database transactional-outbox workflow end to end
54  Close the alpha release-candidate contract without publishing
```

Do **not** add pre-mutation hypothetical simulation, distributed execution, nullable/key-based/nested projected selectors, generic arity 3..8 `.Using(...)` families, runtime auto-tuning, new relation access-plan families, or derived-valued relation predicates in this roadmap.

---

# Global implementation rules

1. Correctness and deterministic consistency semantics outrank explanation richness and performance.
2. Original relation/computation/invariant expressions remain semantic authority.
3. `Invalid` dominates `Dirty`; inherited severity may never weaken.
4. Conservative precision is monotonic downstream; once Conservative, never upgrade to Exact.
5. Causal output may claim only evidence actually observed at the routing/propagation decision that produced the impact.
6. A durable impact/outbox plan must not depend on re-running user classifiers/predicates after database durability.
7. Callers must not need manual `EnsureFresh`, `InvalidateLinks`, or similar correctness orchestration.
8. Basic `Apply` stays the cheapest path. Summary mode must not allocate causal graphs.
9. Preview/plan never dispatches application callbacks.
10. Core stays free of EF Core, persistence, DI, logging, and transport dependencies.
11. Preserve Prepare/Commit version binding, domain-drift checks, exception atomicity, durable identities, and resumable dispatch.
12. Projected dependencies remain one direct, non-null reference into the exact upstream `ObjectSet`.
13. Public API changes require deliberate `PublicAPI.Unshipped.txt` updates.
14. Core semantic changes must pass net8.0 + net10.0; EF adapter changes run on net10.0.
15. Keep commits small/buildable/bisectable. Do not land Tasks 48–54 in one commit.
16. Do not publish NuGet packages, create a GitHub release, or push a release tag without a later explicit instruction.

---

# Task 48 — Capture a real source-scoped causal evidence graph

## Goal

Complete the acceptance gate that Tasks 41–47 only partially addressed. Public causal output must be a projection of evidence captured during routing/propagation, not an inference pass over final snapshots.

## 48A — internal evidence nodes

Introduce internal current-wave-only concepts such as:

```text
PropagationImpactKey
  Definition
  Source

PropagationEvidenceNode
  Id
  Key
  FinalSeverity
  Precision
  DirectCauses[]
  UpstreamImpactIds[]
```

Names are flexible. Required property: `(definition, source reference)` is the identity of a causal impact node.

Do not retain this graph in the runtime after the result is built. Build it only when `RuntimeImpactDetailLevel.Causal` is requested.

## 48B — direct source-member evidence

Keep classification at the existing decision point. Capture:

```text
OriginId
MemberInfo / stable member name
local classified severity
policy kind
Exact precision
```

Never re-run `SourceMemberChanged(... classify)` while constructing the result.

## 48C — relation routing evidence

Move relation cause creation to the point where a relation impact selects a derived source.

Record:

```text
Relation definition
MembershipAdded / MembershipRemoved / RelatedItemChanged / ConservativeCandidate
Exact / Conservative
only the normalized OriginIds that actually routed this source
```

Do not use a later heuristic based only on `MemberInfo.Name` and right-side CLR type.

For exact relation deltas, associate origins with the actual changed source/item or lifecycle pair.
For conservative old/new-key routing, carry the origin through the candidate-routing structure itself.
If exact provenance is not knowable, emit no OriginId rather than a broad guessed set.

After this task, `ResolveRelationOriginIds(...)` should disappear or become a trivial projection of captured evidence; it must not search the whole origin list heuristically.

## 48D — upstream causal references

When a derived impact propagates to a downstream derived/invariant node, attach the actual upstream **source-scoped evidence-node ID**.

Projected case:

```text
LinkValidity(link-42)
   -> upstream reference AvailableQuantity(po-7)
```

The downstream source `link-42` and upstream source `po-7` are different objects. Never pass the downstream source into upstream precision analysis.

Remove post-hoc recursive `IsConservative(...)` inference from result construction. Precision follows the incoming impact edge:

```text
Exact + Exact -> Exact
Exact + Conservative -> Conservative
Conservative anywhere upstream -> Conservative
```

Consider extending `UpstreamDerivedCause` with `UpstreamImpactId` (or equivalent) so two sources of the same derived definition are unambiguous.

## 48E — invariant reaction evidence

Capture escalation exactly when reaction semantics modify the impact:

```text
Reaction
InputSeverity
OutputSeverity
```

No evidence node for a no-op escalation. Already-Invalid input must never render as `Dirty -> Invalid`.

## 48F — graph renderer

Make `RuntimeImpactTraceRenderer` traverse real upstream evidence references, with deterministic ordering and visited-node handling for diamonds.

Conceptual output:

```text
link-validity(link-42) -> Invalid
  because available-quantity(po-7) -> Invalid
    because ordered-quantity changed -> Invalid
  because unit-rate(po-7) -> Invalid
    because price-rate changed -> Invalid
```

## Tests

At minimum:

```text
relevant + unrelated same batch -> only relevant relation OriginIds
multiple right-side objects mutate same member -> each source cause has only routed origins
exact membership add/remove lifecycle provenance
conservative old/new-key candidate provenance
projected upstream uses upstream source identity, not downstream source
projected conservative path remains Conservative
exact + conservative diamond -> Conservative downstream
same derived definition with two different sources -> distinct upstream impact IDs
invariant inherited Invalid -> no fake escalation
ScheduleRepair Dirty -> Invalid -> one explicit escalation
causal diamond deterministic under mutation-order permutations
Summary/basic mode allocates no evidence graph
```

### Acceptance gate

`CreateCauses(...)` no longer infers relation/upstream causes or precision from `RelationImpacts`/`DerivedImpacts`. Public causal records are direct projections of captured evidence nodes.

Suggested commits:

```text
test: expose source-scoped causal provenance gaps
refactor: capture propagation evidence at routing decisions
feat: render source-scoped causal impact graph
```

---

# Task 49 — Add a binding prepared impact plan

## Problem

Current `PreviewDetailed(prepared)` executes the full semantic mutation, restores runtime state, and later `Commit(prepared)` executes the semantics again.

That means this valid public policy:

```csharp
.SourceMemberChanged(x => x.Quantity, (oldValue, newValue) => ...)
```

is a normal `Func<...>` and can technically be stateful/non-deterministic. Relation predicates/computations permitted through incomplete-dependency opt-outs can also be non-pure. A durable outbox row generated from preview must not be allowed to disagree with the later runtime commit merely because semantic code was executed twice.

## Goal

Keep `PreviewDetailed` as a useful non-binding diagnostic operation, but add an explicit **binding plan** for durability-sensitive workflows.

Recommended public shape (names may be refined during API review):

```csharp
PreparedImpactPlan plan = runtime.PlanDetailed(
    prepared,
    RuntimeImpactDetailLevel.Causal);

RuntimeApplyResult result = plan.Result;

// after external database durability
runtime.Commit(plan);
runtime.Dispatch(prepared);
```

The crucial distinction:

```text
PreviewDetailed = inspect, later Commit may execute semantics again
PlanDetailed    = execute once reversibly, capture a forward state patch, later Commit(plan) installs it
```

## 49A — plan identity/binding

A plan is bound to:

```text
RelationRuntime instance
PreparedMutation instance
BaseVersion
captured domain assumptions
RuntimeImpactDetailLevel
```

It can be committed once.

Do not expose mutable runtime internals from the plan.

## 49B — capture a scoped forward patch

During planning:

1. validate prepared/runtime/domain/final projection state;
2. capture scoped pre-state;
3. execute the same semantic mutation core once;
4. build `RuntimeApplyResult` while simulated post-state is active;
5. capture a **scoped post-state patch** for every structure actually mutated;
6. restore pre-state in `finally`;
7. return the immutable plan/result.

The post-state patch may contain internal snapshots/deltas for:

```text
touched object-set entries
affected relation/index state
navigation/projection entries
derived/invariant states
diagnostics deltas / LastRelationImpacts
RuntimePolicyActions
```

Do not keep a whole-runtime clone merely to make plan commit easy.

## 49C — commit the plan without re-running semantic code

`Commit(PreparedImpactPlan plan)` must:

```text
verify plan belongs to runtime/prepared mutation
verify prepared uncommitted
verify runtime Version == BaseVersion
revalidate domain assumptions/final structural constraints
capture current touched pre-state for exception rollback
install planned post-state patch
increment Version
mark PreparedMutation committed with the planned policy actions
```

Do **not** re-run:

```text
conditional impact classifiers
relation predicates
relation candidate resolution
derived impact propagation
invariant impact/reaction planning
causal evidence construction
```

If the state/domain no longer matches, reject as stale/drifted and require a new plan.

## 49D — parity semantics

For a binding plan:

```text
plan.Result == semantic result installed by Commit(plan)
```

by construction, not by hoping two executions are deterministic.

Normal `Commit(prepared)` remains supported and executes directly when no binding plan is used.

## 49E — RelationUnitOfWork integration

Expose an EF-side method such as:

```csharp
PreparedImpactPlan? PlanDetailed(
    RelationRuntime runtime,
    RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary);
```

and commit the same plan after DB transaction durability.

Do not hide the database transaction boundary in a one-line helper.

## 49F — public API/docs

Document purity expectations for ordinary `PreviewDetailed` and recommend `PlanDetailed + Commit(plan)` whenever durable external work depends on exact parity.

### Tests

Include a deliberately stateful classifier whose return value flips on each invocation:

```text
PlanDetailed -> classifier called once
DB-like delay/no runtime mutation
Commit(plan) -> classifier not called again
installed severity exactly equals plan.Result
normal PreviewDetailed + normal Commit can execute twice (documented/non-binding)
```

Also test stale plan, domain drift, double commit, plan from another runtime, plan after unrelated runtime version advance, failed patch install rollback, Summary/Causal plan result.

### Acceptance gate

The recommended transactional-outbox workflow never needs to execute semantic classification/routing twice.

Suggested commits:

```text
feat: add binding prepared impact plans
refactor: apply planned runtime state without semantic reexecution
test: prove binding plan executes classifiers once
```

---

# Task 50 — Harden EF mutation fidelity and store-generated-key sequencing

## 50A — distinguish capture from prepare timing

`ChangeTrackerAdapter.CaptureUnitOfWork(...)` must still happen **before** the first `SaveChanges`, while original property/reference tracking data exists.

But preparation timing depends on key strategy.

### Stable application-assigned keys

Keep the fast/fail-early pattern:

```text
Capture
Prepare/Plan
SaveChanges inside transaction
persist outbox
DB commit
Commit(plan)
Dispatch
```

### Store-generated keys

For identity/sequence-generated object-set keys, the robust pattern is:

```text
Capture unit before SaveChanges
begin DB transaction
first SaveChanges -> generated keys/fixup now available, still not externally durable
Prepare
PlanDetailed
persist outbox
second SaveChanges
DB transaction commit
Commit(plan)
Dispatch
```

If Prepare/Plan fails, roll back the DB transaction; runtime never advanced.

Do not require callers to invent temporary unique IDs solely for Raffinert.

## 50B — multiple generated additions

Add a SQLite test with at least two new identity entities starting with the same default key (`0`).

Expected contract:

```text
Capture before save succeeds
pre-save Prepare may be documented unsupported for store-generated keys
first in-transaction SaveChanges assigns distinct keys
post-save Prepare/Plan succeeds
source identities/outbox data use final generated keys
Commit(plan) registers both exact final keys
```

Make the stable-key/store-generated-key distinction explicit in README/architecture/XML docs.

## 50C — registered key mutation remains forbidden

Do not weaken key immutability for objects already registered in runtime. Only unregistered `ObjectAdded` instances may receive persistence-generated keys before they are prepared/planned/committed.

## 50D — reference-navigation old-value fidelity

Current EF capture emits reference changes with `oldValue = null`. This is not a truthful old value and can produce wrong results if a value-sensitive source-member classifier is configured on a reference member.

Choose and implement one explicit contract:

**Preferred:** recover the previous tracked reference using EF relationship/FK original-value metadata when it is unambiguous, and emit the real old/new object references.

If EF cannot reliably materialize the old reference for a mapping, represent old-value-unavailable explicitly; never silently pass `null` as if null were the actual previous reference. A conditional classifier that requires an unavailable old value must fail explicitly or fall back only under an explicitly documented policy.

Projection/reference retarget routing may continue to use Raffinert's structural reverse index for membership correctness, but causal/value-classification data must remain truthful.

## 50E — relationship/fixup tests

Cover:

```text
reference retarget old principal -> new principal
store-generated principal + dependent added in one transaction
post-save relationship fixup before PlanDetailed
multiple object sets of same CLR type
reference conditional severity sees truthful old/new when supported
unknown old reference never masquerades as null
```

### Acceptance gate

The documented EF transactional workflow works for ordinary multiple identity inserts and does not fabricate reference old values.

Suggested commits:

```text
test: expose multiple generated-key prepare collision
fix: support post-save planning for generated keys
fix: preserve EF reference old-value semantics
docs: distinguish stable and store-generated key workflows
```

---

# Task 51 — Replace broad rollback snapshots with touched-state journals

## Problem

Task 42 removed whole-runtime cloning from projection **validation**, but execution rollback/preview still does broad copies in several paths:

```text
lifecycle mutation -> full touched ObjectSetRuntime snapshot
indexed navigation change -> whole NavigationIndexRegistry snapshot
projection selector/lifecycle change -> whole ProjectionIndexRegistry snapshot
```

A one-link retarget in a 100k-object runtime should not copy unrelated entries just because preview/rollback is enabled.

## 51A — scoped object-set lifecycle journal

Prefer inverse operations or entry-level snapshots for additions/removals:

```text
added instance + final key
removed instance + registered key
```

Rollback should restore only those entries.

Do not clone every object/key dictionary entry in the set for one add/remove.

## 51B — scoped projection snapshots

`ProjectionIndexRegistry` should capture/restore only physical edges and downstream/target buckets touched by:

```text
downstream add/remove
selector retarget
upstream removal validation/commit
```

Reuse the canonical physical edge introduced in Task 42.

## 51C — scoped navigation snapshot/journal

Apply the same principle to `NavigationIndexRegistry` where practical. At minimum, one reference/collection-root change should not copy the entire navigation registry if only one root/index is affected.

## 51D — relation/dependency snapshots

Review relation/dependency snapshot scope used by Preview/Plan. Do not optimize blindly, but add diagnostics/tests proving the captured state is proportional to affected relations/nodes/sources rather than all runtime definitions.

## 51E — benchmark lifecycle + plan cost

Add clean benchmarks for 10k/100k populations:

```text
Prepare one link retarget
Preview one link retarget
PlanDetailed one link retarget
Commit(plan) one link retarget
add one downstream object
remove one downstream object
remove one upstream target with fan-out 1/10/100
```

At equal touched fan-out, costs/allocations should not scale linearly with the full population.

### Acceptance gate

Preview/plan exception atomicity remains intact while lifecycle/reference changes stop copying unrelated object-set/projection/navigation state.

Suggested commits:

```text
refactor: journal object-set lifecycle rollback
refactor: scope projection and navigation rollback state
bench: measure planned lifecycle mutation cost
```

---

# Task 52 — Full semantic equivalence, failure injection, and benchmark correction

## 52A — replace shallow parity assertions

Current parity helpers compare mainly counts and severities. Add semantic comparison utilities that compare:

```text
ChangeImpact
relation definition IDs/keys
added/removed relation pair object identities
AffectedLefts
source-scoped derived/invariant identities
severity
SourceIdentity / DurableSourceIdentity
repair request definition/source/reason
immediate evaluation requests
MutationOrigins including collection kind/item
causal node/cause kind
OriginIds
precision
upstream impact references
reaction escalation
```

Ignore only incidental collection-instance identity/order where the API does not promise it; otherwise enforce deterministic ordering.

## 52B — mixed-model randomized oracle

Use fixed seeds over a graph containing all of:

```text
exact relation-backed derived value
conservative relation-backed derived value
source-only derived value
same-source DAG chain + diamond
one/two-upstream projection
Dirty + Invalid fan-in
ScheduleRepair invariant
lifecycle add/remove
projection retarget
collection change
```

For each accepted batch:

```text
PreviewDetailed Summary
PreviewDetailed Causal
PlanDetailed Causal
runtime-state fingerprint unchanged after each non-commit operation
Commit(plan)
compare complete semantic result
```

Print seed/step on failure.

## 52C — failure injection

Inject deterministic failures from:

```text
conditional source classifier
relation predicate/candidate path where supported
projection selector getter
invariant reaction planning
planned-state installation
```

Prove Preview, PlanDetailed, direct Commit, and Commit(plan) all preserve exception atomicity and leave version/diagnostics/index/cache state coherent.

## 52D — fix prepared planning benchmarks

`PreparedImpactPlanningBenchmarks.PreviewSummary/PreviewCausal` currently perform a preview and then a commit in the same benchmark method. Split measurements so labels match reality.

Recommended setup:

```text
Preview*       -> repeatedly preview one unchanged prepared mutation (non-mutating)
Plan*          -> create/finalize a fresh plan per iteration as needed
Commit*        -> fresh prepared mutation per iteration
CommitPlanned* -> fresh binding plan per iteration
```

Add relation membership and projected fan-out 1/10/100 cases, not only source-only scalar changes.

## 52E — record results

Commit reproducible result docs for:

```text
preview vs commit vs planned commit
projection lifecycle validation
population 10k vs 100k
fan-out 1/10/100
allocations + mean
```

No SLA claims; document machine/runtime/BenchmarkDotNet mode.

### Acceptance gate

The full consistency graph—not just a source-only case—proves Preview/Plan/Commit semantic parity and failure restoration, and benchmark names measure what they say.

Suggested commits:

```text
test: compare complete prepared impact semantics
test: randomize mixed preview plan commit parity
test: inject planned execution failures
bench: correct and expand prepared impact planning measurements
```

---

# Task 53 — Prove the transactional outbox end to end

## Goal

Replace the illustrative SQLite test with a real same-database transactional-outbox proof.

## 53A — real schema

Add a test-only relational model containing:

```text
business entity/entities
outbox row table
stable DefinitionKey + DurableSourceIdentity fields
reason/severity/payload fields needed to prove persistence
```

Do not add an outbox abstraction to the production core package.

## 53B — normal stable-key path

Test:

```text
Capture
Prepare + PlanDetailed(Causal)
begin/continue transaction
SaveChanges business data
write outbox from plan.Result
SaveChanges outbox
commit DB transaction
Commit(plan)
Dispatch
```

Verify business and outbox rows are durable and runtime result equals planned result.

## 53C — store-generated-key path

Test the post-first-save sequence from Task 50 with **multiple** identity rows and an outbox row that contains their final durable source identities.

## 53D — failure matrix

Using SQLite transactions, cover:

```text
first SaveChanges fails -> rollback, runtime unchanged
post-save Prepare/Plan fails -> rollback, runtime unchanged
outbox SaveChanges fails -> rollback, runtime unchanged
DB transaction rollback after successful plan -> runtime unchanged, plan cannot be committed after domain rollback without revalidation
DB commit succeeds -> Commit(plan) succeeds
DB commit succeeds but Commit(plan) is forced to fail -> outbox remains durable; rebuild/reconcile path documented
Dispatch callback fails -> DB/runtime already committed; dispatch remains resumable
```

For the synchronization-failure path, ensure `RelationRuntimeSynchronizationException` or equivalent application guidance reports the pre-failure runtime version and never suggests retrying the database command.

## 53E — docs/sample

Update the transactional-outbox docs to recommend **binding PlanDetailed** rather than non-binding PreviewDetailed for durable integration.

Keep `PreviewDetailed` documented as inspection/debugging/planning where a later direct commit may legitimately re-execute semantics.

### Acceptance gate

There is an automated relational test proving business rows and impact/outbox rows commit atomically while runtime state advances only after DB durability, including generated-key entities.

Suggested commits:

```text
test: persist planned impact in sqlite transactional outbox
test: cover transactional outbox failure matrix
docs: use binding impact plans for durable integration
```

---

# Task 54 — Close the first-alpha release-candidate contract

## 54A — public API/documentation review

Review the unshipped surface after Tasks 48–53, especially:

```text
PreparedImpactPlan / PlanDetailed naming
Commit(plan) overloads
RelationUnitOfWork plan APIs
RuntimeApplyResult wording
causal impact/node IDs and upstream references
projection diagnostics
```

Fix the stale XML summary that currently says `RuntimeApplyResult` is produced by a committed mutation. It is result data that may represent preview/planning or committed execution.

## 54B — roadmap status

Mark Tasks 41–47 as an implemented wave with acceptance gaps superseded by Tasks 48–54. Mark this roadmap complete only after all gates below pass.

## 54C — release candidate workflow verification

The manual release-candidate workflow exists but has never been run. Before declaring alpha-ready, run it once if GitHub permissions/tooling allow and verify:

```text
Release build
net8 core tests
net10 core tests
net10 EF tests
format
pack
package payload validation
packed CoreNet8/CoreNet10/EfNet10 consumers
nupkg + snupkg artifacts
```

If the agent cannot dispatch GitHub Actions, keep the workflow manual and explicitly record that it remains unexecuted; do not claim the RC workflow itself is proven.

## 54D — package/release guardrails

Keep repository-level version source. Do not publish, tag, or create a GitHub release.

README/architecture/CHANGELOG should clearly state:

```text
runtime not thread-safe
Prepare/Plan/DB durability/Commit/Dispatch sequence
PreviewDetailed is non-binding inspection
PlanDetailed is binding durable planning
store-generated-key prepare timing
Dirty vs Invalid
Exact vs Conservative
projection direct/non-null/exact-set restriction
bootstrap/recovery semantics
known alpha limitations
```

## 54E — final gates

Before closing the roadmap:

```text
latest main CI green
full mixed-model parity/failure tests green
prepared planning benchmark results committed
projected lifecycle benchmark results committed
transactional outbox SQLite tests green
packed consumers use the new plan surface
PublicAPI approval files intentional
no NuGet publish workflow/secrets added
no release/tag created
```

Suggested commits:

```text
docs: clarify preview and binding plan contracts
build: smoke test binding plan package consumers
docs: close binding impact plan hardening roadmap
```

---

# Recommended commit sequence

Keep the wave bisectable:

```text
1  test: expose source-scoped causal provenance gaps
2  refactor: capture propagation evidence at routing decisions
3  feat: render source-scoped causal impact graph
4  feat: add binding prepared impact plans
5  refactor: apply planned runtime state without semantic reexecution
6  test: prove binding plan executes classifiers once
7  test: expose multiple generated-key prepare collision
8  fix: support post-save planning for generated keys
9  fix: preserve EF reference old-value semantics
10 refactor: journal object-set lifecycle rollback
11 refactor: scope projection and navigation rollback state
12 test: compare complete prepared impact semantics
13 test: randomize mixed preview plan commit parity
14 test: inject planned execution failures
15 bench: correct and expand prepared impact planning measurements
16 test: persist planned impact in sqlite transactional outbox
17 test: cover transactional outbox failure matrix
18 docs: use binding impact plans for durable integration
19 build: smoke test binding plan package consumers
20 docs: close binding impact plan hardening roadmap
```

Do not squash these into a giant feature commit unless forced by repository tooling.

---

# Definition of done

This roadmap is complete when Raffinert.Relations can truthfully support the following workflow:

```text
ordinary domain mutation
        ↓
Capture/Prepare at the correct persistence boundary
        ↓
PlanDetailed once
        ↓
causal result backed by decision-time evidence
        ↓
persist business + outbox atomically
        ↓
DB durable
        ↓
Commit(the exact plan) without semantic reexecution
        ↓
Dispatch resumably
```

and the repository proves, rather than merely assumes, that:

```text
causes have exact source-scoped provenance
Conservative precision cannot be lost
multiple generated EF identities work
preview/planning rollback is scoped and exception-atomic
mixed exact/conservative/projected graphs preserve parity
benchmark labels are honest
transactional outbox is actually atomic
release-candidate checks are green
```
