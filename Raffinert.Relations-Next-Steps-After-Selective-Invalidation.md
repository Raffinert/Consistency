# Raffinert.Relations — Next Steps After Selective Invalidation

## Context

The current `main` branch has reached an important milestone:

- relation expressions are analyzed;
- equality joins produce scan/hash access plans;
- nested dependencies and arbitrary-depth reverse reference navigation are tracked;
- derived computations and invariant predicates have role-aware dependency analysis;
- relations consumed by derived state maintain exact bidirectional membership;
- `RelationDelta` tracks added/removed pairs and affected source roots;
- join-key, residual, add/remove, and relation-item mutations can invalidate only affected sources;
- randomized/precision tests exist, including a 10k-source regression;
- derived and invariant state are already exposed through `Fresh`, `Dirty`, and `Invalid` states.

The library is no longer at the proof-of-concept stage. The next phase should focus on making the propagation model semantically correct, scalable in both directions, and safe enough to support production adapters and richer modeling features.

The highest priority is **not** collection navigation yet.

---

# 1. P0 — Decouple `Dirty` / `Invalid` from access-plan details

**Status:** Implemented.

Dependency severity is now classified by an explicit internal policy. The default policy marks relation-membership changes `Dirty`, independently of join-key recognition, hash-index participation, or forced-scan planning.

The current runtime uses whether a changed dependency path is a recognized join-key path to decide whether a relation change should be treated as invalidating.

Conceptually this looks like:

```text
join-key dependency changed
    -> relation marked invalidating
    -> affected derived sources become Invalid

residual predicate dependency changed
    -> affected derived sources become Dirty
```

This mixes two independent concepts:

```text
Access semantics
- how candidates are found
- hash key vs residual predicate

Correctness semantics
- whether stale dependent state is merely Dirty
- whether it must be considered Invalid
```

A property is not correctness-critical merely because it happened to participate in an indexable equality.

Likewise, a residual predicate can absolutely be correctness-critical.

## Required direction

Introduce an explicit dependency-impact severity model.

Possible internal abstraction:

```csharp
internal enum DependencyImpactKind
{
    Dirty,
    Invalid
}
```

or a richer policy descriptor later.

Then derive severity from **dependency/policy semantics**, not from `RelationAccessPlan` or `JoinKeyPart`.

Target pipeline:

```text
Property change
    -> relation delta
    -> affected source roots
    -> dependency impact classification
    -> Dirty / Invalid propagation
```

The access planner remains responsible only for candidate lookup and index maintenance.

## Initial conservative policy

Until user-configurable policy exists, a safe initial rule is:

```text
relation membership changed -> Dirty
```

and reserve `Invalid` for dependencies explicitly marked correctness-critical by a higher-level policy.

If the library wants to preserve today's stronger behavior temporarily, make the rule explicit and isolated in one policy component instead of inferring it from join-key metadata.

## Tests

Add tests proving:

1. the same logical relation produces the same `Dirty`/`Invalid` outcome under forced scan and hash plans;
2. changing an equality join dependency does not become `Invalid` merely because the optimizer chose a hash plan;
3. a future explicit invalidating policy can mark a residual dependency as `Invalid`;
4. optimizer changes never alter correctness severity.

## Completion gate

Changing only the access plan must never change the propagated `Dirty`/`Invalid` state.

---

# 2. P0 — Add reverse/bidirectional access plans for exact propagation

**Status:** Implemented.

Relations consumed by derived state now receive a reverse scan/hash plan and maintain a left-side hash index when safe. Right additions and mutations use reverse candidates, while removals use materialized membership directly. Composite and comparer-aware keys are supported in both directions, with scan/hash parity tests and 10k/100k propagation benchmarks.

Exact source-scoped propagation currently materializes bidirectional membership, which is good, but discovering new left/source matches from a changed or added right object can still scan all left objects.

Typical hot path today is conceptually:

```text
right item changed
    -> evaluate predicate against every left/source object
    -> rebuild right -> left membership delta
```

For a domain such as:

```text
100,000 PurchaseOrderLines
1 GoodsReceiptLine changed
```

this can still become O(number of left objects).

That defeats the purpose of exact incremental propagation.

## Required design

Extend relation planning to be directional.

Suggested model:

```text
Forward access plan:  Left  -> candidate Rights
Reverse access plan:  Right -> candidate Lefts
```

For a symmetric equality relation:

```csharp
left.Code == right.Code
```

compile both:

```text
Right index by Code
Left index by Code
```

Then:

```text
left lookup  -> right hash bucket
right change -> left hash bucket
```

without scanning the opposite population.

## Suggested abstractions

```text
RelationAccessPlan
  Forward
  Reverse

DirectionalAccessPlan
  Scan
  HashJoin
```

or equivalent.

Avoid conflating this with public `RelatedFromRight(...)` API policy. Exact propagation itself already creates a strong need for reverse candidate lookup.

## Planner policy

Possible configuration:

```text
ForwardOnly
ReverseOnly
Bidirectional
Automatic
```

Recommended initial behavior:

- ordinary relation queried only from left: `ForwardOnly`;
- relation consumed by `DerivedState`: `Bidirectional` when a safe reverse plan exists;
- unsupported reverse optimization: correct scan fallback.

## Tests

- right join-key mutation uses reverse hash candidates;
- right add/remove does not scan unrelated left objects;
- forced reverse scan and reverse hash remain semantically equivalent;
- composite keys work in both directions;
- comparer-aware string equality works in both directions.

## Benchmarks

Add:

```text
10k / 100k left roots
1 right mutation
```

and compare:

```text
reverse scan
reverse hash
```

## Completion gate

A right-side equality-key change in a large relation should scale with the relevant bucket, not the entire left object set.

---

# 3. P0 — Source lifecycle cleanup

**Status:** Implemented.

Derived and invariant runtime states now participate in source lifecycle notifications. Removing a source clears cached/evaluated entries, materialized membership, and reverse-index state; propagation excludes removed roots so immediate and repair policies cannot recreate state. Re-adding the same reference starts clean, with repeated-cycle retention coverage.

Removing a source root currently removes it from relation membership and the object set, but engine-owned higher-level state can still retain references.

Potential retained state includes:

```text
DerivedRuntimeState cache
InvariantRuntimeState state
materialized relation membership
future policy/repair requests
```

This is both a correctness issue and a memory-retention risk.

## Required design

Introduce source lifecycle propagation.

Possible internal contract:

```csharp
interface ISourceLifecycleParticipant
{
    void OnSourceAdded(object source);
    void OnSourceRemoved(object source);
}
```

or a typed/internal equivalent.

On removal:

```text
ObjectSetRuntime
    -> relation membership cleanup
    -> derived cache cleanup
    -> invariant state cleanup
    -> dependency/policy cleanup
```

## Required tests

1. compute a derived value;
2. evaluate an invariant;
3. remove the source;
4. verify no state remains for the source;
5. re-add the same reference and ensure it starts clean;
6. repeat add/remove many times and verify runtime-owned state does not grow.

## Completion gate

Removing a root object removes every engine-owned source-scoped state entry associated with it.

---

# 4. P0 — Formalize the `Fresh` / `Dirty` / `Invalid` state machine

**Status:** Implemented.

Public state semantics are documented and dependency transitions are centralized. Dirty impact cannot downgrade Invalid state; successful derived recomputation returns to Fresh, while successful invariant evaluation returns to Valid or Violated. Complete transition matrices and mixed-severity runtime tests enforce the contract.

The states already exist, but their semantics should become part of the architectural contract.

Recommended definitions:

```text
Fresh
  Computed/evaluated from all currently accepted dependencies.

Dirty
  Cached state may be stale and should be recomputed/re-evaluated before freshness matters.

Invalid
  Cached state must not be relied upon for correctness-sensitive decisions before successful recomputation/revalidation.
```

