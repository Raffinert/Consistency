# Codex Implementation Plan — Causal Integrity, Runtime Bootstrap, and Cross-Source Dependency Proof

Status: implemented. Tasks 27–33 are represented by the causal evidence/provenance fixes, detailed
prepared commit APIs, runtime bootstrap, projected upstream composition, persisted-link sample, and
release verification additions in this change set.

Baseline: `main` at `3150b7d41cf9a6f3a89f7177927432315eef40ac`.

The previous Tasks 20–26 roadmap is implemented and marked completed. The repository now has value-sensitive source severity, summary/causal detailed apply modes, a compiling procurement sample, packed-package smoke tests, and the post-DAG runtime hardening from earlier waves.

This verification found that the next work should **not** be another optimizer family. The current risks are at the product-contract boundary:

1. causal output is useful but is reconstructed after propagation and can currently make explanations that are less precise than the runtime decision that actually occurred;
2. the safe external transaction path (`Prepare -> database commit -> runtime Commit`) cannot obtain the same detailed/causal result that `ApplyDetailed(...)` can;
3. recovery documentation tells users to rebuild from authoritative state, but the model only creates an empty runtime and has no first-class bootstrap/hydration path;
4. the current procurement sample proves dependency propagation on a PO-line-shaped surrogate for link state, not a real persisted selected-link object;
5. reusing a derived value from another object set is not currently expressible, which is likely the real missing primitive for a non-duplicated persisted-link model.

Implement the next wave in this order:

```text
27  Fix causal truthfulness before extending the API
28  Preserve normalized mutation provenance and capture evidence during propagation
29  Expose detailed prepared-commit results and integrate them with EF/unit-of-work flows
30  Add first-class runtime bootstrap/rebuild from authoritative object sets
31  Add projected upstream derived dependencies across object sets
32  Replace the surrogate procurement sample with a persisted selected-link vertical slice
33  Stabilize the first publishable alpha contract and regression/performance gates
```

Do not start pre-mutation simulation, distributed execution, runtime auto-tuning, a new relation access-plan family, or derived-valued relation predicates during this roadmap.

---

# Verified current state

The following are already implemented and must not be re-created:

```text
expression-defined binary relations
scan/hash query planning
exact and conservative propagation separated from query access
selective old/new-key conservative routing
source-only derived values
one/two-upstream same-source composed derived values
compiled dependency DAG + cycle rejection
topological source-scoped Dirty/Invalid propagation
value-sensitive direct-source member classifiers
single/two-input invariants and repair/immediate-evaluation policy
incremental Count/LongCount/Any/numeric Sum
Prepare -> Commit -> Dispatch
runtime rollback on failed Commit
stable detailed summary results
opt-in causal result records and trace renderer
durable policy-request identity
EF Core 10 adapter
core behavior on net8.0 + net10.0
packed NuGet consumer smoke tests
procurement demonstration sample
```

The current head CI run was still in progress when this plan was prepared. Do not begin semantic implementation until the baseline or its direct documentation successor is green.

---

# Verification findings that drive this plan

## A. Causal records are reconstructed, not produced by the decision point

`DependencyPropagationResult` currently contains only final derived/invariant impact snapshots. `MutationCommit.CreateCauses(...)` later reconstructs causes from definitions, prepared changes, relation impacts, and the final wave.

That creates several concrete correctness gaps in the explanation layer:

```text
DirectSourceMemberCause.ClassifiedSeverity
    currently receives the final merged source severity,
    not necessarily the member rule's own classified severity.

UpstreamDerivedCause.Precision
    is currently always Exact,
    even when the upstream source was selected conservatively.

RelationDependencyCause kind
    currently uses an added / removed / fallback choice,
    so multiple simultaneous cause kinds can collapse to one.

Invariant reaction escalation
    can turn an inherited/direct Dirty impact into Invalid,
    but no distinct cause explains that policy escalation.
```

Runtime state can still be correct while the explanation is misleading. Causal mode must never claim stronger precision or a different local severity than the propagation logic actually used.

## B. Mutation provenance is flattened

Collection mutations are normalized and then converted to `PropertyChange(member, null, null)` for runtime dependency processing. Causal origin capture later infers `CollectionChanged` from the null/null value shape.

