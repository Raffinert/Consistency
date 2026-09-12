# Raffinert.Relations — Next Steps After the First Prototype

## Purpose

This document is the implementation plan for the next development stage of `Raffinert.Relations`.

The repository already proves the first core idea:

> Define object relationships once as expression trees, derive useful dependency/access metadata from those expressions, and maintain relation lookup incrementally.

The next stage should **not** jump directly to invariants or EF Core.

The priority is to make the relation/dependency engine semantically robust and structurally extensible enough that `DerivedState`, invalidation, and invariants can later be built on top without rewriting the runtime.

## 1. Current state

The current prototype already contains a meaningful vertical slice:

- typed object sets with stable keys;
- binary relation declarations;
- compiled predicates as semantic truth;
- direct and nested member-path analysis;
- safe equality-join extraction;
- composite hash indexing;
- scan fallback when no index can be derived;
- runtime add/remove;
- scalar property change application;
- reindexing after indexed property changes;
- one-reference reverse navigation for nested indexed paths;
- runtime relation queries;
- debug diagnostics;
- tests covering the main prototype scenarios.

Conceptually, this already works:

```text
Expression
    ↓
Relation analysis
    ↓
Join-key extraction
    ↓
Hash candidate index
    ↓
Full predicate verification
```

and:

```text
Property change
    ↓
Dependency lookup
    ↓
Affected relation runtime
    ↓
Reindex
```

This is a successful first prototype. The next goal is to turn it into a reliable foundation.

## 2. Main architectural decision

Do **not** implement `Invariant`, `DerivedState`, or EF Core next.

First complete this foundation:

```text
Expression
    ↓
Dependency model
    ↓
Access plan
    ↓
Navigation graph
    ↓
Change impact
    ↓
Atomic change propagation
```

Only then build:

```text
Derived state
    ↓
Fresh / Dirty / Invalid
    ↓
Invariant
    ↓
Repair / recomputation policy
```

`DerivedState` and invariants will depend heavily on correct dependency propagation. If the dependency/change layer remains special-cased around today's hash-index implementation, higher-level abstractions will force a redesign later.

## 3. Immediate cleanup: align public naming with the library

The repository is now named `Raffinert.Relations`, but the main public types still use old invariant-centric terminology:

```csharp
InvariantModelBuilder
CompiledInvariantModel
InvariantRuntime
```

Rename now, before the API spreads further.

Recommended names:

```csharp
RelationModelBuilder
CompiledRelationModel
RelationRuntime
```

Target usage:

```csharp
var model = new RelationModelBuilder();

var invoices = model.Objects<InvoiceLine>()
    .Key(x => x.Id);

var poLines = model.Objects<PurchaseOrderLine>()
    .Key(x => x.Id);

var matches = model.Relation(invoices, poLines)
    .Where((invoice, poLine) =>
        invoice.PurchaseOrderNumber == poLine.PurchaseOrderNumber &&
        invoice.ItemNumber == poLine.ItemNumber);

var compiled = model.Build();
var runtime = compiled.CreateRuntime();
```

## 4. Separate builder handles from compiled object-set handles

`ObjectSetBuilder<T>` currently also acts as the runtime handle passed to `runtime.Add(set, instance)`. That mixes two lifecycle roles: model construction and runtime identity.

Consider making the object set itself the stable public handle:

```csharp
ObjectSet<InvoiceLine> invoices =
    model.Objects<InvoiceLine>()
        .Key(x => x.Id);
```

Internally there may still be a builder. The important separation is:

```text
construction metadata
compiled metadata
public object-set identity
runtime state
```

Do not overcomplicate the fluent API purely to achieve this, but avoid making runtime behavior conceptually depend on a mutable builder.

## 5. Milestone 1 — semantic hardening

The central rule is:

> The original predicate is semantic truth. An access plan may narrow candidates but must never introduce false negatives or cause evaluation behavior the original predicate would not have caused.

### 5.1 Null-safe access-plan key extraction

Consider:

```csharp
(invoice, line) =>
    line.PurchaseOrder != null &&
    invoice.PurchaseOrderNumber == line.PurchaseOrder.Number
```

The predicate safely returns `false` when `line.PurchaseOrder == null`.

An independently evaluated index path such as:

```csharp
line.PurchaseOrder.Number
```

must therefore not throw during key extraction.

Required behavior:

- member-path readers used by access plans must null-propagate intermediate references;
- a null intermediate navigation should produce an internal null/missing key;
- the original predicate remains the final semantic authority;
- do not rewrite the original predicate.

Add tests proving:

- adding a line with a null navigation does not throw;
- querying does not throw;
- it does not match;
- changing `null -> PurchaseOrder` and reporting the reference change reindexes correctly.

### 5.2 Captured and static state must not be reported as fully tracked

Examples:

```csharp
var prefix = "A";
(a, b) => a.Code == b.Code && b.Value.StartsWith(prefix)
```

or:

```csharp
(a, b) => b.Value == SomeMutableStatic.Value
```

The dependency engine cannot receive normal object-property changes for arbitrary closure/static state.

Replace the current single completeness boolean with richer metadata, for example:

```text
Complete
ContainsExternalState
ContainsOpaqueCode
```

A flags enum or equivalent structured result is fine.

The relation can still be queried correctly by the original compiled predicate. The important requirement is that incremental-tracking guarantees are represented honestly.

### 5.3 Stable object keys

Object-set keys are described as stable. Make that contract explicit.

Recommended policy:

> A registered object's identity key is immutable while the object is registered.

If `Change.Property(...)` targets the declared key member, throw a clear exception instructing the caller to remove/re-add the object or use a genuinely stable key.

Also store the registered key beside the instance instead of rereading the object when removing it. This avoids hidden index corruption if the object was mutated before runtime notification.

Add tests for:

- notified key mutation is rejected;
- removal is still correct after accidental unreported key mutation;
- duplicate-key detection remains correct.

## 6. Milestone 2 — explicit access plans

The runtime currently effectively assumes:

```text
join keys exist → hash index
otherwise       → scan
```

Do not let that become the architecture.

Introduce:

```text
RelationExpressionAnalyzer
    ↓
RelationAnalysis
    ↓
RelationPlanner
    ↓
RelationAccessPlan
```

Initial plans:

```text
ScanAccessPlan
HashJoinAccessPlan
```

Later plans can include:

```text
RangeAccessPlan
CompositeAccessPlan
ComparerAwareHashPlan
```

Responsibilities must be separated:

### Expression analysis

Answers:

- what does the predicate reference?
- what recognized logical forms exist?
- which equalities could potentially be join keys?
- how complete is dependency analysis?

### Planner

Answers:

- which recognized information is safe and useful as an access strategy?

### Runtime plan state

Answers:

- which runtime indexes/data structures must be maintained?

Move decisions like:

```csharp
analysis.JoinKeyParts.Count > 0
```

out of runtime code and into the planner.

## 7. Milestone 3 — preserve complete dependency paths

Arbitrary-depth change propagation requires the whole path.

For:

```csharp
line.PurchaseOrder.Supplier.Country.Code
```

preserve:

```text
PurchaseOrderLine
    --PurchaseOrder-->
PurchaseOrder
    --Supplier-->
Supplier
    --Country-->
Country
    --Code-->
string
```

Do not reduce the core representation only to independent segments such as:

```text
PurchaseOrderLine.PurchaseOrder
PurchaseOrder.Supplier
Supplier.Country
Country.Code
```

because the runtime later needs the chain that connects a changed `Country` back to the relevant `PurchaseOrderLine`.

Suggested internal model:

```csharp
internal sealed record DependencyPath(
    Type RootType,
    IReadOnlyList<DependencyPathSegment> Segments);

internal sealed record DependencyPathSegment(
    Type DeclaringType,
    MemberInfo Member,
    Type ValueType);
```

Keep the complete path and derive segment-level metadata from it.

## 8. Milestone 4 — generic reverse navigation indexes

