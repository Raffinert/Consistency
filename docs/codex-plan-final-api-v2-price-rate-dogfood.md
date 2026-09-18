# Codex plan — focused final API v2 dogfood on PriceRate / UnitRate

Status: **FINAL SMALL API SPIKE BEFORE PRODUCTION PLAN — DO NOT MODIFY PRODUCTION PUBLIC API**

Purpose: stop comparing broad API families and dogfood one narrow candidate that combines the strongest findings from the previous API-v2 experiment with the selected runtime materialization direction.

This spike answers one question:

> Does the proposed declaration vocabulary remain coherent when used end-to-end for a realistic `PriceRate -> UnitRate -> materialization -> linking/invalidation/repair` graph, together with `Evaluate` and object-level `Materialize`?

The candidate is intentionally close to previous Variant C:

```text
Derived(source)
From(...)
DependsOn(...)
Select(...)
recognized aggregate operators: Sum / Count / LongCount / Any
Impact(...)
MaterializeTo(...)
Invariant(...).From(...).Must(...).ScheduleRepairWith(...)
```

Runtime consumption:

```csharp
runtime.Evaluate(definition, source);
runtime.Materialize(definition, source);
runtime.Materialize(source);
```

Do not reopen Variants A/B/C/D as equal candidates. Use their completed results as evidence.

---

# 1. Read existing evidence first

Before coding, read at minimum:

```text
docs/consistency-api-v2-concept-results.md
docs/codex-plan-explicit-get-materialize-api.md
docs/codex-plan-object-materialize-api.md
docs/derived-storage-materialization-concept-results.md
experiments/Raffinert.Consistency.ApiV2Concept/**
experiments/Raffinert.Consistency.DerivedStorageConcept/**
src/Raffinert.Consistency/**
src/Raffinert.Consistency.EntityFrameworkCore/**
```

Record these established findings in the new results document and do not re-prove them unless the focused scenario contradicts them:

```text
- Broad API Variant C had the lowest migration cost/new generic noise while retaining explicit ownership/policy concepts.
- A universal Roslyn/provider-style facade added generic/stage-type complexity without adding S1-S9 runtime capability.
- Recognized Sum can hide `.Incrementally()` while still selecting IncrementalSum in the compiled model.
- Cross-object projection is the hardest declaration case and requires explicit syntax.
- DependsOn remains useful for dependencies hidden inside opaque calculator code.
- Invariants and repair reactions should remain explicit and separate from pure derived computation.
- Evaluate means logical freshness without property materialization.
- targeted Materialize synchronizes one configured representation.
- object Materialize synchronizes all configured materialized representations physically located on the requested source object.
- object Materialize must not mean graph-wide repair.
- logical dependencies should use derived handles rather than registered mirror properties.
```

If current repository state differs, document the difference before proceeding.

---

# 2. Scope discipline

This is a **small focused dogfood**, not another framework implementation.

Do:

```text
- implement a disposable facade/adapter over the current engine;
- reuse current runtime semantics;
- use realistic PO/invoice domain names;
- execute the graph end-to-end;
- capture readability, type-system, diagnostics, compiled-plan, and runtime evidence.
```

Do not:

```text
- modify production public API;
- rewrite the engine;
- add concurrency;
- add source generation;
- add YAML persistence;
- build a universal LINQ provider;
- reproduce all S1-S9 variants;
- benchmark micro-optimizations unrelated to API choice.
```

Create/extend a disposable experiment such as:

```text
experiments/Raffinert.Consistency.FinalApiDogfood/
```

Prefer a separate small project so the final candidate is readable without A/B/C/D noise.

---

# 3. Canonical domain model

Use a simplified but realistic version of the Matching Engine problem.

Minimum objects:

```csharp
sealed class InvoiceLine
{
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
}

sealed class PurchaseOrderLine
{
    public decimal Price { get; set; }
    public decimal OrderedQuantity { get; set; }
}

sealed class PurchaseOrderInvoiceLine
{
    public InvoiceLine InvoiceLine { get; set; } = null!;
    public PurchaseOrderLine PurchaseOrderLine { get; set; } = null!;

    public decimal? PriceRate { get; set; }
    public decimal? UnitRate { get; set; }

    // simplified current link state / dependent representation
    public decimal LinkedQuantity { get; set; }
}
```

