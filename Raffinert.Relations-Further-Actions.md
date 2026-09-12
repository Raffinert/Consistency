# Raffinert.Relations — Further Actions

## Purpose

This document defines the next implementation sequence after the first large prototype milestone.

The current repository already contains much more than the original proof of concept:

- relation expression analysis;
- scan and hash-join planning;
- composite and comparer-aware hash keys;
- arbitrary-depth reverse reference navigation;
- semantic/access change impact;
- batched `ChangeSet` processing;
- derived state;
- invariants;
- EF Core change-tracker integration;
- randomized equivalence tests;
- benchmarks.

The next work should therefore focus on **correctness of the dependency graph, selective propagation, lifecycle semantics, collection relationships, and API maturity** rather than adding more unrelated features.

---

# 1. Highest-priority correctness gap: analyze `DerivedState` computations

Today a derived state is invalidated primarily because its backing relation was affected or because some property on the source object set changed.

That is not enough.

Consider:

```csharp
var receiptsForLine = model.Relation(poLines, receipts)
    .Where((po, receipt) =>
        po.PurchaseOrderId == receipt.PurchaseOrderId &&
        po.LineNumber == receipt.PurchaseOrderLineNumber &&
        !receipt.IsCancelled);

var receivedQuantity = model.Derived(poLines)
    .Using(receiptsForLine)
    .Compute((po, receipts) =>
        receipts.Sum(receipt => receipt.Quantity));
```

`GoodsReceiptLine.Quantity` does not affect relation membership.

Changing only:

```csharp
receipt.Quantity
```

must nevertheless invalidate `receivedQuantity`.

The current engine does not yet derive this dependency from the computation expression.

## Required design

Introduce dependency analysis for derived computations.

The analyzer must distinguish at least:

```text
Derived source dependency
Derived relation-item dependency
Relation membership dependency
External / opaque dependency
```

For:

```csharp
(po, receipts) =>
    receipts.Sum(receipt => receipt.Quantity) + po.Adjustment
```

extract:

```text
Source:
  PurchaseOrderLine.Adjustment

Relation items:
  GoodsReceiptLine.Quantity

Membership:
  receipts collection itself
```

## Initial LINQ coverage

Support dependency extraction from common operators first:

```text
Count
Any
All
Sum
Min
Max
Average
Select
Where
First/FirstOrDefault
Single/SingleOrDefault
OrderBy/ThenBy
```

The purpose is dependency extraction, not expression rewriting.

If a computation contains an unsupported/opaque call, preserve semantic execution but mark dependency tracking incomplete.

## Tests

Add explicit regression tests:

1. item scalar used only by `.Compute(...)` becomes dirty after change;
2. nested item property used only by computation becomes dirty;
3. source property used by computation dirties only that source;
4. opaque computation is marked dependency-incomplete;
5. scan/hash relation choice does not affect derived-state correctness.

## Completion gate

A derived value must never remain `Fresh` after any tracked dependency of its computation changes.

---

# 2. Analyze invariant predicates independently

Invariant dependencies are currently effectively inherited through the derived state plus conservative source invalidation.

That loses precision and misses nested dependencies that exist only in the invariant expression.

Example:

```csharp
model.Invariant(poLines)
    .Using(receivedQuantity)
    .Must((po, received) =>
        received <= po.Order.Policy.MaxReceivedQuantity);
```

The engine must understand:

```text
PurchaseOrderLine.Order
PurchaseOrder.Policy
Policy.MaxReceivedQuantity
```

as invariant dependencies even if they are irrelevant to both the relation and the derived computation.

## Required work

Create reusable expression dependency analysis that can operate over parameter roles instead of being relation-specific.

Conceptually:

```text
ExpressionDependencyAnalyzer

parameter 0 → Source
parameter 1 → DerivedValue
nested lambda parameter → RelationItem
```

The same lower-level path model should be reusable by:

```text
Relation
DerivedState
Invariant
```

Do not create three unrelated dependency systems.

## Completion gate

Changing a nested property referenced only by an invariant must update that invariant's state without unnecessarily invalidating unrelated derived values.

---

# 3. Replace global derived-state invalidation with per-source invalidation

Current invalidation is intentionally conservative: when a relation is affected, all cached values for a derived definition can be dirtied/invalidated.

That is correct for a prototype but does not scale.

For 100,000 purchase-order lines, changing one goods receipt must not invalidate 100,000 cached derived values.

