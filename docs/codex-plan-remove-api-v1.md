# Codex plan — remove legacy API v1 after API v2 production landing

Status: **DESTRUCTIVE PUBLIC-API CLEANUP — FOLLOW LITERALLY**

Audience: weaker coding agent. This document intentionally repeats constraints. Do not infer a broader cleanup mandate from the title.

Baseline reviewed by maintainer before this plan:

```text
4b181d9d660b3156344c692704a26037f24f9cdf  api: implement production v2 value flow and materialization
d032d0db23c430704af8624a6cf3c3efae4825d1  efcore: consume core materialization descriptors
785064d038f92e402f1ee18239b0e369312cb076  docs: document API v2 production semantics
```

API-v2 production implementation added `From`, `Select`, recognized `Sum/Count/LongCount/Any`, Core `MaterializeTo`, runtime `Evaluate` and targeted/object `Materialize`, projected value flow, invariant `From`, Core-owned materialization descriptors, EF consumption, diagnostics, validation, and old-v2 parity tests.

This plan removes **legacy public spellings and compatibility-only paths**. It does **not** remove the execution primitives that API v2 reuses internally.

---

# 0. Critical distinction: API v1 surface != v1 implementation primitives

This is the most important rule in the entire plan.

Legacy public syntax currently compiles into the same engine used by v2. Therefore:

```text
REMOVE public compatibility spellings
KEEP shared engine/runtime primitives required by v2
```

Examples:

```text
public `.Using(...)` may be removed
BUT upstream-derived input definitions/indexes must remain because `.From(...)` needs them

public `.Compute(...)` may be removed
BUT compiled computation definitions/delegates must remain because `.Select(...)` needs them

public `.Incrementally()` may be removed
BUT IncrementalSum/IncrementalCount/IncrementalLongCount/IncrementalAny plans must remain

legacy EF `.Materialize(derived, property)` may be removed
BUT MaterializationDescriptor and EF persistence materialization must remain
```

If deleting a v1 member makes you want to delete a runtime class simply because its name predates v2, **stop**. Check whether v2 calls or constructs it.

Do not rename internal architecture merely to make it sound v2-like unless the rename is necessary to remove a public v1 symbol.

---

# 1. Goal

After this work, a new package consumer should have one preferred declaration vocabulary:

```csharp
var priceRate = model
    .Derived(links)
    .DependsOn(
        x => x.InvoiceLine.Price,
        x => x.PurchaseOrderLine.Price)
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");

var unitRate = model
    .Derived(links)
    .From(priceRate)
    .Select((link, rate) => CalculateUnitRate(link, rate))
    .MaterializeTo(x => x.UnitRate)
    .Named("unit-rate");

var received = model
    .Derived(poLines)
    .From(receipts)
    .Impact(...)
    .Sum(x => x.Quantity);

var invariant = model
    .Invariant(links)
    .From(validity)
    .Must((_, valid) => valid)
    .ScheduleRepairWith(Rematch);

var rate = runtime.Evaluate(priceRate, link);
runtime.Materialize(link);
```

There should no longer be two documented/public ways to express the same operation merely for compatibility.

---

# 2. What counts as legacy API v1

Start with this candidate removal inventory, then verify every item against the actual public API files and source.

## 2.1 Derived declaration legacy spellings

Candidates:

```text
DerivedBuilder<T>.Using(Relation<...>)
DerivedBuilder<T>.Using(Derived<...>)
DerivedBuilder<T>.Using(selector, Derived<...>)
DerivedBuilder<T>.Using(first, second)
DerivedBuilder<T>.Using(selector, first, second)
Derived*/Projected*/Relation* builder `.Compute(...)` methods where `.Select(...)` is the v2 replacement
`.Incrementally()` and any public stage/type that exists only to require/represent that opt-in
```

Do not assume every `Using` in the repository is v1. For example, an unrelated concept may legitimately use that English word. Remove only declaration/invariant compatibility members replaced by selected v2 syntax.

## 2.2 Invariant legacy spelling

Candidate:

```text
Invariant builder `.Using(derived)` replaced by `.From(derived)`
```

Keep `Must`, `Named`, `ScheduleRepairWith`, enforcement semantics, invariant definitions and runtime state.

