# Raffinert.Relations — Architecture Review and Next Improvements

Reviewed against `main` after the alpha-readiness work.

## Executive assessment

`Raffinert.Relations` has moved well beyond the original proof of concept. The current repository already has:

- expression-defined binary relations;
- scan/hash planning with semantic fallback;
- reverse access and exact relation propagation;
- nested reference and collection navigation;
- normalized `MutationSet` processing;
- versioned `Prepare` / `Commit` / `Dispatch`;
- derived state with `Fresh`, `Dirty`, and `Invalid`;
- configurable semantic severity;
- invariant reactions and structured policy requests;
- incremental aggregate plans;
- EF Core unit-of-work integration;
- diagnostics, randomized equivalence tests, benchmarks, CI, API approval, and packaging infrastructure.

The next phase should **not** be more feature breadth or more optimizer structures.

The highest-value work now is:

```text
failure safety
    -> durable identity / integration contracts
    -> public API simplification
    -> propagation precision and scalability
    -> generalized dependency composition
    -> maintainability
```

The existing optimizer policy should remain: no new access structure without a measured workload.

---

# 1. P0 — Make runtime commit genuinely failure-safe

## Finding

The public documentation describes mutation application as atomic for runtime-owned state, and `Prepare(...)` is side-effect free.

However, `Prepare(...)` currently validates and normalizes mutations but does **not** precompute every operation that can fail during `Commit(...)`.

`CommitMutations(...)` still performs operations such as:

```text
ObjectSetRuntime.Add / Remove
NavigationIndex refresh
relation reindexing
relation membership refresh
compiled member-path reads
compiled relation predicate evaluation
derived incremental updates
```

Some of those operations can execute user/domain code:

```text
property getters
relation predicates
aggregate selectors
custom equality behavior
```

A representative lifecycle path is currently conceptually:

```text
ObjectSetRuntime.Add
    -> navigation state update
    -> relation AddRight/AddLeft
    -> candidate lookup
    -> predicate evaluation
```

If predicate or getter evaluation throws after the object set or navigation structures have changed, `RelationRuntime.Version` has not advanced yet, but runtime-owned state may already be partially modified.

That is a stronger problem than ordinary validation failure.

## Required contract

Choose one of these contracts explicitly:

### Preferred

> If `Commit(prepared)` throws, runtime-owned state is unchanged.

### Weaker fallback

> Commit is not exception-atomic; after a commit-time exception the runtime must be discarded/rebuilt.

The preferred contract is much more valuable for the intended EF/unit-of-work use case.

## Recommended implementation direction

Introduce an internal commit plan / rollback journal.

Possible shape:

```text
PreparedMutation
    -> ValidatedMutationBatch
    -> RuntimeMutationPlan
       - object-set operations
       - navigation deltas
       - index deltas
       - relation pair deltas
       - dependency state deltas
    -> apply plan
```

Two viable strategies:

### Strategy A — stage then commit

Evaluate all potentially failing readers/predicates/selectors into temporary deltas before changing live runtime state.

Then commit only deterministic dictionary/set operations.

### Strategy B — reversible commit journal

Record inverse operations while applying live updates and roll them back if any later step throws.

Staging is architecturally cleaner, but a rollback journal may be less invasive initially.

## Fault-injection tests

Add tests where each of these deliberately throws:

```text
relation predicate
nested property getter
incremental aggregate selector
custom key/equality path
```

For every case assert:

```text
runtime.Version unchanged
object-set membership unchanged
access indexes unchanged
navigation indexes unchanged
materialized relation pairs unchanged
derived state unchanged
invariant state unchanged
subsequent valid mutation still works
```

## Completion gate

A failed commit cannot leave runtime-owned state partially advanced.

---

# 2. P0 — Detect domain drift between `Prepare` and `Commit`

## Finding

A `PreparedMutation` is protected against **runtime drift** with `BaseVersion`.

It is not equivalently protected against **domain-object drift**.

Example:

```csharp
order.Number = "B";
var prepared = runtime.Prepare(
    MutationSet.Create(Change.Property(order, x => x.Number, "A", "B")),
    ChangeValidationMode.StrictNewValue);

// external code / interceptor / workflow changes it again
order.Number = "C";

runtime.Commit(prepared);
```

The runtime version can still match while the object graph no longer represents the state that was prepared.

This risk matters especially because the EF adapter intentionally spans:

```text
Capture
Prepare
SaveChanges / await
Commit
```

