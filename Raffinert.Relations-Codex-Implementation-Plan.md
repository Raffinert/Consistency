# Raffinert.Relations — Codex Implementation Plan

## 1. Goal

Build the first working version of **Raffinert.Relations** as a declarative dependency and relation engine for .NET object models.

The library must start from the following idea:

> Developers describe relationships and constraints between objects using strongly typed expression trees.  
> The library analyzes those expressions and derives the mechanics needed to maintain them: dependencies, access paths, candidate indexes, change impact, and eventually invariant validation.

This is **not** primarily a validation library.

The long-term direction is:

```text
Expressions
    ↓
Relations / Derived State / Invariants
    ↓
Dependency analysis
    ↓
Access planning and indexing
    ↓
Incremental invalidation
    ↓
Validation / recomputation / repair
```

The first implementation milestone must prove the core idea with **relations** before implementing the full invariant system.

---

# 2. Root Problem

In non-trivial domain models, business relationships are usually expressed as queries over multiple objects.

Example:

```csharp
(invoiceLine, poLine) =>
    invoiceLine.PurchaseOrderNumber == poLine.PurchaseOrder.Number &&
    invoiceLine.ItemNumber == poLine.ItemNumber
```

Today an application normally has to separately implement:

- the business predicate;
- the lookup/query used to find related objects;
- indexes or dictionaries for performance;
- dependency tracking;
- invalidation when properties change;
- reverse navigation from changed nested objects to affected root objects;
- validation/recalculation triggers.

The same business knowledge becomes duplicated across services, queries, indexes, event handlers, and validation code.

Raffinert.Relations should make the expression the **source of truth**.

From the expression above, the library should eventually be able to derive:

```text
Dependencies:
- InvoiceLine.PurchaseOrderNumber
- InvoiceLine.ItemNumber
- PurchaseOrderLine.PurchaseOrder
- PurchaseOrder.Number
- PurchaseOrderLine.ItemNumber

Potential join keys:
- InvoiceLine.PurchaseOrderNumber
    == PurchaseOrderLine.PurchaseOrder.Number

- InvoiceLine.ItemNumber
    == PurchaseOrderLine.ItemNumber
```

Those results can then be used for:

- dependency tracking;
- candidate selection;
- index generation;
- invalidation;
- incremental validation.

---

# 3. Core Design Principles

## 3.1 Expressions define semantics

The developer should write the relationship once:

```csharp
var relation =
    model.Relation(invoiceLines, poLines)
        .Where((invoiceLine, poLine) =>
            invoiceLine.PurchaseOrderNumber == poLine.PurchaseOrder.Number &&
            invoiceLine.ItemNumber == poLine.ItemNumber);
```

Avoid APIs that require repeating the same knowledge manually:

```csharp
// Avoid as normal API:
.DependsOn(...)
.IndexBy(...)
.OnPropertyChanged(...)
```

Manual dependency declarations may exist later only as escape hatches for opaque code.

---

## 3.2 Semantics must not depend on optimizer support

Any valid relation expression supported by the semantic layer must still work even if the optimizer cannot understand it.

Example:

```csharp
(a, b) =>
    a.SupplierId == b.SupplierId &&
    a.Date >= b.ValidFrom &&
    a.Date < b.ValidTo &&
    b.Enabled
```

A first optimizer may understand only:

```text
a.SupplierId == b.SupplierId
```

and use it for candidate lookup.

The remaining expression can be executed as a compiled residual predicate.

Fallback behavior may be slower, but must remain logically correct.

---

## 3.3 Invalidation and recomputation are different

Long-term the library must distinguish:

```text
Dirty
```

from:

```text
Invalid
```

`Dirty` means derived state may be stale and can potentially be lazily recomputed.

`Invalid` means existing dependent state may no longer be relied upon for correctness.

Do not implement the full policy system in milestone 1, but keep the internal design compatible with this distinction.

---

## 3.4 Core must not depend on EF Core

The base package must work with plain .NET objects.

Future integration:

```text
Raffinert.Relations
Raffinert.Relations.EntityFrameworkCore
```

EF Core should eventually translate `ChangeTracker` information into the core `ChangeSet` abstraction.

Do not add an EF Core dependency to the core package.

---

# 4. Target Technology

Use:

- .NET 10
- C#
- nullable reference types enabled
- latest stable language version supported by .NET 10
- xUnit for tests

Prefer:

- immutable internal metadata where practical;
- strongly typed APIs;
- expression trees instead of reflection-based string paths;
- internal implementation types unless they are part of the intended public API.

Do not prematurely introduce:

- dependency injection;
- logging abstractions;
- EF Core;
- source generators;
- Roslyn analyzers;
- persistence;
- distributed features.

Those may come later.

---

# 5. Solution Structure

Create a solution similar to:

```text
Raffinert.Relations.sln

src/
  Raffinert.Relations/
    Raffinert.Relations.csproj

tests/
  Raffinert.Relations.Tests/
    Raffinert.Relations.Tests.csproj
```

Optionally create an additional test domain folder inside the test project:

```text
tests/Raffinert.Relations.Tests/TestDomain/
```

Do not create many projects prematurely.

---

# 6. First Milestone

The first milestone is:

> Define a relation between two object sets using an expression, analyze its dependencies, automatically derive equality join keys when possible, build an in-memory candidate index, and incrementally update relation results when relevant object properties change.

The milestone is complete when this scenario works:

```csharp
var model = new InvariantModelBuilder();

var invoiceLines = model.Objects<InvoiceLine>()
    .Key(x => x.Id);

var poLines = model.Objects<PurchaseOrderLine>()
    .Key(x => x.Id);

var relation =
    model.Relation(invoiceLines, poLines)
        .Where((invoice, po) =>
            invoice.PurchaseOrderNumber == po.PurchaseOrderNumber &&
            invoice.ItemNumber == po.ItemNumber);

var compiled = model.Build();

var runtime = compiled.CreateRuntime();

runtime.Add(invoice);
runtime.Add(po1);
runtime.Add(po2);

runtime.Related(invoice, relation)
    .Should()
    .ContainSingle()
    .Which.Should().BeSameAs(po1);

runtime.Apply(
    Change.Property(
        po1,
        x => x.ItemNumber,
        oldValue: "A",
        newValue: "B"));

runtime.Related(invoice, relation)
    .Should()
    .BeEmpty();
```

The test syntax may be adjusted if necessary, but preserve the semantic intent.

---

# 7. Core Public Model

Start with the following conceptual abstractions.

## 7.1 `InvariantModelBuilder`

Responsibilities:

- register object sets;
- register relations;
- compile the declarative model;
- return immutable compiled metadata.

Example:

```csharp
var model = new InvariantModelBuilder();
```

Possible API:

```csharp
public sealed class InvariantModelBuilder
{
    public ObjectSetBuilder<T> Objects<T>();

    public RelationBuilder<TLeft, TRight> Relation<TLeft, TRight>(
        ObjectSetBuilder<TLeft> left,
        ObjectSetBuilder<TRight> right);

    public CompiledInvariantModel Build();
}
```

Exact signatures may evolve during implementation.

---

## 7.2 `ObjectSet<T>`

An object set represents a logical set of runtime objects of a particular type.

Example:

```csharp
var poLines = model.Objects<PurchaseOrderLine>()
    .Key(x => x.Id);
```

Important:

- this is not a repository;
- this is not a database abstraction;
- this is not `DbSet<T>`;
- this is logical runtime metadata.

A key is used for stable identity.

For milestone 1, requiring a key is acceptable.

Potential types:

```csharp
ObjectSetBuilder<T>
ObjectSetDefinition<T>
CompiledObjectSet<T>
```

Do not expose unnecessary implementation details publicly.

---

## 7.3 `Relation<TLeft, TRight>`

A relation represents:

```text
R ⊆ TLeft × TRight
```

A pair belongs to the relation when its predicate evaluates to true.

API target:

```csharp
var relation =
    model.Relation(invoiceLines, poLines)
        .Where((invoice, po) =>
            invoice.PurchaseOrderNumber == po.PurchaseOrderNumber &&
            invoice.ItemNumber == po.ItemNumber);
```

The `.Where(...)` expression is the semantic definition.

The relation should retain enough metadata so it can later be referenced by:

- runtime queries;
- derived state;
- invariants.

---

# 8. Expression Analysis

Create a dedicated internal subsystem.

Suggested namespace:

```text
Raffinert.Relations.Expressions
```

Do not put all logic into one expression visitor.

Separate concerns.

Suggested model:

```text
RelationExpressionAnalyzer
PredicateNormalizer
DependencyExtractor
JoinKeyExtractor
```

Names may evolve.

---

# 9. Dependency Paths

Introduce an internal first-class concept representing property/member access paths.

Example expression:

```csharp
poLine => poLine.PurchaseOrder.Supplier.Country.Code
```

should be representable as:

```text
DependencyPath

Root:
PurchaseOrderLine

Members:
- PurchaseOrder
- Supplier
- Country
- Code
```

Possible internal model:

```csharp
internal sealed record MemberPath(
    Type RootType,
    IReadOnlyList<MemberInfo> Members);
```

Avoid storing only strings.

The path needs to preserve actual member metadata.

Required behavior:

```csharp
invoice.PurchaseOrderNumber
```

becomes one path.

```csharp
po.PurchaseOrder.Number
```

becomes another path.

---

# 10. Equality Join Extraction

For milestone 1, detect equality comparisons where:

- one side is rooted in the left relation parameter;
- the other side is rooted in the right relation parameter.

Example:

```csharp
invoice.PurchaseOrderNumber == po.PurchaseOrderNumber
```

extract:

```text
JoinKeyPart
LeftPath:  InvoiceLine.PurchaseOrderNumber
RightPath: PurchaseOrderLine.PurchaseOrderNumber
```

For:

```csharp
invoice.PurchaseOrderNumber == po.PurchaseOrder.Number
```

extract:

```text
LeftPath:
InvoiceLine.PurchaseOrderNumber

RightPath:
PurchaseOrderLine.PurchaseOrder.Number
```

Support conjunctions:

```csharp
a.X == b.X &&
a.Y == b.Y
```

as a composite key.

Do not attempt every expression form initially.

Milestone 1 should support at minimum:

```text
AndAlso
Equal
MemberAccess
Parameter
optional Convert nodes
```

The analyzer should fail safely and leave unsupported pieces as residual predicates rather than producing incorrect metadata.

---

# 11. Predicate Decomposition

Internally represent a compiled relation approximately as:

```text
CompiledRelationPlan
├── Dependencies
├── JoinKeyParts
├── CandidateAccessPlan
└── ResidualPredicate
```

Example:

```csharp
(a, b) =>
    a.SupplierId == b.SupplierId &&
    b.Enabled
```

may become:

```text
Join key:
a.SupplierId == b.SupplierId

Residual:
b.Enabled
```

Milestone 1 may compile the entire original predicate as the final verification predicate even if some parts are used for indexing.

This is encouraged.

Candidate lookup must only narrow the candidate set.

The original compiled predicate remains the final semantic authority.

---

# 12. Indexing

Implement the first automatic access strategy:

```text
Hash index for extracted equality join keys
```

Example:

```csharp
(a, b) =>
    a.PurchaseOrderNumber == b.PurchaseOrderNumber &&
    a.ItemNumber == b.ItemNumber
```

should allow an index conceptually equivalent to:

```csharp
Dictionary<
    CompositeKey,
    HashSet<PurchaseOrderLine>>
```

Do not generate runtime anonymous tuple types.

Introduce an internal representation such as:

```csharp
internal readonly struct CompositeKey
{
    ...
}
```

Requirements:

- support one or more components;
- correct equality semantics;
- allow null components if the expression semantics permit them;
- reasonably efficient hashing;
- no dependence on source object reference identity for key values.

A simple object-array-backed implementation is acceptable initially if cleanly encapsulated.

Optimization can come later.

---

# 13. Runtime

Introduce a runtime separate from compiled model metadata.

Conceptually:

```csharp
var runtime = compiled.CreateRuntime();
```

The compiled model contains immutable metadata.

The runtime contains mutable state:

```text
object instances
indexes
reverse indexes
relation runtime state
```

Possible type:

```csharp
InvariantRuntime
```

Responsibilities:

- add objects;
- remove objects;
- apply changes;
- query related objects;
- maintain runtime indexes.

---

# 14. Adding Objects

Expected behavior:

```csharp
runtime.Add(invoice);
runtime.Add(poLine);
```

The runtime must identify the matching registered object set.

If multiple sets of the same CLR type become supported later, object-set identity will matter.

For milestone 1, it is acceptable to require APIs such as:

```csharp
runtime.Add(invoiceLines, invoice);
runtime.Add(poLines, poLine);
```

if that keeps semantics unambiguous.

Prefer correctness and explicitness over clever type inference.

---

# 15. Querying Relations

Provide a strongly typed way to query:

```csharp
runtime.Related(invoice, relation)
```

or:

```csharp
runtime.Related(relation, invoice)
```

Return the currently related right-side objects.

The query should:

1. derive the left object's join key;
2. use the right-side index if available;
3. evaluate the original compiled predicate against candidates;
4. return only true matches.

If no access plan exists, fall back to scanning the right object set.

This fallback is important.

---

# 16. Change Model

Introduce a core change abstraction that is independent of EF Core.

Minimum milestone 1 API:

```csharp
Change.Property(
    instance,
    x => x.ItemNumber,
    oldValue,
    newValue);
```