## 2.3 Legacy EF materialization mapping

Candidate:

```csharp
new ConsistencyEfCoreMappings()
    .Materialize(derived, x => x.Property)
```

V2 preferred declaration is:

```csharp
derived.MaterializeTo(x => x.Property)
```

and EF automatically consumes the Core descriptor.

Remove explicit EF materialization mapping only after all repository consumers have migrated and tests prove Core descriptor persistence parity.

Keep:

```text
ConsistencyEfCoreMappings itself
Map(...)
Enforce(...)
consumer resolver configuration
persistence policy
MaterializationDescriptor
EF materialization execution/rollback
Core descriptor synchronization/validation as needed
```

## 2.4 Runtime legacy read spelling

Inspect the actual public runtime API. If public `Get(derived, source)` still exists solely as the pre-v2 equivalent of `Evaluate(derived, source)`, migrate all repository consumers and remove it.

Do **not** remove:

```text
GetState
TryGetCached / cache-inspection API if it is still intentionally public and not just a v1 alias
internal Get/evaluation machinery used by Evaluate
GetDerivedId/GetInvariantId internal helpers
unrelated Get* diagnostic/query methods
```

The removal criterion is semantic aliasing, not the word `Get`.

## 2.5 Compatibility-only helpers/types

After removing legacy members, identify public types that become unreachable from all remaining public APIs and have no independent intended use.

Examples may include a public `Incrementally` stage/builder used only by v1 syntax.

Do not delete a builder type merely because its name is old if v2 `From(...)` still returns it.

---

# 3. Explicitly NOT part of v1 removal

Do not remove or redesign any of these:

```text
ObjectSet / Objects / Key / Named
Relation / Where / relation indexing
DependsOn
Impact and source-member classifiers
Derived<TSource,TValue> handles
From
Select
Sum / Count / LongCount / Any
MaterializeTo
Evaluate
Materialize(definition, source)
Materialize(source)
GetState
cache state Fresh/Dirty/Invalid
Invariant / Must / ScheduleRepairWith
mutation APIs
Apply / ApplyDetailed / prepared mutation APIs
repair requests/dispatch
compiled diagnostics/debug view
EF Map / Enforce / save/interceptor APIs
scope validation
source lifecycle APIs
optimizer/runtime diagnostics
concurrency contract
```

Do not treat “v1 removal” as permission to simplify unrelated public API.

---

# 4. No deprecation window in this task

This plan is for actual removal, not `[Obsolete]` staging.

Do not add `[Obsolete]` attributes and leave aliases behind unless the maintainer explicitly changes this instruction.

Do not keep hidden compatibility extension methods “just in case”.

Do not introduce `Legacy*` wrappers.

The desired result is one clean pre-release/next-major public vocabulary.

If repository versioning/release policy proves that removing shipped API is forbidden for the intended release, **stop and report that versioning conflict** instead of silently retaining compatibility aliases.

---

# 5. Phase 1 — produce an exact legacy symbol inventory before deleting anything

Read:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

Also inspect all public members in:

```text
src/Raffinert.Consistency/Derived/**
src/Raffinert.Consistency/Invariants/**
src/Raffinert.Consistency/Runtime/**
src/Raffinert.Consistency.EntityFrameworkCore/**
```

Create a temporary checklist in working notes or the final report with columns:

```text
Symbol
Package
Legacy replacement
Repository usages
Shared implementation dependency?
Action: REMOVE / KEEP / INTERNALIZE
Reason
```

For every candidate symbol, explicitly write its v2 replacement.

Examples:

```text
DerivedBuilder.Using(Derived) -> From(Derived)
DerivedBuilder.Using(Relation) -> From(Relation)
DerivedBuilder.Compute -> Select
DerivedUsingBuilder.Incrementally -> recognized Sum/Count/LongCount/Any when applicable
InvariantBuilder.Using(Derived) -> From(Derived)
EF mappings.Materialize -> Derived.MaterializeTo
Runtime.Get(Derived,source) -> Evaluate if and only if semantic parity is exact
```

Do not delete anything until this inventory exists.

---

# 6. Phase 2 — migrate repository production examples and package consumers FIRST

Before removing methods, migrate every non-test consumer under:

```text
README.md
docs/**
samples/** if present
examples/** if present
tests/package-consumers/**
benchmarks/** if they demonstrate public API
```

Rules:

```text
Using(upstream) -> From(upstream)
Using(selector, upstream) -> From(selector, upstream)
Compute -> Select
Using(relation).Incrementally().Compute(recognized aggregate) -> From(relation).<recognized operator>
Invariant.Using -> Invariant.From
EF explicit Materialize mapping -> Core MaterializeTo on definition
runtime.Get -> Evaluate only when it is the logical derived/invariant read alias
```

Do not mechanically translate arbitrary `.Incrementally().Compute(...)` to a recognized operator unless the expression is exactly supported by the v2 operator.

For an old incremental expression not expressible as `Sum/Count/LongCount/Any`, stop and report the missing v2 capability. Do not silently change it to non-incremental `Select`.

After migration, documentation must contain no statement that legacy spellings remain supported.

---

# 7. Phase 3 — migrate tests, but preserve semantic coverage

Do **not** simply delete tests that mention v1.

Classify existing tests into:

```text
A. tests of engine semantics that happen to use v1 syntax
B. explicit old-v2 parity/compatibility tests
C. tests specifically of v1 fluent syntax
```

Actions:

```text
A -> rewrite to v2 syntax; KEEP assertions
B -> replace with direct v2 regression tests; remove old side of comparison only after copying all useful assertions
C -> remove only after equivalent v2 fluent/API tests exist
```

Example from current `ApiV2ProductionTests`:

```text
Recognized_aggregates_match_old_incremental_syntax
```

Do not delete the whole test. Convert it into a v2-only regression test that still verifies:

```text
Sum/Count/LongCount/Any values
incremental plan names
add behavior
item-change behavior
remove/cancel behavior
Fresh/Dirty/Invalid states
```

Likewise convert:

```text
Direct_transitive_and_projected_v2_values_match_legacy_declarations
V2_invariant_facade_matches_old_value_flow_and_repair_behavior
```

into v2-only semantic tests preserving mutation/state/repair assertions.

The fact that v1 is gone must not reduce runtime coverage.

---

# 8. Phase 4 — remove public `.Using(...)` declaration aliases

Only after repository consumers compile on v2 syntax, remove the legacy derived `Using` overloads.

Likely locations include `DerivedBuilders.cs` and invariant builders.

Important implementation rule:

If current v2 `From(...)` is implemented as:

```csharp
public X From(...) => Using(...);
```

then **do not delete `Using` first**.

Refactor in this order:

```text
1. extract/move the real implementation into a private/internal helper OR directly into From
2. make From call the real implementation
3. run tests
4. remove public Using
```

Bad:

```csharp
public X From(...) => Using(...);
// delete Using -> build breaks
```

Good conceptually:

```csharp
public X From(...) => CreateUpstreamBuilder(...);
private X CreateUpstreamBuilder(...) { ... }
```

or simply put validation/construction directly in `From`.

Preserve:

```text
null validation
model ownership validation
exact ObjectSet identity validation
projected selector validation
declared DependsOn propagation
Impact propagation
```

Do this separately for:

```text
relation From
local derived From
projected derived From
multi-value/chained From
invariant From
```

Do not accidentally remove shared builder classes returned by `From`.

---

# 9. Phase 5 — remove public `.Compute(...)` aliases

Current v2 `Select` may delegate to `Compute`. Reverse that dependency before removal.

Refactor order:

```text
1. identify each builder where Select => Compute
2. move definition construction into Select or private/internal CreateDefinition helper
3. make any remaining internal legacy path call the helper, not public Compute
4. run tests
5. remove public Compute
```

Preserve all behavior previously performed by Compute:

```text
expression analysis
opaque calculator completeness rules
declared DependsOn merging
compiled delegate creation
Impact metadata
input metadata
model.AddDerived
model mutability checks
stable definition identity behavior after Named
```

Do not replace expression-based calculations with opaque `Func` indiscriminately.

Keep `SelectOpaque` or equivalent internal helper if v2 method-group support requires it, but it should not reintroduce public `Compute`.

Search docs/tests/package consumers for `.Compute(` after removal. Remaining occurrences are allowed only when they refer to unrelated APIs or historical documents intentionally preserved as historical records.

