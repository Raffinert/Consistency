# Codex Implementation Plan — Remaining Tasks 10–13

Baseline: `main` at `824fbfeab77569c68faa44232626d01e687741e9`.

This plan supersedes the Task 10–13 sections of the archived
`docs/roadmaps/archive/codex-implementation-plan-after-safety-hardening.md`. Tasks 1–9 are considered
implemented at this baseline. Do not reopen them unless a regression is found while implementing the work below.

## Current verified state

The current model still has these structural constraints:

```text
Relation<TSource,TItem>
    -> Derived<TSource,TValue>
    -> Invariant<TSource>
```

`DerivedBuilder<TSource>` can only start from `.Using(relation)`. `IDerivedDefinition` owns exactly one `Relation`. `InvariantBuilder<TSource>` accepts exactly one derived value. Every relation consumed by derived state is forced into exact reverse propagation/materialization through `RequireExactPropagation()` / `EnableExactPropagation()`.

The core package targets `net8.0;net10.0`, but the only test project targets `net10.0` and also references the EF Core adapter. Large implementation files and historical roadmap files remain in place.

Implement in this order:

```text
10A  source-only derived values
10B  internal dependency DAG + cycle detection + topological propagation
10C  derived-on-derived composition
10D  multi-input invariants and selected multi-input derived APIs
10E  diagnostics/documentation/public-API stabilization
11A  introduce propagation-plan abstraction
11B  conservative propagation without permanent pair materialization
11C  planner rules, diagnostics, benchmarks, equivalence tests
12   split core/EF tests and run core behavior on net8 + net10
13   physical maintainability/documentation cleanup
```

Do not combine all of Task 10 into one commit. Each numbered subtask below should leave the repository buildable and testable.

---

# Global implementation rules

1. The original relation predicate remains semantic authority. Candidate/index/propagation plans may produce a superset, never false negatives.
2. Preserve `Dirty` versus `Invalid` semantics and source-scoped severity.
3. Preserve commit exception atomicity and Prepare/Commit domain-drift protection.
4. Preserve the separation between pure `RuntimeApplyResult` data and `PolicyDispatchHandle`.
5. Do not regress durable identity or resumable dispatch semantics.
6. Keep the core package free of EF Core and dependency-injection dependencies.
7. Prefer internal abstractions first. Add public API only when the usage shape is proven by tests/examples.
8. Update `PublicAPI.Unshipped.txt` deliberately for every public signature change.
9. Do not introduce additional access/optimizer strategies without measurements consistent with `docs/optimizer-policy.md`.
10. After each subtask run build, relevant tests, full tests, format verification, and package validation before moving on.

---

# Task 10 — Generalize dependency processing into a real DAG

## Goal

Move from a hard-coded relation -> derived -> invariant pipeline to a compiled dependency DAG while retaining typed public handles and source-scoped incremental invalidation.

The DAG is not a generic workflow engine. It only needs the node/edge semantics required by Raffinert.Relations.

## 10A — Add source-only derived values

### Required public behavior

Support:

```csharp
var lineAmount = model.Derived(poLines)
    .Compute(line => line.Quantity * line.UnitRate);
```

Changing `Quantity` or `UnitRate` must dirty/invalid the source according to the configured source severity. Unrelated member changes must not affect the value.

### Required internal refactor

The current `IDerivedDefinition` is relation-shaped:

```text
SourceSet
Relation
ComputationExpression
```

Refactor it so a derived definition is not required to own a relation. Introduce an internal input/dependency description rather than using nullable special cases throughout the runtime.

Recommended direction:

```text
IDerivedDefinition
  SourceSet
  Inputs[]
  ComputationExpression
  ImpactPolicy

DerivedInput
  SourceMembers
  Relation?              // relation-backed input
  UpstreamDerived?       // introduced in 10B/10C
```

Exact internal names are flexible. The important part is that `Relation` stops being a mandatory property of every derived definition.

For 10A it is acceptable for `Inputs` to contain only source dependencies and for relation-backed definitions to adapt to the same abstraction.

### Runtime behavior

Source-only state still uses the same Fresh/Dirty/Invalid cache model. `Get(derived, source)` recomputes from the source when not fresh. Source lifecycle add/remove must clear state exactly as relation-backed derived state does.

Do not fake source-only derived state by creating an artificial self-relation.

### Tests

Add focused tests for:

```text
source-only compute returns expected value
tracked source member mutation dirties it
SourceChanged(Invalid) invalidates it
unrelated member mutation does not affect it
recompute returns Fresh
source remove clears cache/state
same behavior under rollback/fault paths
```

### Commit boundary

Suggested commit:

```text
feat: support source-only derived values
```

### Acceptance gate

A derived value can exist with zero relation inputs, and no runtime code assumes every derived definition has a relation.

---

## 10B — Introduce explicit DAG metadata, cycle detection, and topological order

Do this before exposing derived-on-derived public overloads.

### Required compiled model

Build explicit edges among dependency nodes. At minimum represent:

```text
source/object-set member dependencies -> derived node
relation membership/item dependencies -> derived node
derived node -> downstream derived node
derived node -> invariant node
source member dependencies -> invariant node
```

The existing member/relation adjacency maps remain useful, but they should become seed indexes into a real compiled graph instead of encoding the graph shape implicitly.

Recommended internal concepts:

```text
DependencyNodeId
CompiledDependencyNode
CompiledDependencyEdge
CompiledDependencyGraph
```

Node identity may remain internal ordinal identity. Do not expose a second public identity system; durable `DefinitionKey` remains the cross-process identity.

### Build-time validation

`RelationModelBuilder.Build()` must reject cycles before creating a runtime.

Examples to reject:

```text
A -> A
A -> B -> A
A -> B -> C -> A
```

The exception should identify the participating derived definition keys when available; otherwise include useful expressions/types.

Compile deterministic topological order. Declaration order must not change semantics.

### Runtime propagation model

Use a source-scoped work queue:

```text
mutation/relation delta
  -> seed impacted nodes
  -> merge impact by (node, source)
  -> strongest severity wins for that source
  -> process node once after its upstream wave is settled
  -> enqueue downstream nodes
```

Requirements:

- no unrelated graph branches visited;
- a node/source is not repeatedly recomputed/marked for each incoming edge in a diamond;
- `Invalid` dominates `Dirty` for the same node/source;
- different sources retain independent severity;
- topological order ensures downstream state sees settled upstream state;
- rollback snapshot selection remains scoped to touched graph nodes.

Do not recompute dirty values eagerly merely because they are in the graph. Preserve lazy derived semantics unless an invariant reaction explicitly requires evaluation.

### Diagnostics

Add internal/runtime counters if useful for tests/benchmarks:

```text
nodes seeded
nodes visited
node/source impacts merged
```

Do not add public diagnostics solely to make tests easy unless they have user value.

### Tests

Add graph-shape tests:

```text
linear chain A -> B -> C
fan-out A -> B,C
diamond A -> B,C -> D
multiple unrelated branches
cycle rejection
strongest-severity merge in diamond
source-scoped merge for two sources
source removal cleanup through downstream nodes
rollback restores touched DAG state
```

### Commit boundary

Suggested commit:

```text
refactor: compile dependency propagation as a DAG
```

### Acceptance gate

The runtime has an explicit acyclic graph and topological propagation mechanism even before public derived-on-derived composition is enabled.

---

## 10C — Add derived-on-derived composition

### Minimum public API

Support at least one-upstream derived composition cleanly:

```csharp
var netQuantity = model.Derived(poLines)
    .Using(receivedQuantity)
    .Compute((line, received) => line.Quantity - received);
```

Then prove two-upstream composition:

```csharp
var availableQuantity = model.Derived(poLines)
    .Using(receivedQuantity, reservedQuantity)
    .Compute((line, received, reserved) =>
        line.Quantity - received - reserved);
```

Do not create overloads for 2..8 inputs as the underlying architecture. Internally use a collection of upstream definitions. Public convenience overloads for one and two inputs are enough for this phase.

### Semantics

When an upstream derived value becomes Dirty/Invalid:

- downstream receives a source-scoped impact;
- severity is merged with impacts from its own source dependencies and other upstream values;
- downstream remains lazy;
- when downstream is requested, upstream values are made fresh first through their runtime state access;
- one source's invalidity must not poison another source.

Avoid accidental repeated recomputation in diamonds. Requesting D in `A -> B,C -> D` should recompute each stale upstream node for the source at most once in that access chain.

### Incremental computation

Existing relation aggregate incremental plans are relation-specific. Do not generalize them prematurely.

Rules for this phase:

