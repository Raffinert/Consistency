# Raffinert.Relations — Architecture Review After Safety Hardening

Reviewed against `main` at `83a60e64e6c6764df6c73c92dc69c18b88c1f184`.

This follows `docs/next-architecture-improvements.md`. Since that review, the major correctness items were implemented: failure-safe prepared commits, Prepare/Commit domain-drift checks, durable definition/source identity metadata, simplified public derived/invariant handles, stable key-shape restrictions, per-source severity classification, and compiled dependency adjacency.

The architecture is materially stronger. The next phase should focus on preserving those guarantees at integration boundaries and making them scale.

## Priority

```text
P0  preserve per-source severity in public results
P0  make SourceIdentity actually durable across serialization/deployment
P0  define policy-dispatch partial-failure/retry semantics
P0  model EF SaveChanges-success/runtime-sync-failure explicitly
P1  replace whole-runtime rollback snapshots with touched-state transactions
P1  separate RuntimeApplyResult data from dispatch capability
P1  generalize relation -> derived -> invariant into a real DAG
P1  separate relation query plans from propagation plans
P1  execute core tests on net8 as well as net10
P2  benchmark safety machinery, modularize files, archive old roadmaps
```

---

## 1. P0 — Public results lose the new per-source severity precision

Internal propagation is now correctly source-scoped, but `RelationRuntime.CreateDetailedResult(...)` groups impacts by definition and promotes the entire group to the strongest severity.

Conceptually:

```text
source A -> Dirty
source B -> Invalid
```

is exposed as:

```text
DerivedMutationImpact
  Severity = Invalid
  Sources = [A, B]
```

The invariant result has the same issue. This loses precision exactly at the external integration boundary.

Prefer a source-scoped public model:

```csharp
public sealed record SourceDependencyImpact(
    object Source,
    SourceIdentity? SourceIdentity,
    DependencySeverity Severity);

public sealed record DerivedMutationImpact(
    int DerivedId,
    string? DefinitionKey,
    IReadOnlyList<SourceDependencyImpact> Sources);
```

Apply the same idea to invariant impacts. A simpler alternative is separate `DirtySources` and `InvalidSources`.

**Gate:** a mixed batch must preserve `A=Dirty`, `B=Invalid` in `RuntimeApplyResult`.

---

## 2. P0 — `SourceIdentity.IsDurable` promises more than `object SourceKey` guarantees

Current shape:

```csharp
public sealed record SourceIdentity(
    string? ObjectSetKey,
    Type SourceType,
    object SourceKey)
{
    public bool IsDurable => ObjectSetKey is not null;
}
```

But keys can be tuple/anonymous composites:

```csharp
.Key(x => (x.OrganizationId, x.Id))
.Key(x => new { x.OrganizationId, x.Id })
```

An arbitrary boxed CLR object is fine for an in-process dictionary, but it is not a complete durable outbox contract. Anonymous types, custom structs, tuple representation, serializer behavior, and versioning make later reconstruction underspecified.

Separate internal keys from external identity. Prefer a structural canonical representation, e.g. named key parts with a deliberately small set of serializable atom types. Composite keys should be decomposed instead of exposing the anonymous/tuple object itself.

Also consider:

```csharp
model.RequireDurablePolicyIdentities();
```

so `Build()` rejects policy-producing definitions without a stable `DefinitionKey`, named source set, and durable source-key representation.

**Gate:** `IsDurable == true` must mean the identity survives serialization, restart, and deployment without knowledge of a compiler-generated key type.

---

## 3. P0 — Policy dispatch can partially execute and still becomes permanently "dispatched"

`RuntimeApplyResult.DispatchPolicies()` sets `PoliciesDispatched = true` before invoking `_dispatch()`. `RelationRuntime.Dispatch(...)` similarly marks the prepared mutation dispatched before running callbacks.

If callback 3 of 10 throws:

```text
callbacks 1-2 may already have side effects
callback 3 failed
callbacks 4-10 were never attempted
result/prepared state says dispatched
retry is rejected
```

The README correctly says callback failure does not roll back runtime state, but partial-delivery semantics are not explicit.

Do not fix this merely by moving the flag after the loop: retry would then repeat callbacks that already succeeded. Either track per-action progress, or explicitly define dispatch as an at-most-once attempt and expose enough structured outcome to recover unattempted work.

A useful shape is:

```csharp
PolicyDispatchResult result = dispatch.Dispatch();
```

with `Succeeded`, `Failed`, and `NotAttempted` actions.

Add a three-callback test where the second throws and assert execution/retry behavior.

---

## 4. P0 — README still teaches the unsafe outbox identity

Despite the new `DefinitionKey` and `SourceIdentity`, the main README example still uses:

```csharp
outbox.Add(request.InvariantId, request.Source, request.Reason);
```

That persists declaration-order-local IDs and live object references — exactly what the durability work was intended to avoid.

The first concrete outbox example should use `DefinitionKey` + durable `SourceIdentity`, and reject a non-durable request before persistence.

---

## 5. P0 — EF needs an explicit "database save succeeded, runtime sync failed" contract

The helper does:

```csharp
unitOfWork.Prepare(runtime);
var result = context.SaveChanges();
unitOfWork.Commit(runtime);
unitOfWork.Dispatch(runtime);
```

Failure-safe runtime commit is correct locally, but a commit-time exception can now leave:

```text
EF SaveChanges succeeded
runtime commit rolled back
```

The database/context can therefore be ahead of the relation runtime.

Expose a dedicated failure type/result so applications can distinguish:

```text
SaveChanges failed
SaveChanges succeeded but runtime synchronization failed
runtime commit succeeded but policy dispatch failed
```

Use wording like `DatabaseSaveSucceeded`, not `DatabaseCommitted`, because SaveChanges may still be inside an outer transaction. Document reconciliation/rebuild behavior explicitly.

---

## 6. P1 — Failure safety currently snapshots the entire runtime on every commit

`Commit(...)` now does:

```csharp
var snapshot = CaptureState();
try
{
    var result = CommitMutations(...);
    ...
}
catch
{
    RestoreState(snapshot);
    throw;
}
```

`CaptureState()` copies all object sets, relation indexes/materialized pairs, navigation indexes, derived/invariant caches, dependency-wave state, impacts, and counters.

This is a strong correctness guarantee, but a one-property mutation can allocate/copy work proportional to the entire retained runtime.

There is additional repeated copying around lifecycle changes: Prepare builds `ObjectSetSimulation` for all sets, commit drift validation builds `PreparedSetState` for all sets, then commit snapshots everything.

First low-risk improvement: construct lifecycle simulations lazily only for touched object sets.

Longer term: replace whole-runtime snapshots with scoped snapshots, a typed undo journal, or a staged mutation plan so cost follows touched state.

Before changing architecture, benchmark successful Commit and failed Commit+rollback while mutation size stays constant and retained runtime grows through 1k/10k/100k objects and 10/100/1000 definitions. Measure allocations as well as latency.

---

## 7. P1 — `RuntimeApplyResult` is still not pure data

It contains a private dispatch `Action`, created from `() => Dispatch(prepared)`, so keeping the result can retain the prepared mutation and runtime graph.

Split:

```text
RuntimeApplyResult    immutable data only
PolicyDispatchHandle in-process execution capability
```

This also gives the dispatch failure semantics from section 3 a natural home.

---

## 8. P1 — Generalize the semantic graph into a DAG

Compiled adjacency is implemented, but the model is still structurally:

```text
Relation -> Derived -> Invariant
```

The next feature-level step should allow source-only derived values and derived-on-derived composition, e.g.:

```csharp
var available = model.Derived(poLines)
    .Using(receivedQuantity, reservedQuantity)
    .Compute((line, received, reserved) =>
        line.Quantity - received - reserved);
```

Required internals: cycle detection, topological ordering, source alignment, per-source severity merging, one evaluation per node/source per mutation wave, and compiled reverse adjacency.

Do not make overloads for `Using` 2..8 inputs the architecture; define a real internal input-edge model first.

---

## 9. P1 — Separate query access from propagation strategy

Relations consumed by derived state still require exact pair materialization. That is ideal for precise deltas and incremental aggregates, but dense relations can approach `O(left * right)` retained pair memory.

Keep separate concepts:

```text
RelationAccessPlan       how Related(...) finds candidates
RelationPropagationPlan  how changes find affected sources
```

Initial propagation modes can be only:

```text
ExactMaterialized
ConservativeSourceInvalidation
```

The conservative mode may invalidate a safe superset and fully recompute on demand without retaining every pair. Add it only when a measured dense-relation workload justifies it.

---

## 10. P1 — Run core behavior on .NET 8

The core package targets `net8.0;net10.0`, but the combined test project targets only `net10.0` because it also references the EF Core 10 adapter.

Split tests into core (`net8.0;net10.0`) and EF (`net10.0`) projects so every advertised core TFM executes the correctness suite in CI, not only compiles.

---

## Recommended implementation order

```text
1. Preserve per-source severity in RuntimeApplyResult
2. Replace arbitrary object SourceKey with canonical durable identity
3. Define policy-dispatch partial-failure/retry semantics
4. Correct README outbox example + durable-integration validation
5. Add explicit EF save-success/runtime-sync-failure contract
6. Benchmark current whole-runtime snapshot cost
7. Make lifecycle simulations touched-set-only
8. Replace whole-runtime snapshots with scoped snapshot/journal/staging
9. Split RuntimeApplyResult data from PolicyDispatchHandle
10. Design generalized dependency DAG
11. Add conservative propagation only with measured evidence
12. Split tests so net8 executes the core suite
13. Modularize Runtime.cs and archive superseded roadmap files
```

## Next-alpha gate

```text
[ ] public impacts preserve per-source severity
[ ] SourceIdentity has a canonical serializable key representation
[ ] README outbox example uses DefinitionKey + durable SourceIdentity
[ ] dispatch exception semantics are explicit and tested
[ ] EF save-success/runtime-sync-failure is explicit and documented
[ ] net8 core behavior runs in CI
```

The whole-runtime snapshot is not necessarily an alpha blocker if measurements show acceptable cost for the intended initial workloads, but it should be measured before performance claims are made.

## Overall assessment

The previous review focused on correctness prerequisites, and those were the right things to implement first. The strongest next concrete change is now **public result precision**: source-scoped severity is correct internally but is currently collapsed again at the point where applications consume it.

After that, make durable identity and dispatch semantics honest across process boundaries, then optimize rollback cost. Adding more optimizer breadth should remain lower priority.