The current one-reference reverse index proves the concept. Replace the relation-local special case with a reusable runtime facility.

For:

```text
PurchaseOrderLine.PurchaseOrder
```

maintain:

```text
PurchaseOrder instance
    →
PurchaseOrderLine instances
```

For:

```text
PurchaseOrder.Supplier
```

maintain:

```text
Supplier instance
    →
PurchaseOrder instances
```

Then a deep change can propagate backwards:

```text
Country.Code changed

Country
  ↑
Supplier[]
  ↑
PurchaseOrder[]
  ↑
PurchaseOrderLine[]
```

### Share navigation indexes

If several relations depend on `PurchaseOrderLine.PurchaseOrder`, they should be able to reuse one model/runtime-level reverse navigation index instead of maintaining duplicate dictionaries.

### Make removal direct

Do not remove a root from a reverse index by scanning all buckets.

Track forward membership as well:

```text
PurchaseOrderLine #42
    →
PurchaseOrder #10
```

Then a reference change/removal can directly update the old reverse bucket.

Target expected complexity for one reference change should be approximately O(1) dictionary work per affected edge.

## 9. Milestone 5 — change routing as a subsystem

Move away from each relation runtime deciding independently what a `PropertyChange` means.

Target pipeline:

```text
Change
    ↓
ImpactResolver
    ↓
affected dependency roots
    ↓
affected access plans / semantic dependents
```

The architecture should later allow the same impact pipeline to feed:

```text
relation indexes
derived state
invariants
```

without wiring every new concept manually into every relation runtime type.

## 10. Distinguish access impact from semantic impact

This distinction is important.

### Access impact

A change modifies candidate indexing.

Example:

```text
PurchaseOrder.Number changed
```

when `Number` participates in a hash join key.

Action:

```text
reindex affected roots
```

### Semantic impact

A change may change relation membership without changing the candidate index.

Example:

```csharp
(invoice, line) =>
    invoice.PurchaseOrderNumber == line.PurchaseOrderNumber &&
    line.Enabled
```

Changing `line.Enabled` requires no hash reindex because the full predicate is reevaluated on query.

But future cached relations, derived state, and invariants must still know relation membership may have changed.

Model these separately:

```text
AccessImpact
SemanticImpact
```

"No reindex required" must not mean "nothing was affected."

## 11. Milestone 6 — nested changes without mandatory root object sets

A nested referenced object such as `PurchaseOrder` currently fits most naturally when it is separately registered as an object set.

That should not necessarily remain a fundamental requirement.

Long-term desired scenario:

```text
ObjectSet<InvoiceLine>
ObjectSet<PurchaseOrderLine>

PurchaseOrderLine
    → PurchaseOrder
```

and:

```csharp
runtime.Apply(
    Change.Property(
        order,
        x => x.Number,
        oldNumber,
        newNumber));
```

The navigation index should be able to find affected `PurchaseOrderLine` roots even if `PurchaseOrder` is not itself a root object set.

This makes object sets mean:

> root populations managed by the model

instead of:

> every CLR type that can emit a relevant change.

Implement this only after generic navigation indexes exist.

## 12. Milestone 7 — `ChangeSet` and atomic application

Introduce batched changes before invariants.

Possible API:

```csharp
var changes = ChangeSet.Create(
    Change.Property(...),
    Change.Property(...),
    Change.Property(...));

runtime.Apply(changes);
```

A domain operation may update several properties together. Applying each independently can expose transient states that are neither the old nor the final state.

For future invariants and repair this matters.

Suggested processing:

```text
1. validate all changes
2. resolve impact
3. update navigation indexes
4. update access indexes
5. mark semantic dependents
6. return/publish impact
```

Do not evaluate future invariant policies in the middle of a batch.

Consider returning an internal or diagnostic `ChangeImpact` result containing concepts such as:

```text
AffectedRelations
ReindexedRelations
AffectedRoots
```

This can later feed derived-state invalidation and invariant scheduling.

## 13. Milestone 8 — expression coverage, safely