```text
relation-backed standalone aggregate -> existing incremental plan may remain
derived-on-derived computation -> full recompute unless a future measured planner is added
source-only computation -> full recompute
```

Keep incremental computation orthogonal to DAG dependency propagation.

### Tests

Cover:

```text
source-only -> derived chain
relation-derived -> derived chain
two upstream derived values
diamond graph recomputation correctness
Dirty + Invalid upstream merge
upstream recompute failure leaves downstream non-fresh
unrelated source does not propagate
compiled graph declaration-order independence
```

### Commit boundary

Suggested commit:

```text
feat: compose derived values through dependency DAG
```

### Acceptance gate

Derived values can depend on other derived values without artificial relations and propagate source-scoped freshness correctly.

---

## 10D — Add multi-input invariants and selected mixed inputs

### Required public behavior

Support an invariant over at least two derived values:

```csharp
var invariant = model.Invariant(poLines)
    .Using(receivedQuantity, orderedQuantity)
    .Must((line, received, ordered) => received <= ordered);
```

The invariant must react when either upstream changes and merge severity per source.

A source-member dependency inside the predicate must also be tracked normally.

### Mixed derived inputs

If the internal model from 10B/10C naturally supports it, allow a derived definition to combine source members with upstream derived values. That is the normal case and should not require a separate artificial input type.

Do not add arbitrary relation + derived + external-token combinations until there is a concrete use case and tests.

### Invariant runtime refactor

The current invariant state is constructed with one `IDerivedRuntimeState`. Replace that with an input resolver/accessor collection so invariant state does not encode single-derived ownership.

The invariant definition should expose upstream derived definitions as a collection.

### Policy semantics

Preserve existing reactions:

```text
EvaluateImmediately
MarkDirty
MarkInvalid
ScheduleRepair
```

Immediate evaluation must first obtain fresh required upstream values for that source. If upstream recomputation throws, do not mark the invariant valid/violated from partial data.

### Tests

Cover each reaction with two upstream values, plus:

```text
one upstream Dirty, one Invalid
both upstream changed in same MutationSet
same invariant reached through a diamond
repair request emitted once per invariant/source
immediate evaluation occurs after committed state
```

### Commit boundary

Suggested commit:

```text
feat: support multi-input invariants
```

### Acceptance gate

Invariant correctness is no longer structurally limited to one derived value.

---

## 10E — Stabilize diagnostics, docs, and public API

Only after 10A–10D behavior is proven.

Update:

```text
CompiledModelDiagnostics
DebugView
README examples
docs/architecture.md
PublicAPI.Unshipped.txt
randomized/full-graph tests
```

Diagnostics should describe upstream dependency definitions without assuming one `RelationId` / one `DerivedId` per consumer. Preserve existing fields only if they remain truthful; because the package is still alpha/unshipped, prefer correcting the model now over carrying misleading compatibility fields.

Add an end-to-end purchasing example such as:

```text
ReceivedQuantity  <- goods-receipt relation
ReservedQuantity  <- reservation relation or source-only placeholder
AvailableQuantity <- OrderedQuantity - ReceivedQuantity - ReservedQuantity
Invariant         <- ReceivedQuantity <= OrderedQuantity
```

### Task 10 final acceptance gate

All of the following must be true:

```text
source-only derived supported
derived-on-derived supported
multi-input invariant supported
cycles rejected at Build
topological propagation explicit
source-scoped severity preserved
unrelated branches not visited
lazy recomputation preserved
relation incremental aggregate behavior not regressed
rollback/fault tests pass
DebugView/diagnostics describe the new graph truthfully
```

---

# Task 11 — Separate relation query plans from propagation plans

## Goal

Using a relation in derived state must no longer automatically imply permanent exact pair materialization.

Current coupling to remove:

```text
derived uses relation
 -> RequireExactPropagation()
 -> ReverseAccessPlan enabled
 -> runtime EnableExactPropagation()
 -> _rightsByLeft / _leftsByRight retained
```

Query access and change propagation are different concerns.

## 11A — Introduce `RelationPropagationPlan`

Keep existing query-side `RelationAccessPlan` unchanged.

Introduce an internal compiled propagation abstraction, for example:

```text
RelationPropagationPlan
  ExactMaterialized
  ConservativeInvalidation
```

Public configuration is not required initially. First make the internal distinction real and visible in diagnostics.