During that interval:

```text
SaveChanges interceptors
store-generated values
relationship fixup
application code
```

can alter tracked objects.

## Recommended design

Store a prepared-domain snapshot for every relevant mutation.

At minimum record:

```text
instance
member
prepared final value
collection membership fingerprint/reset token where needed
registered-set expectation
```

Before the first live runtime mutation in `Commit(...)`, verify that the prepared assumptions still hold.

This is distinct from `RelationRuntime.Version`:

```text
BaseVersion protects runtime state
Prepared snapshot protects domain state
```

## EF Core nuance

Database-generated keys are intentionally late-bound and should have an explicit rule:

```text
Added entity key:
    may be temporary/default at Prepare
    must be final/non-null/unique at Commit
```

Do not treat generated-key finalization as generic drift.

For other store/interceptor changes, either:

1. recapture/merge final tracked values before runtime commit; or
2. reject commit with a clear reconciliation error.

Silent acceptance is worse than an explicit failure.

## Completion gate

A prepared mutation can only commit when both:

```text
runtime version still matches
and
prepared domain assumptions still match
```

except for explicitly modeled late-bound values such as generated keys.

---

# 3. P0 — Add durable definition and source identity before calling policy requests outbox-ready

## Finding

`RuntimeApplyResult` is a useful boundary, but current public request identity is process/model-instance oriented.

Definition IDs are currently assigned from list position during model compilation:

```text
relation id   = relation list index
derived id    = derived list index
invariant id  = invariant list index
```

This is deterministic **inside one compiled model**, but it is not durable across model edits.

For example, inserting a new invariant earlier in model construction can renumber every later invariant.

`RepairRequestInfo` also carries:

```csharp
object Source
```

which is an in-process object reference, not a durable source identity.

Therefore this shape is excellent for in-process diagnostics/dispatch, but not yet a safe persistent outbox contract across deployments.

## Why this matters

A queued request may be processed after:

```text
application restart
new deployment
model definition reordering
```

An integer `InvariantId` that meant one invariant when enqueued can mean another after deployment.

Likewise an object reference cannot be serialized as the logical domain identity without application-specific transformation.

## Recommended model

Keep local ordinal IDs for compact diagnostics, but add optional stable logical keys.

Example direction:

```csharp
var receipts = model.Relation(poLines, goodsReceipts)
    .Named("PoLine.GoodsReceipts")
    .Where(...);

var receivedQuantity = model.Derived(poLines)
    .Named("PoLine.ReceivedQuantity")
    .Using(receipts)
    .Compute(...);

var quantityInvariant = model.Invariant(poLines)
    .Named("PoLine.ReceivedQuantityWithinOrdered")
    .Using(receivedQuantity)
    .Must(...);
```

Enforce uniqueness of explicit names within a compiled model.

Expose both concepts:

```text
RuntimeDefinitionId
    fast/local/ordinal

DefinitionKey
    stable/logical/external
```

## Durable source identity

Object sets already know how to read their stable key.

Structured requests should be able to expose something like:

```text
SourceIdentity
  ObjectSetKey / ObjectSetName
  CLR type
  SourceKey
```

Keep `Source` for in-process convenience if desired, but do not require an outbox integration to reverse-engineer identity from an object reference.

## Completion gate

A repair request can be persisted, survive a restart/deployment, and still identify:

```text
the same logical invariant
the same logical source object
```

without depending on model declaration order or object reference identity.

---

# 4. P0 — Review and simplify the public generic surface before first publication

## Finding

The implementation now has enough real behavior to reveal which generic parameters are implementation details rather than useful public identity.

Current public handles include:

```csharp
Derived<TSource, TItem, TValue>
Invariant<TSource, TItem, TValue>
```

But callers generally care about:

```text
Derived: source type + value type
Invariant: source type
```

The relation item type is primarily an implementation detail of how a particular derived value is computed.

It leaks into runtime calls:

```csharp
runtime.Get<TSource, TItem, TValue>(...)
runtime.Evaluate<TSource, TItem, TValue>(...)
```

and makes future multi-input derived state harder to express.

## Recommended public handles

Strongly consider simplifying to:

```csharp
Derived<TSource, TValue>
Invariant<TSource>
```

while keeping fully generic internal definitions/runtime states.

Example:

```csharp
Derived<PurchaseOrderLine, decimal> receivedQuantity = ...;
Invariant<PurchaseOrderLine> quantityInvariant = ...;

var value = runtime.Get(receivedQuantity, line);
var valid = runtime.Evaluate(quantityInvariant, line);
```

This is easier to understand and removes the assumption that every derived value permanently has exactly one relation-item type.

## Also review callback API

`ScheduleRepairWith(Action<TSource>)` is convenient, but it binds application behavior into compiled model definitions.

Long term, prefer structured repair data as the primary contract and make in-process callbacks an optional convenience layer.

## Why now

The checked-in shipped API baseline is still effectively empty and the alpha has not been auto-published.

This is the cheapest point to remove generic parameters or public concepts that will otherwise become compatibility baggage.

## Completion gate

Do one explicit API-design review where every public type/member answers:

```text
Does a normal consumer need this concept?
Is this semantic identity or an implementation detail?
Does this shape permit the next architecture step?
```

before promoting the alpha API baseline.

---

# 5. P0 — Restrict or fully model object-set key expressions

## Finding

Object-set keys are treated as immutable stable identity.

However, the current key API accepts an arbitrary expression:

```csharp
.Key(x => ...)
```

and key mutation protection is based on collected `MemberInfo` values.

A nested key such as:

```csharp
.Key(x => x.Customer.Id)
```

creates a difficult case:

```text
Customer.Id changes
```

The changed instance is the nested `Customer`, not the registered root object. The current root key check cannot reliably enforce immutability from that signal.

Mutable captured/static state inside a key expression has the same conceptual problem.

## Recommended alpha rule

Keep identity deliberately boring.

Accept only:

```text
direct scalar/value member
tuple/anonymous composite of direct scalar/value members
```

Examples:

```csharp
.Key(x => x.Id)
.Key(x => new { x.OrganizationId, x.Id })
.Key(x => (x.OrganizationId, x.Id))
```

Reject by default:

```text
nested navigation paths
method calls
mutable captured/static state
collection-derived keys
```

with a clear model-build error.

If there is later a real need for navigation-derived identity, model its dependency and immutability explicitly rather than accidentally supporting it.

## Optional future extension

Add explicit key comparer configuration for domains where logical identity requires non-default equality semantics.

## Completion gate

No supported key expression can change without the runtime being able to detect/reject that identity mutation.

---

# 6. P1 — Compile dependency adjacency instead of scanning every derived/invariant node

## Finding

`DependencyGraphRuntime` is conceptually the right abstraction, but its runtime execution is still mostly list-driven.

For each mutation pass it currently iterates:

```text
all derived nodes
all invariant nodes
```

and each node can iterate its dependencies against all property changes.

Conceptually the cost trends toward:

```text
O(definitions × changes × dependencies)
```

even when one changed member can only affect a tiny fraction of the model.

That is acceptable for the current small model/tests, but it does not yet behave like a compiled dependency graph when model size grows.

## Recommended architecture

Build adjacency during `CompiledRelationModel` construction.

Examples:

```text
(Member/DependencyPath key)
    -> affected DerivedNode ids
    -> affected InvariantNode ids

RelationDefinition
    -> dependent DerivedNode ids

DerivedDefinition
    -> dependent InvariantNode ids

ObjectSet
    -> lifecycle participants
```

At runtime, start from the changed dependency keys and process only reachable nodes.

Possible execution model:

```text
mutation
    -> seed impacted graph nodes
    -> source-scoped work queue
    -> propagate until queue empty
```

This also prepares the engine for derived-on-derived dependencies later.

## Related micro-optimizations

Pre-index these too:

```text
relations by LeftSet
relations by RightSet
source lifecycle participants by ObjectSet
```

so `Add`/`Remove` do not scan all relation definitions.

## Benchmark

Add a model-size benchmark independent of object-count benchmarks:

```text
10 definitions
100 definitions
1,000 definitions
```

with one property mutation that affects exactly one branch.

Measure:

```text
Apply latency
allocations
nodes visited
```

## Completion gate

Mutation cost scales primarily with the impacted graph, not the total number of unrelated model definitions.

---

# 7. P1 — Make severity source-scoped, not relation-impact-wide

## Finding

`DerivedImpactPolicy.ClassifyMembership(RelationImpact impact)` currently computes one strongest severity from the complete relation impact:

```text
any added pair   -> consider MembershipAdded
any removed pair -> consider MembershipRemoved
max severity wins
```

That one severity is then applied to every affected source.