## Target API internally

Move from:

```text
DerivedRuntimeState.Invalidate(bool invalid)
```

toward:

```text
Invalidate(source, state)
Invalidate(sources, state)
```

Likewise invariant propagation should accept affected source roots rather than operate on all sources.

## Key problem: old and new relation membership

When a right-side relation item changes, selective invalidation must know both:

```text
sources that matched before
sources that match after
```

Only checking the post-change relation is insufficient because a source can lose a match.

## Recommended solution: relation deltas

Introduce an internal concept:

```text
RelationDelta
  AddedPairs
  RemovedPairs
  PossiblyChangedPairs
```

Do not necessarily materialize every relation globally.

Start by materializing membership only for relations consumed by `DerivedState`, where exact deltas provide clear value.

Possible internal state:

```text
left -> related right set
right -> related left set
```

for materialized relations.

This also gives exact source roots for:

```text
DerivedState invalidation
Invariant impact
Repair scheduling
```

## Completion gate

A change affecting one source root invalidates only that source root's derived/invariant state, including cases where a relation pair is removed.

---

# 4. Introduce a real dependency-node graph

The project now contains relations, derived state, invariants, navigation paths, and impact routing.

The next structural step should make those concepts part of one graph rather than wiring higher-level behavior manually inside `RelationRuntime.Apply`.

Target conceptual graph:

```text
Object member
     │
     ▼
Relation membership
     │
     ▼
Derived value
     │
     ▼
Invariant
     │
     ▼
Policy / repair request
```

Suggested internal abstractions:

```text
DependencyNode
DependencyEdge
ImpactTarget
DependencyGraph
```

Possible node kinds:

```text
MemberDependencyNode
RelationNode
DerivedNode
InvariantNode
```

Do not build a generic graph framework.

The graph only needs targeted forward impact traversal with source-root context.

## Completion gate

Adding a new dependency consumer should not require special-case loops in `RelationRuntime.Apply`.

---

# 5. Define exact `Dirty` vs `Invalid` semantics

The enum exists, but the state model should now be formally specified and tested.

Recommended semantics:

## `Fresh`

The value was computed from all currently accepted dependencies.

## `Dirty`

The cached value may be stale, but no correctness guarantee has been explicitly revoked.

It can normally be recomputed lazily.

## `Invalid`

The cached value or dependent artifact must not be treated as valid for correctness-sensitive decisions until recomputed/revalidated.

This state should survive until successful recomputation/revalidation.

## Required state-transition tests

Cover:

```text
Fresh -> Dirty
Fresh -> Invalid
Dirty -> Invalid
Invalid -> Fresh after recomputation
Dirty -> Fresh after recomputation
```

Also explicitly define whether:

```text
Invalid -> Dirty
```

is allowed. Recommended: no; `Invalid` is stronger and should not be weakened by a later low-severity change.

---

# 6. Clean up derived/invariant runtime lifecycle on object removal

Derived and invariant runtime dictionaries hold source object references.

When a source object is removed from its object set, all source-scoped runtime state should be removed too.

Introduce lifecycle hooks such as:

```text
OnSourceAdded
OnSourceRemoved
```

or a generic object-set lifecycle mechanism.

Clean up:

```text
DerivedRuntimeState cache entries
InvariantRuntimeState entries
Materialized relation memberships
Pending repair requests
```

## Tests

- compute/evaluate a source;
- remove it;
- verify runtime state no longer retains it;
- re-add the same instance if supported and ensure it starts with clean state.

This is both a correctness and memory-retention requirement.

---

# 7. Tighten `ChangeSet` semantics

The current API correctly documents that domain objects are mutated before `Change.Property(...)` is applied.

That means `ChangeSet` cannot literally roll back domain state.

Clarify what "atomic" means.

Recommended contract:

> All changes in a `ChangeSet` are validated before relation runtime indexes are mutated. Runtime index maintenance is committed as one logical batch. Domain-object mutation itself is outside the runtime transaction.

## Validate reported new values

Currently `OldValue` and `NewValue` are useful metadata but runtime maintenance primarily rereads the current object graph.

Add optional strict validation that:

```text
current member value == reported NewValue
```

before applying a change.

This catches a common integration bug:

```csharp
runtime.Apply(Change.Property(x, p, old, new));
// but x.p was not actually set to new
```