Add a simplified GoodsReceipt/Fulfillment relation only where needed to prove recognized aggregates and invalidation behavior.

The point is not to reproduce Semine production code. The point is to make the graph understandable to a new library consumer.

---

# 4. Target declaration style

Dogfood this as the primary candidate.

The exact generic signatures may differ in the disposable facade, but the call-site shape should remain close to:

```csharp
var priceRate = model
    .Derived(links)
    .DependsOn(
        x => x.InvoiceLine.Price,
        x => x.PurchaseOrderLine.Price)
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");

var unitRate = model
    .Derived(links)
    .From(priceRate)
    .DependsOn(
        x => x.InvoiceLine.Quantity,
        x => x.PurchaseOrderLine.OrderedQuantity)
    .Select((link, rate) => CalculateUnitRate(link, rate))
    .MaterializeTo(x => x.UnitRate)
    .Named("unit-rate");
```

Important semantic distinction:

```text
From(derivedHandle)
    = this logical derived value is an explicit input to the calculation.

DependsOn(member path)
    = calculator code reads this source/member opaquely; track it for invalidation/impact.
```

Do not silently make `DependsOn` inject values into the delegate parameters.

Do not use `Using` in the primary candidate unless the experiment proves `From` is misleading.

---

# 5. Test `From` as a real dataflow concept

The spike must prove that:

```csharp
.From(priceRate)
.Select((link, rate) => ...)
```

has one obvious meaning:

```text
PriceRate definition is a logical upstream node
        ↓
UnitRate receives PriceRate's current logical value
```

When PriceRate is Dirty/Invalid and UnitRate is evaluated, UnitRate must consume the logical PriceRate value through the handle. It must not read `link.PriceRate` as an independent source property.

Add a deliberate stale mirror:

```text
runtime PriceRate = 5.5 Fresh
link.PriceRate    = 6 stale
```

Then evaluate UnitRate and prove it uses `5.5`.

This is a release-gate scenario for the API model.

---

# 6. Test `DependsOn` only for opaque reads

Use a calculator where some source member is read inside ordinary code:

```csharp
static decimal? CalculatePriceRate(PurchaseOrderInvoiceLine link)
{
    var invoicePrice = link.InvoiceLine.Price;
    var poPrice = link.PurchaseOrderLine.Price;
    ...
}
```

Declaration:

```csharp
.DependsOn(
    x => x.InvoiceLine.Price,
    x => x.PurchaseOrderLine.Price)
.Select(CalculatePriceRate)
```

Prove that changing either nested member invalidates/recomputes PriceRate correctly.

Then include one intentionally missing dependency in a negative test/documentation example and show why opaque code cannot be inferred automatically by the current runtime.

The final results must explain:

```text
From = value flow
DependsOn = change tracking for opaque reads
```

If those concepts overlap too much in real code, record that as a design problem.

---

# 7. Cross-object projection syntax

Dogfood the hardest previous API case with the final candidate.

Example: an allocation or link depends on a derived value owned by its referenced PO line.

Target shape:

```csharp
var actualQuantity = model
    .Derived(links)
    .From(
        x => x.PurchaseOrderLine,
        remainingQuantity)
    .From(unitRate)
    .Select((link, remaining, rate) => ...)
    .Named("actual-quantity");
```

If multiple chained `From` calls become awkward, compare only one alternative:

```csharp
.From(x => x.PurchaseOrderLine, remainingQuantity, unitRate)
```

Do not reopen `For`, `SelectWith`, or universal provider designs unless this syntax fails materially.

Evaluate:

```text
- Is ownership obvious?
- Is projection direction obvious?
- Can compiler errors identify the wrong source/target types?
- Does the declaration still visually expose the DAG edge?
```

---

# 8. Recognized aggregate operators

Add a small relation-backed quantity scenario.

For example:

```csharp
var matchingReceipts = model
    .Relation(poLines, goodsReceipts)
    .Where(Matches)
    .Named("matching-receipts");

var receivedQuantity = model
    .Derived(poLines)
    .From(matchingReceipts)
    .Impact(p => p
        .MembershipAdded(Dirty)
        .MembershipRemoved(Invalid)
        .ItemChanged(Invalid))
    .Sum(x => x.Quantity)
    .Named("received-quantity");
```