This loses:

```text
CollectionAdd vs CollectionRemove vs CollectionReset
changed collection item
whether a null/null PropertyChange was actually a property signal
```

The runtime may continue to use a property-shaped impact signal internally, but causal provenance must retain the normalized original mutation kind explicitly.

## C. Detailed results are unavailable in the safest transaction integration path

`ApplyDetailed(...)` can return `RuntimeApplyResult`, but it performs Prepare + Commit immediately.

For a database transaction the correct order is:

```text
Prepare
 -> database SaveChanges / transaction commit
 -> runtime Commit
 -> outbox / detailed result handling
 -> Dispatch
```

Public `Commit(PreparedMutation)` currently returns only `ChangeImpact`. `RelationUnitOfWork.Commit(...)` also returns only `ChangeImpact?`.

That means a consumer cannot safely obtain causal/summary result data after database durability without dropping to internal behavior or re-running logic.

## D. There is no first-class runtime bootstrap/rebuild API

`RelationRuntimeSynchronizationException` explicitly says to reconcile or rebuild from authoritative state, but `CompiledRelationModel` only exposes empty `CreateRuntime(...)` overloads.

A production service restart/recovery needs to load existing objects, build object-set indexes, navigation reverse indexes, relation indexes/materialized pairs, and then lazily compute derived state without pretending every existing row is a new business mutation.

## E. The current procurement sample is still a surrogate link model

The sample stores `ReservedQuantity` and `CapturedRate` on `PurchaseOrderLine` and derives `LinkValidity` on the line itself. That proves propagation, but it does not model the real selected link as its own object whose validity depends on current PO-derived state.

The natural model is closer to:

```text
PurchaseOrderLine
  -> ReceivedQuantity
  -> AvailableQuantity
  -> UnitRate
  -> DecisionInputs

PurchaseOrderInvoiceLink
  --references--> PurchaseOrderLine
  -> LinkValidity(DecisionInputs, captured link fields)
  -> repair/rematch invariant
```

Today `Derived<TSource,...>.Using(...)` requires upstream derived values to have the same source set, so the link cannot reuse `DecisionInputs` from its referenced PO line. Duplicating the receipt aggregate per link would violate the central “declare once, derive dependencies” value proposition.

---

# Global rules

1. Runtime correctness remains more important than explanation richness.
2. A causal record must never claim `Exact` when any source-selection step for that causal path was conservative.
3. `Invalid` dominates `Dirty` for final state, but every direct cause must retain its own local classified severity.
4. Causal mode is per committed wave; do not create permanent runtime history/event sourcing.
5. `RuntimeApplyResult` remains data-only. No runtime references, callbacks, or live lookup capabilities in the result.
6. Basic `Apply` must remain the cheapest path; summary mode must not pay causal evidence allocation costs.
7. Preserve Prepare/Commit version checking, domain-drift validation, runtime exception atomicity, durable identity, and resumable dispatch.
8. Core remains free of EF Core, DI, logging, and transport dependencies.
9. Keep the original relation/computation/invariant expressions as semantic authority.
10. No manual `EnsureFresh`-style correctness requirement may be introduced.
11. Cross-source derived support must compile into the same DAG and participate in cycle detection.
12. Do not add generic arity 3..8 `.Using(...)` overload families.
13. Public API changes require deliberate `PublicAPI.Unshipped.txt` updates.
14. Every semantic core change must pass both net8.0 and net10.0 suites.
15. Each commit should remain buildable and bisectable.

---

# Task 27 — Fix causal truthfulness first

Add regression tests that expose the explanation problems before refactoring implementation.

## 27A — Preserve local severity separately from final severity

Example:

```text
local Quantity rule            -> Dirty
upstream dependency            -> Invalid
final downstream impact        -> Invalid
```

The result must say:

```text
SourceDependencyImpact.Severity = Invalid
DirectSourceMemberCause.ClassifiedSeverity = Dirty
UpstreamDerivedCause ... = Invalid path
```

Do not overwrite cause-local meaning with the final merged severity.

For a member-specific classifier, evaluate the classifier once during propagation and retain that result as evidence. Do not invoke the user classifier a second time while constructing public result objects.

## 27B — Make precision monotonic

