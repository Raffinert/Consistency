# Codex design plan — derived storage, materialization, and `runtime.Get(...)` semantics

Status: **DESIGN / DOGFOOD SPIKE FIRST — DO NOT IMPLEMENT THE PREVIOUS RUNTIME READ PLAN YET**

Purpose: decide where the authoritative/current value of a derived definition lives, who assigns a materialized domain property such as `link.PriceRate`, and what `runtime.Get(derived, source)` must do when the derived value is stale.

This plan supersedes the implementation assumptions in:

```text
docs/codex-plan-derived-runtime-read-api.md
```

Do **not** delete that document. Treat it as a downstream implementation candidate whose central materialization assumption is now unresolved.

The unresolved question is:

```text
InvoiceLine.Price changes
        ↓
PriceRate becomes Dirty/Invalid
        ↓
application asks for current PriceRate
        ↓
WHO owns the current value?
        ↓
WHO writes link.PriceRate?
```

The concept must be settled before adding a public `runtime.Get(...)` API.

---

# 1. Motivating dogfood: PriceRate

Use this scenario throughout the experiment.

Domain sketch:

```csharp
public sealed class PurchaseOrderInvoiceLine
{
    public required InvoiceLine InvoiceLine { get; init; }
    public required PurchaseOrderLine PurchaseOrderLine { get; init; }

    // Persisted/materialized derived property.
    public decimal PriceRate { get; set; }
}

public sealed class InvoiceLine
{
    public decimal Price { get; set; }
}

public sealed class PurchaseOrderLine
{
    public decimal Price { get; set; }
}
```

Derived rule:

```text
PriceRate = InvoiceLine.Price / PurchaseOrderLine.Price
```

Example state:

```text
initial:
InvoiceLine.Price       = 60
PurchaseOrderLine.Price = 10
link.PriceRate          = 6
PriceRate state         = Fresh

mutation:
InvoiceLine.Price       = 55

now:
link.PriceRate          = 6       // old persisted/materialized value
logical PriceRate       = 5.5
PriceRate state         = Dirty or Invalid
```

Application code wants:

```csharp
var rate = runtime.Get(priceRate, link);
```

After that call, answer both questions explicitly:

```text
What does `rate` contain?
What does `link.PriceRate` contain?
```

Do not accept a design that cannot answer both in one sentence.

---

# 2. Why this is a separate design problem

There can be three conceptually different things:

```text
A. materialized domain property
   link.PriceRate

B. runtime cached derived value
   runtime cache: link -> 5.5

C. logical current derived value
   PriceRate(link) = 5.5
```

If A and B can diverge, application code has two readable representations of the same business concept:

```csharp
link.PriceRate;                  // maybe stale
runtime.Get(priceRate, link);    // current
```

That creates a correctness trap: every developer must remember which read path is authoritative.

But automatically making A authoritative/cache storage also has costs: `Get` becomes physically mutating, derived definitions without a target property still need storage, rollback/snapshot behavior changes, EF change tracking sees assignments caused by reads, and property assignment can itself trigger dependency tracking.

This spike must compare those trade-offs using executable dogfood. Do not decide from aesthetics.

---

# 3. Hard guardrails

For the design wave:

1. Do not modify production Core or EF projects.
2. Do not modify public API baselines.
3. Do not implement `runtime.Get(...)` in production yet.
4. Do not delete or rewrite existing materialization behavior.
5. Do not add source generators/analyzers.
6. Do not redesign concurrency.
7. Do not require every derived value to have a domain property.
8. Do not assume EF is the only persistence adapter.
9. Do not equate recomputing a derived value with repairing all downstream business consequences.
10. Do not let a read silently clear unrelated pending repair/invariant work.
11. Do not create a second independent consistency engine.
12. Use the existing API-v2 concept project or a new isolated experiment project; production code remains unchanged.
13. The experiment must cover source-only, relation-backed, derived-to-derived, projected cross-object, and EF-materialized cases.
14. Do not optimize line count. Optimize semantic clarity and misuse resistance.

---

# 4. Read before coding

Inspect:

```text
docs/consistency-api-v2-concept-results.md
docs/codex-plan-derived-runtime-read-api.md
src/Raffinert.Consistency/Derived/DerivedRuntimeState.cs
src/Raffinert.Consistency/Derived/**
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
src/Raffinert.Consistency/Runtime/**
src/Raffinert.Consistency.EntityFrameworkCore/**
samples/Raffinert.Consistency.EntityFrameworkCore.Sample/**
samples/Raffinert.Consistency.OrderFulfillmentSample/**
```

Write `Existing storage semantics` notes before implementing any facade. Determine:

```text
where each derived kind currently stores cached values
whether all derived kinds cache values
how Fresh/Dirty/Invalid state is stored
how EF Materialize currently discovers and assigns values
when materialization occurs
whether materialization uses runtime GetValue internally
how snapshot/rollback captures derived cache state
how incremental aggregate updates mutate cached values
how source removal clears cache
whether materialized property assignment is currently reported as a mutation
whether a derived property may itself be a dependency of another definition
```

Do not guess.

---

# 5. Compare three storage/materialization models

Implement concept facades/harnesses for **all three**. They may translate to existing engine behavior or simulate the storage policy outside production Core. The goal is observable semantics.

## Model A — runtime value authoritative; materialization only at explicit/boundary synchronization

Definition sketch:

```csharp
var priceRate = links
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

Read semantics:

```csharp
var rate = runtime.Get(priceRate, link);
```

Expected after stale state:

```text
rate           = 5.5
runtime cache  = 5.5 / Fresh
link.PriceRate = 6
```

Later:

```text
SaveChanges / Complete / explicit Materialize
        ↓
link.PriceRate = 5.5
```

Questions:

```text
How does ordinary code avoid accidentally reading stale link.PriceRate?
Is MaterializeTo misleading if Get does not materialize?
What exact boundary guarantees the property is synchronized?
Can the property remain stale for a long-running in-memory workflow?
Does this require analyzer/encapsulation to be safe?
```

## Model B — runtime cache authoritative, but `Get` synchronizes configured materialized property

Definition sketch is the same:

```csharp
var priceRate = links
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

Read:

```csharp
var rate = runtime.Get(priceRate, link);
```

Expected:

```text
rate           = 5.5
runtime cache  = 5.5 / Fresh
link.PriceRate = 5.5
```

`Get` is logically a read but physically may mutate the source object.

Questions:

```text
Does Get assign only when recomputation occurs or whenever cache/property differ?
Does property assignment trigger EF modification tracking?
Can property assignment recursively invalidate the same derived node?
Can it trigger downstream dependencies that depend on PriceRate property?
How is reentrancy prevented?
What happens if the property setter throws?
If cache update succeeds but assignment fails, what state is restored?
Does rollback restore both runtime cache and property value?
```

## Model C — materialized property is the value storage for materialized derived definitions

Definition sketch:

```csharp
var priceRate = links.Derive(
    target: x => x.PriceRate,
    compute: x => x.InvoiceLine.Price / x.PurchaseOrderLine.Price)
    .Named("price-rate");
```

Conceptually runtime stores primarily:

```text
link -> Fresh / Dirty / Invalid
```

while value storage is:

```text
link.PriceRate
```

Read after stale input:

```csharp
var rate = runtime.Get(priceRate, link);
```

Expected:

```text
compute 5.5
assign link.PriceRate = 5.5
state -> Fresh
return link.PriceRate
```

Questions:

```text
Can materialized derived values eliminate duplicate runtime cache storage?
What does incremental Sum update when the target property is storage?
How does runtime snapshot/rollback capture previous property values?
Can a target property have a private setter?
Can target be a field? nested property?
What if target property conversion differs from TValue?
What if object is detached/untracked by EF?
How are non-materialized derived definitions represented alongside materialized ones?
Can one derived definition have multiple materialization targets?
Can one property be target of two definitions? Must compilation reject this?
```

---

# 6. Add a fourth control case: no materialization

Every model must also support a derived value that has no domain property:

```csharp
var riskScore = links
    .Select(CalculateRiskScore)
    .Named("risk-score");
```

Then:

```csharp
var score = runtime.Get(riskScore, link);
```

