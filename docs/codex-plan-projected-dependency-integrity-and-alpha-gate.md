# Codex Implementation Plan — Projected Dependency Integrity, Indexed Fan-out, and Alpha Gate

Baseline: `main` at `ab3fcda5d9e474658e1b8a9bcfcb137639071405`.

The Tasks 27–33 wave is substantially implemented: detailed prepared commits, normalized mutation provenance, runtime bootstrap, projected cross-object-set derived dependencies, and a persisted-link procurement sample all exist. This verification also found important acceptance gaps that must be closed before the new projection API is treated as an alpha contract.

The most important finding is that cross-object-set projection creates a new correctness boundary: a downstream object can now depend on derived state owned by a different registered object. The first implementation propagates ordinary upstream property changes, but it does not yet enforce the projected target's object-set membership across bootstrap/add/remove/retarget lifecycle operations, and it maps upstream impact by scanning every downstream source. Causal reporting is also still partly reconstructed after propagation and currently allocates direct evidence even when causal mode was not requested.

Implement this wave in order:

```text
34  Restore green CI and make roadmap status truthful
35  Enforce projected-target lifecycle and object-set integrity
36  Replace projected all-source scans with a maintained reverse projection index
37  Finish decision-time causal evidence and remove causal cost from basic/summary paths
38  Add two-upstream projected composition and remove incomplete tracking from the flagship sample
39  Harden bootstrap/recovery around projected dependencies and record real performance baselines
40  Run the first publishable-alpha gate
```

Do **not** land Tasks 34–40 as one giant commit. The previous wave landed as one large feature commit; this wave must be split into reviewable, bisectable commits matching the sequence near the end of this document.

Do not start pre-mutation simulation, distributed execution, runtime auto-tuning, new relation access-plan families, key-based projected lookup, nullable projection semantics, or derived-valued relation predicates during this roadmap.

---

# Verified current state

Already implemented; do not recreate:

```text
expression-defined relations with original predicate as semantic authority
scan/hash query access plans
exact/conservative propagation separated from query access
selective conservative old/new-key routing
source-only and same-source composed derived values
compiled dependency DAG + cycle rejection + topological propagation
Dirty / Invalid monotonic severity
value-sensitive direct-source member severity
single/two-input invariants and policy dispatch
incremental Count/LongCount/Any/numeric Sum
Prepare -> Commit -> Dispatch
CommitDetailed(prepared, detailLevel)
EF RelationUnitOfWork.CommitDetailed(...)
runtime rollback on failed Commit
stable detailed summary results
opt-in public causal result records
normalized collection provenance
runtime bootstrap with Version == 0
one projected cross-object-set upstream derived value
persisted PurchaseOrderInvoiceLink sample
core tests on net8.0 + net10.0
EF Core adapter tests on net10.0
NuGet packing and package-consumer projects
```

## Baseline CI status

CI run #65 is **red**, but the semantic/build stages passed:

```text
Build                         passed
Core tests net8.0             191 passed
Core tests net10.0            191 passed
EF Core tests net10.0          20 passed
Formatting                    passed
Pack                          passed
Packed-package smoke restore  failed
```

The smoke failure is infrastructure, not a package compilation failure. The workflow passes `--source artifacts/packages` while restoring a project under `tests/package-consumers/CoreNet8`; NuGet resolves that source under the consumer directory and reports that `tests/package-consumers/CoreNet8/artifacts/packages` does not exist.

No semantic implementation in Tasks 35–40 should be considered ready until Task 34 restores green CI.

---

# Verification findings that drive this plan

## A. Projected upstream objects are not structurally required to be registered

Current projected computation eventually calls the upstream runtime state directly using the selected object:

```text
link
 -> selector(link) == poLine
 -> upstreamDerivedState.GetValue(poLine)
```

`SourceDerivedRuntimeState.GetValue(object)` does not validate object-set registration. Therefore a projected link can point to an object that is not registered in the exact upstream `ObjectSet`, and a hidden upstream cache entry can be created for an object that will never receive tracked lifecycle/change propagation.