If an upstream `(derived, source)` impact is conservative, every downstream impact reached solely through that upstream must also be conservatively justified.

Required rule:

```text
Exact + Exact         -> Exact path
Exact + Conservative  -> Conservative path
Conservative upstream -> downstream UpstreamDerivedCause cannot be Exact
```

Precision describes source-selection certainty, not Dirty/Invalid severity.

## 27C — Preserve all simultaneous direct causes

For the same `(definition, source)` one wave may contain:

```text
membership added
membership removed
related item changed
direct source member changed
multiple upstream derived causes
```

Do not encode relation cause selection as a single `if added else if removed else ...` choice. Retain every cause that contributed to the wave, de-duplicated deterministically.

## 27D — Explain invariant reaction escalation

When `MarkInvalid` or `ScheduleRepair` turns a Dirty inherited/direct impact into Invalid, expose that policy step explicitly rather than pretending the original source cause itself was Invalid.

An internal/public concept such as this is acceptable:

```text
InvariantReactionCause
  Reaction
  InputSeverity
  OutputSeverity
```

Exact names are flexible.

## Tests

Add at least:

```text
local Dirty + upstream Invalid
local Invalid + upstream Dirty
conservative relation -> derived -> derived -> invariant
exact + conservative fan-in
added + removed relation membership in one batch
membership delta + item value change in one batch
ScheduleRepair escalation from Dirty
MarkInvalid escalation from Dirty
cause ordering deterministic regardless mutation order
trace output never upgrades Conservative to Exact
```

### Acceptance gate

For every public causal statement there is a propagation decision/evidence record with the same local severity and no stronger precision.

Suggested commits:

```text
test: expose causal fidelity edge cases
fix: preserve causal severity and precision
```

---

# Task 28 — Preserve normalized mutation provenance and capture evidence during propagation

Task 27 can patch obvious truthfulness bugs, but do not leave causal mode permanently dependent on post-hoc reconstruction.

## 28A — Keep normalized origin metadata in `PreparedMutation`

Extend validation output so it retains both:

```text
runtime dependency changes
normalized mutation provenance
```

A possible internal shape:

```text
NormalizedMutationOrigin
  Property(instance, set, member, oldValue, newValue)
  Collection(owner, set, member, CollectionChangeKind, item?)
  ObjectAdded(set, instance)
  ObjectRemoved(set, instance)
```

The dependency engine may continue receiving collection changes as a property-shaped invalidation signal. Provenance must not be inferred from `(oldValue == null && newValue == null)`.

Multiple collection operations normalized to a reset must expose a normalized `Reset` origin, not the pre-normalization sequence.

Repeated property chain `A -> B -> C` must expose one normalized `A -> C` origin.

## 28B — Make propagation return evidence, not only final snapshots

Evolve internal `DependencyPropagationResult` so each affected `(definition, source)` can carry:

```text
final severity
precision
zero or more direct evidence records
```

Evidence is created where the runtime knows why the source was selected:

```text
source-member classifier -> direct member evidence
relation membership/item routing -> relation evidence
upstream merge -> upstream evidence reference
invariant direct dependency -> direct evidence
invariant reaction -> policy evidence
```

Do not re-run classifiers or infer precision later in `MutationCommit`.

## 28C — Link relation causes to mutation origins

`RelationDependencyCause` should identify the root origin(s) when they are known. One relation impact can be driven by multiple normalized mutations, so model a list/set rather than forcing one origin.

For conservative routing, keep the cause conservative even when the final recomputed value happens to change.

## 28D — Deterministic identity inside one result

Introduce only per-result IDs/references if needed, for example:

```text
OriginId
ImpactNodeId / (definition id, source)
```

Do not create another globally durable ordinal system. Durable cross-process identity remains `DefinitionKey + DurableSourceIdentity`.

## 28E — Improve renderer by traversing actual evidence

`RuntimeImpactTraceRenderer` should be able to render a multi-hop explanation without fabricating copied paths:

```text
GoodsReceipt.Cancelled changed
 -> matching-receipts MembershipRemoved [Exact]
 -> received-quantity Invalid [Exact]
 -> available-quantity Invalid [Exact]
 -> link-inputs Invalid [Exact]
 -> link-validity Invalid [Exact]
 -> link-validity-invariant ScheduleRepair
```