`ReverseAccessPlan` must no longer be used as a proxy for “materialization enabled.” A reverse lookup strategy may be useful without retaining exact pair membership.

Add explicit runtime properties such as:

```text
PropagationPlan
RetainsPairMembership
SupportsExactMembershipDelta
```

Exact naming is flexible.

### Acceptance gate

A relation can have a reverse candidate lookup without necessarily retaining all matching pairs.

---

## 11B — Implement conservative propagation safely

### Correctness rule

Conservative propagation may produce false positives but never false negatives.

For a full-recompute derived value, exact added/removed pair deltas are often unnecessary. It is sufficient to identify a safe superset of source objects whose relation result may have changed and mark them Dirty/Invalid.

### Required cases

#### Left/source object changed

The affected left source is known directly. Reevaluate/invalidate that source only.

#### Right object added

Use reverse candidate access when available to identify left sources that could now match. Apply the original predicate when safe/useful; otherwise the candidate superset is acceptable.

If no safe reverse candidate plan exists, conservatively affect all registered left sources.

#### Right object removed

Without retained old pair membership, the current domain state may not tell which lefts matched the removed right. Use old-key/change information if available. If exact routing cannot be proven, conservatively affect all registered left sources.

#### Right property changed

For index/key changes, route from both old and new candidate keys where the change information permits it. This is important: routing only by the new key can miss sources whose old match disappeared.

For residual/opaque semantics where safe routing is unavailable, fall back to all left sources.

#### Relation predicate dependencies through navigation

Use existing navigation/root resolution to find candidate relation roots. If old navigation state is needed for removals/replacements, use prepared change information and/or maintained navigation metadata. Never infer a narrow set from only the post-change graph when that could miss an old match.

### Severity

The propagation plan determines *which sources might be affected*, not business severity. Continue using `DerivedImpactPolicy` / dependency policy to classify Dirty vs Invalid.

### Incremental aggregates

Any derived computation requiring exact membership deltas (`Count`, `Sum`, etc. incremental update path) must use `ExactMaterialized` unless another exact strategy is explicitly implemented and tested.

### Tests

For every relation change category compare:

```text
ExactMaterialized result
ConservativeInvalidation + lazy full recompute result
fresh-from-scratch scan oracle
```

Include:

```text
left add/remove/change
right add/remove/change
old key -> new key move
residual predicate change
navigation reference replacement
collection/reset-driven impact
batch MutationSet with add/remove/change together
```

The conservative plan may invalidate more sources, but after recomputation observable values must equal the oracle.

### Acceptance gate

A non-incremental derived value can use a dense relation without storing every matching pair solely for dependency propagation.

---

## 11C — Planner rules, diagnostics, and benchmarks

### Initial deterministic planner

Use simple compile-time rules, not runtime auto-switching:

```text
relation used by recognized incremental aggregate
    -> ExactMaterialized

consumer explicitly needs exact pair deltas
    -> ExactMaterialized

full-recompute-only derived consumer
    -> ConservativeInvalidation candidate

multiple consumers where any requires exact
    -> ExactMaterialized
```

If correctness cannot be guaranteed for conservative routing, choose a broader conservative invalidation set, not exact materialization by default unless memory/performance evidence justifies it.

Do not make density thresholds silently change semantics/plans at runtime.

### Diagnostics

Expose separately:

```text
Query access plan
Reverse candidate plan
Propagation plan
Pair membership retained: yes/no
Reason for exact materialization (e.g. incremental aggregate consumer)
```

Update density warnings so they only apply to plans that actually retain pair membership.

### Benchmarks

Add a dense-relation benchmark comparing:

```text
ExactMaterialized memory / mutation time
ConservativeInvalidation memory / mutation time
lazy recomputation cost after mutation
```

Vary fan-out/density and read-after-write frequency. Record results in a Markdown benchmark result file.

The goal is not to prove conservative is always faster. The goal is to quantify the memory/recompute trade-off.

### Task 11 final acceptance gate

Query and propagation plans are independently represented, full-recompute consumers can avoid permanent pair materialization, incremental aggregate consumers retain exact semantics, and scan/oracle equivalence tests show no false negatives.

---

# Task 12 — Restructure tests so core behavior runs on .NET 8 and .NET 10

## Current problem

The core library targets:

```text
net8.0;net10.0
```

but `tests/Raffinert.Relations.Tests` targets only `net10.0` because it also references the .NET 10 EF adapter and EF 10 packages.

