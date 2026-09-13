# Raffinert.Relations — Development Roadmap After Dependency Analysis

## Context

The current `main` branch already implements the original proof of concept and most of the first architecture roadmap:

- declarative binary relations;
- expression dependency analysis;
- scan/hash access planning;
- composite and comparer-aware equality keys;
- arbitrary-depth reverse reference navigation;
- `ChangeSet` processing;
- access and semantic impact;
- derived values with `Fresh`, `Dirty`, and `Invalid` states;
- invariant evaluation and policies;
- role-aware dependency analysis for relation, derived, and invariant expressions;
- common LINQ dependency extraction;
- EF Core scalar-change adapter;
- randomized relation equivalence tests;
- benchmarks.

The most recent milestone closed an important correctness gap: dependencies used only inside `Derived.Compute(...)` or `Invariant.Must(...)` are now visible to the engine.

The next phase should **not** primarily add more expression syntax. The engine now needs to become precise about *which source instances* are affected, formalize runtime/lifecycle semantics, and separate internal consistency maintenance from application-side actions.

---

# 1. P0 — Exact relation deltas and selective derived invalidation

**Status:** Implemented.

Relations consumed by derived state now maintain internal bidirectional membership, produce added/removed pair deltas, and propagate membership and relation-item changes only to affected source roots. Coverage includes additions, removals, lost/gained membership, residual changes, and a 10,000-source precision regression.

This is the highest-priority next feature.

The current engine can determine that a relation or a relation item was affected, but relation-item changes can still conservatively invalidate every cached source value for a derived definition.

For realistic models this does not scale:

```text
100,000 PO lines
1 GoodsReceiptLine.Quantity changed
```

must not invalidate all 100,000 derived `ReceivedQuantity` values.

## Goal

For every relation consumed by derived state, determine exactly which source roots changed membership or contain a changed relation item.

Introduce an internal concept such as:

```text
RelationDelta<TLeft, TRight>

AddedPairs
RemovedPairs
ChangedPairs / AffectedPairs
```

The exact API can differ, but the runtime must be able to answer:

```text
Which left/source objects were affected by this right/item mutation?
```

including when a pair was **removed** by the mutation.

## Recommended implementation strategy

Materialize membership only for relations that need exact propagation, especially relations consumed by `DerivedState`.

Maintain both directions:

```text
left  -> related right set
right -> related left set
```

Do not materialize every relation by default if it is used only for ad-hoc queries.

## Required mutation handling

Cover:

```text
right item added
right item removed
right indexed key changed
right residual-property changed
left indexed key changed
left residual-property changed
nested dependency changed
reference navigation changed
```

For a change that can alter membership:

```text
before relation members
    vs
current relation members
    -> delta
```

The delta must include both gained and lost source roots.

## Acceptance tests

1. `item.Quantity` used only by a derived computation dirties only source roots currently containing that item.
2. item join-key change from `A -> B` invalidates both sources that lost and sources that gained the item.
3. residual predicate change (`Enabled true -> false`) invalidates only sources that lost membership.
4. right-item removal invalidates only sources that previously referenced it.
5. item addition invalidates only sources it newly matches.
6. 10k unrelated source caches remain `Fresh` after one local mutation.

## Completion gate

No derived definition should require global cache invalidation merely because one relation item changed.

---

# 2. P0 — Source-scoped relation impact as a first-class model

Today `AccessImpact` and `SemanticImpact` mainly expose counts. Internally, source-root information exists in several separate dictionaries/sets.

Make source-scoped impact explicit.

Suggested internal shape:

```text
RelationImpact
  Relation
  AddedSources
  RemovedSources
  ChangedSources
  ReindexedRightRoots
```

or a semantically equivalent model.

This should become the bridge between:

```text
Change routing
    -> Relation delta
    -> Derived invalidation
    -> Invariant impact
```

Avoid having each higher-level feature rediscover affected roots independently.

## Completion gate

`DerivedState` and invariants consume source-scoped relation impact directly instead of relying on global `relationAffected` booleans.

---

# 3. P0 — Clean lifecycle state when root objects are removed

Derived/invariant runtime dictionaries can retain source references after the source object is removed from its object set.

Add explicit source lifecycle propagation.

Possible internal contract:

```text
OnSourceAdded(source)
OnSourceRemoved(source)
```

or object-set lifecycle events consumed by dependent runtime nodes.

On source removal clean:

```text
Derived cache entry
Invariant state entry
Materialized relation membership
Pending repair requests
Any source-scoped dependency state
```

