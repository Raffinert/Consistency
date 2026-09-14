# Codex Implementation Plan — Pre-Commit Impact Planning, Causal Integrity, and Transactional Integration

Baseline: `main` at `ff05a63d9cf96e8194ce4f5fac38cf796b66054f`.

Baseline CI: run #73 completed successfully. Build, core tests on .NET 8/.NET 10, EF Core tests, formatting, packing, and packed-package consumers are green.

Tasks 34–40 are complete. The projected-dependency alpha contract is now intentionally narrow and structurally sound: a projected selector is one direct non-null reference member, the target must belong to the exact upstream object set, reverse projection indexes route by actual fan-out, two projected upstream values are supported, and the procurement sample no longer opts out of dependency completeness.

This verification found that the next work should move up one architectural layer. The highest-value missing capability is **non-mutating impact planning for an already prepared domain mutation**. A caller should be able to ask what a `PreparedMutation` will do, persist durable repair/outbox work inside the same database transaction, then commit runtime state only after database durability. Before exposing that capability, causal evidence must become fully truthful and structural validation must stop copying whole runtime state for small lifecycle changes.

Implement this wave in order:

```text
41  Finish causal evidence at propagation decision points
42  Canonicalize projection edges and make final-state validation incremental
43  Extract a reversible mutation-execution core suitable for preview and commit
44  Add non-mutating PreparedMutation impact preview with commit parity
45  Add EF transactional preview/outbox integration and document the durability boundary
46  Add randomized equivalence, failure injection, and performance gates for planning/validation
47  Stabilize the release-candidate contract and release engineering without publishing
```

Do **not** implement pre-mutation hypothetical simulation in this roadmap. `PreviewDetailed`/impact planning starts from domain objects that have already been mutated and from an already validated `PreparedMutation`. Also do not add distributed runtime execution, runtime auto-tuning, new relation access-plan families, nullable/key-based projections, nested projected selectors, generic arity 3..8 `.Using(...)` families, or derived-valued relation predicates.

---

# Verified current state

Already implemented; do not recreate:

```text
expression-defined binary relations; original predicate remains semantic authority
scan/hash query access plans
exact/conservative propagation separated from query access
selective conservative old/new-key routing
source-only, relation-backed, and same-source composed derived values
one/two-upstream projected composition through a direct reference
compiled dependency DAG, cycle rejection, topological source-scoped propagation
Dirty / Invalid monotonic severity
value-sensitive direct-source member severity
single/two-input invariants and policy dispatch
incremental Count/LongCount/Any/direct numeric Sum
Prepare -> Commit -> Dispatch
CommitDetailed(prepared, detailLevel)
EF RelationUnitOfWork.CommitDetailed(...)
exception-atomic runtime rollback
summary and causal result data
normalized mutation provenance
durable policy-request identity
runtime bootstrap with Version == 0
projected-target exact-set integrity
reverse projection fan-out indexes
packed NuGet consumers for net8/net10/EF
procurement PO / GR / persisted-link sample
```

Current projected-selector alpha contract:

```csharp
model.Derived(links)
    .Using(link => link.PurchaseOrderLine, availableQuantity, unitRate)
    .Compute((link, available, rate) =>
        link.ReservedQuantity <= available &&
        link.CapturedRate == rate);
```

The selector must be one direct tracked reference member. Nested selectors and opaque selectors are rejected rather than pretending freshness can be maintained.

---

# Verification findings that drive this roadmap

## A. Causal output is still partly reconstructed after propagation

Direct source-member classification is now captured only in causal mode, and fake `Dirty -> Invalid` invariant escalation was fixed. However `MutationCommit.CreateCauses(...)` still reconstructs relation/upstream causes and precision after the mutation has executed.

Two concrete correctness problems remain.

### A1 — relation causes currently claim unrelated origins

Current construction assigns:

```csharp
OriginIds = origins.Select(origin => origin.OriginId).ToArray();
```

to every relation cause. In a batch containing unrelated mutations, a relation explanation can therefore claim that every mutation caused that relation impact.

The correct contract is:

```text
one cause -> only the normalized mutation origin(s) that actually routed that cause
unknown/not-provable -> no origin ID, never an invented broad set
```

### A2 — projected upstream precision is still inferred with the wrong abstraction

`UpstreamDerivedCause` precision is determined later by recursively calling `IsConservative(...)`. For a projected edge, the downstream source and upstream source are different objects. Precision must travel with the actual upstream `(definition, source)` impact through the DAG; it must not be re-derived by walking definitions later.

The public trace renderer is consequently still a flat list of inferred causes, not a traversal of the propagation evidence that actually produced the final source impact.

## B. Two projected consumers can duplicate the same physical ownership index

`ProjectionIndexRegistry` currently creates one `Entry` for every `ProjectedUpstreamDerivedInput`.

For:

```csharp
.Using(link => link.PurchaseOrderLine, availableQuantity, unitRate)
```

there are two semantic upstream inputs but one structural reference edge:

```text
PurchaseOrderInvoiceLink.PurchaseOrderLine
    -> po-lines ObjectSet
```

The runtime currently maintains two copies of `DownstreamToTarget` and `TargetToDownstreams` for that same edge. This is correct but wastes memory, bootstrap work, retarget work, rollback snapshot space, and diagnostics.

The physical projection index should be keyed by structural ownership, while multiple upstream-derived consumers reference that one index.

## C. projected final-state validation is correct but O(total runtime state)

`ValidateProjectedFinalState(...)` currently simulates final state by taking:

```csharp
var snapshot = _projections.CaptureState();
var setSnapshots = _sets.ToDictionary(
    pair => pair.Key,
    pair => pair.Value.CaptureState());
```

It then mutates the live object-set/projection structures temporarily, calls `ValidateAll()`, and restores everything.

That means a one-link retarget or one lifecycle add/remove can copy **all registered object sets and the entire projection registry** during `Prepare`, and then repeat validation at `Commit`.

The ordinary projected-propagation benchmark is excellent — 10k and 100k populations have nearly identical cost at equal fan-out — but it does not exercise this lifecycle/retarget validation path.

Prepare-time validation should become a read-only, touched-state calculation rather than a whole-runtime clone.

## D. current detailed results arrive too late for an atomic same-database outbox

Today the documented safe external flow is:

```text
Prepare
 -> database commit
 -> runtime CommitDetailed
 -> persist/use detailed result
 -> Dispatch
```

That is safe for runtime synchronization, but if the durable repair/outbox row lives in the same database as the business mutation, the detailed result is not available until after database durability. Writing the outbox then requires another transaction and loses the normal transactional-outbox guarantee.

The missing operation is not hypothetical simulation. The domain objects are already mutated and `Prepare(...)` has already normalized/validated the mutation. We need:

```text
PreparedMutation
   -> non-mutating detailed impact plan
   -> persist business rows + durable outbox in one DB transaction
   -> DB transaction commit
   -> runtime Commit
   -> Dispatch
```

## E. preview and commit must share one semantic execution engine

Do not implement planning by duplicating relation/derived/invariant logic. A plan must be the result of the same mutation execution semantics as commit, run under reversible runtime state.

The difficult contract is parity:

```text
same prepared mutation
same runtime version
same domain state
        =>
PreviewDetailed result == CommitDetailed result
```

up to fields whose meaning is explicitly commit-only.

## F. release engineering still stops at CI artifacts

There are no GitHub releases and only the CI workflow exists. Package version is currently fixed at `0.1.0-alpha.1`. This roadmap may prepare a release-candidate workflow and contract, but **must not publish to NuGet, create a GitHub release, or push a release tag** unless explicitly requested later.

---

# Global implementation rules

