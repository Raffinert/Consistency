# Codex implementation plan — derived runtime read API

Status: **DESIGN + IMPLEMENTATION PLAN — DO NOT IMPROVISE SEMANTICS**

Purpose: add a small, typed public API for consuming derived values from `ConsistencyRuntime` after a model has declared them.

Target user-facing shape:

```csharp
var value = runtime.Get(priceRate, link);                 // guaranteed fresh; recompute if needed
var found = runtime.TryGet(priceRate, link, out var cached); // cache-only; never recompute
var state = runtime.GetState(priceRate, link);            // Fresh / Dirty / Invalid; never recompute
```

The motivating PriceRate case is:

```csharp
var priceRate = model.Derived(links)
    .DependsOn(x => x.InvoiceLine.Price)
    .DependsOn(x => x.PurchaseOrderLine.Price)
    .Compute(x => x.InvoiceLine.Price / x.PurchaseOrderLine.Price)
    .Named("price-rate");

// after an input mutation:
decimal actualPriceRate = runtime.Get(priceRate, link);
```

The caller must not have to read a possibly stale materialized property such as `link.PriceRate` merely to obtain the logical current value represented by the derived node.

---

## 0. Evidence from the completed API-v2 dogfood

Before coding, read:

```text
docs/consistency-api-v2-concept-results.md
experiments/Raffinert.Consistency.ApiV2Concept/README.md
experiments/Raffinert.Consistency.ApiV2Concept/TYPE-SYSTEM-DIAGNOSTICS.md
```

The completed S1-S9 experiment established that all four candidate declaration APIs can translate to the existing engine and preserve current runtime behavior. It also established that stable explicit definition identity remains important and that typed domain concepts give better diagnostics than a universal provider abstraction.

Do **not** restart the declaration-API experiment in this task. Runtime consumption is a separate concern.

Also note: the optional YAML/portable-model spike was not present when this plan was reviewed. Do not claim it was completed and do not make this runtime API depend on it.

---

# 1. Current implementation facts to verify

Inspect at minimum:

```text
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
src/Raffinert.Consistency/Derived/DerivedRuntimeState.cs
src/Raffinert.Consistency/Derived/DerivedDefinitions.cs
src/Raffinert.Consistency/Derived/DerivedBuilders.cs
src/Raffinert.Consistency/DependencyGraph/DependencyGraphRuntime.cs
src/Raffinert.Consistency/Runtime/MutationCommit.cs
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.Scope.cs
```

Verify these facts rather than assuming them:

1. `ConsistencyRuntime` already owns an `IDerivedRuntimeState` per compiled derived definition.
2. `IDerivedRuntimeState.GetValue(source)` currently recomputes when its own cache entry is absent/Dirty/Invalid and stores the result as Fresh.
3. `IDerivedRuntimeState.GetValueState(source)` currently reports Dirty for an absent cache entry.
4. Runtime state is keyed by source object reference, not merely by source key.
5. Public derived handles retain enough generic information to expose `TSource` and `TValue` safely.
6. Existing internal recomputation of a downstream derived value correctly obtains fresh upstream derived inputs. If it does not, this is a semantic blocker: stop and document it before exposing `Get`.
7. `ConsistencyRuntime` is explicitly not thread-safe. Do not add synchronization in this task.

The agent must write a short `Implementation facts verified` section in its completion report with file/type references.

---

# 2. Public semantic contract

The three APIs deliberately mean different things.

## 2.1 `Get`

```csharp
TValue Get<TSource, TValue>(
    DerivedDefinition<TSource, TValue> derived,
    TSource source)
    where TSource : class;
```

The exact public handle type may differ from the sketch. Use the existing public derived handle returned by the builder. Do not invent a second identity object if the current definition already provides typed identity.

Contract:

```text
- validate that `derived` belongs to this compiled runtime/model
- validate that `source` belongs to the derived definition's source set
- return the logical current value of that derived node for that source
- if the cache entry is Fresh, return it without recomputation
- if absent, Dirty, or Invalid, recompute enough of the dependency chain to return a Fresh value
- after successful return, GetState(derived, source) == Fresh
- reading must not materialize a persisted mirror property merely because the value was requested
- reading must not dispatch repair callbacks or persistence work merely because the value was requested
- exceptions from the computation propagate; do not mark a failed computation Fresh
```

**Critical semantic question:** if downstream `Get` depends on upstream derived values that are Dirty/Invalid, the upstream values must become fresh before the downstream computation uses them. Do not expose public `Get` until a test proves this transitively.

## 2.2 `TryGet`

Target:

```csharp
bool TryGet<TSource, TValue>(
    DerivedDefinition<TSource, TValue> derived,
    TSource source,
    out TValue value)
    where TSource : class;
```