This proves that materialization is optional and that the fundamental `Derived<TSource,TValue>` abstraction is not accidentally reduced to “calculated property”.

Model C must explain where non-materialized values live. A likely answer is runtime cache, meaning C becomes a hybrid storage model. Record that explicitly rather than hiding it.

---

# 7. Mandatory dogfood scenarios

Use identical scenarios for A/B/C.

## D1 — PriceRate direct materialized derived

```text
InvoiceLine.Price + PurchaseOrderLine.Price -> PriceRate -> link.PriceRate
```

Prime at 6, mutate to logical 5.5, call `Get`, inspect returned value, runtime state, runtime cached value if applicable, and property.

## D2 — no read before persistence

Mutate PriceRate input and never call `Get`.

Then execute the persistence/materialization boundary.

Required question:

> Who guarantees `link.PriceRate` becomes 5.5 before persistence?

All viable models must have an answer.

## D3 — repeated fresh read

Call `Get` twice after synchronization.

Assert no second computation and no redundant property assignment where observable.

## D4 — PriceRate -> UnitRate derived chain

```text
Invoice/POL prices
       ↓
PriceRate
       ↓
UnitRate
```

Test combinations:

```text
PriceRate materialized, UnitRate runtime-only
PriceRate materialized, UnitRate materialized
PriceRate runtime-only, UnitRate materialized
```

Calling only:

```csharp
runtime.Get(unitRate, link);
```

must yield a UnitRate based on current PriceRate.

Record whether PriceRate's property is synchronized as a side effect under each model.

## D5 — downstream code directly reads `link.PriceRate`

This is deliberately a misuse-resistance test.

After PriceRate becomes stale but before any explicit `Get`, execute code that reads:

```csharp
Use(link.PriceRate);
```

For each model answer:

```text
Can this silently consume stale state?
Can encapsulation prevent it?
Would a Roslyn analyzer be required?
Would generated/intercepted property access be required?
Is the risk acceptable/documentable?
```

A model that requires every developer to remember “never read this normal-looking public property” receives a major usability warning.

## D6 — relation-backed incremental aggregate materialized to property

Use:

```text
OrderLine + Fulfillments -> FulfilledQuantity -> orderLine.FulfilledQuantity
```

Test:

```text
membership add
membership remove
item Quantity change
incremental Fresh update
Dirty transition
Invalid transition
```

For Model C especially, determine whether incremental update can write directly to the property without a separate cached value.

## D7 — projected cross-object dependency

Use:

```text
OrderLine.RemainingQuantity
       ↓ projected through Allocation.OrderLine
AllocationValidity
```

Materialize at least one upstream/downstream node. Prove cross-object routing still works and materialization does not hide dependency edges.

## D8 — invariant + pending repair

Create:

```text
PriceRate change
   ↓
UnitRate Invalid
   ↓
LinkValidity Invalid
   ↓
pending rematch/repair
```

Then call:

```csharp
runtime.Get(priceRate, link);
```

or `Get(unitRate, link)`.

Prove:

```text
recomputed value can become Fresh
materialized property can become current if the model says so
BUT pending downstream repair remains pending unless its own policy says otherwise
```

Do not allow “read caused repair to disappear”.

## D9 — failed computation

Make calculation throw.

For each model record atomicity:

```text
previous cached value
previous materialized property
previous state
pending consequences
```

After failure, no representation may falsely claim the new value is Fresh.

## D10 — failed property setter/materializer

Applicable to B/C.

Make target assignment throw. Determine rollback semantics. The system must not end with:

```text
runtime says Fresh/current = 5.5
property still = 6
```

unless the architecture explicitly permits divergence and marks it accordingly.

## D11 — EF change tracking

With tracked entity:

```text
Get(priceRate, link)
```

For B/C, inspect whether EF marks `PriceRate` modified. Decide whether that is desired.

Also test no-read persistence boundary.

## D12 — detached/plain object

Repeat PriceRate without EF. Core semantics must remain understandable without a persistence adapter.

---

# 8. Materialization timing matrix

For each model fill this table in results:

| Event | Should derived recompute? | Should target property assign? | Should repair dispatch? |
|---|---:|---:|---:|
| input mutation | usually no/lazy | ? | policy-dependent |
| `runtime.Get` on Fresh | no | ? | no |
| `runtime.Get` on Dirty | yes | ? | no |
| `runtime.Get` on Invalid | yes | ? | no |
| downstream `runtime.Get` requires upstream | as needed | ? | no |
| explicit `Refresh` if proposed | yes | yes? | no |
| EF `Materialize` | yes as needed | yes | no |
| `Complete` / persistence boundary | yes as required | yes as required | existing policy |

Do not fill `?` by intuition. Derive it from each candidate model.

---

# 9. Evaluate whether `MaterializeTo` and `Derive(target, compute)` mean different concepts

Compare these two declaration shapes:

```csharp
var priceRate = links
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate);
```

and:

```csharp
var priceRate = links.Derive(
    x => x.PriceRate,
    CalculatePriceRate);
```

Potential semantic distinction:

```text
Select(...).MaterializeTo(...)
    -> derived value fundamentally exists independently of property
    -> property is an output projection/mirror

Derive(target, compute)
    -> property itself is declared as derived state
    -> target participates in the semantic identity/storage contract
```

Do not treat these as mere syntax aliases until the storage experiment proves they are semantically equivalent.

The results must state which meaning is clearer for PriceRate.

---

# 10. Derived property as dependency: avoid double edges

Suppose:

```text
PriceRate definition computes and writes link.PriceRate
UnitRate depends on PriceRate definition
```

Do **not** accidentally model both:

```text
PriceRateDefinition -> UnitRate
link.PriceRate property mutation -> UnitRate
```

unless duplicate notification is intentionally deduplicated.

Test both declaration styles:

```csharp
unitRate.Using(priceRate)
```

and accidental/direct:

```csharp
unitRate.DependsOn(x => x.PriceRate)
```

Decide whether a materialized derived target property may be used as an ordinary source dependency. Candidate rules to evaluate:

```text
A. allow, compiler canonicalizes to derived dependency
B. allow, but document duplicate-safe propagation
C. reject and require dependency on the derived handle
D. treat property as source only when external mutation is allowed
```

This is mandatory. Materialized derived properties create a feedback-loop risk.

---

# 11. External mutation of a derived target

PriceRate has a public setter today in many anemic models. Ask what happens if application code does:

```csharp
link.PriceRate = 999m;
```

For each model define behavior:

```text
ignored until next recomputation?
marks derived Dirty/Invalid?
accepted as authoritative override?
rejected by analyzer/encapsulation?
causes feedback loop?
```

Recommended design pressure: a property declared as derived should not silently become a second authoritative input.

Dogfood these future protection options without implementing them in production:

```text
private/internal setter
Roslyn analyzer forbidding writes outside generated/materializer code
source-generated/intercepted setter
explicit Override API (only if a real use case exists)
```

Do not add override semantics without a concrete dogfood need.

---

# 12. Cache duplication and memory analysis

Measure/estimate per-source storage for A/B/C.

For a materialized `decimal PriceRate`, identify whether runtime stores:

```text
property decimal
cached decimal
state enum
Dictionary entry/reference/hash overhead
```

For Model C, quantify what duplicate value storage can be removed.

But do not choose C merely for memory savings. Correctness and composability dominate.

Also analyze non-materialized derived values, where runtime cache remains necessary.

---

# 13. Snapshot, Prepare/Plan/Commit, and rollback semantics

Current runtime has snapshot/restore behavior for derived cache state. Materialized properties complicate atomicity.

For B/C answer:

```text
If Get assigns property during a prepared/planned mutation, is that legal?
If commit fails after materialization, who restores property?
If computation succeeds but downstream propagation fails, who restores property?
Does CaptureSourcesState need property snapshots?
Can materialization be delayed until commit to preserve atomicity?
```

Dogfood at least one failed mutation/rollback scenario.

A candidate that requires broad invasive transaction changes must record that cost clearly.

---

# 14. Dirty vs Invalid semantics

Do not redefine these states casually.

Separate:

```text
value freshness
```

from:

```text
business consequences caused by the mutation
```

A successful `Get(priceRate, link)` may legitimately make the **PriceRate node** Fresh.