Possible internal hierarchy:

```text
Change
├── PropertyChange
├── ObjectAdded
└── ObjectRemoved
```

Long-term candidates:

```text
ReferenceChanged
CollectionChanged
```

Do not implement everything now.

For milestone 1 focus on:

- add;
- remove;
- scalar property change.

---

# 17. Applying Property Changes

Decide and document one invariant:

Either:

### Option A

`Change.Property(...)` describes a change that has already happened.

Example:

```csharp
var old = po.ItemNumber;
po.ItemNumber = "B";

runtime.Apply(
    Change.Property(
        po,
        x => x.ItemNumber,
        old,
        po.ItemNumber));
```

or:

### Option B

the runtime itself mutates the value.

Prefer **Option A**.

The runtime should observe domain changes, not become the domain object's mutation API.

The old and new values exist to correctly update indexes and dependency state.

---

# 18. Change Impact

When a relevant indexed property changes:

```text
old key
    ↓
remove object from old index bucket

new key
    ↓
insert object into new index bucket
```

Example:

```text
PO line before:
("PO123", "A")

PO line after:
("PO123", "B")
```

Runtime update:

```text
remove from ("PO123", "A")
add to     ("PO123", "B")
```

Then subsequent relation queries must immediately reflect the new state.

Do not require:

```csharp
relation.Refresh();
```

---

# 19. Nested Member Dependencies

This is an important architectural requirement, but do not fully solve it before the simple milestone works.

Example:

```csharp
(invoice, poLine) =>
    invoice.PurchaseOrderNumber == poLine.PurchaseOrder.Number
```

The dependency is not only:

```text
PurchaseOrderLine.PurchaseOrder
```

but also:

```text
PurchaseOrder.Number
```

The runtime ultimately needs a way to answer:

> When `PurchaseOrder.Number` changes, which `PurchaseOrderLine` instances are affected?

This requires reverse access information:

```text
PurchaseOrder
    → PurchaseOrderLine[]
```

even if the object model only has:

```text
PurchaseOrderLine → PurchaseOrder
```

Long-term, this can be supported by automatic reverse navigation indexes derived from dependency paths.

Architecture should leave room for:

```text
ReverseNavigationIndex
```

Do not block milestone 1 on full arbitrary nested-path propagation.

Recommended progression:

1. direct scalar properties;
2. one reference navigation;
3. arbitrary member paths;
4. collections.

---

# 20. Reverse Navigation Concept

For a path:

```text
PurchaseOrderLine.PurchaseOrder.Number
```

runtime metadata can conceptually derive:

```text
forward:
PurchaseOrderLine → PurchaseOrder

reverse index:
PurchaseOrder → PurchaseOrderLine[]
```

Whenever a `PurchaseOrderLine.PurchaseOrder` reference is registered or changed, update the reverse index.

Then a change:

```text
PurchaseOrder.Number changed
```

can locate:

```text
affected PurchaseOrderLine instances
```

and therefore all relations depending on that path.

Do not implement this by global scans once the reverse-navigation milestone begins.

---

# 21. Dependency Graph

Even though milestone 1 mainly concerns relation indexes, introduce a clean internal representation for dependencies.

Conceptually:

```text
Property dependency
    ↓
relation
```

Later:

```text
Property dependency
    ↓
relation
    ↓
derived state
    ↓
invariant
```

Possible internal entities:

```text
DependencyNode
DependencyEdge
DependencyGraph
```

Avoid overengineering with a generic graph library.

The graph only needs to answer targeted impact-analysis questions.

For milestone 1, a dictionary-based dependency map is sufficient.

Example:

```text
(Type, MemberInfo)
    →
AffectedRelationDefinitions
```

---

# 22. Future `DerivedState`

Do not fully implement this in the first milestone, but preserve the following intended API direction.

Example:

```csharp
var receiptsForPo =
    model.Relation(poLines, receipts)
        .Where((po, receipt) =>
            po.PurchaseOrderId == receipt.PurchaseOrderId &&
            po.LineNumber == receipt.PurchaseOrderLineNumber &&
            !receipt.IsCancelled);

var receivedQuantity =
    model.Derived(poLines)
        .Using(receiptsForPo)
        .Compute((po, received) =>
            received.Sum(x => x.Quantity));
```

Desired state model:

```text
Fresh
Dirty
Invalid
```

Important:

```text
invalidation != recomputation
```

This distinction must remain possible in the architecture.

---

# 23. Future `Invariant`

Do not implement until relations and change propagation are stable.

Target direction:

```csharp
model.Invariant(poLines)
    .Using(receivedQuantity)
    .Must((po, received) =>
        received <= po.Quantity);
```

The invariant should derive dependencies from:

- its own expression;
- dependencies of referenced relations;
- dependencies of referenced derived state.

The system should then automatically know when an invariant is affected.

---

# 24. Unsupported / Opaque Expressions

Example:

```csharp
.Where((a, b) => ExternalService.Check(a, b))
```

The library cannot infer dependencies hidden inside arbitrary method calls.

Milestone 1 behavior should be explicit.

Preferred behavior:

- preserve semantic correctness if possible by compiling and executing the predicate;
- mark dependency analysis as incomplete;
- avoid pretending that change propagation is complete.

Later an escape hatch may be added:

```csharp
.Where((a, b) => MyMethod(a, b))
.DependsOn(
    a => a.X,
    b => b.Y);
```

Do not design the final escape-hatch API yet unless implementation requires it.

---

# 25. Expression Safety

Do not attempt aggressive expression rewriting in the first implementation.

Initially:

- inspect expressions;
- extract recognized metadata;
- compile the original expression;
- use recognized metadata only to optimize candidate lookup.

This drastically reduces semantic risk.

Example:

```text
Original expression
    ↓
Compile()
    ↓
Final truth predicate
```

Metadata extraction is advisory for access planning.

It must never alter logical semantics.

---

# 26. Test Domain

Create simple test domain classes.

Example:

```csharp
internal sealed class InvoiceLine
{
    public Guid Id { get; init; }

    public string PurchaseOrderNumber { get; set; } = "";

    public string ItemNumber { get; set; } = "";
}

internal sealed class PurchaseOrderLine
{
    public Guid Id { get; init; }

    public string PurchaseOrderNumber { get; set; } = "";

    public string ItemNumber { get; set; } = "";
}
```

Later introduce:

```csharp
internal sealed class PurchaseOrder
{
    public Guid Id { get; init; }

    public string Number { get; set; } = "";
}

internal sealed class PurchaseOrderLine
{
    public Guid Id { get; init; }

    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public string ItemNumber { get; set; } = "";
}
```

Use small domain examples rather than artificial `Foo` / `Bar` classes where business meaning makes tests clearer.

---

# 27. Required Tests — Phase 1

Add tests for expression analysis.

## Single equality

```csharp
(a, b) => a.Code == b.Code
```

Expected:

```text
1 equality join key
```

---

## Composite equality

```csharp
(a, b) =>
    a.OrderNumber == b.OrderNumber &&
    a.ItemNumber == b.ItemNumber
```

Expected:

```text
2 join key parts
```

---

## Reversed operands

```csharp
(a, b) => b.Code == a.Code
```

Expected:

```text
same logical join extraction
```

---

## Residual predicate

```csharp
(a, b) =>
    a.Code == b.Code &&
    b.Enabled
```

Expected:

```text
join key:
Code

full predicate still evaluated
```

---

## Unsupported expression

Example:

```csharp
(a, b) => CustomMatch(a, b)
```

Expected:

- no incorrect join metadata;
- relation still evaluable through fallback scanning;
- analyzer reports incomplete/opaque dependency information internally.

---

# 28. Required Tests — Phase 2

Runtime tests.

## Add and query

Register objects and verify relation matching.

---

## Multiple candidates with same key

If several right-side objects share the same join key, execute the full predicate for all candidates.

---

## No match

Return empty results.

---

## Change indexed property

Update a property, apply `Change.Property`, and verify old relation disappears.

---

## Change into matching key

Start with no match, change a property so the key becomes matching, apply the change, and verify the relation appears.

---

## Remove object

Remove a right-side object and verify it no longer appears in relation queries.

---

## Fallback scan

Create a relation for which no index can be derived.

Verify relation query remains correct.

---

# 29. Required Tests — Phase 3

Nested dependency analysis.

Analyze:

```csharp
(invoice, poLine) =>
    invoice.PurchaseOrderNumber ==
        poLine.PurchaseOrder.Number
```

Verify extracted path:

```text
PurchaseOrderLine
    .PurchaseOrder
    .Number
```

Do not implement arbitrary nested invalidation until direct-property behavior is stable.

---

# 30. Internal Architecture Target

Aim toward this shape:

```text
InvariantModelBuilder
        │
        ▼
Declarative definitions
        │
        ▼
RelationExpressionAnalyzer
        │
        ├── DependencyExtractor
        ├── JoinKeyExtractor
        └── Predicate metadata
        │
        ▼
RelationCompiler
        │
        ▼
CompiledInvariantModel
        │
        ▼
InvariantRuntime
        │
        ├── ObjectSetRuntime
        ├── Hash indexes
        ├── RelationRuntime
        └── Change impact
```