1. Runtime correctness remains more important than explanation richness or preview convenience.
2. Preview operates on an already-mutated domain and a `PreparedMutation`; it is not a pre-mutation what-if engine.
3. Preview must leave runtime version, indexes, caches, diagnostics, policy-dispatch state, and `PreparedMutation` state unchanged.
4. Preview never invokes callbacks and never returns a dispatch capability.
5. Preview and commit must execute the same semantic mutation core.
6. A causal record must only claim origins/precision that were recorded at the actual routing/propagation decision point.
7. Conservative precision is monotonic downstream; it can never become Exact again.
8. `Invalid` dominates `Dirty`; inherited severity can never be weakened.
9. Original relation/computation/invariant expressions remain semantic authority.
10. A caller must never need a manual `EnsureFresh`, `InvalidateLinks`, or similar correctness call.
11. Basic `Apply` remains the cheapest path; summary mode must not allocate causal graph structures.
12. Final-state projection validation must be based on the logical final batch state, independent of mutation ordering.
13. Projected edges remain direct-reference/non-null/exact-object-set in this roadmap.
14. Core remains free of EF Core, DI, logging, persistence, and transport dependencies.
15. Preserve Prepare/Commit version binding, domain-drift validation, exception atomicity, durable identities, and resumable dispatch.
16. Public API changes require deliberate `PublicAPI.Unshipped.txt` updates.
17. Semantic core changes must pass both net8.0 and net10.0 suites; EF changes run on net10.0.
18. Keep commits buildable and bisectable; do not land Tasks 41–47 as one large commit.

---

# Task 41 — Make causal evidence a product of propagation, not reconstruction

## Goal

When causal detail is requested, capture a per-wave evidence graph while routing impacts. `RuntimeApplyResult` should be a deterministic projection of that graph.

Do not keep permanent runtime history. Evidence exists only for the current execution/preview result.

## 41A — Introduce internal impact-node evidence

Recommended internal concepts:

```text
PropagationImpactKey
  Definition
  Source

PropagationEvidenceNode
  Key
  FinalSeverity
  Precision
  DirectCauses[]
  UpstreamImpactRefs[]
```

Exact names are flexible. The important point is that evidence is source-scoped: `(derived/invariant definition, source object)` is a distinct impact node.

Each node should receive a deterministic per-result ID only when causal detail is enabled.

## 41B — capture direct-member evidence where classification occurs

Keep the current good behavior: member-specific severity is classified once during propagation.

Record:

```text
normalized OriginId
member
local classified severity
policy kind/name
precision = Exact
```

Never re-run user classifiers in result construction.

## 41C — capture relation evidence when source selection occurs

When a relation impact selects a derived source, record the cause there:

```text
relation definition
MembershipAdded / MembershipRemoved / RelatedItemChanged / ConservativeCandidate
Exact / Conservative
only relevant normalized mutation OriginIds
```

Do not assign all batch origins to every relation cause.

For relation deltas produced by object add/remove, associate the corresponding lifecycle origin. For property/navigation changes, carry origin IDs from the resolved change-routing data. If a cause cannot be mapped exactly, leave `OriginIds` empty rather than inventing provenance.

## 41D — upstream propagation references actual upstream impact nodes

When an upstream derived impact reaches a downstream node, record a reference to the **actual upstream source-scoped evidence node**.

For projected dependencies this is essential:

```text
link source
  --projection--> PO line source
  --upstream derived--> AvailableQuantity(PO line)
```

Do not call `IsConservative(...)` later. Precision on the downstream path is the merge of the actual incoming path precision:

```text
Exact + Exact                 -> Exact
Exact + Conservative          -> Conservative
Conservative anywhere upstream -> Conservative downstream
```

Consider extending `UpstreamDerivedCause` with a per-result upstream impact ID. Avoid relying only on `DerivedId`, which cannot disambiguate sources.

## 41E — invariant reaction evidence

Record reaction escalation at the moment it happens:

```text
Reaction
InputSeverity
OutputSeverity
```

Only emit an escalation when the reaction actually changed the severity. Already-Invalid input must not produce `Dirty -> Invalid` evidence.

## 41F — render the real evidence graph

Refactor `RuntimeImpactTraceRenderer` to traverse evidence references rather than print a fabricated flat list.

A diamond should look conceptually like:

```text
link-validity(link-42) -> Invalid
  because available-quantity(po-7) -> Invalid
    because ordered-quantity changed -> Invalid
  because unit-rate(po-7) -> Invalid
    because price-rate changed -> Invalid
```