Contract for this plan:

> `TryGet` means **cache-only, no recomputation**.

It returns `true` only when a cached value exists. It may return a cached value whose state is Dirty or Invalid. This is intentional: callers choosing `TryGet` asked for the cached snapshot, not a trustworthy current value.

Therefore callers needing to know whether the cached value is safe must pair it with `GetState`.

Example:

```csharp
if (runtime.TryGet(priceRate, link, out var cached))
{
    var state = runtime.GetState(priceRate, link);
    // cached is the previous known value; state tells whether it is Fresh/Dirty/Invalid.
}
```

Do **not** silently redefine `TryGet` to mean “try to get a fresh value”. That would make it an exception-suppression variant of `Get` and destroy the cache-inspection use case.

If implementation evidence shows that returning stale/invalid cached values is too dangerous for a method named `TryGet`, stop at the design gate and compare these names before implementation:

```text
TryGetCached
TryGetSnapshot
TryGetKnownValue
```

Do not choose a different name silently.

## 2.3 `GetState`

```csharp
DerivedValueState GetState<TSource, TValue>(
    DerivedDefinition<TSource, TValue> derived,
    TSource source)
    where TSource : class;
```

Contract:

```text
- never recomputes
- never creates a cache value
- returns Fresh / Dirty / Invalid
- absent cache entry retains current engine semantics: Dirty
- validates model ownership and source registration identically to Get/TryGet
```

If `DerivedValueState` is not currently public, inspect whether exposing it is coherent. Prefer a small public semantic enum over leaking an internal runtime-state type. Preserve the existing Fresh/Dirty/Invalid terminology.

---

# 3. Do not conflate three different values

The implementation and docs must explicitly distinguish:

```text
1. domain/materialized property
   link.PriceRate

2. cached derived value inside ConsistencyRuntime
   possibly Fresh / Dirty / Invalid

3. logical current derived value
   runtime.Get(priceRate, link)
```

A call to `Get` may update #2 but must not automatically update #1.

EF `Materialize(...)` remains responsible for persisted mirrors. Runtime reads are not persistence operations.

Add a test proving:

```text
link.PriceRate == old persisted/materialized value
runtime.Get(priceRate, link) == newly computed logical value
link.PriceRate is still unchanged after Get
```

unless current Core semantics explicitly define derived computation as mutating the domain property. If so, document the conflict before proceeding.

---

# 4. Validation behavior

All three methods must fail consistently for misuse.

Test:

```text
null derived
null source
foreign derived definition from another compiled model/runtime
source from wrong object set
source object of correct CLR type but not registered
same CLR type used by two object sets: definition ownership must determine the valid set
removed source
```

Do not rely only on CLR generic types for object-set ownership. The API-v2 dogfood already found that same-CLR-type/different-set identity remains a runtime validation concern.

Prefer existing exception conventions. Do not invent custom exception types unless the repository already uses one for equivalent misuse.

---

# 5. Transitive freshness — mandatory dogfood

This is the most important test in the plan.

Build:

```text
Source member
   ↓
PriceRate
   ↓
UnitRate
   ↓
AllocationValidity (projected through another source)
```

Prime all values so they are Fresh.

Apply a mutation that makes PriceRate and downstream nodes Dirty/Invalid.

Then call only:

```csharp
var actual = runtime.Get(allocationValidity, allocation);
```

Assert:

```text
PriceRate computation ran if required
UnitRate computation ran if required
AllocationValidity computation ran
returned value reflects the mutation
states of all recomputed nodes are Fresh
unrelated source instances were not recomputed
```

Use diagnostics/recomputation counters where possible rather than implementation-private assertions.

If current downstream computation can call an upstream runtime state in a way that already ensures freshness, preserve that mechanism. Do not duplicate dependency traversal in the public method.

If the current engine does **not** guarantee this, stop and split the work:

```text
Phase A: fix internal transitive read semantics
Phase B: expose public Get API
```

Do not ship a `Get` that only refreshes the final cache entry while consuming stale upstream values.

---

# 6. Relation-backed incremental values

Dogfood a relation-backed `Sum`:

```text
OrderLine + matching Fulfillments -> FulfilledQuantity
```

Cases:

```text
A. Fresh cached Sum, additive mutation handled incrementally
B. Dirty Sum
C. Invalid Sum
D. cache absent
```

Verify:

```text
Get returns correct fresh value in all cases
Get does not force a full recomputation when existing incremental propagation already kept the entry Fresh
Get recomputes when state requires it
TryGet never increments full-recomputation counters
GetState never increments any computation counters
```

Do not make `Get` itself decide whether to use incremental Sum. Incremental propagation remains an engine concern.

---

# 7. Proposed internal surface

