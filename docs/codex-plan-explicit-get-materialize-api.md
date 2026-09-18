# Codex plan — explicit `Evaluate` + `Materialize` derived-value API

Status: **FOLLOW-UP DESIGN / DOGFOOD PLAN — DO NOT MODIFY PRODUCTION API UNTIL THE DESIGN GATE PASSES**

Baseline evidence: commit `48cdbc93ac2d7f15cd8d2f29f89fb1f57a69b69f` (`experiments: compare derived storage semantics`).

Purpose: evaluate an explicit two-operation consumption model for derived values:

```csharp
var current = runtime.Evaluate(priceRate, link);
var materialized = runtime.Materialize(priceRate, link);
```

The naming is intentional. `Evaluate` is preferred over `Get` because obtaining the current logical value may execute dependency evaluation, recompute stale nodes, and mutate runtime cache/state. It is not a passive accessor. `Materialize` adds the separate physical side effect of synchronizing a configured domain-property mirror.

Intended meanings:

```text
Evaluate
    = ensure the logical derived value is Fresh
    = evaluate/recompute only when required
    = update runtime cache/state
    = return TValue
    = DO NOT synchronize a configured domain-property mirror

Materialize
    = Evaluate
    + synchronize the configured target property
    + return TValue
```

`Evaluate` does **not** mean force recomputation. A Fresh cached value must be returned without executing the computation again. If a future force-recompute operation is ever needed, it should be a separately named concept such as `Recompute`, not overloaded into `Evaluate`.

---

# 1. Read completed evidence first

Before coding, read:

```text
docs/codex-plan-derived-storage-materialization-semantics.md
docs/derived-storage-materialization-concept-results.md
docs/codex-plan-derived-runtime-read-api.md
experiments/Raffinert.Consistency.DerivedStorageConcept/**
src/Raffinert.Consistency/Derived/**
src/Raffinert.Consistency/Runtime/**
src/Raffinert.Consistency.EntityFrameworkCore/**
```

Do not repeat Models A/B/C from scratch. Preserve their evidence.

Verify and record:

```text
A: logical evaluation can return Fresh 5.5 while link.PriceRate remains stale 6.
B/C: synchronizing the property during the logical read physically mutates the object and EF tracking state.
All models: an unread stale materialized value requires a persistence/materialization boundary.
All models: direct property access can read stale data before synchronization.
B/C: setter failure and rollback become coupled to logical evaluation.
Derived-handle dependencies are safer than treating a materialized target property as an independent source edge.
```

The previous documents may use the provisional name `Get`. In this plan, interpret that logical-read concept as `Evaluate` unless the section explicitly discusses naming history.

---

# 2. Central proposal

Dogfood this API:

```csharp
TValue Evaluate<TSource, TValue>(derived, source);
TValue Materialize<TSource, TValue>(derived, source);
```

Semantic matrix:

| Operation | Ensure Fresh | May evaluate dependencies | May run computation | Update runtime cache | Assign configured property | Return TValue |
|---|---:|---:|---:|---:|---:|---:|
| `Evaluate` | yes | yes | only if needed | yes | no | yes |
| `Materialize` | yes | yes | only if needed | yes | yes | yes |
| direct property read | no | no | no | no | no | stored value |

Do not add `Get`, `GetActualValue`, `GetFresh`, `EnsureFresh`, `GetAndApply`, `GetAndMaterialize`, or `Refresh` as aliases during this wave.

The concept vocabulary under test is:

```text
Invalidate -> Evaluate -> Materialize -> Repair/Persist
```

Do not assume every transition occurs for every workflow.

---

# 3. Why `Evaluate` is different from `Get`

The agent must explicitly test the naming against behavior.

This call:

```csharp
var value = runtime.Evaluate(priceRate, link);
```

may perform:

```text
inspect PriceRate state
    ↓
find Dirty/Invalid
    ↓
evaluate upstream derived dependencies
    ↓
recompute PriceRate
    ↓
update runtime cache
    ↓
Dirty/Invalid -> Fresh
    ↓
return value
```