A diamond should render one downstream impact with two upstream branches, not duplicate the downstream state node.

## Tests

```text
CollectionAdd origin retained
CollectionRemove origin retained
CollectionReset origin retained
null -> null scalar signal is not mislabelled as collection provenance
normalized A->B->C property origin is A->C
right object add/remove relation cause links to lifecycle origin
nested navigation change links to actual property origin
result remains immutable after later mutation
summary mode contains no causal evidence/origins
basic Apply contains no result/evidence allocation
```

### Acceptance gate

Causal output is a deterministic projection of evidence produced during the committed propagation wave, not a heuristic reconstruction after the fact.

Suggested commits:

```text
refactor: preserve normalized mutation provenance
refactor: capture propagation evidence at decision points
feat: render traceable causal paths
```

---

# Task 29 — Detailed prepared Commit and EF/unit-of-work integration

## Goal

Make detailed/causal results usable with the safe transaction order.

## 29A — Core prepared-commit result API

Add a public API that commits an already prepared mutation and returns stable detailed result data without dispatching callbacks.

Preferred semantic shape:

```csharp
RuntimeApplyResult result = runtime.CommitDetailed(
    prepared,
    RuntimeImpactDetailLevel.Causal);

// persist outbox/audit if desired
runtime.Dispatch(prepared);
```

`CommitDetailed(...)` should return data only, not another independent dispatch capability. `ApplyDetailed(...)` can remain the convenience API that wraps Prepare + `CommitDetailed` + `PolicyDispatchHandle`.

Capture removal identities/provenance before the commit mutates registration state.

Do not allow calling `Commit(...)` after `CommitDetailed(...)` or vice versa on the same prepared mutation.

## 29B — EF `RelationUnitOfWork`

Add the equivalent manual path:

```text
unit.Prepare(runtime)
database save/transaction commit
result = unit.CommitDetailed(runtime, detailLevel)
// persist result/outbox
unit.Dispatch(runtime)
```

Empty units should return a clear empty/null detailed result according to one documented contract.

## 29C — Convenience API only if it remains transactionally honest

A `SaveChangesAndApplyDetailed` / async equivalent is acceptable if its return type clearly separates:

```text
database SaveChanges result
runtime detailed result
```

Do not hide the warning about externally controlled transactions: convenience APIs must not claim durability before the surrounding transaction commits.

## Failure semantics

Preserve all existing boundaries:

```text
database fails -> runtime not committed
runtime commit fails after database success -> RelationRuntimeSynchronizationException
outbox persistence failure after runtime commit -> application-level reconciliation concern
policy callback failure -> runtime/result already committed, dispatch resumable
```

## Tests

```text
core Prepare -> CommitDetailed -> Dispatch
summary and causal prepared commit
removed-source durable identity survives detailed commit
Commit then CommitDetailed rejected
CommitDetailed then Commit rejected
EF SaveChanges success + detailed result
EF database failure yields no runtime result
EF runtime synchronization failure unchanged
external transaction documented/manual sequence
callback retry semantics unchanged
```

### Acceptance gate

Every integration path that can safely call `Commit` after external durability can obtain the same immutable summary/causal data as `ApplyDetailed` without weakening transaction ordering.

Suggested commits:

```text
feat: return detailed results from prepared commit
feat: expose detailed EF unit-of-work commit
```

---

# Task 30 — First-class runtime bootstrap and recovery

## Goal

Create a runtime from authoritative existing objects without representing the initial database contents as a stream of business mutations.

## 30A — Create-time seed API

Prefer a creation-time API rather than mutating an already live runtime, for example:

```csharp
var runtime = compiled.CreateRuntime(seed =>
{
    seed.Add(poLines, loadedPoLines);
    seed.Add(receipts, loadedReceipts);
    seed.Add(links, loadedLinks);
});
```

Exact names are flexible. Important properties:

```text
seed accepts IEnumerable<T> without params-array explosion
all object sets belong to the compiled model
object type/key/null/duplicate validation remains strict
no policy callbacks
no repair requests
no mutation impact result
no artificial dirty/invalid wave
Version starts at 0 for the constructed baseline
initial derived caches are uncomputed/lazy
initial invariant state is Unknown until evaluated
```

