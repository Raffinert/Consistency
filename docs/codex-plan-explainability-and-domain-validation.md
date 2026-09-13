# Codex Implementation Plan — Domain-Semantic Severity, Impact Explainability, and Alpha Validation

Baseline: `main` at `e7a6c2b6b89d7257ec58a32c1fc00bd6115c8c77`.

The post-DAG hardening wave is complete at this baseline. The runtime now has deterministic topological propagation, exact/conservative relation propagation, selective conservative routing, source-only and composed derived values, multi-input invariants, randomized equivalence coverage, multi-target core tests, modularized runtime/derived code, and refreshed benchmark baselines.

This plan is the next active execution roadmap. Its purpose is **not** to start another optimizer wave. The next value is to make domain correctness policies expressive enough for real business rules, make committed impact explainable, and prove the API against an executable procurement scenario before the first serious alpha package is stabilized.

Implement in this order:

```text
20  Fix alpha runtime/API contract inconsistencies and separate basic vs detailed apply cost
21  Make one commit's propagation output explicit and ephemeral
22  Add value-sensitive source-member severity
23  Add internal impact-causality graph
24  Expose opt-in causal results and prove correctness/performance
25  Build an executable PO / GR / link-validity vertical slice
26  Stabilize package-consumer and alpha release contracts
```

Do not start pre-mutation simulation, distributed execution, another relation optimizer family, or derived-fed relation predicates during this roadmap unless Task 25 produces a concrete model that cannot be represented correctly without them.

---

# Verified starting state

The previous roadmap is complete and retained as history in `docs/codex-plan-tasks-10-13.md`.

Verified current capabilities:

```text
relation predicate remains semantic authority
scan/hash access plans
exact/conservative propagation separated from query access
old/new candidate routing for conservative key moves
zero-pair conservative propagation
source-only derived values
one/two-upstream composed derived values
compiled dependency DAG + cycle rejection
topological source-scoped propagation
Dirty / Invalid monotonic severity
single/two-input invariants
incremental Count / LongCount / Any / numeric Sum
Prepare -> Commit -> Dispatch
commit rollback of runtime-owned state
durable policy identity
resumable post-commit dispatch
EF Core 10 adapter
core tests on net8.0 and net10.0
EF tests on net10.0
randomized DAG + propagation equivalence
post-hardening performance baselines
```

Current baseline CI is green.

Verification also found three areas that should be addressed before adding product-oriented explainability:

1. Runtime query registration checks are inconsistent: `Get(...)` and relation queries require a registered source, while derived `GetState(...)`, invariant `Evaluate(...)`, and invariant `GetState(...)` currently do not.
2. Ordinary `Apply(...)` currently goes through `ApplyDetailed(...)`, paying to construct detailed impact/request result objects even when the caller only wants `ChangeImpact`.
3. The public `Conservatively()` name sounds like a guaranteed selected relation plan, while compilation treats it as a consumer preference: another consumer or exact semantic requirement can legitimately promote the relation to exact propagation.

A more important domain expressiveness gap is also now visible: `SourceChanged(...)` is one fixed severity for all direct source-member changes. It cannot express asymmetric rules such as:

```text
PO quantity decreases -> Invalid immediately
PO quantity increases -> Dirty / deferred recomputation
```

That capability is required before Raffinert.Relations can accurately model the motivating PO / invoice / goods-receipt correctness cases.

---

# Global implementation rules