A method named `Get` can look like a passive cache/property accessor even though the operation above has runtime side effects. `Evaluate` exposes that semantic weight.

However, prove that `Evaluate` is still lazy/cached:

```csharp
var a = runtime.Evaluate(priceRate, link);
var b = runtime.Evaluate(priceRate, link);
```

If nothing changed between calls, the second call must not recompute.

Required documentation sentence to dogfood:

> Returns the current logical value of the derived definition, evaluating it only when its cached value is not Fresh.

If users consistently interpret `Evaluate` as “always recompute”, record that as a naming drawback rather than changing semantics.

---

# 4. Fundamental model

Start from **runtime-authoritative derived value + optional materialized mirror**:

```text
DerivedDefinition<TSource,TValue>
        │
        ├── computation/dependencies
        ├── runtime cache + Fresh/Dirty/Invalid
        └── optional materialization target
                    ↓
              source.PriceRate
```

Therefore:

```text
logical current value = runtime derived value
materialized property = synchronized representation/mirror
```

This deliberately differs from property-backed Model C. Do not delete Model C from previous results. The final comparison must state whether explicit `Evaluate`/`Materialize` is preferable after considering stale direct reads and duplicate representations.

---

# 5. Canonical PriceRate lifecycle

Use:

```text
initial:
InvoiceLine.Price       = 60
PurchaseOrderLine.Price = 10
runtime PriceRate       = 6 / Fresh
link.PriceRate          = 6

mutation:
InvoiceLine.Price       = 55

before evaluation:
runtime PriceRate       = 6 / Dirty or Invalid
link.PriceRate          = 6
logical PriceRate       = 5.5
```

## 5.1 Evaluate

```csharp
var current = runtime.Evaluate(priceRate, link);
```

Required:

```text
current                 = 5.5
runtime PriceRate       = 5.5 / Fresh
link.PriceRate          = 6
```

## 5.2 Materialize after Evaluate

```csharp
var value = runtime.Materialize(priceRate, link);
```

Required:

```text
value                   = 5.5
runtime PriceRate       = 5.5 / Fresh
link.PriceRate          = 5.5
PriceRate computation   = NOT run again
```

## 5.3 Direct Materialize

Without prior Evaluate:

```csharp
var value = runtime.Materialize(priceRate, link);
```

Required:

```text
Evaluate internally
compute exactly once if stale
cache 5.5 / Fresh
assign link.PriceRate = 5.5
return 5.5
```

The implementation relationship under test is conceptually:

```text
Materialize(derived, source)
    ↓
Evaluate(derived, source)   // canonical logical evaluation path
    ↓
write configured target
    ↓
return TValue
```

Do not build a second evaluator inside Materialize.

---

# 6. Primary usage story

The API should communicate without extra explanation:

```csharp
var rate = runtime.Evaluate(priceRate, link);     // need current logical value
var stored = runtime.Materialize(priceRate, link); // need link.PriceRate synchronized too
```

After Materialize, both are valid:

```csharp
var rate = runtime.Materialize(priceRate, link);
```

and:

```csharp
runtime.Materialize(priceRate, link);
Use(link.PriceRate);
```

Do not add `EvaluateAndMaterialize`. `Materialize` already performs evaluation as necessary and returns `TValue`.

---

# 7. Runtime-only derived values

A definition may have no target property:

```csharp
var riskScore = ...;
var score = runtime.Evaluate(riskScore, link); // valid
```

Dogfood `Materialize(riskScore, link)` alternatives:

### M1 — reject

Throw a clear misuse exception because there is no materialization target.

### M2 — degenerate to Evaluate

Return the Fresh value and write nothing.

Default hypothesis: **M1** is clearer. `Materialize` should mean that some configured representation was actually synchronized.

Do not choose without evidence.

---

# 8. Where materialization metadata belongs

Determine whether target mapping should live in:

```text
A. Core compiled model
B. EF/persistence adapter only
C. optional Core materialization adapter/extension
```

Dogfood at least A and C conceptually.

The desired plain-object story is:

```csharp
var value = runtime.Materialize(priceRate, link);
Debug.Assert(link.PriceRate == value);
```