After the architecture above is stable, expand recognized expression patterns.

Priority:

### Well-known string equality

Support exact semantics such as:

```csharp
string.Equals(a.Code, b.Code, StringComparison.Ordinal)
string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase)
```

A comparer-aware predicate requires a comparer-aware hash index. Never use default hashing if the predicate's equality semantics differ.

### Nullable/conversion normalization

Normalize harmless conversion nodes only where semantic equivalence is clear.

### Constant filters

Recognize expressions such as:

```csharp
b.Enabled
b.Status == Status.Active
```

for analysis/diagnostics even if they remain residual.

### Range metadata

Recognize:

```csharp
a.Date >= b.ValidFrom &&
a.Date < b.ValidTo
```

but leave it as residual execution initially. Recognition and optimization must remain separate.

## 14. Milestone 9 — consider bidirectional relation access

A mathematical relation is not inherently a one-way lookup.

Eventually consider APIs for both directions, for example:

```csharp
runtime.RelatedFromLeft(relation, invoice);
runtime.RelatedFromRight(relation, poLine);
```

Do not automatically double every index yet. Let future planner policy decide whether forward, reverse, or bidirectional indexes are justified.

Keep the relation abstraction neutral enough that reverse lookup remains possible.

## 15. Milestone 10 — `DerivedState`

Only after arbitrary-depth reference change propagation and atomic `ChangeSet` work correctly.

Target direction:

```csharp
var receiptsForLine =
    model.Relation(poLines, receipts)
        .Where((po, receipt) =>
            po.PurchaseOrderId == receipt.PurchaseOrderId &&
            po.LineNumber == receipt.PurchaseOrderLineNumber &&
            !receipt.IsCancelled);

var receivedQuantity =
    model.Derived(poLines)
        .Using(receiptsForLine)
        .Compute((po, receipts) =>
            receipts.Sum(x => x.Quantity));
```

Start with lifecycle states:

```text
Fresh
Dirty
```

but design for a distinct:

```text
Invalid
```

state.

## 16. `Dirty` and `Invalid` must stay different

`Dirty` means a cached derived value may be stale and can potentially be recomputed lazily.

`Invalid` means an existing dependent result may no longer be relied upon for correctness.

Example:

```text
PO/Invoice link created using UnitRate
    ↓
UnitRate input changes
    ↓
existing link = Invalid
```

Actual rematching may be deferred, but the old link must not remain treated as guaranteed valid.

Preserve:

```text
invalidation != recomputation
```

Do not collapse both into a single `NeedsRefresh` flag.

## 17. Milestone 11 — invariants

Only after derived-state invalidation works.

Target:

```csharp
model.Invariant(poLines)
    .Using(receivedQuantity)
    .Must((po, received) =>
        received <= po.Quantity);
```

Invariant impact must consume the same dependency graph. Do not create a second tracking mechanism.

Target architecture:

```text
Expression analysis
       │
       ▼
Dependency graph
       │
       ├── relation access maintenance
       ├── derived-state invalidation
       └── invariant impact
```

## 18. Milestone 12 — policy layer

Do not hard-code one reaction to an affected invariant.

Eventually support policies such as:

```text
evaluate immediately
mark dirty
mark invalid
recompute on access
schedule repair
reject operation
```

This allows domain distinctions such as:

```text
subtractive / correctness-sensitive change
    ↓
invalidate links immediately
    ↓
rematch later
```

versus:

```text
additive change
    ↓
new/better match may exist
    ↓
rematch may be deferred
```

The relation/dependency engine reports impact. The policy layer decides what to do.

## 19. EF Core comes after the core change model

Do not add EF Core until these are stable:

```text
Change
ChangeSet
dependency routing
nested navigation propagation
```

Then create a separate package:

```text
Raffinert.Relations.EntityFrameworkCore
```

Its narrow responsibility should be:

```text
EF Core ChangeTracker
    ↓
Raffinert ChangeSet
```

The core package should remain unaware of `DbContext`, `EntityEntry`, SQL, or EF metadata.