Do not duplicate the same evidence node recursively forever; track visited nodes and render shared branches deterministically.

## Tests

Add regression coverage for:

```text
batch contains relevant + unrelated mutations -> relation cause contains only relevant origin
added + removed relation membership in one batch
source Dirty + independent upstream Invalid
projected upstream from different source object
projected conservative upstream stays Conservative downstream
exact + conservative fan-in -> Conservative
invariant already Invalid -> no fake escalation
ScheduleRepair Dirty -> Invalid -> explicit escalation
causal diamond deterministic and non-duplicative
same mutation order permutation -> stable causal ordering
basic Apply allocates no causal graph
Summary allocates no causal graph
```

### Acceptance gate

`CreateCauses(...)` no longer infers relation/upstream precision or broad origin sets from final snapshots. Public causal output can be traced to evidence recorded at the decision that actually caused the impact.

Suggested commits:

```text
test: expose causal provenance edge cases
refactor: capture source-scoped propagation evidence
feat: render causal impact graph
```

---

# Task 42 — Canonicalize projection ownership and make final-state validation incremental

## Goal

Keep the direct-reference projection contract, but remove duplicate physical indexes and whole-runtime prepare-time cloning.

## 42A — separate semantic projected inputs from physical projection edges

Compile a structural projection-edge key such as:

```text
ProjectionEdgeKey
  DownstreamSet
  SelectorMember
  UpstreamSet
```

The alpha selector is one direct reference member, so this key is sufficient. Do not generalize to nested/key-based/null projections in this wave.

Multiple derived inputs may consume one physical edge:

```text
link.PurchaseOrderLine -> po-lines
    consumers:
      availableQuantity
      unitRate
      future derived X
```

`ProjectionIndexRegistry` should hold one `DownstreamToTarget` / `TargetToDownstreams` pair per structural edge. `ProjectedUpstreamDerivedInput` should reference the structural edge, not own a separate physical index.

## 42B — clarify diagnostics

Distinguish at least:

```text
ProjectedDependencyConsumerCount   // semantic inputs
ProjectionIndexCount               // unique physical ownership edges
ReverseProjectionEntryCount
ProjectedTargetCount
```

Do not silently change the meaning of an existing diagnostic field if consumers could interpret it differently; rename/update the unshipped contract deliberately.

## 42C — replace `ValidateProjectedFinalState` with a read-only final-state view

Do not mutate `_sets` / `_projections` temporarily during `Prepare`.

Introduce an internal final-membership overlay for only touched object sets:

```text
FinalSetMembershipView
  Contains(set, instance)
  Added(set)
  Removed(set)
```

Then validate only affected projection edges/sources.

Required logic:

```text
downstream added
  -> read its final selector target
  -> target must exist in final upstream membership

downstream selector retargeted
  -> final target must exist in final upstream membership
upstream target removed
  -> inspect reverse bucket
  -> subtract downstream removals and retargets in the same batch
  -> reject surviving dependents
unrelated lifecycle mutation
  -> must not scan/copy unrelated projection state
```

Final validity must remain independent of mutation ordering inside the batch.

## 42D — Commit revalidation

Commit must re-run the same read-only final-state validator after ordinary prepared/domain/version checks. Do not maintain a separate prepare-only and commit-only semantic implementation.

## 42E — rollback snapshot remains scoped

Normal upstream property propagation should still avoid projection snapshots. Selector retarget/add/remove commit rollback may snapshot only the affected physical projection edge(s), not the entire registry.

If scoped inverse operations are cleaner than copying edge dictionaries, use them, but keep exception atomicity simple and testable.

## Tests

Retain all existing lifecycle cases and add:

```text
two projected upstreams share exactly one physical ownership index
multiple downstream definitions using same selector/upstream set share index
same selector member but different upstream ObjectSet does not share index
unrelated object-set lifecycle mutation does not validate/copy projection edges
remove target + remove all dependents same batch succeeds
remove target + retarget all dependents same batch succeeds
remove target with one surviving dependent fails
retarget failed validation leaves projection indexes unchanged
Prepare does not mutate live runtime diagnostics/index state
```