Consider debug/strict mode first if unconditional validation is too expensive.

## Separate commit from user callbacks

Immediate invariant evaluation and repair callbacks may execute user code after internal indexes start changing.

User code can throw.

Do not claim transaction-level atomicity across arbitrary callbacks.

Prefer this shape:

```text
Validate
Prepare
Commit internal runtime state
Produce impact / repair requests
Run optional external policy dispatcher
```

Core state mutation should remain deterministic and side-effect-contained.

---

# 8. Replace direct repair callbacks with repair requests

`ScheduleRepairWith(Action<TSource>)` is convenient but executes arbitrary application code inside runtime change propagation.

A more composable production model is:

```text
Invariant impact
    ↓
RepairRequest<TSource>
    ↓
application scheduler / queue / handler
```

Possible API direction:

```csharp
var result = runtime.Apply(changeSet);

foreach (var request in result.RepairRequests)
    scheduler.Schedule(request);
```

or expose a configurable dispatcher outside the core transaction.

Benefits:

- no arbitrary user exception during internal commit;
- easier async scheduling;
- easier testing;
- future message-bus integration;
- deterministic runtime state transitions.

Keep direct callbacks only as a convenience layer if desired.

---

# 9. Expand EF Core integration from a scalar adapter to a real unit-of-work adapter

The current adapter translates modified scalar properties.

That is a useful starting point but incomplete for real EF Core usage.

Add support step by step.

## 9.1 Modified scalar properties

Keep current behavior and add repeated-call semantics documentation.

## 9.2 Added entities

Translate tracked `Added` root entities into runtime additions.

This requires mapping EF entity CLR types to model object sets.

Do not guess when multiple object sets use the same CLR type.

Provide explicit configuration when ambiguous.

## 9.3 Deleted entities

Translate `Deleted` entities into runtime removal.

## 9.4 Reference navigation changes

Handle relationship changes that are represented by navigation/FK fixup.

The relation engine may depend on:

```text
line.PurchaseOrder.Number
```

so changing `line.PurchaseOrder` must be observable even if application code did not manually create a `Change.Property` for the navigation.

## 9.5 Owned/complex types

Define how EF owned/complex value changes map into nested dependency paths.

## 9.6 Transaction boundary

Provide a recommended integration point, for example:

```text
Before SaveChanges
After DetectChanges but before AcceptAllChanges
```

Document exactly when the runtime should be updated relative to database commit/failure.

Longer term consider SaveChanges interception, but keep it in the adapter package.

---

# 10. Collection navigation support

This is the next major modeling capability after correctness/selective invalidation.

Target cases:

```csharp
order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber)
```

and dependencies such as:

```text
PurchaseOrder.Lines[*].Quantity
```

Do not implement this as generic reflection over every `IEnumerable` mutation.

Define a collection-change model first:

```text
ItemAdded
ItemRemoved
CollectionReset
```

Potential API:

```csharp
Change.CollectionAdd(order, x => x.Lines, line)
Change.CollectionRemove(order, x => x.Lines, line)
```

Then provide adapters later for:

```text
ObservableCollection
EF Core relationship tracking
custom domain events
```

## Collection navigation indexes

Maintain both:

```text
owner -> items
item -> owners
```

with reference identity unless explicit value semantics are configured.

## Completion gate

Adding/removing a collection item propagates relation/derived/invariant impact without rebuilding the entire model runtime.

---

# 11. Improve bidirectional relation access

`RelatedFromRight(...)` currently provides the API but can fall back to scanning all left objects.

Introduce planner policy for directional indexing.

Possible policy:

```text
ForwardOnly
ReverseOnly
Bidirectional
Automatic
```

Default can remain memory-conscious.

For relations consumed by derived state or frequent reverse queries, maintaining both indexes may be justified.

Use benchmarks to decide automatic thresholds later; do not encode guesses yet.

---

# 12. Compile null-safe member accessors instead of reflecting on every read

`MemberPath.Read(...)` is now null-safe, but reads each segment through reflection.

That is correct but becomes a hot path for:

```text
hash-key extraction
relation queries
reindexing
navigation traversal
```

Generate/compile a null-safe accessor once per path.

Conceptually:

```text
MemberPath
    ↓ build-time/runtime compilation once
Func<object, object?>
```

Preserve null propagation.

Benchmark before/after.

Do not add a source generator yet unless expression-compiled accessors prove insufficient.