Recommended monotonic severity rule:

```text
Fresh -> Dirty
Fresh -> Invalid
Dirty -> Invalid

Invalid -> Dirty   NOT allowed through ordinary propagation
```

Recomputation/revalidation may produce:

```text
Dirty   -> Fresh
Invalid -> Fresh
```

## Add explicit tests

Create a transition matrix for both:

```text
DerivedValueState
InvariantEvaluationState
```

including repeated mixed-severity changes.

## Completion gate

State transitions are deliberate and tested rather than incidental consequences of current invalidation code.

---

# 5. P1 — Make source-scoped relation impact a first-class runtime object

**Status:** Implemented.

Post-commit propagation now uses an immutable `RelationImpact` snapshot that unifies added/removed pairs, affected left and right roots, semantic roots, and directional reindex roots. Dependency severity, derived invalidation, and inherited invariant impact consume this bridge directly; pre-commit change routing remains separate.

The runtime now has enough data to know:

```text
added pairs
removed pairs
affected left/source roots
reindexed roots
semantic roots
```

but this information is still distributed across `ResolvedChangeImpact`, relation deltas, and runtime orchestration.

Introduce one source-scoped relation impact abstraction.

Example:

```text
RelationImpact
  Relation
  AddedPairs
  RemovedPairs
  AffectedLefts
  AffectedRights
  AccessReindexedRights
```

This should be the stable bridge between:

```text
Change routing
    -> Relation impact
    -> Derived impact
    -> Invariant impact
```

## Why now

Without this step, every new dependency consumer will re-derive source roots in its own code path.

## Completion gate

Derived and invariant propagation consume a source-scoped relation impact directly rather than recomputing affected roots independently.

---

# 6. P1 — Separate runtime commit from external policy side effects

**Status:** Implemented.

Core mutation now produces an internal `RuntimeApplyResult` and deferred policy-action batch. All invariant states are committed first, immediate evaluations run next, and data-based repair requests dispatch last. Requests are deduplicated by invariant/source. Callback failures do not roll back already committed runtime state.

`ScheduleRepairWith(Action<TSource>)` allows arbitrary application code to execute during runtime propagation.

Potential failure sequence:

```text
indexes updated
relation membership updated
derived/invariant state updated
repair callback invoked
callback throws
Apply(...) throws
```

The runtime state may already be committed even though the public call failed.

## Required direction

Make core application deterministic and side-effect-contained.

Target processing:

```text
1. Validate ChangeSet
2. Resolve dependency/relation impact
3. Prepare internal updates
4. Commit runtime-owned state
5. Produce policy actions / repair requests
6. Application dispatches external actions
```

Introduce a result object such as:

```csharp
RuntimeApplyResult
{
    ChangeImpact Impact;
    IReadOnlyList<RepairRequest> RepairRequests;
}
```

Exact public API can wait; first make the internal boundary clean.

## Repair model

Prefer data:

```text
RepairRequest
  invariant identity
  source object
  reason / severity
```

instead of arbitrary callbacks inside the core commit.

## Completion gate

No arbitrary user/application delegate executes while relation/navigation/index state is being committed.

---

# 7. P1 — Tighten `ChangeSet` semantics

**Status:** Implemented.

Change sets are now normalized deterministically by instance/member. Contiguous repeated changes collapse
to their net transition, conflicts are rejected before runtime state changes, and optional strict validation
checks the current member value against the reported final value. The runtime/domain atomicity boundary is
documented on the public API and in the README.

`ChangeSet` describes domain mutations that have already happened.

Therefore the library cannot provide full transaction rollback over domain object state.

Document the contract explicitly:

> A `ChangeSet` is atomically validated and applied to runtime-maintained indexes/dependency state. Domain object mutation is external and must already have occurred.

## Strict new-value validation

Add an optional validation mode that checks:

```text
actual current member value == reported NewValue
```

before runtime state changes.

This catches integration mistakes such as:

```csharp
runtime.Apply(Change.Property(order, x => x.Number, "A", "B"));
// order.Number was never changed to "B"
```

## Duplicate/conflicting changes

Handle explicitly:

```text
same instance/member appears more than once
```

Valid chain:

```text
A -> B
B -> C
```

can potentially normalize to:

```text
A -> C
```

Invalid chain:

```text
A -> B
A -> C
```

should be rejected or explicitly defined.

## Completion gate

`ChangeSet` behavior is deterministic for repeated/conflicting changes and its atomicity claim is precise.

---

# 8. P1 — Refactor propagation into a focused dependency graph

**Status:** Implemented.

Source-scoped propagation is now owned by a focused `DependencyGraphRuntime` with derived and invariant
nodes, member-path dependency edges, relation-impact inputs, and policy-action outputs. `RelationRuntime`
commits navigation and relation state, then hands impacts to this graph instead of maintaining custom
top-level derived/invariant loops.

`RelationRuntime` is increasingly becoming the central orchestrator for:

```text
navigation refresh
relation reindexing
relation deltas
derived invalidation
invariant propagation
policy reactions
```

After relation-impact semantics stabilize, extract a focused dependency graph runtime.

Do not build a generic graph framework.

Suggested node kinds:

```text
MemberDependencyNode
RelationNode
DerivedNode
InvariantNode
PolicyNode
```

Edges must preserve source-root context.

Target:

```text
Change
  -> Member dependency
  -> RelationImpact
  -> DerivedImpact
  -> InvariantImpact
  -> PolicyAction
```

## Design criterion

Adding a new dependency consumer should not require adding another custom top-level loop inside `RelationRuntime.Apply`.

---

# 9. P1 — Collection navigation support

**Status:** Implemented.

Explicit collection add, remove, and reset changes now refresh affected owners. Collection-aware
dependency paths and bidirectional owner/item navigation indexes allow item-property changes to resolve
their registered roots without scanning every source. Relation deltas then flow through the existing
derived and invariant dependency graph.

Only after P0/P1 propagation semantics are stable.

Target cases:

```csharp
order.Lines.Any(line => line.ItemNumber == invoice.ItemNumber)
```

and:

```text
PurchaseOrder.Lines[*].Quantity
```

Do not attempt to observe arbitrary `IEnumerable` mutations magically.

Start with explicit collection changes:

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
EF Core relationship changes
ObservableCollection events
custom domain events
```

## Completion gate

Collection add/remove updates relations, derived values, and invariants incrementally without rescanning all roots.

---

# 10. P1 — Mature the EF Core adapter into a unit-of-work adapter

**Status:** Implemented.

The EF adapter now captures added, deleted, scalar, reference, owned-entry, and collection-reset work
before `SaveChanges`, with explicit/selectable mappings for CLR types used by multiple sets. Sync and async
helpers apply the captured work only after database success, and the remaining post-database runtime-failure
reconciliation boundary is documented explicitly.

The current adapter handles modified scalar properties.

Expand in stages.

## Stage A — entity lifecycle

Map:

```text
EntityState.Added   -> runtime.Add
EntityState.Deleted -> runtime.Remove
```

Require explicit mapping if one CLR type belongs to multiple object sets.

## Stage B — reference/FK changes

Translate relationship changes into reference-navigation changes.

## Stage C — collection relationship changes

Once collection changes exist in core, map EF collection fixup/state to them.

## Stage D — owned/complex types

Map nested owned/complex changes into normal dependency paths.

## Stage E — transaction lifecycle

Define exactly when runtime changes happen relative to:

```text
DetectChanges
SaveChanges
DB transaction commit
AcceptAllChanges
failure/rollback
```

Do not permanently advance runtime state before database success unless rollback/reconciliation is defined.

A SaveChanges interceptor can later provide convenience, but must remain in the EF adapter package.

---

# 11. P2 — Stabilize the public object-set API

**Status:** Implemented.

`ObjectSetBuilder<T>` now exists only during mutable model construction, while `Key(...)` returns a stable
`ObjectSet<T>` identity handle used by relations, derived state, changes, runtimes, adapters, and benchmarks.
Compiled metadata remains internal and immutable.

`ObjectSetBuilder<T>` still serves both as build-time fluent configuration and runtime identity handle.

Before public alpha, separate those roles.

Target:

```csharp
ObjectSet<InvoiceLine> invoices = model.Objects<InvoiceLine>()
    .Key(x => x.Id);