---

# 10. Phase 6 — remove `.Incrementally()` compatibility syntax, NOT incremental execution

This phase is high risk.

Remove public syntax/types whose only purpose is:

```csharp
.From/Using(relation)
.Incrementally()
.Compute(...)
```

But keep all execution machinery for:

```text
IncrementalSum
IncrementalCount
IncrementalLongCount
IncrementalAny
incremental cache delta application
fallback full recomputation
impact policies
relation delta tracking
```

Before deleting an incremental builder/stage type, search for all constructors/usages from v2 recognized aggregate methods.

If v2 `Sum/Count/LongCount/Any` currently call through a public `.Incrementally()` builder, refactor recognized operators to call the shared internal planner directly first.

Required regression tests after removal:

```text
Sum selects IncrementalSum
Count selects IncrementalCount
LongCount selects IncrementalLongCount
Any selects IncrementalAny
membership add incremental behavior unchanged
membership remove/item change Invalid behavior unchanged
full recomputation fallback unchanged
```

Do not rename plan diagnostics from `IncrementalSum` merely because `.Incrementally()` public syntax was removed.

---

# 11. Phase 7 — remove invariant `.Using(...)`

If v2 invariant `From` currently calls legacy `Using`, invert/refactor exactly as with derived builders.

Keep:

```text
Invariant<T>
Invariant builder types still returned by v2
Must
Named
ScheduleRepairWith
repair requests
repair dispatch
state propagation
```

Required regression:

```text
From(logicalDerived) consumes logical value
invalid mutation creates expected repair request
Materialize does not dispatch repair
normal dispatch executes repair exactly once
```

---

# 12. Phase 8 — remove legacy EF `.Materialize(derived, property)` mapping

Do this only after all production/test/package consumers use:

```csharp
derived.MaterializeTo(x => x.Property)
```

and EF automatic Core descriptor consumption is proven.

Refactor carefully because current EF code may still maintain a list containing both legacy descriptors and synchronized Core descriptors.

Desired final model:

```text
Core compiled model owns MaterializationDescriptor
EF reads applicable Core descriptors
EF persistence planner uses them
no user-facing duplicate EF mapping API exists
```

Remove compatibility-only code such as `MaterializationDescriptor.CreateLegacy(...)` **only if** no remaining internal feature needs it.

Remove legacy merge/conflict logic whose only purpose was reconciling explicit EF mapping with Core mapping, but retain:

```text
duplicate Core target validation
sink-only validation
EF mapped-property validation
key-property rejection
tracked-source validation
value comparer/equality behavior
physical rollback
IsModified restoration
```

Required SQLite tests:

```text
Evaluate does not modify mirror
SaveChangesConsistently consumes MaterializeTo automatically
changed mirror is persisted
unchanged mirror assignment is skipped
unrelated properties untouched
setter failure rolls back physical values/EF flags
key target rejected
mirror used as graph input rejected
```

Do not remove `ConsistencyEfCoreMappings` itself.

---

# 13. Phase 9 — inspect/remove legacy runtime `Get` only if it is truly replaced by `Evaluate`

Do not grep for the word `Get` and delete everything.

Find the exact public derived/invariant read methods.

For each public `Get` candidate answer:

```text
Does Evaluate have identical logical freshness semantics?
Does Get have extra cache-only/force-recompute behavior?
Is Get used for relations, diagnostics, state, IDs or unrelated queries?
```

Only remove the exact semantic alias replaced by `Evaluate`.

If `Evaluate` currently calls public `Get`, invert the implementation:

```text
extract internal EvaluateCore/GetCore
Evaluate -> core helper
remove public legacy Get
```

Keep cache inspection APIs with distinct semantics.

Required tests:

```text
Fresh Evaluate returns cached value
Dirty/Invalid Evaluate refreshes according to engine rules
Evaluate never materializes mirror
Evaluate invariant works
Evaluate rejects foreign handle/source as before
```

---

# 14. Phase 10 — prune compatibility-only public types

After member removal, inspect public builder/stage types.

A type may be removed/internalized only if all are true:

```text
1. no remaining public API returns it
2. no remaining public API accepts it
3. it has no independent user-constructible purpose
4. v2 does not require its public generic shape for fluent chaining
```