## 30B — Order-independent initialization

Seeding must not depend on the order object sets are declared in the seed callback.

Preferred phases:

```text
validate/collect all instances
register all object-set instances
build navigation reverse indexes
build relation forward/reverse access indexes
materialize exact relation pairs where required
leave derived/invariant caches lazy
```

Reusing existing runtime-state primitives is preferred, but do not expose partially initialized runtime state if seed validation fails.

## 30C — Recovery guidance

Update `RelationRuntimeSynchronizationException` docs and architecture docs with a concrete recovery pattern:

```text
reload authoritative entities
create a new seeded runtime
optionally warm selected derived/invariant values
atomically swap runtime reference at application boundary
```

Do not add an in-place `ResetFromDatabase()` API in this task. Building a new runtime is easier to reason about and avoids readers observing a half-rebuilt state.

## 30D — Benchmark

Measure at least:

```text
10k and 100k objects
hash relation
exact materialized relation
conservative relation
nested navigation roots
```

Compare seed construction against equivalent batched `Change.Add` initialization. The new API should primarily improve semantics and operational clarity; optimize only measured hotspots.

## Tests

```text
empty seed
multiple sets
same CLR type in two explicit sets
duplicate key rejection
navigation indexing after seed
exact pair membership after seed
conservative zero-pair behavior after seed
Get derived after seed matches fresh oracle
invariant begins Unknown
Version == 0 after successful seed
seed failure returns no usable runtime
normal mutations work immediately after seed
```

### Acceptance gate

The documented “rebuild from authoritative state” recovery path is represented by a real API and produces a runtime equivalent to the same authoritative domain graph without emitting business mutation side effects.

Suggested commits:

```text
feat: seed runtime from authoritative object sets
test: verify seeded runtime equivalence
bench: measure runtime bootstrap
```

---

# Task 31 — Project upstream derived values across object sets

## Business need

A selected link object must be able to depend on derived state of the PO line it references without duplicating the PO line's receipt aggregation and rate logic.

Same-source composition cannot express this today.

## 31A — Minimal projected-upstream API

Implement one projected upstream dependency first. Preferred direction:

```csharp
var linkInputs = model.Derived(links)
    .Using(link => link.PurchaseOrderLine, poDecisionInputs)
    .Compute((link, inputs) => ...);
```

where:

```text
links                   = ObjectSet<PurchaseOrderInvoiceLink>
poDecisionInputs        = Derived<PurchaseOrderLine, DecisionInputs>
link.PurchaseOrderLine  = tracked reference path to the upstream source object
```

Do not start with an arbitrary key join or many-to-many projected derived dependency. A tracked object-reference projection is enough to prove the architecture and maps naturally to the existing navigation-index machinery.

## 31B — Internal input model

Add a distinct input kind, conceptually:

```text
ProjectedUpstreamDerivedInput
  UpstreamDerived
  SourceSelectorExpression
  tracked selector path
```

The compiled dependency graph gets an upstream-derived edge exactly like same-source composition, but runtime propagation maps the upstream source object to downstream source roots through the tracked selector/navigation path.

## 31C — Runtime semantics

Required behavior:

```text
upstream Dirty -> mapped downstream Dirty
upstream Invalid -> mapped downstream Invalid
downstream direct source changes merge with projected upstream severity
lazy Get downstream makes projected upstream fresh first
multiple downstream sources may reference one upstream source
changing the downstream reference remaps dependency roots
unrelated upstream source does not affect downstream roots
```

No developer call such as `EnsureFresh` is allowed.

## 31D — Registration/lifecycle contract

Define this explicitly rather than discovering it accidentally.

Recommended initial contract:

- the projected upstream source must belong to the upstream object set when its value is read;
- registering a downstream source with a dangling reference may be allowed, but `Get` must fail clearly until the referenced upstream is registered;
- removing an upstream source while downstream roots still reference it must invalidate those downstream cached values;
- removing both link and upstream source in one `MutationSet` must leave no stale downstream state;
- reference replacement must route from both old and new targets through existing prepared old-state/navigation machinery.

If a stricter registration invariant is substantially simpler and produces better failure behavior, choose it deliberately and document it.

## 31E — Cycles