1. The original relation/computation/invariant expressions remain semantic authority.
2. `Invalid` dominates `Dirty`; no downstream policy may weaken inherited `Invalid` to `Dirty`.
3. Conservative propagation may over-invalidate but must never create false negatives.
4. Derived recomputation stays lazy unless a configured invariant reaction explicitly evaluates it.
5. Domain objects are already mutated when runtime changes are reported. Do **not** pretend current APIs are pre-mutation simulation APIs.
6. Preserve Prepare/Commit version checks, domain-drift checks, exception atomicity, durable identities, and resumable dispatch.
7. `RuntimeApplyResult` remains data-only. Do not put callbacks/runtime references into causal result data.
8. Core remains dependency-free from EF Core, DI, logging frameworks, and transport libraries.
9. Do not add 3..8 generic `.Using(...)` overload families. The internal graph must remain arbitrary-edge-capable without exploding the public API.
10. Do not introduce derived values directly into relation predicates in this roadmap. Model downstream link validity through composed derived state/invariants first; only reopen the relation model if Task 25 proves a real expressiveness failure.
11. No new access-plan strategy without benchmark evidence and `docs/optimizer-policy.md` admission criteria.
12. Every semantic core change must pass both net8.0 and net10.0 behavior suites.
13. Public API changes must deliberately update `PublicAPI.Unshipped.txt`.
14. Keep commits reviewable and bisectable; each task should leave `main` buildable.

---

# Task 20 — Fix alpha runtime/API contracts and basic-path cost

## 20A — Make registration requirements consistent

Current behavior is inconsistent:

```text
Related(...)             -> validates registration
RelatedFromRight(...)    -> validates registration
Get(derived, source)     -> validates registration
GetState(derived, source)-> currently does not
Evaluate(invariant, src) -> currently does not
GetState(invariant, src) -> currently does not
```

An invariant must never create cache state for an arbitrary source object that is outside its declared object set.

Introduce one internal registration guard and use it consistently for all public operations whose source is defined by an object set.

Preferred internal shape:

```csharp
private void EnsureRegistered(IObjectSetDefinition set, object source, string role)
```

or an equivalent helper that produces consistent exceptions.

Required tests:

```text
GetState(derived) rejects unregistered source
Evaluate(invariant) rejects unregistered source
GetState(invariant) rejects unregistered source
removed source is rejected after removal
same CLR object type registered in a different object set is rejected
valid registered source behavior is unchanged
failed query does not create derived/invariant state entries
```

Do not silently register objects as a convenience.

## 20B — Stop ordinary `Apply(...)` from building detailed result data

Current basic apply path is effectively:

```text
Apply
 -> ApplyDetailed
 -> Prepare
 -> Commit
 -> build RuntimeApplyResult arrays/identities
 -> Dispatch
 -> return ChangeImpact
```

For the common path, detailed result construction is unnecessary.

Refactor to make the basic path explicit:

```text
Apply
 -> Prepare
 -> Commit
 -> Dispatch
 -> return ChangeImpact
```

`ApplyDetailed(...)` should continue to construct immutable structured result data and return a separate dispatch handle.

Do not duplicate mutation semantics. Both paths must share Prepare/Commit and policy-action behavior.

Add focused equivalence tests:

```text
Apply and ApplyDetailed produce identical committed runtime state
same invariant reaction behavior
same callback order
same callback failure semantics
same runtime Version progression
```

Add a BenchmarkDotNet comparison for representative 1/10/100-change batches:

```text
Apply
ApplyDetailed + Dispatch
```

Record allocations and latency. The purpose is a regression baseline, not a promise that `ApplyDetailed` is cheap.

## 20C — Make propagation preference naming truthful

`Conservatively()` is a request from one consumer, not necessarily the final relation-wide propagation plan. Compilation may choose exact propagation because:

```text
another consumer requires exact deltas
this consumer uses an incremental computation plan
configured membership severity requires precise added/removed semantics
```

Because the public API is still unshipped, prefer renaming:

```csharp
.Conservatively()
```

to:

```csharp
.PreferConservativePropagation()
```

The method should remain a preference, not throw merely because the final relation plan becomes exact. Exact propagation is a safe promotion.

Update README, architecture docs, examples, tests, diagnostics language, and the public API baseline.

Also correct the `Incrementally()` XML documentation: this is an opt-in to **exact incremental maintenance** for recognized aggregate expressions, not “conservative incremental planning.”

If a significantly better name emerges while implementing, it is acceptable, but the public contract must clearly distinguish:

```text
consumer preference
vs
compiled relation plan
```

### Task 20 acceptance gate