## 20. Performance work

Add a benchmark project once generic reverse navigation exists:

```text
benchmarks/
  Raffinert.Relations.Benchmarks/
```

Suggested scenarios:

```text
10k / 100k right-side objects
single-key hash relation
composite-key relation
one-level navigation relation
three-level navigation relation
scalar property reindex
reference navigation change
```

Measure:

```text
add throughput
relation lookup
change application
allocations
```

Protect especially against accidental O(N) work in single-object change propagation.

## 21. Runtime concurrency contract

Do not imply thread safety accidentally.

For early versions explicitly document:

> A `RelationRuntime` is not thread-safe. Mutations and queries must be externally synchronized.

This is better than adding locks throughout the prototype before real concurrency requirements exist.

## 22. Diagnostics should follow the planner

Evolve `DebugView` to show both analysis and access planning.

Example:

```text
Relation InvoiceLine -> PurchaseOrderLine

Predicate:
  ...

Dependency paths:
  InvoiceLine.PurchaseOrderNumber
  PurchaseOrderLine.PurchaseOrder.Number

Analysis:
  Equality join:
    InvoiceLine.PurchaseOrderNumber
      ==
    PurchaseOrderLine.PurchaseOrder.Number

Access plan:
  HashJoin

Navigation indexes:
  PurchaseOrderLine.PurchaseOrder
    reverse tracked

Dependency completeness:
  Complete

Residual predicate:
  Yes
```

For opaque/external-state expressions, diagnostics should explain why tracking is incomplete.

## 23. Test strategy

### Semantic-equivalence tests

For every access plan, compare results against forced scan evaluation over the same object graph.

Core property:

```text
indexed results == scan results
```

after arbitrary sequences of:

```text
add
remove
scalar change
reference change
nested property change
```

Randomized/property-based tests would be especially valuable.

### Navigation tests

Cover:

```text
null → object
object → null
object A → object B
depth 1
depth 2
depth 3
many roots → one referenced object
one path used by multiple relations
```

### `ChangeSet` tests

Cover:

```text
two indexed fields changing together
navigation + nested scalar change
remove + add in one batch
invalid change rejects whole batch
```

### External-state tests

Verify closure/static/opaque dependencies never claim complete tracking.

## 24. Recommended implementation order

Implement in this order unless a discovered correctness issue requires adjustment:

```text
1. Rename invariant-centric public types
2. Harden object-set key identity
3. Make access-path key reads null-safe
4. Represent dependency-analysis completeness explicitly
5. Introduce RelationAccessPlan abstraction
6. Preserve complete DependencyPath objects
7. Extract reusable NavigationIndex runtime
8. Replace one-level relation-local reverse navigation
9. Support arbitrary-depth reference navigation
10. Separate AccessImpact from SemanticImpact
11. Introduce ImpactResolver/change routing
12. Support nested-object changes without root ObjectSet registration
13. Add ChangeSet
14. Make batch application atomic
15. Add semantic-equivalence/randomized tests
16. Add benchmarks
17. Expand recognized equality/comparer semantics
18. Implement DerivedState
19. Implement Dirty vs Invalid propagation
20. Implement Invariant
21. Add EF Core adapter
```

Do not move steps 18–21 ahead of the dependency/change foundation.

## 25. Concrete next Codex task

The next Codex session should **not** attempt the whole roadmap.

Give it this task:

> Refactor the current prototype into a semantically hardened relation engine without adding high-level features.

Scope:

1. rename:
   - `InvariantModelBuilder` → `RelationModelBuilder`
   - `CompiledInvariantModel` → `CompiledRelationModel`
   - `InvariantRuntime` → `RelationRuntime`;

2. update README/tests/usages consistently;

3. store stable registered object keys rather than rereading keys on removal;

4. reject notified mutation of a registered object-set key;

5. make `MemberPath` access null-safe for intermediate reference members;

6. add tests proving a null-guarded nested relation cannot throw because of index key extraction;