Likely candidate: a stage that existed only after `.Incrementally()`.

Likely NON-candidates: upstream/projected builder types that `From(...)` still returns.

Do not make a public return type internal; that creates inconsistent accessibility/build failures.

Run API compatibility generation/check after every type-level visibility change.

---

# 15. Phase 11 — PublicAPI files are an output/checkpoint, not the first edit

Do not start by deleting lines from `PublicAPI.Shipped.txt`.

Correct order:

```text
1. change source
2. compile
3. inspect API analyzer failures/diff
4. update PublicAPI files intentionally
5. compile again
```

Because this is deliberate breaking removal, remove exactly the legacy symbol entries that no longer exist.

Do not accept unrelated public API changes.

Create a before/after public API diff and inspect it manually.

Expected removals should correspond to the inventory from Phase 1.

Expected additions: ideally none, except an internal refactor should not create public additions.

If an unexpected public symbol disappears, restore it or stop and report why it is coupled to v1.

---

# 16. Phase 12 — delete old compatibility tests/docs only after migration

Remove text such as:

```text
"legacy ... remains supported"
"old API"
"compatibility alias"
".Using(...).Incrementally().Compute(...) remains supported"
```

from current user-facing docs.

Historical design/experiment documents may keep old syntax if clearly historical. Do not rewrite every experiment just to erase history.

For current README/architecture/EF docs:

```text
show v2 only
explain one vocabulary
remove migration-window promises that are no longer true
```

CHANGELOG must explicitly record the breaking removals.

If this is unreleased API churn, describe it according to repository release conventions; do not falsely claim a previously released stable version had v2 if it did not.

---

# 17. Phase 13 — repository-wide legacy-token audit

After source/tests/docs migration, perform searches for at least:

```text
.Using(
.Compute(
.Incrementally(
.Materialize(   // inspect manually: runtime Materialize is v2 and MUST remain
CreateLegacy
legacy
compatibility
old API
old syntax
runtime.Get(    // inspect manually; do not blanket-delete
```

Every match must be classified.

Allowed examples:

```text
historical experiment/docs
internal helper name not exposed and still architecturally valid
runtime.Materialize (v2)
unrelated APIs using words like Compute/Using
changelog/history
```

Not allowed:

```text
current README teaching v1
package consumer compiling through v1
production public v1 alias left accidentally
compatibility-only EF path left accidentally
v1 parity test keeping old API alive
```

Put the classification summary in the final agent report.

---

# 18. Phase 14 — build/test matrix

Run the repository's normal build/test commands discovered from solution/workflows.

At minimum verify all applicable targets:

```text
Core unit tests
EF Core tests
package consumer Core .NET 8
package consumer Core .NET 10
package consumer EF target(s)
public API analyzer/check
format/analyzers if configured
```

Do not claim success because only `ApiV2ProductionTests` pass.

If GitHub Actions exists, do not edit workflows merely to make removal pass unless the workflow references removed sample syntax and genuinely needs migration.

Record exact commands and results.

---

# 19. Mandatory semantic regression gates

Before declaring v1 removal complete, prove all of these still pass with **v2-only declarations**:

```text
G1 direct derived calculation
G2 explicit DependsOn opaque calculator
G3 local From transitive derived flow
G4 projected From flow
G5 chained projected + local From
G6 same-CLR wrong ObjectSet rejection
G7 cross-model handle rejection
G8 Sum incremental plan
G9 Count incremental plan
G10 LongCount incremental plan
G11 Any incremental plan
G12 additive relation mutation behavior
G13 subtractive/cancellation Invalid behavior
G14 source-member asymmetric Impact classifier
G15 invariant From + Must
G16 exactly-once repair dispatch
G17 Evaluate logical-only behavior
G18 targeted Materialize physical scope
G19 object Materialize physical scope
G20 evaluate-first/write-second behavior
G21 physical rollback on setter failure
G22 Fresh logical cache reuse after materialization retry
G23 mirror dependency Build rejection
G24 duplicate MaterializeTo target rejection
G25 EF automatic MaterializeTo persistence
G26 EF unchanged assignment skip
G27 EF rollback/IsModified restoration
G28 debug graph v2 vocabulary
G29 runtime remains non-thread-safe contractually as before
G30 all package consumers compile without v1 symbols
```