without requiring EF.

If this is a real requirement, EF-only metadata cannot power the Core API by itself.

Do not move production EF APIs during the spike.

---

# 9. Declaration syntax

Compare:

```csharp
var priceRate = links
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

and current-builder equivalent:

```csharp
var priceRate = model.Derived(links)
    .Compute(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

Do not default to:

```csharp
Derive(x => x.PriceRate, compute)
```

because that syntax implies property-backed identity/storage (previous Model C), while this plan tests runtime-authoritative value + mirror semantics.

If `MaterializeTo` is unclear for Core, compare `MirrorTo`, `StoreTo`, `WriteTo`, and `MaterializeTo`, but do not add aliases.

---

# 10. Fresh runtime + stale mirror is first-class

After:

```csharp
runtime.Evaluate(priceRate, link);
```

this is valid:

```text
runtime value = 5.5 / Fresh
link.PriceRate = 6
```

Do not overload Fresh/Dirty/Invalid to describe mirror synchronization.

Dogfood whether mirror synchronization needs separate state:

```text
Unknown / Synchronized / Stale
```

or can be determined by comparing current runtime value to target property at Materialize time.

Prefer no extra state if comparison is sufficient and correct.

Test nullable values, custom equality, external property changes, and setters that normalize values.

---

# 11. Materialize idempotence

Test:

```csharp
runtime.Materialize(priceRate, link);
runtime.Materialize(priceRate, link);
```

Second call should not recompute when logical value is Fresh.

Dogfood whether it should avoid redundant assignment when target already equals current value. Record equality semantics. Do not introduce configurable comparers without a demonstrated need.

---

# 12. External writes to mirror

Test:

```csharp
runtime.Evaluate(priceRate, link); // runtime Fresh 5.5
link.PriceRate = 999m;
```

Then:

```csharp
runtime.Evaluate(priceRate, link);     // still 5.5
runtime.Materialize(priceRate, link);  // restores mirror to 5.5
```

Under this candidate, external writes to the mirror never become authoritative inputs.

Analyze protection options:

```text
allow but overwrite
Roslyn analyzer warning/error
private/internal setter
encapsulation/source-generation later
```

Do not implement protection in production during this spike.

---

# 13. Derived handle versus mirror-property dependency

Use:

```text
PriceRate -> UnitRate
```

Preferred:

```csharp
unitRate.Using(priceRate)
```

Dogfood accidental:

```csharp
unitRate.DependsOn(x => x.PriceRate)
```

Because `link.PriceRate` is only a mirror, the latter can observe stale state after `Evaluate(priceRate, link)`.

Compare compiler policies:

```text
A. allow and accept split semantics
B. warn/reject dependencies on registered materialization targets
C. canonicalize target-property dependency to derived-handle dependency
```

Default hypothesis: B or C.

This is mandatory because explicit Evaluate makes the distinction between logical node and physical mirror sharper.

---

# 14. Transitive Evaluate must not materialize mirrors

Scenario:

```text
PriceRate (materialized mirror)
    ↓
UnitRate (materialized mirror)
```

After both become stale:

```csharp
var unit = runtime.Evaluate(unitRate, link);
```

Required:

```text
PriceRate logical value evaluated as needed
UnitRate logical value evaluated
both runtime states Fresh
link.PriceRate may remain old
link.UnitRate may remain old
```

This is the key semantic benefit of the name `Evaluate`: transitive graph evaluation is allowed; representation writes are not implied.

Then dogfood:

```csharp
runtime.Materialize(unitRate, link);
```

Compare:

### T1 — requested target only

```text
link.UnitRate updated
link.PriceRate may remain stale
```

### T2 — materialized upstream closure

```text
link.PriceRate updated
link.UnitRate updated
```

Default hypothesis: **T1** for explicit single-node Materialize. Persistence bulk materialization may intentionally process an affected closure.

---

# 15. Incremental aggregate

Use:

```text
OrderLine + Fulfillments -> FulfilledQuantity -> orderLine.FulfilledQuantity
```

When incremental propagation already keeps runtime aggregate Fresh:

```text
runtime cache = current aggregate / Fresh
property mirror = old aggregate
```

Then:

```csharp
runtime.Evaluate(fulfilledQuantity, line);
```

must return the Fresh cache with no full recompute and no property write.

```csharp
runtime.Materialize(fulfilledQuantity, line);
```

must write that already-Fresh value without a full scan/recompute.

This scenario proves `Evaluate` means “ensure current”, not “execute calculator”.

---

# 16. Dirty / Invalid and pending repair

Use:

```text
PriceRate change
    ↓
UnitRate Invalid
    ↓
LinkValidity Invalid
    ↓
pending rematch
```

Call:

```csharp
runtime.Evaluate(priceRate, link);
```

or:

```csharp
runtime.Materialize(priceRate, link);
```

Required:

```text
PriceRate may become Fresh
PriceRate mirror changes only for Materialize
UnitRate may remain Invalid
LinkValidity remains Invalid
pending repair remains pending
```

Evaluation/materialization is not business repair.

---

# 17. Computation failure

If evaluation throws:

```csharp
runtime.Evaluate(priceRate, link);
```

prove:

```text
no new value is marked Fresh
mirror is untouched
pending consequences are preserved
exception propagates according to existing conventions
```

`Materialize` must use the same evaluation semantics and must not assign the target when evaluation failed.

---

# 18. Setter/materializer failure

If logical evaluation succeeds but target setter throws during:

```csharp
runtime.Materialize(priceRate, link);
```

candidate semantics should normally be:

```text
runtime logical value may remain 5.5 / Fresh
link.PriceRate remains 6
Materialize throws
```

This is acceptable because logical freshness and mirror synchronization are separate.

On retry, Materialize should reuse Fresh 5.5 and retry only assignment.

This is a major simplification versus designs where logical read itself promises target synchronization.

---

# 19. EF tracking

For a tracked entity:

```csharp
runtime.Evaluate(priceRate, link);
```

must not mark `PriceRate` modified.

```csharp
runtime.Materialize(priceRate, link);
```

may naturally mark `PriceRate` modified if assignment changes it. This side effect is explicit in the method name.

Compare this directly with previous B/C evidence where provisional `Get` changed EF state.

Existing EF persistence materialization should reuse the same underlying semantic primitive where possible. Avoid two materialization implementations that can diverge.

---

# 20. Persistence without explicit Materialize

Application code must not be forced to write:

```csharp
runtime.Materialize(priceRate, link);
await db.SaveChangesAsync();
```

when persistence mapping already declares PriceRate as materialized state.

Required persistence lifecycle:

```text
input mutation
    ↓
no Evaluate
    ↓
no explicit Materialize
    ↓
SaveChanges / Complete
    ↓
evaluate logical value as required
    ↓
synchronize mapped property
    ↓
persist current value
```

Explicit runtime Materialize is for in-memory/application synchronization, not a mandatory persistence ritual.

---

# 21. Plain-object use

Prove or document blocker for:

```csharp
var value = runtime.Materialize(priceRate, link);
Debug.Assert(link.PriceRate == value);
```

without DbContext.

This determines whether materialization belongs in Core semantics or only persistence adapters.

---

# 22. Bulk materialization

Dogfood conceptually, but do not add production APIs:

```csharp
runtime.Materialize(priceRate, links);
runtime.Materialize(link);
```

Questions:

```text
Is there a concrete non-EF use case?
Can affected-source tracking avoid scanning everything?
Should per-source Materialize synchronize every configured mirror?
Does persistence already cover the only important bulk case?
```

If no concrete use case exists, keep the candidate API single-definition/single-source.

---

# 23. Naming experiment: `Evaluate` vs `Get`

Although `Evaluate` is now the primary candidate, the agent must collect evidence rather than merely replace strings.

Compare:

```csharp
runtime.Evaluate(priceRate, link);
runtime.Materialize(priceRate, link);
```

with historical:

```csharp
runtime.Get(priceRate, link);
runtime.Materialize(priceRate, link);
```

Ask reviewers/readers what each first method implies about:

```text
cache use
recomputation
runtime state mutation
property mutation
force recomputation
```

The desired interpretation for `Evaluate` is:

> Return the current logical value, evaluating only what is necessary when the cached value is not Fresh.

Record two possible naming risks:

```text
Get risk: sounds too passive for an operation that can recompute and mutate runtime state.
Evaluate risk: may sound like it always executes the computation.
```

Do not add both names to production. The results must recommend one for maintainer approval.

Also compare `Materialize` against `GetAndApply` and `Sync` only as naming evidence. Do not implement aliases.

---

# 24. Do we need GetState?

Keep separate from the Evaluate/Materialize decision.

Dogfood whether normal business code ever needs:

```csharp
runtime.GetState(priceRate, link);
```

before Evaluate or Materialize.

If not, recommend treating state inspection as diagnostics/advanced API rather than core consumption vocabulary.

`TryGetCached` remains out of the normal public API unless a concrete use case emerges.

---

# 25. Misuse analysis

Explicitly analyze:

```csharp
Use(link.PriceRate); // may read stale mirror
```

```csharp
var rate = runtime.Materialize(priceRate, link); // unnecessary physical write if only logical value needed
```

```csharp
unitRate.DependsOn(x => x.PriceRate); // split graph risk
```

```csharp
link.PriceRate = 999m; // rogue mirror write
```

```csharp
runtime.Evaluate(priceRate, link); // reader incorrectly expects forced recompute
```

For each state whether the design prevents, diagnoses, makes obvious, or silently permits it.

Do not claim Evaluate/Materialize solves stale direct property reads. It makes the two legitimate operations explicit; direct property access remains a separate misuse/protection problem.

---

# 26. Experiment deliverables

Extend:

```text
experiments/Raffinert.Consistency.DerivedStorageConcept/
```

Preferred additions/renames:

```text
ModelD.ExplicitEvaluateMaterialize.cs
Scenarios/
    EM01EvaluateThenMaterialize.cs
    EM02DirectMaterialize.cs
    EM03RepeatedEvaluateUsesCache.cs
    EM04RuntimeOnlyDerived.cs
    EM05TransitiveDerived.cs
    EM06IncrementalAggregate.cs
    EM07RepairSurvival.cs
    EM08ComputationFailure.cs
    EM09SetterFailure.cs
    EM10EfTracking.cs
    EM11PersistenceWithoutExplicitMaterialize.cs
    EM12PlainObject.cs
```

Update:

```text
docs/derived-storage-materialization-concept-results.md
```

with:

```text
## Model D — explicit Evaluate / Materialize
```

Do not rewrite previous A/B/C evidence except to clarify that their provisional `Get` terminology corresponds to logical evaluation in the comparison.

---

# 27. Results matrix

Append:

| Question | A runtime-authoritative | B read syncs mirror | C property-backed | D Evaluate/Materialize |
|---|---|---|---|---|
| logical read returns current value | | | | |
| logical read name communicates possible computation | | | | |
| repeated Fresh logical read avoids recompute | | | | |
| logical read has domain-object side effects | | | | |
| caller can explicitly synchronize property | | | | |
| synchronization returns TValue | | | | |
| direct property can be stale before synchronization | | | | |
| Fresh runtime + stale mirror representable | | | | |
| setter failure requires logical-cache rollback | | | | |
| EF tracking side effect obvious from call | | | | |
| runtime-only derived supported | | | | |
| incremental Fresh value materializes without recompute | | | | |
| transitive evaluation avoids unwanted mirror writes | | | | |
| pending repair survives evaluation/materialization | | | | |
| plain-object materialization possible | | | | |
| persistence works without explicit app call | | | | |
| invasive Core changes required | | | | |
| invasive EF changes required | | | | |

No numeric scoring.

---

# 28. Decision criteria

Evaluate Model D primarily against:

```text
1. hidden physical mutation in logical-read API
2. clarity that logical read may perform computation
3. stale direct property reads
4. duplicate authoritative representations
5. setter failure/rollback complexity
6. EF tracking surprise
7. feedback loops/split dependency graph
8. persistence ritual burden
9. runtime-only derived support
10. implementation invasiveness
```

Syntax length is secondary.

---

# 29. Production design gate

After dogfood, stop and ask maintainer decisions:

```text
Q1. Is `Evaluate` preferable to `Get` for the logical read?
Q2. Is Evaluate explicitly cache-aware/lazy rather than force-recompute? (default: yes)
Q3. Must Evaluate never synchronize a domain-property mirror? (default: yes)
Q4. Should Materialize return TValue? (default: yes)
Q5. Should Materialize reject definitions without a target?
Q6. Should Materialize synchronize only requested target or upstream materialized closure?
Q7. Does materialization metadata belong in Core, optional Core adapter, or persistence adapter?
Q8. Should dependencies on registered mirror properties be rejected/canonicalized to derived handles?
Q9. Should external writes to mirror properties be protected by analyzer/encapsulation?
Q10. Is Model D preferable to property-backed Model C after considering stale direct reads?
```

Do not modify production public API before these are answered.

---

# 30. If Model D is selected

Create/update a separate production implementation plan with phases:

```text
Phase 1: typed runtime.Evaluate
Phase 2: optional materialization-target abstraction
Phase 3: runtime.Materialize returning TValue
Phase 4: unify/reuse EF materialization semantics
Phase 5: target-property dependency diagnostics
Phase 6: PriceRate/UnitRate dogfood
Phase 7: public API baselines/docs/samples
```

Core implementation principle:

```text
Materialize
    ↓
Evaluate
    ↓
write target
    ↓
return same TValue
```

Do not duplicate graph evaluation.

---

# 31. Completion checklist

```text
[ ] previous A/B/C evidence preserved
[ ] provisional Get terminology mapped to Evaluate in Model D
[ ] Model D added without production API changes
[ ] Evaluate stale -> recompute -> Fresh proven
[ ] repeated Fresh Evaluate -> no recompute proven
[ ] Evaluate -> no property assignment proven
[ ] Evaluate then Materialize -> no second recompute proven
[ ] direct Materialize -> evaluate once + assign proven
[ ] runtime-only derived behavior M1/M2 compared
[ ] transitive materialization T1/T2 compared
[ ] incremental aggregate materializes Fresh cache without recompute
[ ] pending repair survives Evaluate/Materialize
[ ] computation failure behavior proven
[ ] setter failure retry behavior proven
[ ] EF tracking behavior proven
[ ] persistence succeeds without explicit app Materialize
[ ] plain-object materialization proven or blocker documented
[ ] mirror dependency split-graph risk analyzed
[ ] external mirror writes analyzed
[ ] Evaluate vs Get naming evidence recorded
[ ] Evaluate force-recompute ambiguity explicitly evaluated
[ ] GetAndApply/Sync/Materialize naming compared
[ ] GetState necessity reconsidered
[ ] A/B/C/D results matrix completed
[ ] no production Core/EF/public API baseline changes made
[ ] agent stops for maintainer decision
```

---

# 32. Final instruction to the coding agent

Do not mechanically rename `Get` to `Evaluate` and call the task complete.

Prove that the vocabulary matches behavior:

```text
input changes
    ↓
derived logical value becomes stale
    ↓
Evaluate
    ↓
evaluate only what is necessary
    ↓
logical value becomes Fresh
    ↓
(optional period where mirror remains stale)
    ↓
Materialize
    ↓
reuse Fresh logical value
    ↓
object property becomes synchronized
    ↓
persistence can reuse the same semantics
```

The developer story should fit in two lines:

```csharp
var rate = runtime.Evaluate(priceRate, link);      // current logical value; no property write
var stored = runtime.Materialize(priceRate, link); // current value + link.PriceRate synchronized
```

And this must remain true:

```csharp
var rate = runtime.Materialize(priceRate, link);
// rate == link.PriceRate
```

The word `Evaluate` must mean **ensure current through lazy/cached graph evaluation**, not **force execution every time**.

If the candidate cannot keep those statements true across transitive dependencies, incremental aggregates, computation/setter failures, repair policy, EF tracking, and plain objects, do not recommend it for production.