Prefer the smallest bridge from public typed definitions to existing internal runtime state.

Conceptually:

```csharp
public TValue Get<TSource, TValue>(DerivedDefinition<TSource, TValue> derived, TSource source)
    where TSource : class
{
    var state = ResolveDerivedState(derived, source);
    return (TValue)state.GetValue(source)!;
}
```

But do not blindly implement this sketch. The actual definition hierarchy may include source-only, relation-backed, composed, projected, or API-v2 wrapper handles.

Add internal operations only if necessary, preferably:

```text
Resolve typed definition -> IDerivedRuntimeState
validate source registration
GetValue
TryGetCachedValue
GetValueState
```

`IDerivedRuntimeState` currently has `GetValue` and `GetValueState`. It does not necessarily expose cache-presence/value without recomputation. Add an internal cache-only operation rather than inspecting private dictionaries from `ConsistencyRuntime`.

Suggested internal semantic shape:

```csharp
bool TryGetCachedValue(object source, out object? value);
```

Each runtime-state implementation should implement this directly against its cache.

Do not implement `TryGet` by:

```csharp
if (GetValueState(source) == Fresh)
    return GetValue(source);
```

because that cannot return existing Dirty/Invalid snapshots and blurs cache inspection with fresh reads.

---

# 8. Naming gate

Before modifying public API, create a small compile-only comparison in tests/docs for:

```csharp
runtime.Get(priceRate, link);
runtime.TryGet(priceRate, link, out var cached);
runtime.GetState(priceRate, link);
```

versus:

```csharp
runtime.Get(priceRate, link);
runtime.TryGetCached(priceRate, link, out var cached);
runtime.GetState(priceRate, link);
```

Evaluate only this ambiguity:

> Does a normal .NET developer reasonably expect `TryGet` to return an Invalid cached value?

If yes/no is not obvious, prefer `TryGetCached` because the longer name makes the no-recompute/stale-allowed contract explicit.

The target concept is fixed; the exact cache-only method name is a human decision gate.

Do not rename `Get` to `GetActualValue`. `Get` is the primary logical read; the domain property is the materialized value.

---

# 9. Async question

Do not add `GetAsync` in this task.

Current derived computations are synchronous. A synchronous dependency graph should expose synchronous reads.

Record as future work only if a future derived node can perform async computation. Do not pre-design `ValueTask<T>` now.

---

# 10. Concurrency question

Do not add locks, concurrent dictionaries, snapshots, or thread-safe promises.

The existing runtime contract says mutations and queries must be externally synchronized. `Get`, cache-only read, and `GetState` are queries under that same contract.

Document that `Get` mutates runtime cache state even though it is logically a read. This matters for callers doing external synchronization.

---

# 11. Scope / EF interaction

Test behavior inside and outside `ConsistencyScope`.

Questions to answer with tests/documentation:

```text
Can Get be called during an authoritative EF scope before Complete()?
Does it see already-reported/tracked mutations at that point?
Can Get accidentally make an Invalid node Fresh and thereby weaken an EF Enforce decision?
Does Enforce evaluate logical current values independently of cache state?
Does reading a derived value schedule/consume repair work?
```

The desired principle is:

> refreshing a derived cache entry is not equivalent to repairing the domain consequence that caused Invalid state.

This is subtle. If `Invalid` currently means “derived cached value is unsafe” and `Get` recomputes it, becoming Fresh is correct for the value node. But any separately scheduled repair/invariant consequence must not disappear merely because somebody read the value.

Add a test around an Invalid value with pending repair/invariant consequence and prove a `Get` does not erase required policy work.

---

# 12. Tests to add

Create focused tests in the existing test project. Follow repository naming/style.

Minimum matrix:

| Case | Get | cache-only TryGet | GetState |
|---|---|---|---|
| no cache entry | computes + Fresh | false | Dirty |
| Fresh cache | same value, no recompute | true + value | Fresh |
| Dirty cache | recompute + Fresh | true + old cached value | Dirty before Get |
| Invalid cache | recompute + Fresh | true + old cached value | Invalid before Get |
| computation throws | throws, not Fresh | previous cache semantics preserved | previous state preserved |
| foreign definition | throws | throws | throws |
| unregistered source | throws | throws | throws |
| removed source | throws | throws | throws |
| transitive upstream Dirty | refresh chain | no recompute | unchanged |
| projected upstream Dirty | refresh projected chain | no recompute | unchanged |
| relation Sum already incrementally Fresh | no full recompute | returns current cache | Fresh |
| materialized property stale | returns logical fresh value | returns cache if present | correct state |

Also test nullable/reference `TValue` and a value type such as `decimal` so `TryGet` does not confuse “cached null/default” with “no cache entry”.