Example policy:

```csharp
.MembershipAdded(Dirty)
.MembershipRemoved(Invalid)
```

and one batch produces:

```text
source A: one pair added
source B: one pair removed
```

Current classification can make **both** sources `Invalid` because the relation impact contains at least one removal somewhere.

This is safe but unnecessarily conservative and weakens the library's selective-invalidation story.

## Recommended change

Classify membership severity per source.

Conceptually:

```text
RelationImpact
    -> group AddedPairs by Left
    -> group RemovedPairs by Left
    -> classify each Left independently
```

Result:

```text
source A -> Dirty
source B -> Invalid
```

Expose/internalize a structure such as:

```text
SourceRelationImpact
  Source
  AddedRights
  RemovedRights
  SemanticImpact
```

## Also add direct-source severity

Current public `DerivedImpactPolicyBuilder` configures:

```text
MembershipAdded
MembershipRemoved
ItemChanged
```

but a derived computation may also depend directly on source properties.

Direct source dependency changes currently enter the dirty set without a public severity override.

Add at least:

```csharp
.SourceChanged(DependencySeverity severity)
```

Later, if real use cases require it, allow path-specific overrides:

```csharp
.SourceChanged(x => x.Quantity, DependencySeverity.Invalid)
```

Do not start with a large rule engine.

## Completion gate

A mixed relation delta gives each source the strongest severity caused by **that source's own delta**, not by unrelated sources in the same batch.

---

# 8. P1 — Generalize the dependency graph beyond exactly one relation -> one derived -> one invariant

## Finding

The current public model intentionally started narrow:

```text
Relation<TSource,TItem>
    -> Derived<TSource,TItem,TValue>
    -> Invariant<TSource,TItem,TValue>
```

That was a good way to prove the architecture.

It is now the main expressiveness ceiling.

Real derived state often depends on:

```text
source members
multiple relations
other derived values
external/manual dependency tokens
```

Example:

```text
AvailableQuantity
    = OrderedQuantity
    - ReceivedQuantity
    - ReservedQuantity
```

where `ReceivedQuantity` and `ReservedQuantity` may come from different relations.

An invariant may likewise depend on multiple derived values.

## Recommended staged evolution

### Stage A — source-only derived values

Support:

```csharp
model.Derived(poLines)
    .Compute(line => line.Quantity * line.UnitRate);
```

### Stage B — derived-on-derived

Support a typed form such as:

```csharp
model.Derived(poLines)
    .Using(receivedQuantity, reservedQuantity)
    .Compute((line, received, reserved) =>
        line.Quantity - received - reserved);
```

### Stage C — invariants over multiple derived values

Example:

```csharp
model.Invariant(poLines)
    .Using(receivedQuantity, orderedQuantity)
    .Must((line, received, ordered) => received <= ordered);
```

Internally this turns the dependency graph into a real DAG.

## Required supporting work

- cycle detection during `Build()`;
- topological propagation order;
- source-scoped impact merging;
- one node recomputed at most once per mutation wave;
- simplified public handles from section 4 so relation item types do not leak into composition.

## Do not do first

Do not immediately create `Using` overloads for 2...8 dependencies as the architecture.

Design the internal DAG first; add only enough typed convenience overloads to validate the public shape.

## Completion gate

A derived/invariant model can compose multiple upstream values without creating artificial relations solely to work around the API.

---

# 9. P1 — Separate relation query access plans from dependency propagation plans

## Finding

The code correctly separates semantic severity from hash/scan access planning.

There is one more orthogonal concern to separate:

```text
query access strategy
vs
change-propagation strategy
```

Today, any relation consumed by derived state calls `RequireExactPropagation()` and materializes bidirectional relation membership.

That gives excellent precision and enables incremental aggregates, but can cost:

```text
O(left × right)
```

memory for dense relations.

Diagnostics currently warn about density, which is useful, but there is no alternative propagation mode.

## Proposed abstraction

Introduce a separate concept such as:

```text
RelationPropagationPlan
```

Possible initial modes:

```text
ExactMaterialized
ConservativeSourceInvalidation
```

Later, if justified:

```text
KeyRoutedExact
BroadcastInvalidation
```

### ExactMaterialized

Current behavior:

- retain pair membership;
- exact add/remove deltas;
- supports incremental Count/Sum/Any;
- highest memory use.

### ConservativeSourceInvalidation

Do not retain every pair solely for dependency propagation.