It must not imply:

```text
UnitRate repaired
links repaired
invariants satisfied
scheduled rematching cancelled
```

unless those nodes/actions were actually processed.

The results must contain a state timeline for D8.

---

# 15. Optional explicit synchronization API

Only if dogfood demonstrates a real need, compare:

```csharp
runtime.Refresh(priceRate, link);
```

or:

```csharp
runtime.Materialize(priceRate, link);
```

with `Get`.

Possible semantics:

```text
Get          -> ensure logical value Fresh and return it
Refresh      -> ensure Fresh; synchronize target if configured; no return needed
Materialize  -> synchronize configured property/persistence mirror
```

Do not add all three merely because names are available. Prefer the smallest coherent surface.

The experiment should answer whether persistence adapters alone are sufficient for explicit bulk materialization.

---

# 16. Bulk materialization

PriceRate may exist for thousands of links. Test conceptual APIs such as:

```csharp
runtime.Refresh(priceRate, links);
```

or adapter-owned:

```text
EF Materialize all required Dirty/Invalid PriceRates before SaveChanges
```

Questions:

```text
Can bulk synchronization avoid N repeated graph-resolution overhead?
Can it materialize only affected sources?
Does Model C simplify this because property is storage?
Does Model B create duplicate writes cache -> property?
```

No production bulk API is required in this spike.

---

# 17. API-v2 interaction

Run the storage concepts against at least the strongest API-v2 candidate(s) from `consistency-api-v2-concept-results.md`.

Do not reopen the whole A-D declaration competition. Test only whether the chosen/leading declaration style can clearly express both:

```text
runtime-only derived value
materialized derived value
```

Examples to compare:

```csharp
var priceRate = links
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

```csharp
var priceRate = links.Derive(
    x => x.PriceRate,
    CalculatePriceRate)
    .Named("price-rate");
```

```csharp
var riskScore = links
    .Select(CalculateRiskScore)
    .Named("risk-score");
```

The type system should make the distinction discoverable if the semantics differ.

---

# 18. Deliverables

Create:

```text
experiments/Raffinert.Consistency.DerivedStorageConcept/
    Raffinert.Consistency.DerivedStorageConcept.csproj
    README.md
    Domain.cs
    Harness.cs
    ModelA.RuntimeAuthoritative.cs
    ModelB.GetSynchronizesProperty.cs
    ModelC.PropertyBacked.cs
    Scenarios/
        D01PriceRate.cs
        D02PersistenceWithoutRead.cs
        D03RepeatedRead.cs
        D04DerivedChain.cs
        D05DirectPropertyRead.cs
        D06RelationAggregate.cs
        D07ProjectedDependency.cs
        D08RepairSurvival.cs
        D09ComputationFailure.cs
        D10SetterFailure.cs
        D11EfTracking.cs
        D12PlainObject.cs
```

If using actual EF in the concept project would add disproportionate setup, D11 may live in an existing EF test project behind concept-only tests. Document why.

Create final results:

```text
docs/derived-storage-materialization-concept-results.md
```

---

# 19. Results matrix

The results document must include:

| Question | A Runtime authoritative | B Get syncs property | C Property-backed |
|---|---|---|---|
| `Get` always returns current value | | | |
| property current immediately after `Get` | | | |
| property current before persistence without prior read | | | |
| direct property read can silently be stale | | | |
| duplicate value storage | | | |
| supports runtime-only derived | | | |
| supports relation incremental aggregate | | | |
| supports derived -> derived | | | |
| supports projected dependency | | | |
| setter failure atomicity simple | | | |
| rollback integration simple | | | |
| EF tracking behavior unsurprising | | | |
| feedback-loop risk | | | |
| external write protection required | | | |
| requires invasive Core changes | | | |
| requires invasive EF changes | | | |
| works for plain objects without EF | | | |

No numeric scoring. Give factual evidence and code references.

Also include a separate `Misuse analysis` section answering:

> What is the easiest incorrect thing a normal developer can write under each model?

That question is more important than syntax length.

---

# 20. Decision criteria

Do not automatically select a winner. Recommend one only if evidence clearly supports it, and label the recommendation as a proposal for maintainer approval.

The preferred model should minimize these failure modes in this order:

```text
1. silently reading a stale business value
2. silently losing required downstream repair/invariant consequences
3. two authoritative representations diverging
4. non-atomic cache/property synchronization
5. feedback loops / duplicate dependency propagation
6. inability to represent runtime-only derived values
7. invasive changes to existing engine semantics
8. performance/memory overhead
9. syntax verbosity
```

Syntax beauty is intentionally last.

---

# 21. Likely hybrid worth explicitly testing

Do not miss this possibility:

```text
Derived<TSource,TValue>
    ├── runtime-only storage policy
    └── property-backed/materialized storage policy