For the first alpha, this is unacceptable. A projected dependency is a tracked model edge, so its target must be a registered member of the exact object set that owns the upstream derived value.

## B. Upstream lifecycle operations do not currently propagate through projected dependencies

`DependencyGraphRuntime.ApplyChangeImpacts(...)` is seeded from property changes and relation impacts. Object add/remove is handled separately through `CommitAdd`/`CommitRemove` and source lifecycle hooks for runtime states whose **own source set** matches the changed set.

That was sufficient before cross-set projections. It is not sufficient now.

Examples that need a defined contract:

```text
remove PO line while a surviving invoice link still references it
add link and referenced PO line in one batch
remove link and PO line in one batch
retarget link from old PO line to new PO line while removing old PO line
retarget link to an object of the right CLR type but wrong ObjectSet
```

The first alpha should enforce projected reference integrity rather than silently producing stale/untracked derived state.

## C. Projected propagation currently scans the entire downstream object set

Current inherited mapping effectively does:

```text
for every downstream source:
    projected = selector(source)
    if impactedUpstreamSources contains projected:
        invalidate source
```

So one changed PO line costs O(all invoice links), even if only one link references that line. This defeats the incremental dependency model for the exact scenario the projection API was added to support.

The current benchmark file named `BootstrapAndProjectionBenchmarks` only benchmarks bootstrap; there is no projected-propagation measurement yet.

## D. Projected selector execution uses `Delegate.DynamicInvoke`

`ProjectedUpstreamDerivedInput.Project(object source)` currently delegates through `DynamicInvoke`. This sits on impact routing and causal construction paths and should not become part of the stabilized hot path.

## E. Causal evidence is still only partially captured at decision time

Direct source-member classification now records local classification evidence during propagation. Relation causes, upstream causes, conservative precision, and invariant reaction causes are still reconstructed later in `MutationCommit.CreateCauses(...)`.

Concrete remaining problems:

```text
projected conservative cause checks can use the downstream source where the upstream source is required
relation causes have no origin IDs
invariant reaction cause can claim Dirty -> Invalid even when input was already Invalid
relation/upstream precision is inferred recursively rather than propagated as evidence
```

The implementation therefore still does not meet the previous acceptance statement that causal output is purely a projection of evidence captured where the propagation decision occurred.

## F. Causal bookkeeping is now paid by basic Apply and summary mode

`DerivedNode.ClassifySourceRoots(...)` always allocates a `List<DirectClassificationEvidence>` and records evidence even when callers use plain `Apply(...)` or summary `ApplyDetailed(...)`.

Mutation validation also constructs a separate normalized provenance array for every prepare/apply operation. Some normalized metadata must survive until commit, especially for collection changes, but public causal records and propagation evidence should not be allocated unless causal detail was requested.

The intended cost hierarchy remains:

```text
Apply                       cheapest
ApplyDetailed(Summary)      summary/result allocation only
ApplyDetailed(Causal)       summary + causal evidence/origin allocation
```

## G. The persisted-link sample still opts out of complete dependency tracking

The new sample models a real `PurchaseOrderInvoiceLink`, which is the right domain direction. But it constructs an intermediate `PurchaseOrderDecisionInputs` record and calls `.AllowIncompleteDependencies()` because the analyzer conservatively marks arbitrary `new` expressions as opaque.

The flagship business-value sample should not weaken freshness guarantees.

Do **not** fix this by declaring all constructors safe: constructors can hide side effects/external state. Instead add the specific composition primitive the sample needs: two projected upstream derived values from the same selected upstream source.

---

# Global implementation rules