```

Possible internal split:

```text
ObjectSetBuilder<T>      mutable construction state
ObjectSet<T>             stable public handle
ObjectSetDefinition<T>   compiled metadata
ObjectSetRuntime         runtime population
```

This will make the public API easier to reason about and evolve.

---

# 12. P2 — Compile null-safe path readers

**Status:** Implemented.

`MemberPath` now compiles a null-propagating object reader once. Navigation and strict-change validation
share cached compiled single-member readers, while hash-key extraction automatically uses compiled paths.
A BenchmarkDotNet reflection-versus-compiled-path benchmark records the performance tradeoff.

`MemberPath.Read(...)` is correct but reflection-heavy.

Compile once per path:

```text
MemberPath
    -> Func<object, object?>
```

while preserving null propagation.

Use the compiled reader in:

```text
hash key extraction
navigation indexing
change routing
relation lookup
```

Benchmark before/after.

Do not add a source generator unless compiled expression accessors are demonstrably insufficient.

---

# 13. P2 — Expand LINQ dependency coverage pragmatically

**Status:** Implemented.

Dependency analysis now supports `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Last`,
`LastOrDefault`, `Distinct`, `Take`, `Skip`, and `Contains`. Analysis records explicit membership,
item-property, and ordering semantics, including item members selected through first/last operators.

Current common aggregate/filter/projection support is a good base.

Add only well-understood operators with explicit semantics:

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

For each operator define whether it introduces:

```text
membership dependency
item-property dependency
ordering dependency
```

Unsupported cases should remain marked opaque rather than guessed.

---

# 14. P2 — Range planning only after measurement

**Status:** Evaluated; no dedicated range index added.

A 10k/100k BenchmarkDotNet workload now compares the existing equality hash prefix plus residual date
range against a full scan. The prefix plan measured about 147x and 180x faster respectively on the
recorded machine, so the evidence does not justify interval-tree complexity. Results and the rerun command
are committed under `benchmarks/RangePlanning-Results.md`.

Range expressions are already recognized diagnostically.

Do not introduce interval trees prematurely.

Benchmark realistic predicates first:

```csharp
rule.SupplierId == invoice.SupplierId &&
rule.ValidFrom <= invoice.Date &&
invoice.Date < rule.ValidTo
```

The existing hash prefix may already make residual range evaluation cheap enough.

Only add a dedicated range plan if benchmark evidence justifies it.

Possible first implementation:

```text
Hash equality prefix
    -> ordered candidate subset
    -> full predicate