## Benchmark

Add lifecycle/validation measurements:

```text
downstream population: 10k, 100k
operation: retarget one link
operation: add one link
operation: remove one link
operation: remove one target with small fan-out
```

At equal touched fan-out, cost should not scale linearly with all registered object sets or all projection entries.

### Acceptance gate

A small lifecycle/retarget batch performs work proportional to touched sets/edges/fan-out rather than cloning the complete runtime model.

Suggested commits:

```text
refactor: share structural projection ownership indexes
refactor: validate projected final state with overlays
perf: scope projection rollback state
bench: measure projected lifecycle validation
```

---

# Task 43 — Extract one reversible mutation-execution core

## Goal

Prepare the runtime for impact preview without duplicating commit semantics.

Current `CommitWithResult(...)` combines:

```text
prepared/version/domain validation
impact planning
rollback snapshot capture
mutation execution
version increment
PreparedMutation.MarkCommitted(...)
```

Split these responsibilities so commit and preview can invoke the same semantic execution code.

## 43A — internal execution context

Introduce an internal execution method/result along these lines:

```text
ExecutePreparedMutation(
    PreparedMutation prepared,
    detail/evidence mode)
      -> RuntimeCommitResult
```

It may mutate runtime structures while running, but it must **not** itself:

```text
increment runtime Version
mark PreparedMutation committed
dispatch callbacks
construct a PolicyDispatchHandle
```

Version advancement and `MarkCommitted(...)` belong to the commit wrapper.

## 43B — one impact/result builder

`CreateDetailedResult(...)` currently reads `prepared.PolicyActions!`, which only exists after commit marks the prepared mutation committed.

Refactor result creation to consume `RuntimeCommitResult.PolicyActions` directly. This is required so a preview can build the identical report without mutating `PreparedMutation` state.

## 43C — reusable execution snapshot

Factor snapshot acquisition/restoration around the exact structures execution can mutate:

```text
touched object sets
affected relations
navigation indexes
projection indexes
derived/invariant runtime state
diagnostics / LastRelationImpacts
```

Do not snapshot unrelated structures merely because preview exists.

## 43D — failure behavior

If execution throws:

```text
commit -> restore runtime, rethrow, prepared remains uncommitted
preview -> restore runtime, rethrow, prepared remains uncommitted
```

Preserve current exception atomicity.

### Acceptance gate

There is one semantic path that computes relation deltas, dependency propagation, policy requests, and detailed result inputs; commit and future preview differ only in whether the executed state is retained and whether the prepared mutation/version are advanced.

Suggested commit:

```text
refactor: extract reversible prepared mutation execution
```

---

# Task 44 — Add non-mutating `PreparedMutation` impact preview

## Goal

Expose the result of the same runtime execution without committing runtime-owned state.

Preferred API:

```csharp
RuntimeApplyResult preview = runtime.PreviewDetailed(
    prepared,
    RuntimeImpactDetailLevel.Causal);
```

Keep one immutable impact payload rather than creating a parallel duplicate DTO graph. Update XML documentation for `RuntimeApplyResult` so the object means stable impact/report data and does not itself imply that runtime state was committed.

Do not return `RuntimeApplication` or `PolicyDispatchHandle` from preview.

## 44A — semantics

Preview is valid only when:

```text
prepared belongs to this runtime
prepared is not committed
prepared BaseVersion == runtime.Version
prepared domain assumptions still hold
projected final-state validation succeeds
```

Execution semantics:

```text
validate
capture scoped execution state
execute the same mutation core
construct Summary/Causal RuntimeApplyResult
restore state in finally
return immutable result
```

After return:

```text
runtime.Version unchanged
PreparedMutation.IsCommitted == false
PreparedMutation.IsDispatched == false
runtime indexes unchanged
cached derived/invariant states unchanged
runtime diagnostics unchanged
LastRelationImpacts unchanged
no callbacks invoked
```

Repeated preview of unchanged runtime/domain state should be deterministic.

## 44B — preview/commit parity