Building the core for net8 is not enough; its behavior must execute under net8.

## Required project structure

Preferred split:

```text
tests/
  Raffinert.Relations.Tests/
    TargetFrameworks: net8.0;net10.0
    references core only

  Raffinert.Relations.EntityFrameworkCore.Tests/
    TargetFramework: net10.0
    references core + EF adapter
    EF Core InMemory/Sqlite packages
```

Move at least:

```text
EntityFrameworkCoreAdapterTests.cs
EntityFrameworkCoreSqliteTests.cs
```

into the EF test project. Search all test files for EF namespaces/packages and move any additional EF-specific scenarios too.

Do not use conditional compilation in one giant test project merely to avoid the split.

## Shared test domain code

If both projects need common PO/Invoice test models, either:

- keep a very small linked/shared test source file, or
- duplicate tiny fixtures when that is clearer.

Do not create a new published/shared production assembly just for tests.

## CI

Install both runtimes/SDKs explicitly:

```yaml
dotnet-version: |
  8.0.x
  10.0.x
```

Run explicit behavioral gates so failures are obvious:

```text
dotnet test tests/Raffinert.Relations.Tests/... -c Release -f net8.0
dotnet test tests/Raffinert.Relations.Tests/... -c Release -f net10.0
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/... -c Release -f net10.0
```

Keep solution build/format/pack gates too.

If total CI time becomes excessive, restore/build once where practical, but do not remove either runtime execution.

## Compatibility tests

Ensure Task 10 DAG behavior and Task 11 propagation-plan behavior are part of the multi-target core tests. Do not leave the newest architecture tests net10-only accidentally.

## Documentation

Update README from:

```text
Tests and benchmarks run on .NET 10
```

to accurately state that core tests execute on .NET 8 and .NET 10, while EF tests/benchmarks may remain .NET 10.

## Acceptance gate

CI executes core behavioral tests successfully on both net8.0 and net10.0, and EF-specific tests remain isolated on net10.0.

Suggested commit:

```text
build: run core behavior tests on net8 and net10
```

---

# Task 13 — Maintainability and documentation cleanup

Do this after Tasks 10–12 so the physical split reflects the final architecture rather than being immediately invalidated by it.

This task should be behavior-preserving except for documentation corrections.

## 13A — Split implementation files by responsibility

Current large files include roughly:

```text
Runtime.cs                    ~73 KB
NavigationAndImpact.cs        ~25 KB
DerivedState.cs               ~22 KB
DependencyGraphRuntime.cs     ~21 KB
PolicyActions.cs              ~18 KB
```

Use folders/files to make architectural boundaries visible without changing public namespaces.

Recommended direction after DAG work:

```text
Model/
  CompiledRelationModel.cs
  RelationModelBuilder.cs
  ObjectSetDefinition.cs
  DerivedDefinitions.cs
  InvariantDefinitions.cs
  CompiledDependencyGraph.cs

Runtime/
  RelationRuntime.cs
  MutationPreparation.cs
  MutationCommit.cs
  RuntimeRollbackState.cs
  ObjectSetRuntime.cs
  RelationRuntimeState.cs

Dependencies/
  DependencyGraphRuntime.cs
  DependencyWorkQueue.cs
  NavigationIndexRegistry.cs
  ImpactResolver.cs

Planning/
  RelationAccessPlans.cs
  RelationPropagationPlans.cs
  DerivedComputationPlans.cs

Policies/
  PolicyActions.cs
  DurablePolicyIdentity.cs

Diagnostics/
  CompiledDiagnosticsBuilder.cs
  RuntimeDiagnostics.cs
  DebugViewRenderer.cs
```

Treat this as a responsibility map, not a mandatory folder taxonomy. Prefer cohesive types over hitting arbitrary file-size limits.

Do not change namespaces solely because files moved.

## 13B — Split oversized test fixtures

In particular, split `DerivedStateTests.cs` by behavior, for example:

```text
DerivedSourceTests
DerivedRelationTests
DerivedDagTests
IncrementalAggregateTests
InvariantPolicyTests
```

Keep scenario naming descriptive. Do not create inheritance-heavy test fixture frameworks.

## 13C — Archive historical roadmaps

Move superseded root planning documents under:

```text
docs/roadmaps/archive/
```

including the old root `Raffinert.Relations-*.md` roadmap/implementation-plan documents that are no longer current.