## Tests

- compute a derived value;
- evaluate an invariant;
- remove the source;
- verify all source-scoped runtime state is gone;
- re-add the same reference, if supported, and verify it begins as a clean source;
- repeated add/remove does not cause retained runtime state growth.

## Completion gate

Removing a root object removes all engine-owned source-scoped state associated with it.

---

# 4. P0 — Formalize `Dirty` and `Invalid` transitions

The enum exists and current behavior is useful, but semantics should now become part of the library contract.

Recommended meaning:

```text
Fresh
  computed from all accepted current dependencies

Dirty
  cached value may be stale and may be lazily recomputed

Invalid
  cached/dependent result must not be used for correctness-sensitive decisions
  before recomputation/revalidation
```

Required transitions:

```text
Fresh   -> Dirty
Fresh   -> Invalid
Dirty   -> Invalid
Dirty   -> Fresh       after recomputation
Invalid -> Fresh       after successful recomputation/revalidation
```

Recommended rule:

```text
Invalid -> Dirty
```

must **not** happen because a later weaker change occurred. `Invalid` is stronger.

Add a state-transition test matrix for both derived state and invariant state.

---

# 5. P1 — Separate core runtime commit from policy side effects

`ScheduleRepairWith(Action<TSource>)` currently allows arbitrary user code to execute from inside dependency propagation.

That makes change-application failure semantics difficult:

```text
indexes committed
cache states changed
user callback throws
Apply(...) throws
```

The runtime should not pretend this is one transactional operation.

## Target processing model

```text
1. Validate ChangeSet
2. Resolve impact
3. Prepare relation/navigation updates
4. Commit core runtime state
5. Produce policy actions / repair requests
6. Application dispatches actions
```

Introduce an output model such as:

```text
RuntimeApplyResult
  ChangeImpact
  RepairRequests
  ImmediateEvaluations / PolicyActions (if needed)
```

Prefer data over callbacks in the core.

Possible request:

```csharp
RepairRequest<TSource>
```

with invariant identity and source.

Keep callback-based convenience APIs only outside the core commit path if desired.

## Completion gate

No arbitrary application delegate executes while relation/navigation/index state is being committed.

---

# 6. P1 — Tighten `ChangeSet` validation and document atomicity

A `ChangeSet` describes mutations that have already occurred on domain objects, so it cannot roll back domain state.

Document exact semantics:

> All changes are validated before runtime state is changed. Runtime-maintained indexes and dependency state are updated as one logical batch. Domain object mutation is external to this transaction.

## Strict new-value validation

Add an optional strict/debug mode that verifies:

```text
current member value == reported NewValue
```

before commit.

This catches integration errors such as:

```csharp
runtime.Apply(Change.Property(order, x => x.Number, "A", "B"));
// order.Number is still "A"
```

Also consider validating duplicate/conflicting changes in one batch:

```text
same instance/member changed twice
A -> B and A -> C
inconsistent old/new chains
```

Normalize valid chains when useful:

```text
A -> B
B -> C
```

can be treated as one logical `A -> C` dependency mutation if intermediate state is not observable.

---

# 7. P1 — Introduce a unified dependency graph runtime

Relations, derived states, invariants, navigation paths, and policies now form a genuine dependency graph.

Avoid growing more special-case loops inside `RelationRuntime.Apply`.

Introduce a focused internal graph, not a generic graph library.

Possible nodes:

```text
MemberDependencyNode
RelationNode
DerivedNode
InvariantNode
PolicyNode
```

Edges carry enough context to preserve affected source roots.

Target:

```text
Member change
   -> relation impact
   -> derived source impact
   -> invariant source impact
   -> policy request
```

## Design rule

Adding a new consumer type later should require registering another dependency node/edge type, not another top-level loop in `RelationRuntime.Apply`.

Do this after exact relation deltas exist; otherwise the graph will only encode conservative propagation.

---

# 8. P1 — Complete LINQ dependency-analysis coverage pragmatically

The new shared analyzer supports the important aggregate/filter/projection operators, but common useful forms remain.

Add deliberately, with tests:

```text
First
FirstOrDefault
Single
SingleOrDefault
Last
LastOrDefault
Distinct
Take
Skip
Contains
```

For each operator decide whether it implies:

```text
membership dependency
item-property dependency
ordering dependency
external/opaque dependency
```

Do not mark unsupported operators as complete.

Also add explicit support/tests for indexer-based access when reasonable:

```csharp
matches[0].Quantity
```

