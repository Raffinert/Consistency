# Codex plan — explicit `Get` + `Materialize` derived-value API

Status: **FOLLOW-UP DESIGN / DOGFOOD PLAN — DO NOT MODIFY PRODUCTION API UNTIL THE DESIGN GATE PASSES**

Baseline evidence: commit `48cdbc93ac2d7f15cd8d2f29f89fb1f57a69b69f` (`experiments: compare derived storage semantics`).

Purpose: evaluate an explicit two-operation consumption model for derived values:

```csharp
var current = runtime.Get(priceRate, link);
var materialized = runtime.Materialize(priceRate, link);
```

with these intended meanings:

```text
Get
    = ensure the logical derived value is Fresh and return it
    = DO NOT synchronize a configured domain-property mirror

Materialize
    = ensure the logical derived value is Fresh
    + synchronize its configured target property
    + return the materialized TValue
```

For a materialized `PriceRate`, after successful `Materialize`:

```csharp
var value = runtime.Materialize(priceRate, link);

Debug.Assert(value == link.PriceRate);
```

This plan exists because the previous storage experiment showed that making `Get` itself synchronize properties (Model B/C) introduces hidden physical mutation, EF tracking surprises, setter atomicity, feedback-loop risk, and rollback complexity. It also showed that Model A leaves an ordinary-looking property stale after `Get`. The explicit `Materialize` operation is a fourth design candidate that keeps evaluation and representation synchronization separate and makes the side effect visible at the call site.

---

# 1. Read the completed evidence first

Before writing code, read:

```text
docs/codex-plan-derived-storage-materialization-semantics.md
docs/derived-storage-materialization-concept-results.md
docs/codex-plan-derived-runtime-read-api.md
experiments/Raffinert.Consistency.DerivedStorageConcept/**
src/Raffinert.Consistency/Derived/**
src/Raffinert.Consistency/Runtime/**
src/Raffinert.Consistency.EntityFrameworkCore/**
```

Do not repeat Models A/B/C from scratch. Their results are evidence for this plan.

Record these verified facts from the completed experiment:

```text
A: Get can return Fresh 5.5 while link.PriceRate remains stale 6.
B/C: Get can synchronize link.PriceRate, but a method named Get then physically mutates the object and EF tracking state.
All models: an unread stale materialized value still requires an explicit persistence/materialization boundary.
All models: direct property access can read stale data before synchronization.
B/C: setter failure and rollback require atomic cache/property handling.
Derived-handle dependencies are safer than treating a materialized target property as an ordinary independent source edge.
```

If repository evidence has changed since the baseline commit, document the difference before proceeding.

---

# 2. Central proposal to dogfood

The proposal is **not** `GetAndApply`.

Use explicit domain terminology:

```csharp
TValue Get<TSource, TValue>(derived, source);
TValue Materialize<TSource, TValue>(derived, source);
```

Intended semantic matrix:

| Operation | Ensure Fresh | Update runtime cache | Assign configured property | Return TValue |
|---|---:|---:|---:|---:|
| `Get` | yes | yes | no | yes |
| `Materialize` | yes | yes | yes | yes |
| direct property read | no | no | no | stored value |

Do not add `GetAndApply` unless the dogfood proves `Materialize` is semantically wrong. `Apply` is already overloaded by mutation/application concepts and does not say what is being synchronized.

Do not add `Refresh` in this wave. It is ambiguous between “make runtime value Fresh” and “write the target property”.

---

# 3. Fundamental model for this candidate

For this experiment, start from **runtime-authoritative derived value + optional materialized mirror**:

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

This deliberately differs from property-backed Model C.

Do not delete Model C from previous results. The final comparison must say whether explicit `Materialize` removes enough of Model A's usability problem to prefer runtime-authoritative storage over property-backed storage.

---

# 4. PriceRate canonical example

Use this exact lifecycle as the first executable test:

```text
initial:
InvoiceLine.Price       = 60
PurchaseOrderLine.Price = 10
runtime PriceRate       = 6 / Fresh
link.PriceRate          = 6

mutation:
InvoiceLine.Price       = 55

before read:
runtime PriceRate       = 6 / Dirty or Invalid
link.PriceRate          = 6
logical PriceRate       = 5.5
```

## 4.1 `Get`

```csharp
var current = runtime.Get(priceRate, link);
```

Required result:

```text
current                 = 5.5
runtime PriceRate       = 5.5 / Fresh
link.PriceRate          = 6
```

## 4.2 `Materialize`

Then:

```csharp
var value = runtime.Materialize(priceRate, link);
```

Required result:

```text
value                   = 5.5
runtime PriceRate       = 5.5 / Fresh
link.PriceRate          = 5.5
```

Because runtime state is already Fresh after `Get`, `Materialize` must not recompute. It only synchronizes the configured target if needed.

Also test direct materialization without prior Get:

```csharp
var value = runtime.Materialize(priceRate, link);
```

Required:

```text
compute exactly once if stale
cache 5.5 / Fresh
assign link.PriceRate = 5.5
return 5.5
```

---

# 5. The important consequence: after `Materialize`, read the property directly

This is intentional and must be documented as a primary use case:

```csharp
runtime.Materialize(priceRate, link);
Use(link.PriceRate);
```

or:

```csharp
var rate = runtime.Materialize(priceRate, link);
```

Both are valid.

Do not invent an additional `GetAndMaterialize` convenience method. `Materialize` already returns `TValue`, so it covers that use case.

The API should communicate:

```text
Need the current logical value only?          Get
Need the object property to become current?   Materialize
```

---

# 6. What happens for runtime-only derived values?

A definition may have no target property:

```csharp
var riskScore = ...; // runtime-only derived
```

Then:

```csharp
runtime.Get(riskScore, link); // valid
```

What should this do?

```csharp
runtime.Materialize(riskScore, link);
```

Dogfood two alternatives:

### M1 — reject

Throw a clear misuse exception because no materialization target exists.

### M2 — degenerate to Get

Return the Fresh value without writing anything.

Default design hypothesis: **M1 is clearer**. A method called `Materialize` should mean that a representation was actually synchronized. Silent degeneration to `Get` hides configuration mistakes.

Do not choose until tested/documented.

---

# 7. Where does the materialization target belong?

This is mandatory.

The current repository has EF materialization concepts. Determine whether explicit runtime materialization requires the target mapping to live in:

```text
A. Core compiled model
B. EF/persistence adapter only
C. separate optional Core materialization extension/adapter
```

Dogfood at least A and C conceptually.

The desired plain-object scenario is:

```csharp
runtime.Materialize(priceRate, link);
Debug.Assert(link.PriceRate == current);
```

without requiring an EF `DbContext`.

If this is a real requirement, EF-only mapping cannot power the public Core operation by itself.

Do not move existing EF APIs into Core during the spike. Create concept-only mapping facades.

---

# 8. Declaration syntax alternatives

Compare at least:

```csharp
var priceRate = links
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

and:

```csharp
var priceRate = model.Derived(links)
    .Compute(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

Do **not** use:

```csharp
Derive(x => x.PriceRate, compute)
```

as the default in this candidate, because that syntax strongly suggests the property is the derived value's storage/identity (Model C). This plan is testing mirror/output semantics.

If `MaterializeTo` feels too persistence-specific for Core, compare names:

```text
MaterializeTo
StoreTo
MirrorTo
ProjectTo
WriteTo
```

Do not rename based on taste. Record which name best matches actual semantics.

---

# 9. Materialize must be idempotent when already synchronized

Test:

```csharp
runtime.Materialize(priceRate, link);
runtime.Materialize(priceRate, link);
```

Second call should:

```text
not recompute if runtime value is Fresh
not assign property if target already equals current value, unless setter semantics make equality checks unsafe
return current TValue
```

Record how equality is determined:

```text
EqualityComparer<TValue>.Default?
configured comparer?
always assign?
```

Do not introduce configurable equality without a demonstrated need. But do not assume decimal-only semantics.

---

# 10. Fresh cache + stale mirror is a first-class state

This candidate intentionally permits:

```text
runtime value = 5.5 / Fresh
link.PriceRate = 6
```

after `Get`.

The implementation must not confuse **value freshness** with **mirror synchronization**.

Therefore determine whether the runtime needs separate mirror state:

```text
ValueState: Fresh / Dirty / Invalid
MirrorState: Unknown / Synchronized / Stale
```

or whether synchronization can be determined by reading/comparing the target property on each `Materialize`.

Dogfood both approaches.

Prefer no extra state if equality comparison is sufficient and correct.

But test cases where:

```text
external code changes target property
property setter normalizes value
custom value equality
nullable target
```

Do not overload Fresh/Dirty/Invalid to mean mirror synchronization. Those states belong to logical derived correctness.

---

# 11. External writes to mirror property

Test:

```csharp
runtime.Get(priceRate, link); // runtime Fresh 5.5
link.PriceRate = 999m;
```

Then:

```csharp
runtime.Get(priceRate, link);         // must still return 5.5
runtime.Materialize(priceRate, link); // must restore target to 5.5
```

Under mirror semantics, an external write must **not** become authoritative input to the derived definition.

Decide whether external writes are:

```text
allowed but overwritten on Materialize
warned/analyzed
prevented through setter encapsulation
```

This candidate does not require hard prohibition for correctness because runtime cache remains authoritative, but direct reads can still observe the rogue value. Record that usability risk.

---

# 12. Derived handle versus target-property dependency

Use:

```text
PriceRate -> UnitRate
```

Preferred declaration:

```csharp
unitRate.Using(priceRate)
```

Test accidental declaration:

```csharp
unitRate.DependsOn(x => x.PriceRate)
```

Because `link.PriceRate` is only a mirror, the second declaration has different semantics and can be stale after `Get(priceRate, link)`.

The experiment must decide whether compilation should:

```text
A. allow it as an ordinary property dependency and accept mirror semantics
B. warn/reject dependencies on registered materialization targets
C. canonicalize target-property dependency to the derived handle
```

Default hypothesis: **B or C**, because allowing both creates a subtle split graph.

Do not implement compiler rejection in production during this plan.

---

# 13. Transitive Get must not materialize upstream mirrors

Scenario:

```text
PriceRate (materialized mirror)
    ↓
UnitRate (materialized mirror)
```

After both become stale:

```csharp
var unit = runtime.Get(unitRate, link);
```

Required candidate semantics:

```text
PriceRate logical value recomputed as needed
UnitRate logical value recomputed
both runtime states Fresh
link.PriceRate mirror may remain old
link.UnitRate mirror may remain old
```

`Get` means evaluation, not materialization, even transitively.

Then:

```csharp
runtime.Materialize(unitRate, link);
```

Dogfood two policies:

### T1 — materialize only requested target

```text
link.UnitRate updated
link.PriceRate may remain stale
```

### T2 — materialize materialized upstream dependency closure

```text
link.PriceRate updated
link.UnitRate updated
```

Default hypothesis: **T1**. `Materialize(unitRate)` should synchronize the representation explicitly requested, while evaluation can use runtime values without requiring every intermediate mirror to be written.

But persistence bulk materialization may deliberately choose closure/all affected mappings.

Do not decide without tests.

---

# 14. Relation-backed incremental aggregate

Use:

```text
OrderLine + Fulfillments -> FulfilledQuantity -> orderLine.FulfilledQuantity
```

When an incremental add keeps the runtime aggregate Fresh:

```text
runtime cache = new current aggregate / Fresh
property mirror = old aggregate
```

Then:

```csharp
runtime.Get(fulfilledQuantity, line);
```

must not write the property.

```csharp
runtime.Materialize(fulfilledQuantity, line);
```

must write the already-Fresh cached aggregate without forcing a full scan/recompute.

This scenario is mandatory because it demonstrates why evaluation and materialization are independent operations.

---

# 15. Dirty / Invalid and pending repair

Use the existing PriceRate -> UnitRate -> link validity -> rematch scenario.

After mutation:

```text
PriceRate Invalid
UnitRate Invalid
LinkValidity Invalid
repair pending
```

Call:

```csharp
runtime.Materialize(priceRate, link);
```

Expected:

```text
PriceRate becomes Fresh
link.PriceRate becomes current
UnitRate may remain Invalid
LinkValidity remains Invalid
repair remains pending
```

Materialization must not be interpreted as business repair.

Then dispatch/complete existing policy and prove repair still occurs exactly as required.

---

# 16. Setter/materializer failure atomicity

This is the most important implementation-risk test.

Suppose runtime value is stale and target setter throws:

```csharp
runtime.Materialize(priceRate, link);
```

There are two stages:

```text
1. ensure logical value Fresh
2. assign target property
```

If stage 1 succeeds and stage 2 fails, candidate semantics should normally be:

```text
runtime value may remain Fresh = 5.5
property remains 6
Materialize throws
```

This is **not** necessarily inconsistent because mirror synchronization is separate from logical freshness.

That is a major simplification compared with Model B/C, where Get promised the property was synchronized before returning.

Dogfood whether this simple behavior is sufficient.

On retry:

```csharp
runtime.Materialize(priceRate, link);
```

must reuse Fresh 5.5 and retry only the assignment.

Do not roll runtime value back merely to make mirror assignment atomic unless a real invariant requires it.

---

# 17. EF tracking semantics

For tracked entity:

```csharp
runtime.Get(priceRate, link);
```

must not mark `PriceRate` modified.

```csharp
runtime.Materialize(priceRate, link);
```

should naturally mark `PriceRate` modified if the setter/property assignment changes it.

This side effect is expected because the method explicitly says Materialize.

Compare this to the previous Model B/C result where `Get` itself unexpectedly changed EF tracking state.

Also prove the existing SaveChanges/EF materialization boundary can reuse the same underlying materialization primitive or at least the same semantics.

Avoid two implementations that can diverge.

---

# 18. Persistence without explicit application Materialize

The application must not be required to remember:

```csharp
runtime.Materialize(priceRate, link);
await db.SaveChangesAsync();
```

if the EF mapping declares PriceRate as persisted materialized state.

Existing persistence integration should still guarantee:

```text
input mutation
    ↓
no Get
    ↓
no explicit Materialize
    ↓
SaveChanges / Complete
    ↓
logical value evaluated
    ↓
property synchronized
    ↓
correct value persisted
```

The explicit runtime method is for in-memory/application synchronization, not a mandatory persistence ritual.

Dogfood both paths and assert identical final property/persisted values.

---

# 19. Plain-object use without EF

This is a key reason to consider materialization a Core-level concept.

Prove a plain object can do:

```csharp
var value = runtime.Materialize(priceRate, link);
Debug.Assert(link.PriceRate == value);
```

without DbContext or persistence adapter.

If the mapping currently exists only in EF configuration, prototype a concept-only Core mapping registration and compare complexity.

Do not move production mapping yet.

---

# 20. Bulk materialization

Dogfood conceptually:

```csharp
runtime.Materialize(priceRate, links);
```

and/or:

```csharp
runtime.Materialize(link);
```

Do not add either to production.

Questions:

```text
Is bulk by definition useful for non-EF workflows?
Can affected-source tracking make it efficient?
Should Materialize(link) synchronize every configured derived mirror for one source?
Can ordering follow the compiled DAG?
Does persistence already solve the only real bulk use case?
```

If there is no concrete non-EF use case, keep public API single-definition/single-source for now.

---

# 21. Naming dogfood

Compare only these candidate pairs:

```csharp
runtime.Get(priceRate, link);
runtime.Materialize(priceRate, link);
```

```csharp
runtime.Get(priceRate, link);
runtime.GetAndApply(priceRate, link);
```

```csharp
runtime.Get(priceRate, link);
runtime.Sync(priceRate, link);
```

Evaluate:

```text
Does the name expose the physical side effect?
Does it reuse an existing Consistency term with another meaning?
Does it scale to persistence/bulk operations?
Does it make sense when called without a prior Get?
Does returning TValue feel natural?
```

Expected hypothesis: `Materialize` is strongest because it names representation synchronization directly and already exists conceptually in the project.

Do not add aliases.

---

# 22. Do we still need `GetState`?

Keep this question separate.

The minimal business-facing API may be only:

```csharp
runtime.Get(...);
runtime.Materialize(...);
```

`GetState` may be diagnostics/advanced control rather than ordinary consumption.

Dogfood whether any D1-D12/GM scenarios require application code to inspect Fresh/Dirty/Invalid before calling Get/Materialize.

If not, recommend keeping `GetState` advanced/internal or exposing it only for diagnostics. Do not implement/remove it in this spike.

`TryGetCached` remains out of the normal public API unless a concrete use case appears.

---

# 23. Misuse analysis

The results must explicitly compare these incorrect snippets:

```csharp
// stale mirror risk
Use(link.PriceRate);
```

```csharp
// unnecessary materialization when only a logical value was needed
var rate = runtime.Materialize(priceRate, link);
```

```csharp
// split dependency graph
unitRate.DependsOn(x => x.PriceRate);
```

```csharp
// rogue mirror write
link.PriceRate = 999m;
```

For each, state whether the API:

```text
prevents it
makes it obvious
can diagnose it
silently allows it
```

Do not claim explicit Materialize solves stale direct reads. It only makes synchronization explicit when the caller chooses it.

---

# 24. Concept project / deliverables

Extend the existing storage experiment rather than creating another engine.

Preferred additions:

```text
experiments/Raffinert.Consistency.DerivedStorageConcept/
    ModelD.ExplicitMaterialize.cs
    Scenarios/
        GM01GetThenMaterialize.cs
        GM02DirectMaterialize.cs
        GM03RuntimeOnlyDerived.cs
        GM04TransitiveDerived.cs
        GM05IncrementalAggregate.cs
        GM06RepairSurvival.cs
        GM07SetterFailure.cs
        GM08EfTracking.cs
        GM09PersistenceWithoutExplicitMaterialize.cs
        GM10PlainObject.cs
```

Update:

```text
docs/derived-storage-materialization-concept-results.md
```

with a new section:

```text
## Model D — explicit Get / Materialize
```

Do not rewrite the previous A/B/C evidence.

---

# 25. Required Model D results matrix

Append this comparison:

| Question | A Get-only mirror | B Get syncs mirror | C property-backed | D explicit Materialize |
|---|---|---|---|---|
| Get returns current logical value | | | | |
| Get has domain-object side effects | | | | |
| caller can explicitly synchronize property | | | | |
| synchronization operation returns TValue | | | | |
| direct property read can be stale before synchronization | | | | |
| Fresh cache + stale mirror representable | | | | |
| setter failure requires cache rollback | | | | |
| EF tracking side effect obvious from call name | | | | |
| supports runtime-only derived | | | | |
| incremental Fresh cache can materialize without recompute | | | | |
| transitive Get avoids unwanted upstream writes | | | | |
| pending repair survives materialization | | | | |
| plain-object materialization possible | | | | |
| persistence works without explicit application call | | | | |
| invasive Core changes required | | | | |
| invasive EF changes required | | | | |

No numeric scores.

---

# 26. Decision criteria

Evaluate Model D primarily against the failure modes identified by the previous experiment:

```text
1. hidden mutation inside a method named Get
2. stale direct property reads
3. duplicate authoritative representations
4. setter failure atomicity complexity
5. EF change-tracking surprise
6. feedback loops / split dependency graph
7. persistence ritual burden
8. runtime-only derived support
9. implementation invasiveness
```

Model D knowingly does **not** eliminate stale direct property reads. Its proposed advantage is that it makes the transition from logical value to object representation explicit and side-effectful by name, while preserving simple runtime-authoritative evaluation.

The results must say whether that trade is actually better than A/B/C.

---

# 27. Production design gate

After Model D dogfood, stop and ask the maintainer to decide:

```text
D1. Should `Get` be strictly non-materializing?
D2. Should `Materialize` return TValue? (default proposal: yes)
D3. Should Materialize reject definitions without a target?
D4. Should Materialize synchronize only the requested definition or its materialized upstream closure?
D5. Should materialization target metadata become a Core concept, an optional Core adapter, or remain persistence-adapter-specific?
D6. Should dependencies on registered materialization target properties be rejected/canonicalized to derived-handle dependencies?
D7. Should external writes to mirror properties remain allowed-but-overwritten, or should analyzer/encapsulation protection be recommended?
D8. Is Model D preferable to property-backed Model C after considering stale direct reads?
```

Do not implement production public APIs before these are answered.

---

# 28. If Model D is selected

Only after approval create/update the production implementation plan. It should likely contain these phases:

```text
Phase 1: public typed runtime.Get
Phase 2: Core-level optional materialization-target abstraction
Phase 3: runtime.Materialize returning TValue
Phase 4: unify/reuse materialization semantics from EF adapter
Phase 5: dependency-target misuse diagnostics
Phase 6: PriceRate/UnitRate dogfood
Phase 7: API baselines/docs/samples
```

Important implementation principle:

```text
Materialize(derived, source)
    ↓
Get(derived, source)       // canonical evaluation path
    ↓
write configured target    // separate synchronization path
    ↓
return TValue
```

Do not create a second computation engine inside Materialize.

---

# 29. Completion checklist

```text
[ ] previous A/B/C evidence read and preserved
[ ] Model D concept added without production changes
[ ] GM01 Get then Materialize proven
[ ] GM02 direct Materialize proven
[ ] GM03 runtime-only derived behavior compared M1/M2
[ ] GM04 transitive derived behavior compared T1/T2
[ ] GM05 incremental aggregate materializes Fresh cache without recompute
[ ] GM06 pending repair survives Materialize
[ ] GM07 setter failure retry semantics proven
[ ] GM08 EF tracking behavior proven
[ ] GM09 persistence succeeds without explicit application Materialize
[ ] GM10 plain-object materialization proven or architectural blocker documented
[ ] Fresh runtime + stale mirror state explicitly modeled
[ ] target-property dependency split-graph risk analyzed
[ ] external mirror writes analyzed
[ ] GetAndApply / Sync / Materialize naming compared
[ ] GetState necessity reconsidered
[ ] A/B/C/D results matrix completed
[ ] no production Core/EF/public API baseline changes made
[ ] agent stops for maintainer decision
```

---

# 30. Final instruction to the coding agent

The goal is not to make this compile:

```csharp
runtime.Materialize(priceRate, link);
```

The goal is to prove that two explicit operations form a coherent lifecycle:

```text
input changes
    ↓
derived logical value becomes stale
    ↓
Get
    ↓
logical value becomes Fresh
    ↓
(optional period where mirror remains stale)
    ↓
Materialize
    ↓
object property becomes synchronized
    ↓
persistence can reuse the same semantics
```

The key developer story must fit in two lines:

```csharp
var rate = runtime.Get(priceRate, link);          // current value, no property write
var stored = runtime.Materialize(priceRate, link); // current value + link.PriceRate synchronized
```

And, because `Materialize` returns the value, this must also be a complete and natural usage:

```csharp
var rate = runtime.Materialize(priceRate, link);
// rate == link.PriceRate
```

If Model D cannot keep those statements true across transitive dependencies, incremental aggregates, setter failures, repair policy, EF tracking, and plain objects, do not recommend it for production.