Use navigation/access metadata to identify a safe superset of affected sources and mark their derived state dirty/invalid.

Then full recomputation on demand restores freshness.

This trades some recomputation for bounded memory.

## Planner rule

The propagation plan should be driven by dependency needs, not public query access.

Examples:

```text
incremental aggregate requested
    -> ExactMaterialized likely required

full-recompute derived value over very dense relation
    -> conservative invalidation may be preferable
```

Do not auto-switch modes from runtime density yet. Start with explicit/compiled policy and benchmarks.

## Completion gate

Using a dense relation in a derived value no longer necessarily implies materializing every matching pair forever.

---

# 10. P1 — Make structured apply results truly data-first

## Finding

`RuntimeApplyResult` exposes good public data, but internally it also stores a private `Action` closure used by `DispatchPolicies()`.

That closure captures the prepared/runtime dispatch path.

Consequences:

```text
keeping RuntimeApplyResult alive can keep runtime/prepared state alive
result is not conceptually pure data
serialization cannot preserve dispatch capability
```

Likewise `PreparedMutation` contains runtime identity and internal policy-action state.

## Recommended direction

Prefer this conceptual split:

```text
RuntimeApplyResult
    pure immutable data

PolicyDispatchHandle
    in-process one-shot capability
```

or move dispatch back to the runtime:

```csharp
var result = runtime.ApplyDetailed(mutations);

await outbox.Store(result.RepairRequests);

runtime.DispatchPolicies(result.DispatchToken);
```

The exact API can remain simple, but avoid describing the result as stable data while it also secretly owns execution capability and potentially large object graphs.

## Completion gate

Persisting/caching a result does not accidentally retain the runtime or imply that a serialized result can later execute in-process callbacks.

---

# 11. P2 — Split the large runtime files by responsibility

## Finding

The architecture has matured faster than the physical code layout.

Notable files now combine many responsibilities, especially:

```text
Runtime.cs
NavigationAndImpact.cs
DerivedState.cs
```

`Runtime.cs` contains, among other things:

```text
CompiledRelationModel
DebugView construction
compiled diagnostics construction
RelationRuntime public API
prepare/commit/dispatch pipeline
mutation validation/normalization
object-set simulation
ObjectSetRuntime
RelationRuntimeState
CompositeKey
reference comparers
```

This makes local reasoning and review harder than the conceptual architecture actually is.

## Recommended split

A possible layout:

```text
Model/
  CompiledRelationModel.cs
  CompiledDiagnosticsBuilder.cs

Runtime/
  RelationRuntime.cs
  MutationPreparation.cs
  MutationCommit.cs
  ObjectSetRuntime.cs
  RelationRuntimeState.cs
  RelationMaterialization.cs

Dependencies/
  DependencyGraphRuntime.cs
  NavigationIndexRegistry.cs
  ImpactResolver.cs
  DependencyPolicies.cs

Planning/
  RelationAccessPlans.cs
  DerivedComputationPlans.cs

Diagnostics/
  RuntimeDiagnostics.cs
  DebugViewRenderer.cs
```

Do not change namespaces merely for folder layout unless there is a public API reason.

Split tests similarly; a 60k derived-state test file is already a signal that scenario categories deserve separate fixtures.

## Completion gate

A developer can inspect one concern without loading most of the runtime implementation into context.

---

# 12. P2 — Add model-scale and failure-mode testing, not just data-scale testing

Current randomized equivalence and 10k/100k data benchmarks are valuable.

The next blind spots are different.

## Add model-scale benchmarks

Vary number of definitions:

```text
relations / derived / invariants:
10
100
1,000
```

Keep affected branch size constant.

This measures whether graph routing scales with impacted nodes or total model size.

## Add fault-injection tests

Inject exceptions into:

```text
predicate evaluation
member access
incremental selector
policy callback
```

and assert the exact failure contract.

## Add identity-stability tests

Compile logically identical models with different declaration order.

Assert:

```text
local ordinal IDs may change
stable external DefinitionKey does not
```

## Add GC/retention tests where practical

After source removal and result disposal/release, use weak references to verify runtime structures do not retain detached roots unexpectedly.

This is especially useful once dispatch/result ownership is refactored.

---

# 13. Documentation cleanup

The repository root now contains several historical implementation-plan and roadmap Markdown files.

They are useful development history, but a new consumer has to determine which document is current.

Recommended structure:

```text
docs/
  architecture.md
  optimizer-policy.md
  purchase-order-example.md
  next-architecture-improvements.md
  roadmaps/
    archive/
      ... historical plans ...
```

Do not delete the history; move or clearly mark superseded plans.

Add a short status line at the top of archived roadmaps:

```text
Status: superseded / completed
Current plan: ../next-architecture-improvements.md
```

Keep README focused on package behavior and links to current architecture rather than implementation-history narrative.

---

# Recommended execution order

The recommended order from the current codebase is:

```text
1. Commit failure safety / rollback or staged plan
2. Prepare -> Commit domain-drift detection
3. Stable DefinitionKey + durable SourceIdentity
4. Public generic/API simplification
5. Key-expression contract hardening
6. Compiled dependency adjacency/work queue
7. Per-source severity classification + SourceChanged severity
8. Multi-input derived/invariant DAG
9. Separate propagation plans from query access plans
10. Pure-data result / dispatch-capability separation
11. File/runtime modularization
12. Model-scale/failure-mode benchmarks and tests
13. Documentation roadmap cleanup
```

Do not add another access-plan data structure before these unless a benchmark demonstrates a real bottleneck.

---

# Concrete next Codex task 1 — failure-safe commit

Give Codex this bounded task first:

> Make `RelationRuntime.Commit` exception-safe for runtime-owned state without changing public relation semantics.

Scope:

1. add fault-injection tests for throwing relation predicates/member reads;
2. prove the current partial-mutation failure with a regression test if reproducible;
3. introduce either staged runtime deltas or an internal rollback journal;
4. guarantee that failed commit leaves:
   - object-set membership,
   - navigation indexes,
   - access indexes,
   - materialized relation membership,
   - derived/invariant state,
   - runtime version
   unchanged;
5. do not change domain objects;
6. do not add asynchronous behavior;
7. run full randomized equivalence tests and benchmarks after correctness passes.

Do not combine this task with API redesign.

---

# Concrete next Codex task 2 — durable request identity

After failure safety:

> Add stable logical identity for externally persisted policy requests.

Scope:

1. introduce optional/required-before-external-use names for object sets, relations, derived values, and invariants;
2. validate uniqueness at `Build()`;
3. keep existing ordinal IDs for local diagnostics;
4. add stable `DefinitionKey` to public structured diagnostics/results;
5. add source stable key information to repair/immediate-evaluation request data;
6. document which fields are durable across process restarts/model reorder;
7. add reorder/rebuild tests.

Do not add a queue implementation to the core package.

---

# Concrete next Codex task 3 — public API simplification spike

Before promoting the shipped API baseline:

> Prototype simplified public derived/invariant handles and report the resulting API diff.

Target spike:

```csharp
Derived<TSource, TValue>
Invariant<TSource>
```

Keep internal implementations free to remain strongly generic.

Do not merge automatically if the required type erasure makes runtime code substantially worse; first compare the resulting API clarity and implementation complexity.

---

# Concrete next Codex task 4 — compiled graph routing

After public-contract work:

> Replace all-node dependency propagation scans with compiled adjacency and a source-scoped work queue.

Required benchmark:

```text
10 / 100 / 1,000 unrelated definitions
one affected dependency branch
```

The result should demonstrate that adding unrelated model definitions has little effect on mutation latency.

---

# Release gate recommendation

Before publishing the first alpha, I would require these items:

```text
[ ] failed Commit cannot corrupt runtime-owned state
[ ] Prepare/Commit drift behavior is explicit and tested
[ ] durable requests do not depend only on ordinal definition IDs/object references
[ ] public Derived/Invariant generic shape has been reviewed deliberately
[ ] supported key expressions have a strict stable-identity contract
[ ] README atomic/outbox wording matches actual guarantees exactly
```

The graph-adjacency, multi-input composition, and alternative propagation-plan work can safely follow in later alphas if the release is intentionally experimental.

---

# What should remain unchanged

Several current design choices are strong and should be preserved:

- the original expression remains semantic authority;
- unsupported optimization falls back rather than changing semantics;
- access planning stays separate from correctness severity;
- core mutation/graph work stays synchronous;
- external side effects occur only after runtime-owned commit;
- explicit mutation reporting is preferred over magical observation;
- EF Core remains a separate adapter package;
- optimizer additions require measured evidence;
- randomized optimized-vs-reference testing remains a release gate.

The next improvements should strengthen these contracts rather than replace them.