7. replace the single `IsDependencyAnalysisComplete` boolean with metadata that distinguishes at least:
   - complete tracked dependencies;
   - opaque method calls;
   - captured/static external state;

8. add tests for closure/static analysis;

9. preserve:
   - the original compiled predicate as semantic truth;
   - scan fallback;
   - current hash-index behavior;
   - current one-level reverse navigation;

10. run:

```bash
dotnet build
dotnet test
```

and leave everything green.

Do **not** implement in this task:

```text
DerivedState
Invariant
EF Core
ChangeSet
arbitrary-depth navigation
range indexes
```

## 26. Second Codex task

After semantic hardening:

> Introduce access planning as a first-class internal abstraction.

Target:

```text
RelationExpressionAnalyzer
    ↓
RelationAnalysis
    ↓
RelationPlanner
    ↓
RelationAccessPlan
```

Implement only:

```text
ScanAccessPlan
HashJoinAccessPlan
```

Move hash-index selection out of raw `JoinKeyParts.Count` checks.

No new optimization behavior is required. This task is an architectural separation.

## 27. Third Codex task

After the planner refactor:

> Replace relation-local one-reference reverse-navigation handling with reusable dependency navigation indexes.

Scope:

- preserve complete dependency paths;
- introduce reusable navigation-edge metadata;
- create runtime `NavigationIndex`;
- share navigation indexes between relations where possible;
- eliminate reverse-index removal that scans all buckets;
- support at least two-level reference navigation;
- add tests for `null → object`, `object → null`, `A → B`, and two-level nested property changes.

Only after that should arbitrary-depth propagation be generalized.

## 28. Definition of done before `DerivedState`

Do not start `DerivedState` until all are true:

- public naming reflects `Relations`;
- access planning is explicit;
- scan fallback remains semantic authority;
- null nested paths cannot cause optimizer-only exceptions;
- external/opaque dependencies are represented honestly;
- stable object-set identity cannot silently corrupt;
- complete dependency paths are preserved;
- reverse navigation is reusable rather than relation-local;
- arbitrary-depth reference propagation works;
- access impact and semantic impact are distinguishable;
- nested-object changes can be routed correctly;
- `ChangeSet` exists;
- batch application is atomic;
- indexed-vs-scan semantic equivalence is heavily tested.

## 29. Long-term architecture target

```text
                  RelationModel
                       │
              expression definitions
                       │
                       ▼
              Expression Analysis
                       │
            ┌──────────┴───────────┐
            ▼                      ▼
     Dependency Paths       Logical Predicate Info
            │                      │
            ▼                      ▼
     Dependency Graph        Relation Planner
            │                      │
            │                      ▼
            │               Access Plans
            │                      │
            └──────────┬───────────┘
                       ▼
                 RelationRuntime
                       │
                Navigation Indexes
                       │
                   ChangeSet
                       │
                 Impact Resolver
                       │
        ┌──────────────┼───────────────┐
        ▼              ▼               ▼
    Relations     Derived State     Invariants
                       │               │
                       ▼               ▼
                Dirty / Invalid     Policies
```

## 30. Core rule

Keep this as the architectural test for every new feature:

> The developer declares the relationship or constraint once. Raffinert.Relations derives the mechanisms required to access it and maintain its consistency.

If ordinary supported relations start requiring users to repeat:

```csharp
.DependsOn(...)
.IndexBy(...)
.InvalidateOn(...)
.RefreshOn(...)
```

for information already present in the expression, reconsider the design.

Escape hatches are acceptable. Duplication as the normal model is not.

## 31. Summary

The first prototype has validated the idea.

The next stage is not about feature count. It is about turning:

```text
expression → hash index
```

into the general engine:

```text
expression
    → dependency graph
    → access plan
    → navigation indexes
    → atomic change impact
```

Once that layer is trustworthy, the higher-level features become natural:

```text
DerivedState
Dirty / Invalid
Invariant
Repair policy
EF Core integration
```

The quality of those future features will depend primarily on the quality of this dependency/change foundation.