For an unchanged prepared mutation:

```csharp
var preview = runtime.PreviewDetailed(prepared, Causal);
var committed = runtime.CommitDetailed(prepared, Causal);
```

assert semantic equality of:

```text
ChangeImpact
relation pair deltas
source-scoped derived impacts
invariant impacts
repair requests
immediate-evaluation requests
causal origins/evidence/precision
```

Do not compare incidental collection object identity.

## 44C — lifecycle and removal identity

Preview must work for:

```text
object add
object remove
add + relation membership
remove + relation membership
projected link add/remove/retarget
```

Result construction must not depend on source registration that has already been restored. Build identities/result data while simulated state is present, or explicitly capture required stable identities before restoration.

## 44D — stale/drift behavior

Tests:

```text
preview -> runtime commits another mutation -> original Commit rejected stale
preview -> reported domain property drifts -> Commit rejected
preview of stale prepared mutation rejected
preview after prepared already committed rejected
preview failure leaves runtime unchanged
preview can be called repeatedly before commit
```

## 44E — distinguish from hypothetical simulation

README/API docs must explicitly state:

> PreviewDetailed predicts runtime impact for an already-mutated, prepared domain state. It does not apply hypothetical old/new values to an untouched object graph.

Pre-mutation what-if remains out of scope.

### Acceptance gate

A caller can obtain the same impact/policy plan that commit will produce, without any durable or observable change to runtime state.

Suggested commits:

```text
feat: preview prepared mutation impact without commit
 test: verify preview and commit semantic parity
```

---

# Task 45 — EF transactional preview and atomic outbox workflow

## Goal

Make the new preview capability useful in the integration where it matters most: persist business rows and durable repair/outbox work in the same database transaction, while runtime state is committed only after database durability.

## 45A — `RelationUnitOfWork.PreviewDetailed`

Add:

```csharp
RuntimeApplyResult? PreviewDetailed(
    RelationRuntime runtime,
    RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary);
```

Contract:

```text
must be prepared first
empty unit -> null
may be called repeatedly before Commit
never marks unit committed
does not dispatch
uses runtime.PreviewDetailed(...)
```

## 45B — document the explicit-transaction pattern

Recommended same-database transactional-outbox flow:

```csharp
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
unit.Prepare(runtime);

await using var tx = await context.Database.BeginTransactionAsync();

// Business writes happen, but are not durable outside the transaction yet.
await context.SaveChangesAsync();

// Domain objects now also contain DB-generated/fixup values produced by SaveChanges.
var impact = unit.PreviewDetailed(runtime, RuntimeImpactDetailLevel.Causal);
PersistDurableRepairOutboxRows(context, impact);
await context.SaveChangesAsync();

await tx.CommitAsync();

// Only after DB durability:
unit.Commit(runtime);
unit.Dispatch(runtime);
```

Do not add persistence/outbox abstractions to the core package. The library provides deterministic impact data; the application owns its schema and transport.

## 45C — generated-key contract

Explicitly test/document added entities whose persistence layer may assign keys.

Current core object-set keys are required to be stable once registered. Decide one of these contracts deliberately:

1. **preferred if practical:** allow an unregistered Added object's key to change between pre-save capture/prepare and post-save preview/commit, while validating uniqueness/nullability against the final value at commit; or
2. document that object-set keys used with the adapter must be application-assigned/stable before `Prepare`.

Do not silently support one added identity entity while rejecting two default-key additions as duplicates. Add a focused EF/SQLite test so the actual contract is explicit.

Do not weaken the rule that keys of already registered runtime objects cannot change.

## 45D — transactional failure matrix

Use a real relational transaction provider for these tests (SQLite is sufficient; do not rely only on EF InMemory).

Cover:

```text
Prepare fails -> no DB write
first SaveChanges fails -> runtime unchanged, no outbox commit
Preview fails -> transaction can roll back, runtime unchanged
outbox SaveChanges fails -> DB transaction rolls back, runtime unchanged
DB transaction commit succeeds -> runtime Commit succeeds -> normal path
DB commit succeeds but runtime Commit fails -> synchronization exception/rebuild path; outbox remains durable
Dispatch callback fails -> DB/runtime already committed; dispatch remains resumable
```