1. No projected downstream value may depend on an unregistered upstream target.
2. The exact upstream `ObjectSet` matters; matching CLR type is insufficient when the model contains multiple sets of that type.
3. Validate projected reference integrity against the **final logical batch state**, not mutation ordering.
4. A caller must never need a manual `EnsureFresh`, `InvalidateLinks`, or similar correctness call.
5. `Invalid` dominates `Dirty`; no downstream edge may weaken inherited severity.
6. Conservative precision is monotonic along causal paths; downstream evidence may not upgrade it to Exact.
7. Original relation/computation/invariant expressions remain semantic authority.
8. Basic `Apply` remains the cheapest mutation API and must not allocate causal result/evidence structures.
9. Runtime bootstrap emits no business mutation wave, policy callbacks, or artificial version increment.
10. Core remains free of EF Core, DI, logging, and transport dependencies.
11. Preserve Prepare/Commit version binding, final-domain-state validation, exception atomicity, durable identities, and resumable dispatch.
12. Projected edges compile into the same dependency DAG and participate in cycle detection.
13. Do not add public `.Using(...)` arity 3..8 families.
14. Public API changes require deliberate `PublicAPI.Unshipped.txt` updates.
15. Semantic core changes must pass net8.0 and net10.0 behavior suites.
16. Every task must leave the repository buildable; Task 34 must leave CI fully green.

---

# Task 34 — Restore green CI and roadmap truth

## 34A — Fix packed-package source resolution

Update `.github/workflows/ci.yml` so every package-consumer restore uses an absolute local source rooted at the workspace.

Preferred shape:

```bash
PACKAGE_SOURCE="$GITHUB_WORKSPACE/artifacts/packages"

dotnet restore tests/package-consumers/CoreNet8 \
  --source https://api.nuget.org/v3/index.json \
  --source "$PACKAGE_SOURCE"
```

Apply the same source to CoreNet10 and EfNet10.

Do not solve this by changing consumer project working directories or by referencing project outputs directly. These tests must continue to prove consumption of the packed `.nupkg`.

## 34B — Ensure the smoke programs exercise the new public surface

Before the end of Task 40, consumer programs must compile against packed packages and cover at least:

```text
Runtime seed API
projected derived API
CommitDetailed causal API
EF RelationUnitOfWork.CommitDetailed API
```

Task 34 only needs to restore the current smoke run; additional scenarios may land with their feature tasks.

## 34C — Roadmap bookkeeping

The Tasks 27–33 document is an implemented wave with acceptance gaps superseded by this plan. Update `docs/roadmaps/README.md` so Tasks 34–40 are the only active implementation roadmap.

### Acceptance gate

- build/tests/format/pack all pass;
- all three packed consumer restores/runs pass using the actual package artifact;
- CI is green before semantic Task 35 work is considered complete;
- roadmap index points to this plan.

Suggested commit:

```text
build: restore packed package smoke tests
```

---

# Task 35 — Enforce projected-target lifecycle and object-set integrity

## Goal

Make projected derived dependencies structurally safe before optimizing them.

For a projected edge:

```csharp
model.Derived(links)
    .Using(link => link.PurchaseOrderLine, availableQuantity)
```

`link.PurchaseOrderLine` must resolve to an object registered in the exact `ObjectSet<PurchaseOrderLine>` owning `availableQuantity`.

## 35A — Compile explicit projection metadata

Do not treat projection as merely an arbitrary delegate on `UpstreamDerivedInput`.

Introduce an internal model concept along these lines:

```text
ProjectedDerivedDependency
  DownstreamSet
  UpstreamDefinition
  UpstreamSet
  SelectorExpression
  SelectorPath
  CompiledSelector
```

Store the exact upstream source set from `upstream.Definition.SourceSet`.

## 35B — Restrict selector shape for alpha

The current public signature accepts any `Expression<Func<TSource,TUpstreamSource>>`. Before stabilization, require a completely tracked reference/member path.

Accept:

```text
link => link.PurchaseOrderLine
link => link.Invoice.PurchaseOrderLine
```

only if the existing navigation machinery can maintain the whole path correctly.

Reject or require `AllowIncompleteDependencies()`? For projected edges, prefer **rejecting** unsupported selector shapes because object-set integrity and reverse fan-out cannot be guaranteed. A projected edge with opaque selection must not claim normal freshness semantics.

If arbitrary nested paths make Task 36 unsafe, constrain alpha to a direct reference member instead of shipping a misleadingly broad API.