Keep metadata/compiler/runtime responsibilities separate.

---

# 31. Suggested Internal Types

These are guidance, not mandatory names.

```csharp
internal sealed record MemberPath(...);

internal sealed record JoinKeyPart(
    MemberPath Left,
    MemberPath Right);

internal sealed class RelationAnalysis
{
    ...
}

internal sealed class CompiledRelationPlan<TLeft, TRight>
{
    ...
}

internal sealed class HashRelationIndex
{
    ...
}

internal readonly struct CompositeKey
{
    ...
}
```

Public API should remain much smaller than internal implementation.

---

# 32. Performance Rules

Correctness first.

Still avoid obviously pathological design.

Do not:

- compile expressions on each query;
- use reflection for every candidate property read if an accessor can be compiled once;
- scan all objects when a valid equality index already exists;
- rebuild all indexes after one scalar change.

Compile member-path accessors once.

For example:

```text
MemberPath
    ↓
compiled Func<T, object?>
```

or a more strongly typed equivalent.

Cache them in compiled metadata.

---

# 33. Equality Semantics

Be careful with nulls and value types.

The relation's final semantics always come from the compiled original expression.

Index semantics must not accidentally disagree.

For initial hash keys:

- use `EqualityComparer<T>.Default` semantics where feasible;
- ensure null values are handled consistently;
- if exact semantic equivalence cannot be guaranteed for a particular expression, do not use that expression part as an index key.

Correct fallback is better than incorrect optimization.

---

# 34. Error Handling

Model building should reject structurally invalid definitions early.

Examples:

- object set missing required key, if keys are mandatory;
- relation references object sets from another builder/model;
- duplicate registration identity if unsupported;
- malformed expression where required assumptions are violated.

Expression forms that are merely unoptimizable should generally not be rejected.

Distinguish:

```text
invalid model
```

from:

```text
valid but not optimizable
```

---

# 35. Diagnostics

Introduce useful metadata that tests can inspect internally.

Eventually it may become public diagnostics.

Example conceptual output:

```text
Relation: InvoiceToPurchaseOrderLine

Dependencies:
  InvoiceLine.PurchaseOrderNumber
  InvoiceLine.ItemNumber
  PurchaseOrderLine.PurchaseOrderNumber
  PurchaseOrderLine.ItemNumber

Access plan:
  HashIndex

Key:
  PurchaseOrderNumber
  ItemNumber

Residual predicate:
  yes/no
```

Do not build a logging framework.

Simple metadata and `ToString()` / debug views are sufficient.

---

# 36. Debug View

A debug representation will make development much easier.

Add something like:

```csharp
compiled.DebugView
```

or internal equivalent.

Possible output:

```text
ObjectSet InvoiceLine
  Key: Id

ObjectSet PurchaseOrderLine
  Key: Id

Relation InvoiceLine -> PurchaseOrderLine
  Predicate:
    invoice.PurchaseOrderNumber == po.PurchaseOrderNumber
    && invoice.ItemNumber == po.ItemNumber

  Dependencies:
    InvoiceLine.PurchaseOrderNumber
    InvoiceLine.ItemNumber
    PurchaseOrderLine.PurchaseOrderNumber
    PurchaseOrderLine.ItemNumber

  Join key:
    PurchaseOrderNumber <-> PurchaseOrderNumber
    ItemNumber <-> ItemNumber

  Access:
    HashIndex
```

Snapshot tests for this may be useful later.

---

# 37. Implementation Phases

## Phase 1 — Project skeleton

Create:

```text
solution
library project
test project
basic CI-compatible build
nullable enabled
```

Acceptance:

```bash
dotnet build
dotnet test
```

both succeed.

---

## Phase 2 — Object sets

Implement:

```csharp
model.Objects<T>().Key(...)
```

Store:

- CLR type;
- key expression;
- compiled key accessor;
- object-set identity.

Tests for registration and key extraction.

---

## Phase 3 — Relation declarations

Implement:

```csharp
model.Relation(left, right).Where(...)
```

Store the original expression.

Compile it into a delegate.

Initially support scan-based runtime relation lookup if necessary.

Acceptance:

```text
relations work correctly without indexing
```

This provides a semantic baseline.

---

## Phase 4 — Expression analysis

Implement:

- parameter-root detection;
- member path extraction;
- flattening `AndAlso`;
- equality join extraction;
- dependency collection.

Acceptance:

```text
analysis metadata is correct for direct scalar-property joins
```

---

## Phase 5 — Hash access plan

Generate a composite equality key from extracted join parts.

Build a right-side hash index.

Relation query:

```text
left object
    ↓
left key
    ↓
right index bucket
    ↓
original predicate
    ↓
matches
```

Acceptance:

- same semantic results as scan implementation;
- candidate count reduced in tests/diagnostics.

---

## Phase 6 — Runtime changes

Implement:

```text
Add
Remove
PropertyChange
```

Update hash indexes incrementally.

Acceptance:

- relation results immediately reflect changes;
- no global refresh.

---

## Phase 7 — Nested member path analysis

Support extracting:

```text
PurchaseOrderLine.PurchaseOrder.Number
```

Do not yet promise arbitrary nested change propagation.

Acceptance:

- metadata is accurate;
- path accessor can read current value.

---

## Phase 8 — Reverse navigation prototype

Implement one reference-navigation level.

Given:

```text
PurchaseOrderLine.PurchaseOrder.Number
```

maintain:

```text
PurchaseOrder → PurchaseOrderLine[]
```

Then allow:

```text
PurchaseOrder.Number change
```

to identify affected PO lines and update relevant indexes.

Acceptance:

- nested property change updates relation behavior without scanning all PO lines.

---

# 38. Explicit Non-Goals for First Version

Do not implement yet:

- EF Core adapter;
- source generator;
- Roslyn analyzer;
- async APIs;
- distributed synchronization;
- database indexes;
- SQL generation;
- persistence;
- collection navigation dependencies;
- derived state;
- invariant execution;
- repair actions;
- background recomputation;
- automatic domain event publishing;
- arbitrary method-body dependency analysis;
- expression decompilation;
- range indexes;
- B-tree indexes;
- multi-process cache coherence.

Keep the first version focused.

---

# 39. Naming Guidance

The library is currently named:

```text
Raffinert.Relations
```

However, do not force every type name to include `Invariant`.

The underlying engine is broader.

Prefer precise concepts:

```text
ObjectSet
Relation
Dependency
MemberPath
AccessPlan
Change
Runtime
```

Avoid generic names such as:

```text
Manager
Helper
Processor
Utils
```

unless no domain-specific name exists.

---

# 40. Code Quality Expectations

Use:

- clear small types;
- immutable metadata where possible;
- no unnecessary inheritance;
- composition over framework-style base classes;
- no service locator;
- no static global state;
- no hidden runtime mutation of domain objects.

Keep public surface minimal.

Prefer internal types until an abstraction is clearly needed publicly.

---

# 41. Documentation Expectations

Add a README once the first vertical slice works.

Initial README should explain the conceptual problem before API details.

Suggested opening:

> Raffinert.Relations is an experimental declarative dependency engine for .NET object models.
>
> Relationships are defined as expression trees. The library analyzes those expressions to derive dependencies, access paths, indexes, and change impact, allowing dependent state and invariants to be maintained incrementally.

Include the simplest relation example.

Do not market unimplemented features as existing functionality.

Clearly separate:

```text
Implemented
Planned
```

---

# 42. Architectural Constraint to Preserve

The most important architectural rule is:

> A developer defines the business relationship once.  
> Optimization and incremental maintenance are derived from that definition.

Avoid any design that gradually turns this into:

```csharp
.Relation(...)
.DependsOn(...)
.IndexBy(...)
.InvalidateOn(...)
.RefreshWith(...)
```

for ordinary supported expressions.

If implementation starts requiring repeated declarations of the same dependency information, stop and reconsider the abstraction.

---

# 43. First End-to-End Example

Use this as the first complete vertical slice.

Domain:

```csharp
internal sealed class InvoiceLine
{
    public Guid Id { get; init; }

    public string PurchaseOrderNumber { get; set; } = "";

    public string ItemNumber { get; set; } = "";
}

internal sealed class PurchaseOrderLine
{
    public Guid Id { get; init; }

    public string PurchaseOrderNumber { get; set; } = "";

    public string ItemNumber { get; set; } = "";
}
```

Model:

```csharp
var model = new InvariantModelBuilder();

var invoices = model.Objects<InvoiceLine>()
    .Key(x => x.Id);

var poLines = model.Objects<PurchaseOrderLine>()
    .Key(x => x.Id);

var matches =
    model.Relation(invoices, poLines)
        .Where((invoice, poLine) =>
            invoice.PurchaseOrderNumber == poLine.PurchaseOrderNumber &&
            invoice.ItemNumber == poLine.ItemNumber);

var compiled = model.Build();
var runtime = compiled.CreateRuntime();
```