For the runtime-sync-failure case, document that durable outbox data is preferable to trying to roll back an already committed database transaction.

## 45E — no misleading convenience API

Do not create a one-line `SaveChangesAndPreviewAndApply` helper that hides the transaction boundary. Keep the explicit transaction sequence visible. Existing convenience APIs may remain for simpler cases.

### Acceptance gate

The docs/tests demonstrate a real transactional-outbox pattern in which the impact plan is persisted atomically with business data and runtime state is not advanced before the database transaction commits.

Suggested commits:

```text
feat: expose EF prepared impact preview
 test: verify transactional outbox ordering with sqlite
 docs: document atomic outbox integration
```

---

# Task 46 — Equivalence, resilience, and performance gates

## Goal

Prove that planning is not a second semantics engine and that the Task 42 validation improvements removed hidden whole-model costs.

## 46A — randomized preview/commit oracle

Generate deterministic mixed mutation sequences over models containing:

```text
exact relation-backed derived value
conservative relation-backed derived value
source-only derived value
same-source DAG composition
projected one/two-upstream composition
Dirty + Invalid fan-in
invariant with ScheduleRepair
lifecycle add/remove
projection retarget
collection change
```

For each accepted prepared batch:

```text
preview Summary
preview Causal
capture runtime diagnostics/state fingerprint
commit Detailed Causal
compare semantic preview/commit result
assert preview left fingerprint unchanged
```

Use fixed seeds and print the failing seed/step.

## 46B — failure injection

Inject failures from:

```text
relation predicate / recomputation path where supported
impact policy classifier
invariant policy preparation
projection final-state validation
```

Verify preview and commit both restore runtime-owned state and do not partially change diagnostics/indexes/caches.

Do not intentionally invoke application callbacks during preview.

## 46C — benchmark planning overhead

Add BenchmarkDotNet scenarios for:

```text
Commit basic
CommitDetailed Summary
CommitDetailed Causal
PreviewDetailed Summary
PreviewDetailed Causal
```

with:

```text
small source-only mutation
relation membership change
projected fan-out 1 / 10 / 100
```

Record mean + allocations. Preview is allowed to cost more than commit because it must restore state, but cost should scale with touched state/fan-out, not total model population.

## 46D — benchmark final-state validation

Record 10k/100k populations for one projected retarget/add/remove and compare equal touched fan-out. Ensure Task 42 actually removed O(total-population) prepare validation.

## 46E — memory gate for shared projection index

For two projected upstream consumers sharing one direct reference edge, diagnostics/tests must prove one physical ownership index is retained.

### Acceptance gate

Preview/commit parity has both deterministic unit coverage and randomized coverage, failure restoration is tested, and benchmark evidence shows no new O(total downstream population) hot path.

Suggested commits:

```text
test: randomize preview and commit equivalence
 test: inject preview rollback failures
 bench: measure prepared impact planning
 bench: measure projected final-state validation
```

---

# Task 47 — Release-candidate contract and release engineering

## Goal

Leave the repository ready for an explicit first-alpha publication decision without publishing anything automatically.

## 47A — final public API review

Review the full unshipped surface with special attention to:

```text
PreviewDetailed naming and semantics
RuntimeApplyResult documentation now that preview can return it
projected `.Using(...)` one/two-upstream overloads
RuntimeDiagnostics projection counters
causal impact IDs/references
RelationUnitOfWork preview/commit APIs
```

Remove accidental/public implementation concepts now rather than carrying them into a published alpha.

Do not add arity 3..8 overloads.

## 47B — package/version source of truth

Avoid scattered hard-coded alpha versioning. Prefer one repository-level version source (`Directory.Build.props` or an equivalent existing convention) consumed by both core and EF packages.

Do not change the version merely to create this roadmap. Choose the next alpha version only as part of the release-candidate preparation.

## 47C — release-candidate workflow

A manual `workflow_dispatch` workflow may be added that:

```text
restores/builds/tests/formats
packs core + EF
runs packed-package consumers
validates package contents/symbol packages
uploads .nupkg/.snupkg as workflow artifacts
```

It must **not** push NuGet packages, create a GitHub release, or create/push a git tag without a later explicit instruction.

## 47D — documentation gate

Update README/architecture/CHANGELOG with:

```text
runtime is not thread-safe
Prepare -> Preview -> DB durability -> Commit -> Dispatch semantics
preview is post-domain-mutation, not hypothetical what-if
projected selector is direct/non-null/exact-set
Dirty vs Invalid
exact vs conservative propagation
bootstrap/recovery
transactional outbox example
current supported TFMs
known alpha limitations
```

Known limitations should include at least:

```text
no pre-mutation what-if overlay
no nullable/key-based/nested projected selectors
no distributed dependency graph
no runtime auto-tuning
no derived-valued relation predicates
no automatic persistence of impact history
```

## 47E — final gates

Before marking this roadmap complete:

```text
latest main CI green
net8 core green
net10 core green
net10 EF green
format green
pack green
packed consumers green
preview/commit randomized suite green
transactional SQLite integration green
PublicAPI approvals intentional
benchmark result docs refreshed
procurement sample runs from clean build
no automatic publish side effect
```

### Acceptance gate

The repository is a release candidate for a deliberately chosen alpha, but publication remains a separate explicit user action.

Suggested commits:

```text
build: centralize alpha package versioning
build: add manual release candidate verification
 docs: document prepared impact planning and outbox flow
 docs: close precommit planning roadmap
```

---

# Recommended implementation sequence

Keep commits small and bisectable. A good sequence is:

```text
1.  test: expose causal provenance edge cases
2.  refactor: capture source-scoped propagation evidence
3.  feat: render causal impact graph
4.  refactor: share structural projection ownership indexes
5.  refactor: validate projected final state with overlays
6.  perf: scope projection rollback state
7.  bench: measure projected lifecycle validation
8.  refactor: extract reversible prepared mutation execution
9.  feat: preview prepared mutation impact without commit
10. test: verify preview and commit semantic parity
11. feat: expose EF prepared impact preview
12. test: verify transactional outbox ordering with sqlite
13. docs: document atomic outbox integration
14. test: randomize preview and commit equivalence
15. test: inject preview rollback failures
16. bench: measure prepared impact planning
17. build: centralize alpha package versioning
18. build: add manual release candidate verification
19. docs: stabilize prepared impact planning contract
```

If a task reveals a correctness issue, fix and test it before continuing. Do not compensate for a semantic problem by broad invalidation unless the plan explicitly allows conservative over-selection; structural projected-reference integrity and causal truthfulness are exact contracts.

---

# End-state architecture after Tasks 41–47

The desired flow is:

```text
Domain mutation already happened
        |
        v
Capture / Prepare
  - normalize mutations
  - validate final structural state
  - bind runtime version/domain assumptions
        |
        +----------------------+
        |                      |
        v                      v
PreviewDetailed          database transaction
(non-mutating)             business SaveChanges
        |                      |
        | impact + causal      | persist durable outbox
        | policy plan -------->| using preview result
        |                      |
        |                      v
        |                 DB COMMIT
        |                      |
        +----------------------+
                               v
                         Runtime Commit
                               |
                               v
                            Dispatch
```

Inside runtime impact execution:

```text
normalized mutation origins
        |
        v
relation/source/projection routing
        |
        v
source-scoped dependency DAG
        |
        +--> severity + precision
        +--> decision-time evidence graph
        +--> policy requests
        |
        v
immutable RuntimeApplyResult
```

For projected state:

```text
semantic projected consumers
       \   |   /
        \  |  /
   one structural projection edge
             |
             v
   shared reverse ownership index
             |
             v
   actual impacted downstream fan-out
```

The strategic result is important: Raffinert.Relations no longer only tells the application what became dirty after a mutation. It can deterministically **plan the consistency consequences of an already-prepared domain change before runtime commit**, while keeping the final runtime application failure-atomic and the database/outbox durability boundary explicit.