If semantics are difficult to track precisely, classify as opaque rather than guessing.

---

# 9. P1 — Collection navigation as a modeled change source

Reference navigation is now strong enough; collection navigation is the next major modeling capability.

Target expressions:

```csharp
order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber)
```

and dependency paths such as:

```text
PurchaseOrder.Lines[*].Quantity
```

Do not attempt to magically observe arbitrary `IEnumerable` mutation.

Introduce explicit changes first:

```csharp
Change.CollectionAdd(order, x => x.Lines, line)
Change.CollectionRemove(order, x => x.Lines, line)
Change.CollectionReset(...)
```

Maintain navigation indexes:

```text
owner -> items
item  -> owners
```

Later adapters can translate:

```text
EF relationship changes
ObservableCollection
custom domain events
```

## Completion gate

Adding/removing a collection member updates relations, derived values, and invariants incrementally without rescanning all roots.

---

# 10. P1 — Mature the EF Core adapter into a unit-of-work adapter

Current integration handles modified scalar properties. Expand in stages.

## Stage A — entity lifecycle

Support:

```text
EntityState.Added   -> runtime.Add
EntityState.Deleted -> runtime.Remove
```

Require explicit object-set mapping when one CLR type belongs to more than one modeled set.

## Stage B — reference/FK changes

Translate EF relationship changes into navigation `PropertyChange` or future collection changes.

## Stage C — owned/complex types

Map nested owned/complex property changes into the same dependency path model.

## Stage D — SaveChanges lifecycle

Document and test exact integration semantics around:

```text
DetectChanges
runtime apply
SaveChanges
AcceptAllChanges
transaction failure
```

Do not automatically mutate runtime state permanently before a database transaction is known to have succeeded unless rollback/reconciliation is defined.

A SaveChanges interceptor may become useful later, but keep it in the adapter package.

---

# 11. P2 — Stable public object-set handle

`ObjectSetBuilder<T>` still serves both as fluent build-time configuration and runtime identity handle.

Before a public alpha, separate those roles.

Target user experience can remain fluent:

```csharp
ObjectSet<InvoiceLine> invoices = model.Objects<InvoiceLine>()
    .Key(x => x.Id);
```

Possible internal split:

```text
ObjectSetBuilder<T>     internal mutable construction state
ObjectSet<T>            public stable model handle
ObjectSetDefinition<T>  compiled metadata
ObjectSetRuntime        mutable runtime population
```

This will make the public API easier to evolve and reason about.

---

# 12. P2 — Direction-aware relation planning

`RelatedFromRight(...)` is available but currently does not necessarily have an index optimized for that direction.

Add planner direction metadata:

```text
ForwardOnly
ReverseOnly
Bidirectional
Automatic
```

Derived-state consumption can influence planning because it naturally requires left/source-scoped impact.

Do not enable two indexes everywhere by default.

Benchmark memory and update cost before selecting an `Automatic` policy.

---

# 13. P2 — Compile null-safe path accessors

`MemberPath.Read(...)` is correct and null-safe but reflective segment-by-segment access is a hot-path cost.

Compile once per path:

```text
MemberPath
  -> Func<object, object?>
```

The generated expression must preserve null propagation at every reference segment.

Use for:

```text
hash-key extraction
navigation indexing
change routing
relation queries
```

Benchmark before and after.

Do not introduce a source generator unless compiled expression accessors are proven insufficient.

---

# 14. P2 — Range planning only after measurement

Range expressions are already recognized diagnostically.

Do not build a general interval-tree subsystem yet.

First benchmark realistic patterns such as:

```csharp
rule.SupplierId == invoice.SupplierId &&
rule.ValidFrom <= invoice.Date &&
invoice.Date < rule.ValidTo
```

The existing hash equality prefix already narrows candidates efficiently in many cases.

Implement a true range plan only if benchmarks demonstrate a meaningful need.

Potential first implementation:

```text
Hash prefix -> sorted range candidates -> full predicate
```

Always preserve forced-scan equivalence tests.

---

# 15. P2 — Expand randomized correctness testing from relations to the full graph

The core property remains:

```text
optimized behavior == reference behavior
```

Create a slow reference model for tests and compare not only relation query output but:

```text
Derived values
Derived state
Invariant result/state
Affected source set
Relation deltas
Repair requests
```

Random operations should include:

```text
add/remove roots
join-key changes
residual-property changes
nested navigation replacements
null transitions
relation-item value changes
source-only derived dependencies
invariant-only dependencies
collection changes when implemented
```

Use deterministic seeds and print the operation trace on failure.