Objects:

```csharp
var invoice = new InvoiceLine
{
    Id = Guid.NewGuid(),
    PurchaseOrderNumber = "PO-100",
    ItemNumber = "ITEM-1"
};

var po1 = new PurchaseOrderLine
{
    Id = Guid.NewGuid(),
    PurchaseOrderNumber = "PO-100",
    ItemNumber = "ITEM-1"
};

var po2 = new PurchaseOrderLine
{
    Id = Guid.NewGuid(),
    PurchaseOrderNumber = "PO-200",
    ItemNumber = "ITEM-1"
};
```

Register:

```csharp
runtime.Add(invoices, invoice);
runtime.Add(poLines, po1);
runtime.Add(poLines, po2);
```

Query:

```csharp
var related = runtime.Related(matches, invoice);
```

Expected:

```text
po1 only
```

Then:

```csharp
var oldValue = po1.ItemNumber;
po1.ItemNumber = "ITEM-2";

runtime.Apply(
    Change.Property(
        poLines,
        po1,
        x => x.ItemNumber,
        oldValue,
        po1.ItemNumber));
```

Query again.

Expected:

```text
no matches
```

No manual relation refresh is allowed.

---

# 44. Second End-to-End Example

Once nested paths are implemented:

```csharp
var matches =
    model.Relation(invoices, poLines)
        .Where((invoice, poLine) =>
            invoice.PurchaseOrderNumber ==
                poLine.PurchaseOrder.Number &&
            invoice.ItemNumber ==
                poLine.ItemNumber);
```

The analyzer should report:

```text
InvoiceLine.PurchaseOrderNumber
InvoiceLine.ItemNumber

PurchaseOrderLine.PurchaseOrder
PurchaseOrder.Number
PurchaseOrderLine.ItemNumber
```

Long-term expected propagation:

```text
PurchaseOrder.Number changes
        ↓
find PurchaseOrderLine instances referencing it
        ↓
their relation keys are affected
        ↓
update relation index
        ↓
relation query changes immediately
```

This is the architectural direction even if implemented in a later phase.

---

# 45. How Codex Should Work

Implement incrementally.

For every phase:

1. inspect the existing code first;
2. make the smallest coherent change;
3. add or update tests;
4. run:

```bash
dotnet build
dotnet test
```

5. fix warnings/errors related to the change;
6. keep public API changes deliberate;
7. do not implement future phases early unless required by current architecture.

Prefer several small coherent commits over one giant rewrite if operating in a Git repository.

---

# 46. Decision Rule When Ambiguous

When choosing between:

```text
more automation with hidden assumptions
```

and:

```text
explicit, correct, inspectable behavior
```

choose the latter.

When choosing between:

```text
an optimizer that might be semantically wrong
```

and:

```text
a slower fallback scan
```

choose the fallback scan.

Correctness is part of the framework's core value proposition.

---

# 47. Definition of Done for Initial Prototype

The initial prototype is complete when all of the following are true:

- object sets can be declared;
- object keys can be declared;
- a binary relation can be declared using an expression;
- the original expression is compiled and used as semantic truth;
- direct member dependencies are extracted;
- equality join keys are extracted from supported expressions;
- composite equality keys work;
- a hash index is generated automatically;
- relation lookup uses the index;
- unsupported predicates fall back to correct scanning;
- objects can be added and removed;
- direct indexed property changes update indexes incrementally;
- relation query results immediately reflect changes;
- there is no manual `Refresh()` requirement;
- tests cover the above;
- build and tests are clean;
- EF Core is not referenced;
- README accurately documents only implemented behavior.

---

# 48. What Comes Immediately After the Prototype

After the initial vertical slice is stable, proceed in this order:

```text
1. Nested member paths
2. Reverse navigation indexes
3. Change propagation through nested references
4. Dependency graph improvements
5. DerivedState abstraction
6. Dirty vs Invalid state
7. Invariant abstraction
8. Batch ChangeSet support
9. EF Core adapter
10. Optional Roslyn/source-generation tooling
```

Do not jump directly to EF Core or invariant syntax before the dependency/access machinery is proven.

---

# 49. Core Thesis

Keep this statement visible while implementing the library:

> Raffinert.Relations should let developers describe relationships and constraints as expressions, while the framework derives how those relationships are accessed, indexed, invalidated, and eventually verified.

The main technical asset is therefore not `Invariant<T>`.

It is the pipeline:

```text
Expression
    ↓
Dependency model
    ↓
Access plan
    ↓
Incremental change impact
```

Build that foundation first.