- all public runtime queries/evaluations enforce object-set registration consistently;
- ordinary `Apply` does not allocate detailed result structures merely to return `ChangeImpact`;
- basic/detailed paths have equivalent semantics;
- public propagation configuration does not imply a guarantee the compiler does not make;
- benchmark baseline is recorded.

Suggested commits:

```text
fix: enforce source registration across runtime queries
perf: avoid detailed result construction in basic apply
refactor: clarify conservative propagation preference
```

---

# Task 21 — Make commit propagation output explicit and ephemeral

## Goal

Prepare the runtime for explainability without making “last mutation” data another hidden mutable subsystem.

Today relation impacts are retained in `LastRelationImpacts`, and dependency nodes retain transient `DirtySources` / `InvalidSources` plus `_previousDerived` / `_previousInvariants` so `CreateDetailedApplication(...)` can query the latest wave afterward.

For causal reporting, make one commit's output an explicit value produced by commit propagation.

## Required internal result

Introduce an internal immutable result for one dependency wave, for example:

```text
DependencyPropagationResult
  DerivedImpacts
  InvariantImpacts
  [later: causal edges]
```

and extend the internal commit result conceptually to:

```text
RuntimeCommitResult
  ChangeImpact
  RelationImpacts
  DependencyPropagationResult
  RuntimePolicyActions
```

Exact type names are flexible.

`DependencyGraphRuntime.ApplyChangeImpacts(...)` should return the wave result. `CreateDetailedApplication(...)` should consume the returned commit result rather than interrogating mutable “last wave” state.

## Reduce persistent transient state

Prefer local per-wave source/severity maps used during propagation:

```text
(node, source) -> final severity for this wave
```

Runtime nodes still own actual cached value/evaluation state, but mutation-result bookkeeping should not live beyond the commit unless required for rollback of real runtime state.

Remove or reduce these historical-result mechanisms when possible:

```text
GetDerivedImpacts()
GetInvariantImpacts()
_previousDerived
_previousInvariants
node DirtySources/InvalidSources retained only to answer later result queries
```

Do not force a risky rewrite merely to delete fields. The acceptance criterion is that the detailed public result is derived from an explicit commit result, not from mutable global “last result” state.

`LastRelationImpacts` may remain temporarily for internal tests/diagnostics, but `RuntimeApplyResult` construction must not depend on it.

## Rollback

Rollback snapshots should capture runtime-owned state that can actually be mutated during commit. A failed commit must not leave a partially populated propagation-result object observable to the caller.

## Required tests

```text
ApplyDetailed result remains unchanged after a later mutation
consecutive commits cannot leak previous derived/invariant impacts
empty/unrelated next wave does not repeat old impacts
failed Commit restores caches and publishes no commit result
rollback after downstream DAG impact restores all touched state
summary data ordering remains deterministic
basic Apply still avoids detailed result construction
```

### Task 21 acceptance gate

One commit produces one explicit immutable relation/dependency/policy result. Public detailed data is built from that result and is stable after later runtime mutations.

Suggested commit:

```text
refactor: make dependency propagation return explicit wave results
```

---

# Task 22 — Add value-sensitive source-member severity

## Business problem

A single fixed `SourceChanged(DependencySeverity)` cannot represent many correctness rules.

The motivating example is asymmetric:

```text
OrderedQuantity 10 -> 8
    may invalidate an existing allocation/link immediately

OrderedQuantity 8 -> 10
    may only make additional capacity available and rematching may be deferred
```

The first should be able to produce `Invalid`; the second `Dirty`.

This policy belongs in the dependency model, not in every caller reporting a mutation.

## 22A — Internal member-specific classifier model

Extend derived impact policy with optional direct-source member rules.

A rule must identify a tracked **direct source member** and classify its normalized old/new transition.

Conceptually:

```text
SourceMemberImpactRule
  Member
  Classify(oldValue, newValue) -> DependencySeverity
```

Keep the existing fixed source severity as the fallback for source dependencies without a specific rule.

Do not use raw member names as identity internally; use `MemberInfo`/compiled member metadata consistent with the existing dependency analyzer.

Initial scope: direct source members only. Nested source paths may continue to use the fixed fallback unless there is an obviously safe extension during implementation.