---

# 13. Add range access planning only after dependency correctness work

Range metadata already exists diagnostically.

After the dependency graph and selective invalidation are stable, add a first ordered/range plan.

Candidate expressions:

```csharp
rule.ValidFrom <= invoice.Date &&
invoice.Date < rule.ValidTo
```

Potential structures:

```text
SortedDictionary
binary-searchable sorted arrays
interval tree
```

Do not overgeneralize initially.

A good first plan is:

```text
hash equality prefix + residual range filter
```

Example:

```text
SupplierId equality -> hash bucket
Date interval         -> residual predicate
```

Only implement a true range index after benchmarks show a real need.

---

# 14. Strengthen optimizer correctness testing

Keep the central property:

```text
optimized result == forced scan result
```

Expand randomized testing to include:

```text
nested navigation changes
null navigation transitions
relation-item property changes
source property changes
collection add/remove (when implemented)
string comparer variants
multiple relations sharing paths
multiple derived states sharing relations
invariants with source-only dependencies
```

Add deterministic random seeds to failures so every mismatch is reproducible.

Consider FsCheck or CsCheck later, but a custom deterministic generator is sufficient initially.

---

# 15. Benchmark the actual dependency engine, not only relation lookup

The current benchmark suite is a useful baseline.

Extend it with:

```text
Derived recomputation
Dirty propagation
Invalid propagation
Selective vs global invalidation
Deep leaf-property change
Relation-delta maintenance
ChangeSet with 1 / 10 / 100 changes
Multiple relations sharing one navigation edge
```

Track both:

```text
runtime
allocations
```

Add a benchmark baseline document with representative hardware/environment rather than committing absolute performance promises to README.

---

# 16. Public API cleanup before first NuGet alpha

The project is still early enough for breaking API improvements.

Review:

```text
ObjectSetBuilder<T>
Relation<TLeft, TRight>
Derived<TSource,TItem,TValue>
Invariant<TSource,TItem,TValue>
ChangeSet
ChangeImpact
```

## Object-set handle

`ObjectSetBuilder<T>` currently serves both as build-time fluent builder and runtime set handle.

Consider introducing stable public:

```csharp
ObjectSet<T>
```

and keeping mutable builder implementation internal.

Target feel:

```csharp
ObjectSet<InvoiceLine> invoices = model.Objects<InvoiceLine>()
    .Key(x => x.Id);
```

Runtime APIs should receive semantic handles rather than mutable builder objects.

## Naming review

Before alpha, review whether:

```text
Derived
Invariant
ReactWith
ScheduleRepairWith
```

communicate the final model clearly.

Do this before compatibility constraints matter.

---

# 17. Package and repository production readiness

The core `.csproj` currently contains only the basic framework/compiler settings.

Before publishing an alpha, add:

```text
PackageId
Description
Authors
RepositoryUrl
PackageTags
PackageLicenseExpression
GenerateDocumentationFile
ContinuousIntegrationBuild
Deterministic
SourceLink
```

Add package metadata to the EF Core adapter as well.

## CI

Add GitHub Actions for:

```text
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release
```

There is currently no repository workflow protecting `main`.

Optionally add a separate manually-triggered benchmark workflow rather than running full benchmarks on every commit.

## Static quality

Consider:

```text
TreatWarningsAsErrors in CI
API compatibility checks after first public alpha
format/analyzer enforcement
```

Do not freeze API compatibility before the public surface is reviewed.

---

# 18. Documentation and examples

After selective dependency propagation is implemented, create an `examples` project with one realistic domain.

Recommended example:

```text
PurchaseOrder
PurchaseOrderLine
InvoiceLine
GoodsReceiptLine
```

Demonstrate:

```text
relation definition
nested access
received quantity derived state
quantity invariant
change propagation
Dirty vs Invalid
repair request generation
EF Core adapter
```

This example should become the primary architecture test for whether the API remains understandable.

---

# 19. Recommended implementation order

Execute the next work in this order:

```text
1. Analyze DerivedState computation dependencies
2. Analyze Invariant predicate dependencies
3. Register derived/invariant dependency paths in navigation tracking
4. Add correctness tests for item-only and invariant-only dependencies
5. Introduce per-source derived invalidation APIs
6. Introduce exact relation/source impact or RelationDelta support
7. Make invariant propagation source-selective
8. Clean runtime state when source objects are removed
9. Formalize Dirty/Invalid state transitions
10. Tighten ChangeSet consistency/atomicity contract
11. Move repair callbacks outside core state commit
12. Expand EF Core adapter: Added / Deleted / reference navigation
13. Add collection change model
14. Add collection navigation indexing
15. Compile null-safe member-path accessors
16. Improve reverse-direction access planning
17. Expand randomized correctness tests
18. Expand benchmarks around dependency propagation
19. Review public API/ObjectSet handle
20. Add package metadata and CI
21. Add realistic PO/Invoice/GR example
22. Publish first alpha only after the above core correctness gates pass
```

---

# 20. Next Codex task — do only this first

Do **not** ask Codex to implement this whole document in one run.

The next task should be:

> Add dependency analysis for derived computations and invariants, fixing cases where changes outside the relation predicate currently fail to invalidate dependent state.

## Scope

1. Create reusable internal expression-dependency infrastructure instead of embedding all logic only inside `RelationExpressionAnalyzer`.

2. Preserve existing relation analysis behavior.

3. Analyze `Derived.Compute(...)` for:
   - source member dependencies;
   - relation-item member dependencies inside supported LINQ lambdas;
   - relation membership dependency;
   - opaque/external dependencies.

4. Initially support at least:

```text
Count
Any
Sum
Min
Max
Average
Select
Where
```

5. Analyze `Invariant.Must(...)` for source-member dependencies independently of the derived value.

6. Add required dependency paths to navigation-index registration even when those paths are not used by a relation predicate.

7. Keep invalidation conservative for now: it is acceptable to invalidate all cached values for a derived definition in this task. **Do not implement selective per-source invalidation yet.**

8. Add regression tests proving:

```csharp
matches.Sum(x => x.Quantity)
```

becomes dirty when `Quantity` changes even though relation membership does not change.

9. Add a nested relation-item dependency test.

10. Add an invariant-only nested source dependency test.

11. Preserve all current relation/query tests.

12. Run:

```bash
dotnet build
dotnet test
```

and leave the solution green.

## Non-goals for this Codex task

Do not implement yet:

```text
selective per-source invalidation
relation membership materialization
collection navigation
range indexes
new EF Core features
NuGet packaging
source generators
```

---

# 21. Second Codex task

After dependency correctness is fixed:

> Make derived/invariant propagation source-selective.

Design exact source-root impact first.

Prefer introducing explicit relation deltas or equivalent old/new membership tracking rather than trying to infer removed matches only from post-change object state.

Acceptance tests must include two independent source objects where a change affecting one source leaves the other's derived value `Fresh`.

---

# 22. Third Codex task

After selective invalidation:

> Formalize lifecycle and change-transaction semantics.

Scope:

```text
source cache cleanup on remove
Dirty/Invalid transition rules
ChangeSet validation contract
strict NewValue/current-value validation
post-commit repair requests instead of in-commit arbitrary callbacks
```

Only after this task should collection navigation be started.

---

# 23. Definition of done before collection navigation

Do not start collection navigation until:

- derived computations have tracked dependencies;
- invariant predicates have tracked dependencies;
- nested paths used only by derived/invariant expressions propagate correctly;
- per-source invalidation works;
- removed relation pairs invalidate the correct previous sources;
- source removal cleans dependent runtime state;
- Dirty/Invalid transitions are formally tested;
- `ChangeSet` runtime-commit semantics are documented and tested;
- arbitrary repair callbacks are no longer able to corrupt the internal commit boundary.

At that point the dependency engine is strong enough to generalize from reference navigation to collection navigation.

---

# 24. Architectural target

The next mature shape should be:

```text
                    Expressions
                        │
                        ▼
              Dependency extraction
                        │
        ┌───────────────┼────────────────┐
        ▼               ▼                ▼
    Relations      Derived State      Invariants
        │               │                │
        └───────────────┬┴────────────────┘
                        ▼
                 Dependency Graph
                        │
                Navigation indexes
                        │
                     ChangeSet
                        │
                   Impact resolver
                        │
                 Relation deltas
                        │
          selective source propagation
                        │
         ┌──────────────┴──────────────┐
         ▼                             ▼
  Dirty / Invalid                Repair requests
```

The long-term value of `Raffinert.Relations` is not merely faster relation lookup.

It is the ability to declare object-model semantics once and derive the machinery needed to keep dependent state consistent incrementally.