First-alpha null contract: projected target is non-null. Do not silently skip null and do not add nullable semantics in this wave.

## 35C — Validate final batch state

Extend mutation validation/simulation so projected-reference integrity is checked against the final logical state after all lifecycle/property mutations in the batch.

Required behavior:

```text
bootstrap link -> missing upstream target                 reject
add link -> missing upstream target                       reject
retarget link -> missing target                           reject
remove upstream target with surviving referencing link   reject
add target + add link in same batch                       allow
remove link + remove target in same batch                 allow
retarget link + remove old target in same batch           allow
same CLR type but target belongs to wrong ObjectSet       reject
```

Mutation order inside `MutationSet` must not change validity when final state is identical.

The domain object is already mutated when Prepare runs; use that final object graph plus simulated final set membership. Do not add caller orchestration.

## 35D — Prevent hidden upstream cache state

Even with model-level validation, add an internal defensive guard at projected upstream value access so a bug cannot create cache state for an object outside the upstream object set.

This guard should use runtime object-set membership, not CLR type.

## 35E — Lifecycle tests

Add focused tests for all cases above plus:

```text
removed target after bootstrap
projected downstream source removed before upstream mutation
multiple links to one target
one CLR type registered in two sets
failed projected-integrity Prepare leaves runtime unchanged
failed Commit due final-state drift leaves runtime unchanged
```

### Acceptance gate

There is no valid public execution path that leaves a registered projected downstream source referencing an unregistered upstream target.

Suggested commits:

```text
refactor: compile projected dependency metadata
fix: enforce projected dependency target integrity
```

---

# Task 36 — Maintain a reverse projection index

## Goal

Replace O(all downstream sources) propagation with O(actual projected fan-out).

Current behavior scans the entire downstream object set for every impacted upstream source. That must not become the alpha architecture.

## 36A — Internal index

Prefer reusing/generalizing `NavigationIndexRegistry` if it can naturally represent projection ownership. Otherwise introduce a dedicated internal `ProjectionIndexRegistry`.

Required mappings per projected edge:

```text
upstream target reference -> downstream source references
downstream source reference -> current upstream target reference
```

Use reference identity, consistent with the runtime's object membership semantics.

The index must be initialized and maintained for:

```text
runtime bootstrap
downstream source add
downstream source remove
projected reference retarget
tracked nested selector path changes, if nested projection is supported
rollback after failed Commit
```

## 36B — Indexed impact propagation

For an upstream impact set:

```text
{ poLineA, poLineB }
```

resolve downstream links directly from reverse buckets. Do not enumerate every `PurchaseOrderInvoiceLink`.

The same index should support lifecycle-integrity validation where appropriate, especially “cannot remove upstream target while surviving dependents reference it.”

## 36C — Remove DynamicInvoke from hot routing

Compile a typed or object-adapted selector once at model compilation/runtime construction. `DynamicInvoke` should not be used for normal projection routing or recomputation.

## 36D — Diagnostics

Add enough diagnostics to detect pathological projection fan-out, at minimum:

```text
projection edge count
reverse projection entry count
projected target count
```

Average/max fan-out are useful if they fit existing diagnostic style, but do not overbuild a metrics subsystem.

## 36E — Correctness oracle

Add tests comparing indexed projected propagation with a simple scan oracle over randomized operations:

```text
upstream property change
downstream add/remove
reference retarget
batched retarget + upstream change
batched add/remove
dirty/invalid severity
multiple projected downstream definitions
```

## 36F — Real benchmark

Replace the misleading benchmark gap with an actual projected-propagation benchmark.

Representative matrix:

```text
downstream sources: 10k, 100k
fan-out for changed upstream target: 1, 10, 100
operation: upstream property mutation -> propagation
```

Record latency + allocation and optionally compare an internal scan oracle if practical.

The acceptance target is architectural rather than a magic number: work should scale with impacted target fan-out, not total downstream object-set size.

### Acceptance gate

No normal projected upstream change performs a full downstream object-set scan.

Suggested commits:

```text
refactor: index projected dependency ownership
perf: propagate projected impacts by reverse fanout
bench: measure projected dependency propagation
```

---

# Task 37 — Finish decision-time causality and restore opt-in cost

## Goal

Causal output must be built from propagation evidence and must cost essentially nothing beyond normal semantics when causal detail is not requested.

## 37A — Add a causal capture mode to commit propagation

Thread an internal detail/capture flag from:

```text
Commit                 -> no causal capture
CommitDetailed Summary -> no causal capture
CommitDetailed Causal  -> causal capture
```

Do not make source classification run twice. The classifier still executes exactly once for semantics; when causal capture is enabled, record its result at that point.

## 37B — Store evidence per affected source

Evolve internal propagation result from definition-level snapshots plus direct evidence into source-scoped impact evidence containing conceptually:

```text
Definition
Source
FinalSeverity
EffectivePrecision
DirectEvidence[]
RelationEvidence[]
UpstreamEvidence[]
ReactionEvidence[]
```

Exact types/names are flexible. The public result should be a deterministic projection of this structure.

## 37C — Relation evidence with origin IDs

`RelationDependencyCause` must identify normalized root mutation origin IDs where known.

Capture cause kind at the relation routing/delta decision point:

```text
MembershipAdded
MembershipRemoved
RelatedItemChanged
ConservativeCandidate
```

One source can have multiple simultaneous relation causes. Keep all of them in deterministic order.

Do not infer them later from a single added/removed/fallback branch.

## 37D — Upstream evidence and projected precision

When downstream impact is inherited, capture:

```text
upstream definition
actual upstream source (important for projection)
upstream final severity
effective precision
```

Precision rule:

```text
Exact inherited through exact edge            -> Exact
Conservative upstream                         -> Conservative
fan-in containing any conservative cause      -> final impact may have Conservative cause/path
```

Delete recursive post-hoc `IsConservative(...)` inference when evidence carries the truth directly.

## 37E — Invariant reaction evidence

Record the actual input/output transition.

Examples:

```text
Dirty + ScheduleRepair -> Invalid   reaction escalation cause
Invalid + ScheduleRepair -> Invalid no fake Dirty -> Invalid statement
Dirty + MarkInvalid -> Invalid      reaction escalation cause
```

If policy action itself is useful to explain even without severity escalation, model that explicitly rather than lying about input severity.

## 37F — Normalize mutation metadata without unconditional public-causal allocation

The runtime must retain enough normalized mutation information for a later `CommitDetailed(Causal)` call after `Prepare`, especially collection Add/Remove/Reset.

Prefer storing the normalized intrinsic mutation forms already needed by commit, e.g. lifecycle/property/normalized collection records, rather than eagerly constructing a second `NormalizedMutationProvenance[]` solely for explanation.

Construct public `MutationOrigin` objects only in causal mode.

## 37G — Performance regression tests/benchmarks

Update basic/summary/causal benchmark after refactor.

Required assertions/bench evidence:

```text
basic Apply allocates no causal evidence lists/origins
Summary allocates no causal evidence lists/origins
Causal retains exact same runtime semantics as Summary
```

Add regression tests for:

```text
projected conservative chain
local Dirty + upstream Invalid
inherited Invalid + ScheduleRepair
Dirty + ScheduleRepair
multiple relation cause kinds
relation cause OriginIds
collection Add/Remove/Reset origins
normalized A->B->C property origin
causal result immutable after later commit
```

### Acceptance gate

Every causal claim comes from evidence captured at a runtime decision point, and non-causal APIs do not allocate causal evidence structures.

Suggested commits:

```text
refactor: capture causal evidence during propagation
fix: preserve projected causal precision and reaction semantics
perf: avoid causal allocation outside causal mode
```

---

# Task 38 — Add two-upstream projected composition

## Goal

Remove `.AllowIncompleteDependencies()` from the flagship procurement flow without blessing arbitrary constructors as safe.

Add exactly the projected composition shape the domain needs:

```csharp
var linkValidity = model.Derived(links)
    .Using(
        link => link.PurchaseOrderLine,
        availableQuantity,
        unitRate)
    .Compute((link, available, rate) =>
        link.ReservedQuantity <= available &&
        link.CapturedRate == rate);
```

Do not add public projected overloads for arities 3..8.

## Semantics

Both upstream definitions must belong to the same exact upstream source set selected by the projection. The compiled DAG receives two upstream edges sharing one projection mapping.

Required behavior:

```text
either upstream Dirty -> downstream Dirty unless stronger local/inherited policy
any upstream Invalid -> downstream Invalid
same wave changes to both upstreams merge once for downstream source
reference retarget updates both dependencies atomically
cycle detection still works
lazy recomputation fetches both upstream values for the selected target
```

Reuse one reverse projection index for the shared selector/target set where practical.

## Flagship sample

Rewrite the procurement sample to remove:

```text
PurchaseOrderDecisionInputs
.AllowIncompleteDependencies()
```

The sample must build under normal complete dependency validation and continue to demonstrate:

```text
ordered quantity increase -> Dirty
ordered quantity decrease -> Invalid
receipt cancellation -> received quantity Invalid -> link repair
price/unit-rate change -> link Invalid -> repair
persisted link remains its own object
```

Do not change `ExpressionDependencyAnalyzer.VisitNew` to treat arbitrary constructors as complete merely to make the sample pass.

### Acceptance gate

The end-to-end procurement sample contains no `AllowIncompleteDependencies()` and no duplicated PO/receipt calculation on the link object.

Suggested commits:

```text
feat: compose two projected upstream values
sample: remove incomplete tracking from procurement flow
```

---

# Task 39 — Bootstrap and recovery hardening for projected graphs

## Goal

Make the seed API a credible restart/recovery mechanism for the new cross-set model.

## 39A — Validate after complete seed collection

All seed entries should be collected and object-set membership/key uniqueness established before projected-reference integrity is accepted.

This permits seed order independence:

```text
seed links first, then PO lines    valid if final seed graph is valid
seed PO lines first, then links    same result
```

An invalid graph must fail before the caller receives a runtime.

## 39B — Initialize projection indexes as baseline state

Bootstrap must create the same projection reverse-index state that a live runtime would have after equivalent authoritative registration, without emitting policy actions or incrementing `Version`.

## 39C — Bootstrap equivalence tests

Compare a seeded runtime against a reference runtime representing the same authoritative objects for:

```text
exact relation queries
conservative relation queries
source-only derived values
projected one-upstream values
projected two-upstream values
invariants
first mutation after bootstrap
reference retarget after bootstrap
upstream removal validation after bootstrap
```

The expected differences are only bootstrap semantics such as `Version == 0` and absence of startup policy work.

## 39D — Diagnostics contract

Decide and document whether cumulative runtime diagnostics start at zero after bootstrap or include bootstrap construction work. Prefer zeroing mutation/work counters after seed construction so runtime diagnostics describe post-startup incremental activity, while structural index-size diagnostics still describe the initialized baseline.

## 39E — API ergonomics

If useful and low-churn, add a combined overload so diagnostics do not become mutually exclusive with seeding, e.g.:

```csharp
compiled.CreateRuntime(options, seed => { ... });
```

Do not add an overload matrix beyond what is needed.

## 39F — Measured startup baseline

Run the bootstrap benchmark after the projection index exists and record real results for 10k/100k object scenarios on a documented environment. Include memory allocation.

### Acceptance gate

A service can rebuild the runtime from authoritative PO/receipt/link objects with complete projected-reference validation, initialized projection indexes, no startup policy wave, and predictable post-bootstrap mutation behavior.

Suggested commits:

```text
test: harden projected runtime bootstrap
bench: record runtime bootstrap baseline
```

---

# Task 40 — First publishable-alpha gate

Do not add major new capability in this task. Close the contract and prove it.

## 40A — Cross-source randomized oracle

Add randomized/state-machine coverage spanning:

```text
upstream property changes
downstream direct property changes
downstream add/remove
upstream add/remove where valid
reference retarget
batched retarget + old target removal
multiple links per upstream target
Dirty/Invalid fan-in
one/two projected upstream values
exact/conservative upstream relation-derived values
```

Compare optimized indexed propagation against a simple authoritative scan/recompute oracle.

## 40B — Packed consumer proof

Packed NuGet consumers must compile and run representative public APIs from the package rather than project references:

```text
net8 core seed + projected dependency
net10 core CommitDetailed(Causal)
net10 EF RelationUnitOfWork.CommitDetailed
```

## 40C — Performance gates

Record and commit baselines for:

```text
basic vs summary vs causal apply
projected propagation at 10k/100k and fan-out 1/10/100
bootstrap at 10k/100k
```

Do not invent hard SLA numbers. Fail review if an architectural regression reintroduces O(all downstream) work for a single projected upstream impact or causal allocations into plain `Apply`.

## 40D — Documentation

Update README + architecture docs to state clearly:

```text
projected target must be registered in the exact upstream object set
projected target is non-null in the alpha contract
lifecycle batches are validated against final state
removing a target with surviving projected dependents is rejected
projection fan-out is reverse-indexed
bootstrap creates authoritative baseline without mutation/policy wave
causal detail is opt-in
```

Update the procurement document to describe the real persisted-link object and two projected upstream values.

## 40E — Public API / package review

Before publishing `0.1.0-alpha.1`:

```text
CI fully green
PublicAPI.Unshipped.txt changes reviewed intentionally
CHANGELOG matches actual behavior
no AllowIncompleteDependencies in flagship sample
no DynamicInvoke in projected hot path
no known lifecycle hole for projected dependencies
packed package consumer smoke tests green
benchmark results committed
roadmap status updated from active -> completed only after all gates pass
```

Keep publication manual; do not add automatic NuGet publishing in this wave.

### Acceptance gate

At the end of Task 40, the repository has a defensible first-alpha contract for cross-object-set dependency maintenance rather than merely a compiling projection feature.

Suggested commits:

```text
test: add randomized projected dependency oracle
build: expand packed alpha consumer coverage
bench: record projected alpha performance baselines
docs: stabilize projected dependency alpha contract
```

---

# Recommended commit sequence

Use small commits in approximately this order:

```text
1.  build: restore packed package smoke tests
2.  refactor: compile projected dependency metadata
3.  fix: enforce projected dependency target integrity
4.  refactor: index projected dependency ownership
5.  perf: propagate projected impacts by reverse fanout
6.  bench: measure projected dependency propagation
7.  refactor: capture causal evidence during propagation
8.  fix: preserve projected causal precision and reaction semantics
9.  perf: avoid causal allocation outside causal mode
10. feat: compose two projected upstream values
11. sample: remove incomplete tracking from procurement flow
12. test: harden projected runtime bootstrap
13. bench: record runtime bootstrap baseline
14. test: add randomized projected dependency oracle
15. build: expand packed alpha consumer coverage
16. docs: stabilize projected dependency alpha contract
```

Do not squash these into one implementation commit while the work is in progress. Each semantic step should be independently reviewable and should leave the repository buildable/testable.

---

# Final architectural target after this wave

The intended model is:

```text
ordinary C# domain expressions
        ↓
compiled semantic dependencies
        ↓
relation access/propagation plans
        ↓
source-scoped dependency DAG
        ↓
reverse-indexed projected ownership across object sets
        ↓
Dirty / Invalid consistency semantics
        ↓
invariants + durable repair requests
        ↓
optional truthful causal explanation
```

For the motivating procurement case:

```text
GoodsReceipt changes
      ↓
ReceivedQuantity (PO line)
      ↓
AvailableQuantity (PO line) ───────┐
                                   ├─ projected through Link.PurchaseOrderLine
UnitRate (PO line) ────────────────┘
                                   ↓
LinkValidity (persisted invoice/PO link)
                                   ↓
repair / rematch invariant
```

The important property is structural: callers report ordinary domain mutations. They do not manually orchestrate invalidation, refresh, link repair eligibility, or cross-object dependency propagation.