## 22B — Typed public configuration

Avoid exposing `MemberInfo` and object casts to normal users.

Preferred direction is a typed source-aware builder, for example:

```csharp
var available = model.Derived(poLines)
    .Using(receivedQuantity)
    .Impact(policy => policy
        .SourceChanged(DependencySeverity.Dirty)
        .SourceMemberChanged(
            line => line.OrderedQuantity,
            (oldValue, newValue) => newValue < oldValue
                ? DependencySeverity.Invalid
                : DependencySeverity.Dirty))
    .Compute((line, received) => line.OrderedQuantity - received);
```

A generic `DerivedImpactPolicyBuilder<TSource>` is acceptable because the API is unshipped. If there is a cleaner typed sub-builder that avoids unnecessary public churn, use it.

Do not expose a generic “classify any PropertyChange” callback as the first API; that loses type safety and makes policies harder to understand.

## 22C — Semantics

Required rules:

```text
normalized transition is classified once
multiple changed source members merge by strongest severity
Invalid > Dirty
inherited upstream Invalid cannot be downgraded by a local Dirty rule
member rule applies only when that member is actually a dependency of the computation
unrelated member rules must not manufacture dependency edges
rule exception aborts Commit and rollback restores runtime-owned state
```

Repeated property changes already normalize `A -> B`, `B -> C` to `A -> C`; classification must use the normalized transition.

For a mutation batch where two direct dependencies change:

```text
member A -> Dirty
member B -> Invalid
```

the source's final direct severity is `Invalid`.

The classifier is part of deterministic model semantics and executes during commit. Document that it must be side-effect-free. Do not invoke it during post-commit dispatch.

## 22D — Diagnostics

Current diagnostics expose one fixed `SourceChangedSeverity`. Once conditional rules exist, that field alone becomes misleading.

Evolve diagnostics so users can distinguish at least:

```text
fixed source policy
conditional/member-specific source policy
fallback severity
member rule count / member names
```

Do not serialize delegate implementation details or `ToString()` arbitrary closures.

## Tests

At minimum:

```text
quantity increase -> Dirty
quantity decrease -> Invalid
same rule on source-only derived
same rule on composed derived
fixed fallback for another tracked source member
unrelated member does not affect derived state
two member changes merge to Invalid
upstream Invalid + local Dirty => Invalid
normalized A->B->C classifier receives A/C semantics
classifier throw causes commit rollback
scan/hash/conservative relation choices do not change source classification
compiled diagnostics report conditional source policy truthfully
```

### Task 22 acceptance gate

A domain author can declare asymmetric source-change correctness once in the model. Callers continue to report ordinary property changes and cannot forget special invalidation orchestration.

Suggested commits:

```text
refactor: model source-member impact classifiers
feat: support value-sensitive source severity
```

---

# Task 23 — Build internal impact causality

## Goal

Move from:

```text
Derived X / source S -> Invalid
```

to internally knowing:

```text
because PO quantity decreased
because relation membership was removed
because upstream ReceivedQuantity became Invalid
```

without turning the core into a generic event-sourcing system.

## 23A — Normalize mutation origins

Assign deterministic per-commit origin IDs to normalized mutations that can seed dependency impact.

Origin kinds should cover at least:

```text
source property/member change
collection change/reset
object added
object removed
```

Use the normalized mutation set after repeated property changes have been collapsed.

An origin is local to one commit result. Do not create another globally durable ordinal identity system.

## 23B — Model direct causes, not copied full paths

Represent causality as direct edges. Suggested cause kinds:

```text
DirectSourceMember
RelationMembershipAdded
RelationMembershipRemoved
RelatedItemChanged
ConservativeRelationCandidate
UpstreamDerived
InvariantSourceMember
SourceLifecycle
```

A downstream derived cause should point to its immediate upstream derived definition/source impact, not copy the complete ancestor chain into every result.

For a diamond:

```text
A -> B -> D
 \-> C -> D
```

`D` may have two direct upstream causes, but there is still one final `(D, source)` impact with strongest merged severity.

## 23C — Precision

Add an explicit precision concept:

```text
Exact
Conservative
```

Examples:

- an exact removed relation pair is an exact cause;
- a source included only because it was in a conservative candidate bucket has conservative precision;
- scan fallback that affects all left sources is conservative precision.

Do not represent conservative inference as exact simply because the final lazy recomputation later happens to change the value.

## 23D — Conditional severity cause

For Task 22 source-member rules, retain enough metadata to explain:

```text
member: OrderedQuantity
classified severity: Invalid
rule: member-specific conditional policy
```

Do not expose executable delegates or arbitrary closure state.

## 23E — No sensitive value capture by default

Causal bookkeeping should identify mutations by definition/source/member and change kind. Do not automatically retain old/new arbitrary object values in long-lived public result graphs.

The existing `PropertyChange` may use values during commit and classification, but the default causal record should not become an accidental data-exfiltration/audit payload.

### Internal tests

```text
direct source change -> derived cause
relation add/remove -> relation-derived cause
item-only change -> related-item cause
conservative candidate cause marked Conservative
composed downstream -> UpstreamDerived edge
diamond retains both direct causes without duplicate impact
multi-input invariant references both affected upstreams
conditional source classifier cause retains member/policy metadata
ordering deterministic across equivalent mutation ordering
```

### Task 23 acceptance gate

The propagation wave can answer “what directly caused this source-scoped impact?” with exact/conservative precision while preserving one merged severity per affected node/source.

Suggested commit:

```text
feat: track dependency impact causality internally
```

---

# Task 24 — Expose opt-in causal results and prove their cost

Do not make every normal mutation pay for a rich explanation graph.

## 24A — Detail level

Add an explicit detailed-result option, for example:

```text
RuntimeImpactDetailLevel.Summary
RuntimeImpactDetailLevel.Causal
```

or an equivalent `RuntimeApplyOptions` shape.

Requirements:

```text
Apply(...) never captures causal details
ApplyDetailed(...) defaults to current summary-level behavior
caller explicitly opts into causal capture
Summary and Causal produce identical committed runtime semantics
```

Avoid a boolean whose meaning will become ambiguous when more diagnostic detail levels appear.

## 24B — Public causal data model

Keep the existing flat impact records because they are convenient for ordinary consumers. Enrich them with references into a causal graph or add a sibling graph to `RuntimeApplyResult`.

Prefer a type-safe cause hierarchy rather than one record with many nullable properties.

Conceptually:

```text
RuntimeApplyResult
  MutationOrigins[]
  RelationImpacts[]
  DerivedImpacts[]
  InvariantImpacts[]
  CausalEdges[]   // only in Causal mode
```

or:

```text
SourceDependencyImpact.Causes[]
```

with normalized origin/upstream references.

The chosen shape must avoid copying the entire transitive path into every source impact.

Public references should include stable `DefinitionKey` where available and local numeric IDs for in-process correlation, following existing identity rules.

## 24C — Deterministic trace rendering

Provide a small deterministic diagnostic renderer/helper that can produce a human-readable chain such as:

```text
GoodsReceipt.Cancelled changed
 -> MatchingReceipts membership removed [Exact]
 -> ReceivedQuantity invalid
 -> AvailableQuantity invalid (upstream ReceivedQuantity)
 -> LinkValidity invalid (upstream AvailableQuantity)
 -> LinkInvariant repair requested
```

This is diagnostic output, not a logging dependency.

Do not add JSON serialization opinions to core yet; records should be serialization-friendly data but transport formatting belongs outside core.

## 24D — Durable identity rules

Causality itself is not automatically durable merely because policy requests are durable.

When a cause references a registered source in a named object set with a canonical key, reuse `SourceIdentity`/`DurableSourceIdentity` semantics.

For removed objects, capture the registered key before removal so a detailed result can still identify the removed source when appropriate.

For unregistered nested navigation objects, do not invent a durable object identity. Identify the registered affected root plus member/dependency path instead.

## 24E — Correctness tests

Add deterministic and randomized assertions:

```text
Summary vs Causal committed values/states are identical
policy request set is identical
causal mode does not alter severity
causal mode does not alter conservative candidate selection
causal edge ordering deterministic
no duplicate cause edge in diamonds
no previous-wave cause leakage
removed source identity captured safely
failed commit returns no causal result
callback failure does not mutate already-produced result
```

Extend the randomized DAG oracle so a subset of operations run both Summary and Causal variants.

## 24F — Performance

Benchmark causal capture for:

```text
1 / 10 / 100 property changes
linear DAG
diamond DAG
relation membership changes
selective conservative candidate routing
```

Measure latency + allocation for:

```text
Apply
ApplyDetailed Summary
ApplyDetailed Causal
```

Set no arbitrary microsecond target before measurement. The gate is architectural: Summary should not accidentally allocate the full causal graph, and basic Apply should remain the cheapest path.

If a safety cap on cause count is ever needed, truncation must be explicit in result data (`IsTruncated`, counts, etc.). Never silently drop causes.

## 24G — Static topology diagnostics

While touching diagnostics, add only topology metadata with clear user value. At minimum make invariant diagnostics explicitly include `SourceObjectSetId`, so tooling does not have to infer it indirectly from upstream IDs.

An explicit compiled dependency-edge list can be added if it materially simplifies graph visualization/tests, but do not expose the entire internal compiler representation without a consumer.

### Task 24 acceptance gate

Applications can opt into a stable, deterministic explanation of a committed mutation without changing semantics, and ordinary/basic mutation paths do not pay the full causal allocation cost.

Suggested commits:

```text
feat: expose opt-in causal impact results
test: verify causal and summary equivalence
bench: measure causal impact capture
```

---

# Task 25 — Build an executable procurement vertical slice

## Goal

Prove that the library solves the motivating business problem without hand-written “after X call Y/Z” orchestration.

The current `docs/purchase-order-example.md` is useful but too small: it covers received quantity and one invariant, not the dependency chain that motivated the architecture.

Add a compiling sample project, preferred location:

```text
samples/Raffinert.Relations.PurchaseOrderSample/
```

Use a project reference while developing the sample. Add it to the solution/build so API examples cannot silently rot.

## Domain model

Keep it small but realistic enough to prove the engine:

```text
PurchaseOrderLine
GoodsReceipt
InvoiceLine or SelectedMatch/Allocation record
```

Model at least these concepts:

```text
matching goods receipts
received quantity
reserved/matched quantity (if useful for the scenario)
available quantity
price rate / unit rate
selected link's captured rate/quantity
link validity / usability
repair/rematch invariant
```

Do not recreate a whole ERP.

## Required dependency chain

The sample should demonstrate a graph equivalent to:

```text
GoodsReceipt membership/value
       |
       v
ReceivedQuantity
       |
       v
AvailableQuantity ---- PriceRate / UnitRate
       |                    |
       +---------+----------+
                 v
            LinkValidity
                 |
                 v
        Invariant / repair request
```

A selected persisted match/link can be represented as domain state and validated downstream. Do not force `UnitRate` into the relation predicate merely to make the sample look clever.

## Required business scenarios

### Scenario A — additive change can be deferred

Example:

```text
PO OrderedQuantity 8 -> 10
```

Use the Task 22 conditional policy so downstream capacity-related state becomes `Dirty` where appropriate rather than automatically `Invalid`.

Show that no caller manually invokes invalidation/rematching methods.

### Scenario B — subtractive change invalidates existing decision

Example:

```text
PO OrderedQuantity 10 -> 8
```

When the existing selected link/allocation may now exceed valid capacity, downstream link usability must become `Invalid` and the invariant/repair policy must surface the need for rematching/fixup.

### Scenario C — goods receipt cancellation/removal

```text
GoodsReceipt.Cancelled false -> true
```

or removal must propagate through relation membership -> received quantity -> availability -> link validity and produce the configured repair behavior.

### Scenario D — rate change

Change `PriceRate` or another input to `UnitRate`; demonstrate:

```text
PriceRate changed
 -> UnitRate impacted
 -> existing link validity impacted
 -> repair/rematch request if correctness requires it
```