Required:

```text
- declaration contains no `.Incrementally()`;
- compiled debug plan shows the optimized/incremental Sum strategy;
- additive mutation follows existing incremental semantics;
- subtractive/cancellation mutation follows existing Invalid semantics;
- fallback/recompute behavior remains correct.
```

Also create compile/execution coverage for:

```csharp
.Count()
.LongCount()
.Any()
```

If current engine does not yet have equivalent optimized implementations for all three, classify each as:

```text
syntax can be supported now / engine optimization missing / semantics unclear
```

Do not fake optimized plans.

The API principle under test is:

> semantic aggregate operators select the best available execution plan; users should not declare an implementation strategy for ordinary recognized operators.

---

# 9. Impact locality

Keep `Impact` next to the dependency/relation whose change semantics it describes.

Good:

```csharp
.From(matchingReceipts)
.Impact(p => p
    .MembershipAdded(Dirty)
    .MembershipRemoved(Invalid)
    .ItemChanged(Invalid))
.Sum(...)
```

For direct/nested source-member impact, dogfood whether this remains readable:

```csharp
.DependsOn(x => x.OrderedQuantity)
.Impact(p => p.SourceMemberChanged(
    x => x.OrderedQuantity,
    IncreaseDirtyDecreaseInvalid))
```

If `Impact` can accidentally refer to the wrong preceding dependency, treat that as an API-stage/type-safety issue. Do not accept positional ambiguity merely to keep the chain short.

Record whether impact belongs better:

```text
- immediately on a dependency stage;
- inside DependsOn/From configuration;
- on the derived definition as today.
```

Do not create more than one alternative implementation unless ambiguity is demonstrated.

---

# 10. Materialization declaration

Dogfood:

```csharp
.MaterializeTo(x => x.PriceRate)
```

and:

```csharp
.MaterializeTo(x => x.UnitRate)
```

The declaration must register physical mirror metadata without changing logical dependency semantics.

Mandatory checks:

```text
- target member type must match derived TValue;
- target must belong to the derived source object;
- registered mirror property must not become an authoritative source edge;
- two definitions cannot ambiguously materialize to the same target member unless explicitly rejected;
- runtime-only definitions simply omit MaterializeTo.
```

Prefer build/declaration-time rejection for duplicate/invalid targets.

---

# 11. Reject mirror-property dependencies

This is now a key graph-integrity rule.

Given:

```csharp
priceRate.MaterializeTo(x => x.PriceRate)
```

this should not be accepted as an independent dependency:

```csharp
model.Derived(links)
    .DependsOn(x => x.PriceRate)
    ...
```

when the intended logical dependency is PriceRate.

The correct form is:

```csharp
.From(priceRate)
```

Dogfood where this validation can happen:

```text
- declaration immediately;
- model Build();
- Roslyn analyzer only.
```

Preferred direction: compiled-model/build validation is correctness enforcement; analyzer may later improve developer feedback but must not be the only guard.

Error should explain the fix, e.g. conceptually:

```text
'PriceRate' is a materialization target of derived definition 'price-rate'.
Depend on the derived definition with From(priceRate) instead of treating the mirror property as an independent source dependency.
```

---

# 12. Invariant and repair syntax

Keep this explicit.

Target:

```csharp
var linkInvariant = model
    .Invariant(links)
    .From(linkValidity)
    .Must((_, valid) => valid)
    .Named("link-validity")
    .ScheduleRepairWith(Rematch);
```

or, if current invariant builder makes `From` impractical, compare:

```csharp
.Invariant(links)
.Using(linkValidity)
```

only here.

Do not compress Must + repair into one helper.

Prove:

```text
- materializing PriceRate/UnitRate does not consume repair;
- invalidation can leave rematch pending;
- normal completion/dispatch executes repair according to existing semantics;
- invariant declaration remains visually separate from pure computation.
```

---

# 13. Full PriceRate -> UnitRate -> linking lifecycle

Run this timeline end-to-end.

### Initial