A removed v1 test is not evidence for these gates. There must be surviving v2 regression coverage.

---

# 20. Mandatory negative API-surface gates

After successful build, verify that package consumers can **no longer** compile representative v1 snippets.

Use compile-fail/source API inspection rather than keeping broken code in normal projects.

Verify absence of:

```csharp
model.Derived(set).Using(upstream)
model.Derived(set).Using(relation)
model.Derived(set).Using(selector, upstream)
model.Derived(set).Compute(...)
model.Derived(set).Using(relation).Incrementally()
model.Invariant(set).Using(derived)
efMappings.Materialize(derived, x => x.Mirror)
```

Also verify the selected v2 equivalents compile.

If public `runtime.Get(derived, source)` was classified as legacy and removed, verify its absence too.

Do not test absence by reflection against implementation-private helpers; test the public surface.

---

# 21. Suggested commit sequence

Keep this destructive change reviewable. Suggested commits:

```text
1. refactor: make v2 From and Select independent of legacy aliases
2. tests: migrate engine coverage from v1 syntax to v2
3. api!: remove derived Using and Compute compatibility surface
4. api!: remove Incrementally declaration surface
5. api!: remove invariant Using compatibility surface
6. efcore!: remove explicit legacy Materialize mapping
7. api!: remove legacy runtime Get alias if verified equivalent
8. api!: prune unreachable compatibility-only public stage types
9. tests: add v2-only semantic and negative public-surface gates
10. docs!: remove API v1 documentation and document breaking cleanup
11. api: reconcile PublicAPI baselines and package consumers
```

If a step has no applicable symbol after inspection, record “not applicable”; do not invent work to satisfy the commit list.

Every commit should build where practical.

---

# 22. Anti-dumb implementation rules

The agent MUST obey all of these:

1. **Do not delete shared runtime primitives because their original public caller was v1.**
2. **Do not delete builder classes returned by v2 `From`.**
3. **Do not delete incremental execution plans when deleting `.Incrementally()`.**
4. **Do not delete `MaterializationDescriptor` when deleting EF explicit `.Materialize`.**
5. **Do not delete runtime `Materialize`; it is v2.**
6. **Do not delete `GetState`; it is not the same as legacy logical `Get`.**
7. **Do not replace every string `Using`/`Compute` blindly. Inspect context.**
8. **Do not update PublicAPI files before source changes.**
9. **Do not make v2 call a method you are about to remove. Invert dependencies first.**
10. **Do not delete parity tests without preserving their semantic assertions in v2-only tests.**
11. **Do not downgrade an incremental computation to ordinary Select just to remove v1 syntax.**
12. **Do not reintroduce compatibility through extension methods.**
13. **Do not add `[Obsolete]` instead of removing unless maintainer explicitly changes scope.**
14. **Do not remove unrelated public APIs.**
15. **Do not change concurrency semantics.**
16. **Do not change Fresh/Dirty/Invalid semantics.**
17. **Do not change repair dispatch boundaries.**
18. **Do not make materialized mirrors logical graph inputs.**
19. **Do not change exact ObjectSet identity checks to CLR-type checks.**
20. **Do not create a second engine for v2 after removing aliases.**
21. **Do not hide build errors by weakening analyzers or API checks.**
22. **Do not delete historical experiment folders unless separately requested.**
23. **Do not perform unrelated renames/style cleanup in the same commits.**
24. **Do not claim all tests pass unless you actually ran the full applicable matrix.**
25. **Do not push past a stop condition below.**

---

# 23. Stop conditions — report instead of guessing

Stop and ask the maintainer if any of these occurs:

```text
S1 v2 From cannot function without a public Using member and no straightforward private/internal extraction exists
S2 v2 Select cannot function without exposing public Compute
S3 recognized aggregates require public Incrementally rather than only internal planner machinery
S4 a repository use of Incrementally expresses semantics not representable by Sum/Count/LongCount/Any
S5 removing invariant Using loses a behavior not available through From
S6 EF Core still requires user explicit Materialize mapping for a scenario Core MaterializeTo cannot represent
S7 legacy runtime Get has semantics not covered by Evaluate
S8 a supposedly compatibility-only builder type is still a return/parameter type of v2 public API
S9 PublicAPI diff removes symbols outside the approved inventory
S10 v2-only rewritten tests fail where old-v2 parity tests previously passed
S11 package consumer requires a v1 operation with no v2 replacement
S12 version/release policy does not permit the intended breaking removal
S13 API analyzer requires suppressing unrelated breakages
S14 removing CreateLegacy or merge logic breaks Core-descriptor EF persistence
S15 any proposed fix requires changing logical/materialization/repair semantics
```

Do not solve a stop condition using `dynamic`, reflection hacks, public `object` escape hatches, duplicated APIs, or test deletion.

---

# 24. Definition of done

Do not report “done” until every applicable checkbox is true:

```text
[ ] exact v1 public symbol inventory produced
[ ] every removed symbol has a documented v2 replacement
[ ] v2 From implementation no longer calls public Using
[ ] v2 Select implementation no longer calls public Compute
[ ] recognized operators no longer depend on public Incrementally stage
[ ] invariant From no longer depends on public invariant Using
[ ] all current docs/examples/package consumers migrated to v2
[ ] derived Using overloads removed from public API
[ ] derived Compute overloads removed from public API
[ ] Incrementally compatibility surface removed
[ ] invariant Using removed
[ ] explicit EF Materialize mapping removed if no stop condition found
[ ] CreateLegacy removed if truly unused
[ ] legacy runtime Get removed only if verified semantic alias
[ ] compatibility-only unreachable public types removed/internalized
[ ] shared graph/runtime primitives preserved
[ ] IncrementalSum preserved
[ ] IncrementalCount preserved
[ ] IncrementalLongCount preserved
[ ] IncrementalAny preserved
[ ] Core MaterializationDescriptor preserved
[ ] Evaluate preserved
[ ] targeted Materialize preserved
[ ] object Materialize preserved
[ ] GetState preserved
[ ] From local/projected/relation preserved
[ ] Select preserved
[ ] DependsOn preserved
[ ] Impact preserved
[ ] MaterializeTo preserved
[ ] invariant Must/repair preserved
[ ] old-v2 parity tests converted to v2-only regression coverage
[ ] G1-G30 semantic gates pass
[ ] negative public-surface gates prove representative v1 snippets unavailable
[ ] PublicAPI diff contains only intentional v1 removals
[ ] Core tests pass
[ ] EF tests pass
[ ] package consumers compile on all configured TFMs
[ ] analyzers/API checks pass
[ ] README current examples are v2-only
[ ] architecture docs are v2-only for current API
[ ] EF docs teach MaterializeTo, not explicit mapping
[ ] CHANGELOG records breaking cleanup
[ ] repository-wide legacy-token audit completed and classified
[ ] no concurrency semantics changed
[ ] no YAML/source-generator scope added
[ ] no unrelated cleanup mixed in
```

---

# 25. Final report format

When finished, report exactly these sections:

```text
1. Commits created
2. Public v1 symbols removed
3. Symbols inspected but intentionally kept, with reasons
4. v1 -> v2 migration mapping
5. Shared implementation primitives preserved
6. Tests converted rather than deleted
7. Full build/test commands and results
8. PublicAPI before/after summary
9. Remaining `.Using/.Compute/.Incrementally/CreateLegacy/legacy` search matches and why each is allowed
10. Breaking-change documentation updated
11. Stop conditions encountered (or “none”)
12. Anything still preventing release candidate
```

Do not merely say “all legacy APIs removed”. Show the symbol list and test evidence.

---

# 26. Final mental model

You are removing **synonyms**, not capabilities.

Before:

```text
v1 syntax ─┐
           ├── shared compiled graph/runtime
v2 syntax ─┘
```

After:

```text
v2 syntax ─── shared compiled graph/runtime
```

You are NOT doing this:

```text
remove v1 syntax
     ↓
accidentally remove shared engine capability
     ↓
reimplement capability differently for v2
```

The successful end state has less public surface and essentially the same proven runtime semantics.

If removal changes what the graph means, how invalidation works, how incremental aggregates execute, how repair dispatches, or how materialization behaves, the removal has gone too far.