This is the important proof that a direct source-derived value can invalidate downstream business decisions without a developer remembering a procedural chain.

## Causal output

Run at least one scenario with causal details enabled and render the explanation chain.

The output should be understandable without knowing internal graph IDs. Prefer stable names:

```csharp
.Named("po-lines")
.Named("received-quantity")
.Named("available-quantity")
.Named("link-validity")
.Named("link-validity-invariant")
```

Use durable request identity in the example where background repair/outbox is demonstrated.

## EF adapter coverage

If practical without bloating the sample, add an integration test demonstrating the same model through EF Core capture/save/commit/dispatch ordering. It is acceptable for the console sample itself to remain core-only while the EF test lives in the existing EF test project.

## What to learn from the sample

If the scenario exposes a genuine missing primitive, document it with the exact model/expression that cannot be represented.

Do **not** automatically implement:

```text
derived-valued relation predicates
relations depending on derived nodes
pre-mutation what-if simulation
a workflow engine
```

unless the sample cannot model correctness without duplication and the limitation is demonstrated by a focused failing design test.

## Documentation

Update `docs/purchase-order-example.md` to match the compiling sample. Prefer linking to source files for longer code rather than maintaining two unrelated versions.

Add a short README section showing the business-value chain, not only relation-query syntax.

### Task 25 acceptance gate

A compiling, tested procurement example demonstrates additive vs subtractive severity, GR cancellation, rate-driven downstream invalidation, deferred repair, durable requests, and an explainable impact chain without manual orchestration calls.

Suggested commits:

```text
sample: add procurement dependency vertical slice
docs: demonstrate explainable domain consistency flow
```

---

# Task 26 — Alpha package-consumer and release stabilization

Do this only after Tasks 20–25 are green.

## 26A — Consumer smoke tests from packed NuGet artifacts

Current CI packs packages but normal tests reference projects directly. Add a post-pack smoke test that consumes the produced `.nupkg` files from a local package source rather than project references.

Verify at least:

```text
core package consumable from net8.0
core package consumable from net10.0
EF adapter package consumable from net10.0
README/license/repository metadata present
symbols package produced
```

A tiny generated/checked-in consumer project is acceptable. Keep the smoke test focused on packaging/public surface, not duplicating the full test suite.

## 26B — Public API review

Before moving any API to shipped baseline, review the actual sample-facing surface:

```text
RelationModelBuilder
ObjectSetBuilder / ObjectSet
RelationBuilder / Relation
Derived builders / Derived
impact policy configuration
propagation preference naming
Invariant builders / Invariant
MutationSet / Change helpers
Apply / ApplyDetailed / causal options
RuntimeApplyResult and causal records
durable identity records
EF unit-of-work APIs
```

Remove accidental public types rather than freezing them because a baseline file already lists them.

Do not add convenience overloads without a sample/test demonstrating real friction.

## 26C — Changelog and version

`CHANGELOG.md` currently describes the older alpha surface and does not capture the post-DAG/conservative architecture wave.

Update release notes with user-visible changes including:

```text
composed dependency DAG
exact/conservative propagation separation
selective conservative routing
conditional source-member severity
impact causality (if public in this release)
procurement sample
multi-target behavioral verification
```

Do not assume whether `0.1.0-alpha.1` has already been externally published. At release preparation time:

- if `alpha.1` was published, increment prerelease version appropriately;
- if it was never published, retaining `alpha.1` may be valid.

Do not publish automatically from this roadmap.