This should become one of the project's strongest correctness assets.

---

# 16. P2 — Benchmarks for propagation precision

Existing benchmarks focus mostly on relation operations.

Add benchmarks that reveal whether the engine remains incremental:

```text
1 affected source among 10k / 100k
Derived item-value mutation
Join-key mutation with one lost + one gained source
Deep nested leaf change
Invariant-only nested source change
ChangeSet of 1 / 10 / 100 changes
Multiple derived states sharing one relation
Multiple relations sharing one navigation edge
```

Measure:

```text
time
allocations
affected source count
```

A regression from O(local impact) toward O(total model size) should be visible immediately.

---

# 17. P2 — CI and package-quality baseline

Before publishing even an experimental NuGet alpha, add GitHub Actions for:

```text
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release
```

Add package metadata:

```text
PackageId
Description
RepositoryUrl
PackageLicenseExpression
Authors
PackageTags
GenerateDocumentationFile
```

Enable stricter build quality gradually:

```text
TreatWarningsAsErrors
API compatibility checks later
nullable warnings clean
```

Do not publish automatically until the public API is intentionally reviewed.

---

# 18. P2 — Documentation around the real value proposition

The README should evolve from a feature list into a concise explanation of the problem solved:

```text
Business relation expressed once
    -> dependency extraction
    -> access planning
    -> incremental impact
    -> derived consistency
    -> invariant consistency
```

Add one meaningful end-to-end PO/Invoice/GR-style example showing why this is different from:

```text
validation libraries
specification pattern
LINQKit
EF Core query helpers
rule engines
```

Avoid promising automatic observation of arbitrary domain changes; clearly explain that adapters or explicit `Change`/`ChangeSet` reports provide mutation information.

---

# Recommended execution order

Execute the next work in this order:

```text
1. Exact relation delta / selective source invalidation
2. Source-scoped relation impact model
3. Source-removal lifecycle cleanup
4. Formal Dirty/Invalid state-transition contract
5. Separate runtime commit from repair/policy side effects
6. Tighten ChangeSet validation and semantics
7. Introduce unified dependency graph runtime
8. Complete high-value LINQ dependency forms
9. Collection navigation + collection change model
10. Mature EF Core adapter
11. Stable ObjectSet<T> public handle
12. Direction-aware planning
13. Compiled null-safe member-path accessors
14. Full-graph randomized reference-model testing
15. Propagation benchmarks
16. Range access planning only if justified
17. CI/package baseline
18. Public docs/API review
```

---

# Next Codex task — implement only this

Do **not** ask Codex to implement the whole roadmap in one commit.

The next task should be:

> Implement exact source-scoped invalidation for relations consumed by `DerivedState`.

## Scope

1. Introduce an internal materialized relation-membership state only for relations that require exact dependent propagation.
2. Maintain both:
   - `left -> right members`;
   - `right -> left members`.
3. Produce an internal relation delta containing at least added and removed `(left,right)` pairs or equivalent affected left/source roots.
4. Handle:
   - right add/remove;
   - join-key change;
   - residual predicate change;
   - nested property/navigation changes already supported by the engine.
5. Replace global derived invalidation for relation membership changes with invalidation of only affected source roots.
6. Replace global derived invalidation for `RelationItem` computation dependencies with only source roots whose related item set contains the changed item.
7. Preserve the distinction:
   - membership/access changes may mark `Invalid`;
   - computation-only relation-item value changes normally mark `Dirty`.
8. Do not change public API unless necessary.
9. Add regression tests covering lost membership and gained membership separately.
10. Add a scale test with many cached sources proving one item mutation does not dirty unrelated sources.
11. Keep forced-scan and optimized relation semantics equivalent.
12. Run the complete solution build/tests.

## Explicitly out of scope for this task

Do not implement yet:

```text
collection navigation
EF Added/Deleted support
new repair-request public API
ObjectSet<T> API refactor
range index
source generator
thread safety
```

## Completion gate

Given many cached sources, a mutation of one relation item changes state only for source roots whose derived result can actually be affected, including roots that lose relation membership.

---

# Architectural rule to preserve

The central rule remains:

> The user declares relationship/consistency semantics once. The framework derives dependency paths, access strategies, affected roots, and consistency propagation from those declarations.

Do not solve precision problems by forcing ordinary users to duplicate expression knowledge through `.DependsOn(...)`, `.InvalidateOn(...)`, or manually maintained indexes.

Escape hatches are acceptable for opaque code; duplication must not become the default model.