---

# 13. Public API / documentation

If implementation passes the semantic gates:

1. update `PublicAPI.Unshipped.txt` according to repository conventions;
2. add XML docs that explicitly say whether a method recomputes;
3. add README example based on PriceRate or the neutral Association/UnitRate sample;
4. add a short section titled `Reading derived values`;
5. document materialized-property versus runtime-value distinction;
6. document cache-only read semantics and warn that Dirty/Invalid cached values are snapshots, not current trustworthy values;
7. do not imply thread safety.

Suggested README example:

```csharp
var state = runtime.GetState(priceRate, link);
var current = runtime.Get(priceRate, link); // guaranteed Fresh on successful return

// A materialized property may still contain its previously persisted value.
Console.WriteLine(link.PriceRate);
```

Do not encourage `TryGet` for normal business logic. `Get` is the normal consumption API; cache-only access is primarily diagnostics/advanced usage.

---

# 14. Performance acceptance

Add a small benchmark or diagnostic test if the repository already has an appropriate benchmark pattern. Otherwise use counters in tests.

Required properties:

```text
Fresh Get: O(1) derived-state lookup + cache lookup; no computation
GetState: O(1) derived-state lookup + cache lookup; no computation
cache-only TryGet: O(1) derived-state lookup + cache lookup; no computation
Dirty/Invalid Get: recompute only required dependency chain/source, not all instances
```

Do not introduce reflection/expression compilation on each read. Definition-to-runtime-state lookup must use existing compiled-model identity dictionaries.

---

# 15. Implementation sequence for a weak coding agent

## Task R1 — Verify current read semantics

Read the files from section 1. Add no product code yet. Write notes in the PR/commit report proving how source-only, relation-backed, upstream-derived, and projected-derived runtime states obtain values.

## Task R2 — Add failing public-contract tests

Add tests for the matrix in section 12 using the desired `Get`, cache-only read, and `GetState` semantics. If naming is unresolved, use `TryGetCached` in the tests until the human gate decides otherwise.

Do not weaken tests to fit current implementation.

## Task R3 — Add internal cache-only primitive

Extend `IDerivedRuntimeState` with a direct cache-presence/value operation. Implement it for every runtime-state class. Add internal tests if useful.

Do not recompute in this primitive.

## Task R4 — Add typed runtime resolution + validation

Implement one shared internal validation path used by all three public operations. It must validate definition ownership and source registration/object-set ownership.

Do not duplicate subtly different checks per method.

## Task R5 — Implement `GetState`

Implement the no-recompute state query first. Run focused tests.

## Task R6 — Implement cache-only read

Implement `TryGetCached` or maintainer-approved `TryGet`. Prove Dirty/Invalid cached snapshots can be observed without recomputation and absent cache returns false.

## Task R7 — Implement `Get`

Delegate actual recomputation to existing derived runtime-state logic. Do not implement a second graph evaluator.

Run the mandatory transitive freshness tests before proceeding.

## Task R8 — Test policy/repair and EF boundaries

Prove `Get` does not materialize properties, dispatch repair work, erase pending required work, or weaken persistence enforcement.

## Task R9 — Public API/docs

Update API baselines, XML docs, README, and samples only after semantics pass.

## Task R10 — Full verification

Run at minimum:

```bash
dotnet build Raffinert.Consistency.sln -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build
```

If the API-v2 concept experiment remains in the repository, also run:

```bash
dotnet build experiments/Raffinert.Consistency.ApiV2Concept/Raffinert.Consistency.ApiV2Concept.csproj -c Release
```

---

# 16. Stop conditions

Stop and report instead of improvising if any is true:

```text
[ ] downstream Get can consume stale upstream derived values
[ ] recomputing Invalid state erases required repair/policy work
[ ] public typed derived identity cannot be resolved without exposing internal types
[ ] same-CLR-type object sets cannot be validated correctly
[ ] Get necessarily mutates materialized domain properties
[ ] EF authoritative scope semantics become ambiguous
[ ] cache-only semantics cannot distinguish absent cache from cached default/null
```

For each stop condition, write the smallest engine/API change needed and wait for maintainer approval.

---

# 17. Completion report

The coding agent must finish with:

```text
## Implementation facts verified
## Public API implemented
## Exact cache-only method name chosen
## Transitive freshness evidence
## Dirty/Invalid behavior evidence
## Materialized-property separation evidence
## Repair/invariant interaction evidence
## EF scope/enforcement evidence
## Validation/misuse evidence
## Performance/counter evidence
## Commands run and results
## Remaining open questions
```

Do not report “done” merely because the three methods compile.

The feature is complete only when `runtime.Get(derived, source)` can be trusted as the canonical logical read of a derived node.