Per `RELEASING.md`, move approved API entries from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt` only as part of the actual reviewed release/tag preparation, not merely because Task 26 tests are green.

## 26D — Final validation

Run the full matrix:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Then run package-consumer smoke tests against `artifacts/packages`.

Review benchmark documents for accidental large regressions in:

```text
commit safety
propagation precision
exact/conservative propagation
basic vs detailed apply
causal capture
```

### Task 26 acceptance gate

The package can be consumed from its actual NuGet artifacts, the public API has been intentionally reviewed against a real domain sample, release notes reflect the architecture users will receive, and the repository is ready for a manual alpha release decision without another core redesign.

Suggested commits:

```text
build: smoke test packed NuGet consumers
docs: prepare explainability alpha release notes
```

---

# Required regression matrix

Before declaring this roadmap complete:

```text
[ ] unregistered derived/invariant queries are rejected consistently
[ ] failed query/evaluation does not create runtime cache state
[ ] basic Apply bypasses detailed result construction
[ ] Apply and ApplyDetailed have equivalent mutation semantics
[ ] conservative public naming reflects preference rather than guaranteed plan
[ ] source-member transition rules support Dirty vs Invalid asymmetry
[ ] normalized multi-change batches classify the net transition
[ ] multiple source-member causes merge with Invalid dominance
[ ] classifier failure rolls back runtime-owned state
[ ] relation predicate remains semantic authority
[ ] exact/conservative propagation equivalence remains green
[ ] incremental aggregates retain exact delta semantics
[ ] one commit produces one immutable propagation result
[ ] detailed result remains stable after subsequent commits
[ ] no previous-wave impact/cause leakage
[ ] causal mode does not alter committed state or policy requests
[ ] exact vs conservative causal precision is truthful
[ ] diamond graphs retain direct causes without duplicate impacts
[ ] removed-source causal identity is safe and deterministic
[ ] causal capture is opt-in; Summary/basic paths do not build full cause graphs
[ ] randomized DAG tests cover causal/summary equivalence
[ ] procurement sample compiles and runs
[ ] PO quantity increase vs decrease demonstrates different declared severity
[ ] GR cancellation propagates to link validity/repair
[ ] PriceRate/UnitRate change propagates to downstream link validity
[ ] sample requires no manual invalidation/rematch orchestration calls
[ ] durable repair identity still round-trips canonically
[ ] Prepare/Commit drift and rollback guarantees remain green
[ ] dispatch remains resumable after callback failure
[ ] EF DB-success/runtime-sync-failure remains distinguishable
[ ] core tests pass net8.0 and net10.0
[ ] EF tests pass net10.0
[ ] packed NuGet consumer smoke tests pass
[ ] format/package validation remains green
[ ] README/architecture/changelog describe actual behavior
```

---

# Recommended commit sequence

Keep the implementation bisectable. A good target sequence is:

```text
1.  fix: enforce source registration across runtime queries
2.  perf: avoid detailed result construction in basic apply
3.  refactor: clarify conservative propagation preference
4.  refactor: return explicit dependency propagation results
5.  refactor: model source-member impact classifiers
6.  feat: support value-sensitive source severity
7.  feat: track dependency impact causality internally
8.  feat: expose opt-in causal impact results
9.  test: verify causal summary equivalence and randomized traces
10. bench: measure basic detailed and causal apply costs
11. sample: add procurement dependency vertical slice
12. docs: document explainable domain consistency
13. build: smoke test packed NuGet consumers
14. docs: prepare alpha release surface and notes
```

Do not squash the semantic phases while implementing; intermediate commits should remain buildable and focused enough to revert independently.

---

# Explicitly deferred after this roadmap

These may be valuable later, but they are not part of Tasks 20–26:

```text
pre-mutation what-if / preview simulation
automatic domain mutation interception / transparent proxies
INotifyPropertyChanged integration
distributed dependency execution
built-in Service Bus/Kafka dispatchers
new range/tree relation access plans
runtime automatic plan switching
derived values inside relation predicates / relation nodes depending on derived nodes
public variadic 3+ upstream generic overload families
persistence of derived caches across process restarts
```

The most likely next product extension after causality is a **what-if impact preview**, but that requires a deliberate staged-domain overlay because the current mutation protocol observes changes that have already occurred. Do not fake preview by calling the committed mutation path and rolling it back.

# Completion criterion

This roadmap is complete when Raffinert.Relations can express asymmetric domain correctness policies declaratively, produce an optional deterministic explanation of why each committed source-scoped impact occurred, prove those semantics in a compiling PO/GR/link-validity workflow, and be consumed from its actual packed NuGet artifacts without weakening the safety and performance guarantees established by the earlier DAG hardening work.