Projected edges participate in the existing compile-time cycle detector across object sets.

Add cross-set cycle tests such as:

```text
A-derived -> projected B-derived -> projected A-derived
```

and reject at Build.

## Scope boundaries

Do **not** add in this task:

```text
key-based projected lookup
relation-many projected upstream values
derived values inside relation predicates
3..8 projected upstream overloads
```

## Tests

```text
one link -> one PO line
many links -> one PO line
links -> different PO lines
upstream Dirty/Invalid propagation
value-sensitive PO decrease through projection
reference replacement old/new routing
upstream removal
same-batch downstream + upstream removal
lazy recomputation ordering
projected cross-set cycle rejection
randomized projected DAG vs plain-C# oracle on net8/net10
```

### Acceptance gate

A downstream object set can reuse a single derived computation owned by another object set through a tracked reference, with automatic source-scoped propagation and no duplicated business computation.

Suggested commits:

```text
refactor: model projected upstream derived inputs
feat: compose derived values across tracked references
test: verify projected dependency lifecycle and cycles
```

---

# Task 32 — Real persisted selected-link procurement vertical slice

Replace the current surrogate `PurchaseOrderLine` link fields with a first-class selected link entity.

## Target model

Use an executable sample/test domain along these lines:

```text
PurchaseOrderLine
GoodsReceipt
PurchaseOrderInvoiceLink

matchingReceipts : PO line -> receipts
receivedQuantity : PO line
availableQuantity: PO line
unitRate         : PO line
poDecisionInputs : PO line = (availableQuantity, unitRate)

linkValidity     : link
  projected from link.PurchaseOrderLine -> poDecisionInputs
  plus link.LinkedQuantity / link.CapturedUnitRate

linkInvariant    : link -> ScheduleRepair / rematch request
```

The PO-side aggregate and rate calculations must be declared once. Do not duplicate matching-receipt aggregation per link merely to make the sample compile.

## Required scenarios

```text
GR quantity increase/decrease
GR cancellation
GR add/remove
PO ordered quantity increase -> deferrable Dirty
PO ordered quantity decrease -> Invalid
PriceRate/UnitRate input change -> Invalid selected link
multiple selected links against one PO line
one link removed while others remain
PO line reference on a link replaced
PO line removal / same-batch link removal
seeded restart then first mutation
causal explanation for a subtractive change
repair request durable identity belongs to the link source, not the PO line
```

## Demonstrate delayed rematching explicitly

The model should show the distinction:

```text
existing link validity becomes Invalid immediately
repair/rematch work is emitted as data / scheduled post-commit
new matching itself may happen later
```

Do not make the sample perform automatic rematching merely to demonstrate invalidation.

## If Task 31 is insufficient

Do not hide the problem with duplicated rules. Document the exact missing primitive in a short design note and stop before implementing a broader mechanism. Any expansion beyond tracked-reference projection requires another explicit plan.

### Acceptance gate

The executable sample models a real persisted selected-link object and demonstrates the original business problem without duplicated PO-derived computations or caller-managed invalidation chains.

Suggested commit:

```text
sample: model persisted purchase-order link invalidation
```

---

# Task 33 — Alpha contract, performance, and release-candidate stabilization

Do this only after Tasks 27–32.

## Public API review

Review all unshipped additions introduced since the previous roadmap, especially:

```text
RuntimeImpactDetailLevel
MutationOrigin and origin kinds
DependencyImpactCause hierarchy
ImpactCausePrecision
RuntimeImpactTraceRenderer
CommitDetailed
runtime seed/bootstrap types
projected-upstream builder types
```

Prefer one coherent vocabulary over compatibility aliases because nothing has shipped yet.

Check whether the non-generic `DerivedImpactPolicyBuilder` still needs to be public now that all user-facing `.Impact(...)` entry points are typed. If it exists only as an implementation base, consider making the public design cleaner before alpha publication.

## Causal documentation contract

State explicitly:

```text
causal results explain one committed runtime wave
cause precision can be Exact or Conservative
causal data is not a durable event log by default
live Source object references are in-process conveniences
DefinitionKey + DurableSourceIdentity are required for durable correlation
summary/basic modes remain cheaper
```