```

For example:

```csharp
var riskScore = links
    .Select(CalculateRiskScore);             // runtime cache

var priceRate = links.Derive(
    x => x.PriceRate,
    CalculatePriceRate);                     // property-backed
```

The runtime could expose the same consumption operation:

```csharp
runtime.Get(riskScore, link);
runtime.Get(priceRate, link);
```

while each definition owns its storage strategy.

This may be more coherent than forcing all derived values into one storage model. Test it explicitly as `C-Hybrid` in the results if Model C proves viable.

---

# 22. Questions that require maintainer decision after dogfood

The agent must stop after results and ask for decisions on at least:

```text
Q1. Is a configured materialized property allowed to remain stale after runtime.Get?
Q2. Should Get be allowed to physically mutate a source object?
Q3. Is a materialized derived property's property value itself the cache/storage, or only a mirror?
Q4. Should direct external writes to derived target properties be prohibited?
Q5. Should downstream definitions depend on the derived handle rather than the target property?
Q6. Is MaterializeTo output semantics or should Derive(target, compute) declare property-backed state?
Q7. Is an explicit Refresh/Materialize runtime API actually needed?
```

Do not proceed to production implementation until Q1-Q6 are settled. Q7 may remain deferred.

---

# 23. After human decision

Only after maintainer approval:

1. update `docs/codex-plan-derived-runtime-read-api.md` to match the selected storage semantics;
2. create a separate production implementation plan if the selected model requires substantial engine changes;
3. implement public `runtime.Get(...)` only after storage semantics are fixed;
4. add API baseline changes last;
5. add PriceRate-style documentation showing both declaration and consumption;
6. add misuse tests for direct property access/write according to the selected policy.

Do not merge concept facades into Core.

---

# 24. Completion checklist

```text
[ ] existing storage/materialization behavior inspected and documented
[ ] Models A, B, C dogfooded
[ ] runtime-only control derived dogfooded
[ ] D1-D12 attempted for every applicable model
[ ] PriceRate returned value vs property value explicitly recorded
[ ] no-read-before-persistence case proven
[ ] PriceRate -> UnitRate chain proven
[ ] relation incremental aggregate proven
[ ] projected dependency proven
[ ] Dirty/Invalid vs repair consequences separated
[ ] computation failure atomicity tested
[ ] setter/materializer failure atomicity tested for B/C
[ ] EF tracking behavior inspected
[ ] plain-object behavior inspected
[ ] external target mutation analyzed
[ ] duplicate-edge/feedback-loop risk analyzed
[ ] snapshot/rollback cost analyzed
[ ] duplicate cache/property storage analyzed
[ ] MaterializeTo vs Derive(target, compute) semantics compared
[ ] results matrix completed
[ ] misuse analysis completed
[ ] no production Core/EF/API baseline changes made
[ ] agent stopped for maintainer decision
```

---

# 25. Final instruction to the coding agent

Do not solve only this line:

```csharp
runtime.Get(priceRate, link);
```

Solve the lifecycle of the value:

```text
input mutation
    ↓
derived becomes stale
    ↓
optional read
    ↓
recomputation
    ↓
materialized property synchronization
    ↓
downstream invalidation/repair remains correct
    ↓
persistence
```

The key success criterion is that a developer can answer, without knowing runtime internals:

> “When I look at `PriceRate`, which value is authoritative, and who guarantees it is current?”

If the candidate architecture cannot make that answer simple, it is not ready for the public runtime read API.