```

Forced-scan equivalence remains mandatory.

---

# 15. P2 — Expand full-graph randomized correctness testing

**Status:** Implemented.

A deterministic 300-operation full-graph test compares hash and forced-scan runtimes plus direct reference
calculations across source/item lifecycle, join and residual changes, nested leaf changes, navigation
replacement, relation deltas, affected roots, derived values/states, invariant results/states, and repair
requests. Failures emit the seed and complete operation trace.

The key invariant is now broader than relation lookup:

```text
optimized runtime behavior == slow reference behavior
```

Compare:

```text
Relation results
RelationDelta
Derived values
Derived state
Invariant results/state
Affected source roots
Repair requests
```

Random operations should include:

```text
root add/remove
right item add/remove
join-key changes
residual changes
nested property changes
navigation replacement
null transitions
source-only derived dependencies
item-only derived dependencies
invariant-only dependencies
collection changes when available
```

Use deterministic random seeds and emit the operation trace on failure.

This can become one of the library's strongest correctness assets.

---

# 16. P2 — Benchmark propagation precision

**Status:** Implemented.

Runtime diagnostics now expose predicate-evaluation and affected-source counts. A 10k/100k
BenchmarkDotNet suite measures 1/10/100-change batches through deep nested leaves with two derived states
sharing one exact-propagation relation; `MemoryDiagnoser` records allocations alongside runtime.

Extend benchmarks beyond relation lookup.

Important scenarios:

```text
1 affected source among 10k / 100k
right join-key mutation
right residual mutation
relation-item derived dependency mutation
deep nested leaf mutation
source-only invariant dependency
ChangeSet with 1 / 10 / 100 changes
multiple derived states sharing one relation
multiple relations sharing one navigation path
```

Measure:

```text
runtime
allocations
number of evaluated predicates
number of affected sources
```

The benchmark suite should make accidental regressions from O(local impact) to O(total model size) obvious.

---

# 17. P2 — CI and package-readiness baseline

Before public NuGet alpha add CI:

```text
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release
```

Then add package metadata:

```text
PackageId
Description
RepositoryUrl
PackageLicenseExpression
Authors
PackageTags
GenerateDocumentationFile
```

Gradually enable:

```text
TreatWarningsAsErrors
nullable-clean builds
API compatibility checks later
```

Do not auto-publish packages until the public API is intentionally reviewed.

---

# Recommended execution order

Execute the next work in this order:

```text
1. Decouple Dirty/Invalid from join-key/access-plan semantics
2. Add reverse/bidirectional access plan for exact propagation
3. Add source lifecycle cleanup
4. Formalize state transitions
5. Promote source-scoped relation impact to a first-class abstraction
6. Separate runtime commit from policy/repair side effects
7. Tighten ChangeSet validation and semantics
8. Refactor propagation into dependency graph nodes
9. Add collection navigation
10. Mature EF Core unit-of-work integration
11. Stabilize ObjectSet<T> public API
12. Compile null-safe member accessors
13. Expand LINQ dependency coverage
14. Evaluate range planning from benchmarks
15. Expand full-graph randomized tests
16. Expand precision/performance benchmarks
17. Add CI and package metadata
```

---

# Next Codex task — implement only this

Do **not** implement the whole roadmap in one run.

The next Codex task should be:

> Decouple `Dirty` / `Invalid` propagation semantics from relation access-plan and join-key metadata.

## Scope

1. identify every place where `Invalid` is currently inferred from:
   - `JoinKeyPart`;
   - hash index participation;
   - access-plan choice;

2. introduce one explicit internal impact-severity abstraction;

3. ensure relation membership deltas and access/index maintenance remain separate concepts;

4. make current default relation-membership propagation use one explicit default severity policy rather than optimizer structure;

5. preserve all exact source-scoped invalidation behavior;

6. add forced-scan vs hash tests proving identical `Dirty`/`Invalid` results;

7. add tests proving an access-plan change alone cannot alter severity;

8. keep the design open for future per-dependency/per-invariant invalidation policy;

9. do **not** implement collection navigation, EF Core lifecycle support, or range indexes in this task;

10. run:

```bash
dotnet build
dotnet test
```

and keep all tests green.

## Architectural acceptance criterion

This must become true:

```text
optimizer decides HOW to access affected data
policy decides HOW SERIOUS stale dependent state is
```

Those responsibilities must no longer be coupled.

---

# Task after that

Once severity semantics are clean, implement reverse/bidirectional access planning for exact-propagation relations.

That task should specifically eliminate full left-set scans for equality relations when processing a right-side mutation.

Do not combine the two tasks: correctness semantics should be stabilized before optimizing reverse candidate lookup.