Do not describe conservative source selection as a fact that the source's semantic value definitely changed.

## Regression suites

Add/extend deterministic randomized tests covering:

```text
projected cross-set DAG
exact + conservative causes
value-sensitive member rules
collection provenance
seeded runtime then random mutations
summary vs causal semantic equivalence
fresh plain-C# oracle convergence
```

Run on net8.0 and net10.0.

## Performance gates

Record:

```text
basic vs summary vs causal Apply
seed/bootstrap 10k/100k
projected upstream fan-out (1/10/100 links per upstream source)
existing exact/conservative relation baselines
commit safety baseline
```

Do not optimize projected propagation until measurements identify a material cost.

## Package consumer tests

Packed-package consumers must compile and execute representative public APIs, not only `CreateRuntime()`:

```text
Core net8: model + seed + projected derived + mutation
Core net10: causal CommitDetailed flow
EF net10: packaged unit-of-work detailed commit path
```

## Docs/release files

Update:

```text
README.md
docs/architecture.md
docs/purchase-order-example.md
docs/roadmaps/README.md
CHANGELOG.md
RELEASING.md if the checklist changes
PublicAPI.Unshipped.txt for both packages
```

Do not move APIs to `PublicAPI.Shipped.txt`, create a release tag, or publish to NuGet unless explicitly requested by the repository owner. This task prepares a release candidate; publication is a separate human decision.

## Full validation

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Then execute the packed-package consumer smoke tests from `artifacts/packages` exactly as CI does.

---

# Final regression matrix

Before closing this roadmap, verify:

```text
[ ] causal local severity is distinct from final merged severity
[ ] conservative precision never becomes Exact downstream
[ ] multiple simultaneous causes are retained
[ ] invariant reaction escalation is explicit
[ ] collection add/remove/reset provenance is preserved
[ ] normalized property chain has one net origin
[ ] causal output is produced from propagation evidence, not heuristic replay
[ ] basic Apply remains free of summary/causal result construction
[ ] prepared Commit can return summary/causal data before Dispatch
[ ] EF manual transaction path can obtain detailed result safely
[ ] runtime can be seeded from authoritative objects with Version == 0
[ ] seed emits no mutation policy actions
[ ] seeded runtime is query-equivalent to authoritative graph
[ ] projected upstream dependency works across object sets
[ ] projected Invalid remains Invalid downstream
[ ] projected reference replacement handles old/new roots
[ ] cross-set projected cycles are rejected at Build
[ ] persisted selected-link sample declares PO computations once
[ ] subtractive PO/GR/rate changes invalidate existing links immediately
[ ] rematching remains deferrable/post-commit
[ ] repair request identity is link-scoped in the persisted-link sample
[ ] randomized projected DAG/oracle tests pass net8 + net10
[ ] packed consumer tests exercise new public surfaces
[ ] benchmark docs contain post-wave baselines
[ ] README/architecture/changelog match actual behavior
[ ] CI is green
```

---

# Recommended commit sequence

```text
1.  test: expose causal fidelity edge cases
2.  fix: preserve causal severity and precision
3.  refactor: preserve normalized mutation provenance
4.  refactor: capture propagation evidence at decision points
5.  feat: render traceable causal paths
6.  feat: return detailed results from prepared commit
7.  feat: expose detailed EF unit-of-work commit
8.  feat: seed runtime from authoritative object sets
9.  test: verify seeded runtime equivalence
10. bench: measure runtime bootstrap
11. refactor: model projected upstream derived inputs
12. feat: compose derived values across tracked references
13. test: verify projected dependency lifecycle and cycles
14. sample: model persisted purchase-order link invalidation
15. test: extend randomized projected DAG equivalence
16. build: exercise new APIs from packed package consumers
17. docs: stabilize causal and recovery alpha contracts
```

Run the full core matrix after Tasks 28, 29, 30, and 31. Run the EF matrix after Task 29 and again before closure.

# Completion criterion

This roadmap is complete when Raffinert.Relations can truthfully explain why a committed change affected each cached domain decision, can produce those results through the transaction-safe prepared-commit path, can be rebuilt cleanly from authoritative state, and can invalidate a real persisted selected link by reusing PO-derived state across an object-set boundary without duplicated business rules or manual caller orchestration.