```text
InvoiceLine.Price = 60
POL.Price         = 10
PriceRate logical = 6 Fresh
UnitRate logical  = 6 Fresh
link.PriceRate    = 6
link.UnitRate     = 6
linking valid
```

### Mutation

```text
InvoiceLine.Price = 55
```

Expected graph consequence:

```text
PriceRate stale
UnitRate stale through From(priceRate)
link validity/linking consequence follows configured impact
materialized mirrors may still be old
```

### Logical-only consumption

```csharp
var rate = runtime.Evaluate(priceRate, link);
```

Expected:

```text
rate = 5.5
PriceRate logical Fresh
link.PriceRate may still be 6
```

### Downstream logical consumption

```csharp
var unit = runtime.Evaluate(unitRate, link);
```

Expected:

```text
UnitRate consumes logical PriceRate 5.5, not stale link.PriceRate 6
```

### Object materialization

```csharp
runtime.Materialize(link);
```

Expected:

```text
link.PriceRate synchronized
link.UnitRate synchronized
no unrelated object materialized
no repair silently executed merely due to materialization
```

### Repair boundary

Run existing completion/dispatch and prove pending linking/rematching behavior occurs exactly once as configured.

This scenario is the primary release gate for the final candidate.

---

# 14. Additive vs subtractive linking scenario

Use a simplified quantity/receipt link to preserve the important Consistency use case:

```text
additive change
    -> can often remain Dirty / postpone expensive relinking

subtractive change or cancellation
    -> may invalidate existing link correctness immediately
    -> mark Invalid / schedule repair
```

The declaration should make this policy visible without exposing execution mechanics.

Dogfood whether the final syntax can express this clearly using `Impact` while still keeping PriceRate/UnitRate declaration readable.

Do not implement a domain-specific matching engine. A minimal counter/state assertion is enough.

---

# 15. Declaration + consumption must read as one conceptual model

Put these snippets adjacent in the experiment README/results.

Declaration:

```csharp
var priceRate = model
    .Derived(links)
    .DependsOn(
        x => x.InvoiceLine.Price,
        x => x.PurchaseOrderLine.Price)
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");

var unitRate = model
    .Derived(links)
    .From(priceRate)
    .Select((link, rate) => CalculateUnitRate(link, rate))
    .MaterializeTo(x => x.UnitRate)
    .Named("unit-rate");
```

Consumption:

```csharp
var logicalRate = runtime.Evaluate(priceRate, link);

runtime.Materialize(link);
Use(link.PriceRate, link.UnitRate);
```

Ask a new-reader question:

> Can a .NET developer infer, without reading engine internals, which things are logical nodes, which are tracked opaque dependencies, and which are physical mirrors?

Record concrete confusion, not subjective scores.

---

# 16. `Select` vs `Compute` — one final naming check

Do not build two full variants.

Implement the candidate with `Select` and create a side-by-side declaration excerpt using `Compute` only:

```csharp
.From(priceRate)
.Select((link, rate) => ...)
```

versus:

```csharp
.From(priceRate)
.Compute((link, rate) => ...)
```

Evaluate these specific questions:

```text
- Does Select incorrectly imply collection projection?
- Does Compute conflict conceptually with runtime Evaluate?
- Which reads better after From?
- Which produces clearer IntelliSense next-operation vocabulary?
- Which is clearer for direct Derived(source).Select(...) with no From?
```

Do not choose based only on familiarity with LINQ.

If evidence is weak, keep `Select` as the candidate because it matches the successful Variant C experiment and avoids introducing another computation term beside `Evaluate`.

---

# 17. `From` vs `Using` — one final naming check

Likewise, do not build another full API.

Compare only representative snippets:

```csharp
.Derived(links)
.From(priceRate)
.Select(...)
```

versus:

```csharp
.Derived(links)
.Using(priceRate)
.Select(...)
```

and cross-object:

```csharp
.From(x => x.PurchaseOrderLine, remainingQuantity)
```

versus current-style `Using`.

Questions:

```text
- Does From communicate dataflow better?
- Is Using clearer when dependency is not literally a value argument?
- Does retaining DependsOn make From's role sufficiently distinct?
- Does From(selector, derived) make projection direction understandable?
```

Default hypothesis: `From` for explicit logical value flow, `DependsOn` for opaque reads.