Add:

```text
docs/roadmaps/README.md
```

with:

```text
Current implementation plan: ../codex-plan-tasks-10-13.md
Architecture review: ../architecture-review-after-safety-hardening.md
Archive: historical context only; not current instructions
```

Preserve history via moves rather than deleting useful documents.

Search README/docs for links to moved files and fix them.

## 13D — Correct stale documentation found during verification

README currently explains canonical durable identity correctly near the outbox example, but later says `SourceIdentity.IsDurable` is true only when the object set is named. Correct that statement: durability also requires a canonically representable source key and, for a durable policy request, a stable `DefinitionKey`.

After Tasks 10–11 also remove text that assumes:

```text
one relation per derived value
all derived relations use ExactPropagation
one derived value per invariant
```

Update `docs/architecture.md`, README Implemented/Further Work, and DebugView examples accordingly.

## 13E — Final architecture verification

Run:

```text
restore
Release build
net8 core tests
net10 core tests
net10 EF tests
randomized graph equivalence tests
format --verify-no-changes
pack
package validation
```

Review public API baseline intentionally.

Search for stale terms/contracts:

```text
RequireExactPropagation
ExactPropagation used as materialization+propagation synonym
IDerivedDefinition.Relation singular assumptions
IInvariantDefinition.Derived singular assumptions
old RuntimeApplyResult.DispatchPolicies examples
old SourceIdentity durability wording
```

Some internal compatibility names may remain if justified, but docs and public contracts must describe current behavior accurately.

Suggested cleanup commits:

```text
refactor: split runtime implementation by responsibility
test: organize dependency graph scenarios
docs: archive superseded roadmaps and refresh architecture docs
```

## Task 13 acceptance gate

A new contributor can locate model compilation, runtime mutation, DAG propagation, relation planning, policy integration, and diagnostics without reading a 70 KB catch-all file; current documentation has one obvious active plan; and no docs describe superseded identity/propagation/DAG constraints.

---

# Required regression matrix before declaring Tasks 10–13 complete

The final implementation must retain all earlier guarantees while adding the new architecture.

```text
[ ] relation predicate remains semantic authority
[ ] hash/scan query equivalence remains green
[ ] exact/conservative propagation both converge to scan oracle
[ ] Dirty/Invalid source-scoped severity remains precise
[ ] failed Commit leaves runtime-owned state unchanged
[ ] Prepare/Commit domain drift remains detected
[ ] durable policy identity round-trips canonically
[ ] dispatch remains resumable after callback failure
[ ] RuntimeApplyResult remains data-only
[ ] EF DB-success/runtime-sync-failure remains distinguishable
[ ] source-only derived works
[ ] derived-on-derived works
[ ] multi-input invariant works
[ ] dependency cycles rejected at Build
[ ] diamond graph does not duplicate source work/recomputation
[ ] source removal cleans downstream state
[ ] incremental aggregates still receive exact membership semantics
[ ] dense full-recompute relations can avoid pair materialization
[ ] core behavioral suite passes on net8.0
[ ] core behavioral suite passes on net10.0
[ ] EF suite passes on net10.0
[ ] randomized optimized-vs-oracle tests cover DAG and propagation modes
[ ] docs/public diagnostics reflect actual graph and propagation plans
[ ] historical roadmaps are clearly archived
```

---

# Recommended commit sequence

Keep commits small enough to review/revert independently:

```text
1. feat: support source-only derived values
2. refactor: compile dependency propagation as a DAG
3. feat: compose derived values through dependency DAG
4. feat: support multi-input invariants
5. docs: describe composed dependency graph
6. refactor: separate relation propagation from query planning
7. feat: add conservative relation propagation
8. bench: compare exact and conservative propagation
9. build: run core behavior tests on net8 and net10
10. refactor: split runtime implementation by responsibility
11. test: organize dependency graph scenarios
12. docs: archive superseded roadmaps and refresh architecture docs
```

Do not squash these into one giant architecture commit during implementation. The intermediate states should remain buildable so regressions can be bisected.

# Final completion criterion

Tasks 10–13 are complete when Raffinert.Relations is no longer structurally tied to `relation -> derived -> invariant`, query strategy is independent from propagation/materialization strategy, supported core behavior is executed under every advertised target framework, and repository structure/documentation reflects the resulting architecture without weakening any safety guarantees established by Tasks 1–9.