---

# 18. Naming / stable identity

Keep explicit stable naming:

```csharp
.Named("price-rate")
```

Do not infer durable identity from local variable/property names in this spike.

If D-style context properties improve organization, show them only as an optional consumer organization example:

```csharp
sealed class MatchingConsistencyDefinitions
{
    public Derived<PurchaseOrderInvoiceLine, decimal?> PriceRate { get; }
    public Derived<PurchaseOrderInvoiceLine, decimal?> UnitRate { get; }
}
```

Do not make context inheritance/helper construction part of the candidate declaration API.

---

# 19. EF adapter interop

Prove the final candidate's returned handles can be intentionally unwrapped/bridged to existing EF materialization mapping without leaking raw implementation handles into ordinary declaration code.

Desired conceptual usage:

```csharp
priceRate.MaterializeTo(x => x.PriceRate)
```

should eventually provide enough metadata for both:

```text
runtime.Materialize(link)
EF persistence materialization
```

The disposable facade may retain raw handles internally.

Document what production bridge/public abstraction would be required. Do not redesign the entire EF adapter in this spike.

---

# 20. Type-system misuse tests

Add compile-fail examples or documented compiler/build failures for at least:

```text
T1. From a derived value owned by an incompatible source without a projection selector.
T2. Cross-object From selector returns wrong owner type.
T3. MaterializeTo targets wrong TValue property type.
T4. MaterializeTo targets a property on the wrong object.
T5. DependsOn uses unrelated member path.
T6. mirror property used as independent DependsOn when a registered derived target exists.
T7. handle from a different model/runtime where current builder rejects it.
T8. same CLR type but different ObjectSet identity.
```

Classify each failure as:

```text
compile-time generic/type failure
fluent-stage/API prevention
model Build validation
runtime validation
not currently preventable
```

The goal is not “everything compile-time”. The goal is useful failure location and message.

---

# 21. IntelliSense surface check

At each major stage, list the public methods that a consumer would see conceptually.

Examples:

```text
model.Derived(links).
    DependsOn
    From
    Select
    ...

Derived(...).From(priceRate).
    From
    DependsOn
    Impact
    Select
    Sum?   // only if source shape supports it
```

Reject a facade design where unrelated operators appear everywhere just because extension methods can technically be constrained at runtime.

Use staged generic types only where they materially improve discoverability/type safety; do not reproduce Variant A's provider-stage explosion.

---

# 22. Generated/debug graph view

Print a debug representation of the final graph using logical vocabulary, for example:

```text
price-rate [Derived<PurchaseOrderInvoiceLine, decimal?>]
  depends-on InvoiceLine.Price
  depends-on PurchaseOrderLine.Price
  materializes-to PriceRate

unit-rate [Derived<PurchaseOrderInvoiceLine, decimal?>]
  from price-rate
  depends-on InvoiceLine.Quantity
  depends-on PurchaseOrderLine.OrderedQuantity
  materializes-to UnitRate

received-quantity [Aggregate<PurchaseOrderLine, decimal>]
  from matching-receipts
  operator IncrementalSum(Quantity)
```

This is not a new public API requirement. It is evidence that declaration vocabulary maps cleanly to the compiled semantic graph.

---

# 23. Minimal results document

Create:

```text
docs/final-api-v2-price-rate-dogfood-results.md
```

Keep it concise but evidence-based.

Required sections:

```text
1. Candidate API shown as one complete code block
2. PriceRate -> UnitRate lifecycle
3. From vs DependsOn semantic distinction
4. Cross-object projection result
5. recognized aggregate result (Sum/Count/LongCount/Any)
6. MaterializeTo + Evaluate/Materialize integration
7. mirror-property dependency rejection
8. invariant/repair behavior
9. type-system/diagnostic findings
10. Select vs Compute naming finding
11. From vs Using naming finding
12. production blockers
13. final recommendation / unresolved maintainer decisions
```

Do not create numeric rankings.

---

# 24. Production-readiness gates

The focused candidate is ready for a production implementation plan only if all of these are true:

```text
G1. PriceRate declaration is shorter/clearer than current API without hiding correctness dependencies.
G2. UnitRate explicitly consumes PriceRate through From(handle), not the materialized property.
G3. stale PriceRate mirror cannot corrupt UnitRate evaluation.
G4. MaterializeTo metadata integrates coherently with runtime.Materialize(source).
G5. object Materialize updates PriceRate and UnitRate without executing repair implicitly.
G6. recognized Sum chooses existing incremental strategy without `.Incrementally()`.
G7. Count/LongCount/Any have honest capability classifications.
G8. cross-object projection syntax is understandable and type-safe enough.
G9. DependsOn remains clearly distinct from From.
G10. mirror-property dependency misuse is rejected no later than Build().
G11. invariant + repair syntax remains explicit.
G12. facade does not require Variant-A-style generic/provider explosion.
G13. EF adapter can consume/unwrap the resulting definitions intentionally.
G14. no new engine capability is required merely to support the declaration syntax, except explicitly documented validation/metadata gaps.
```

If any gate fails, do not silently patch the facade until it passes. Record the failure and propose the smallest correction.

---

# 25. Maintainer decisions after the spike

Stop after results and ask for decisions only on unresolved items, expected to be approximately:

```text
Q1. Select or Compute?
Q2. From or Using for explicit derived-value flow?
Q3. Exact cross-object From syntax?
Q4. Exact placement/configuration shape for Impact?
Q5. Should mirror-property dependency rejection happen during declaration or Build()?
Q6. Should MaterializeTo live directly on the derived builder/definition or in a separate mapping layer?
Q7. Are Count/LongCount/Any ready to be first-class recognized operators at initial API-v2 release?
Q8. Should D-style definition context/properties be documented as an optional organization pattern only?
```

Do not ask the maintainer to choose among A/B/C/D again.

---

# 26. Completion checklist

```text
[ ] previous API-v2 and materialization results read
[ ] small isolated final-candidate experiment created
[ ] no production public API changed
[ ] PriceRate declared with DependsOn + Select + MaterializeTo
[ ] UnitRate declared with From(priceRate)
[ ] stale PriceRate mirror proven not to affect UnitRate evaluation
[ ] nested opaque member invalidation proven through DependsOn
[ ] cross-object projection dogfooded
[ ] relation-backed Sum dogfooded without Incrementally
[ ] Count coverage added/classified honestly
[ ] LongCount coverage added/classified honestly
[ ] Any coverage added/classified honestly
[ ] Impact locality/ambiguity checked
[ ] MaterializeTo target validation checked
[ ] mirror-property dependency rejection dogfooded
[ ] Evaluate(priceRate, link) integrated
[ ] Materialize(priceRate, link) integrated
[ ] Materialize(link) updates all link mirrors
[ ] materialization does not consume repair
[ ] additive/subtractive invalidation scenario executed
[ ] invariant + repair remains explicit
[ ] Select vs Compute excerpt compared
[ ] From vs Using excerpt compared
[ ] EF bridge requirement documented
[ ] misuse/diagnostic cases classified
[ ] IntelliSense surface reviewed
[ ] debug graph printed
[ ] G1-G14 production-readiness gates answered
[ ] concise results document created
[ ] agent stops for maintainer decision
```

---

# 27. Final instruction to the coding agent

This experiment is intentionally narrower than the previous API-v2 dogfood.

Do not optimize for novelty. Optimize for a coherent developer story:

```csharp
var priceRate = model
    .Derived(links)
    .DependsOn(x => x.InvoiceLine.Price, x => x.PurchaseOrderLine.Price)
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");

var unitRate = model
    .Derived(links)
    .From(priceRate)
    .Select((link, rate) => CalculateUnitRate(link, rate))
    .MaterializeTo(x => x.UnitRate)
    .Named("unit-rate");

var currentRate = runtime.Evaluate(priceRate, link);

runtime.Materialize(link);
Use(link.PriceRate, link.UnitRate);
```

The API should make three different things visually different:

```text
From(...)          = logical value flow through the dependency graph
DependsOn(...)     = tracked dependency hidden inside ordinary calculator code
MaterializeTo(...) = physical representation/mirror
```

If those three concepts remain obvious in the PriceRate/UnitRate/linking scenario, and the runtime semantics remain those already proven by the previous experiments, the next step should be a production implementation plan rather than another syntax spike.