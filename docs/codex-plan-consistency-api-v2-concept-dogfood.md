# Codex concept plan — Consistency API v2 dogfooding

Status: **DESIGN / CONCEPT PROOF ONLY — DO NOT SHIP ANY API FROM THIS PLAN**

Reviewed baseline: `2e5f77e68f6c12fb75d5900a8c192e792a9ab26a`

Purpose: compare several possible public model-building APIs for Raffinert.Consistency before the first public API is treated as settled.

This plan is deliberately written for a weak coding agent. Do not improvise architecture. Do not choose a winner early. Do not modify production API until the comparison is complete and a human explicitly approves one direction.

---

## 0. Why this experiment exists

The current API is semantically capable, but model declarations expose a builder-oriented vocabulary:

```csharp
var fulfilledQuantity = model.Derived(lines)
    .Using(matchingFulfillments)
    .Impact(...)
    .Incrementally()
    .Compute((_, rows) => rows.Sum(x => x.Quantity))
    .Named("fulfilled-quantity");
```

Raffinert already compiles a dependency DAG and runtime propagation follows that graph. The question is whether the public declaration syntax should look more like the graph it describes.

Roslyn incremental generators and Guacamole use provider pipelines such as `Select`, `Where`, `Combine`, and collection/value provider distinctions. The useful idea to test is not “copy Roslyn names”. The useful idea is:

> graph construction should be visible as ordinary composition, while execution strategy remains a compiler/runtime concern.

Do not assume that a Roslyn-like API is automatically better. Raffinert has semantics Roslyn/Guacamole do not model directly: object identity, binary relations, cross-object reverse routing, relation membership deltas, Dirty vs Invalid, invariants, repair policies, EF authoritative scope, persisted mirrors, and causal impact reporting.

The experiment must discover whether provider syntax clarifies those semantics or hides them.

---

# 1. Non-negotiable guardrails

For the concept phase:

1. **Do not modify `src/Raffinert.Consistency/**`.**
2. **Do not modify `src/Raffinert.Consistency.EntityFrameworkCore/**`.**
3. **Do not modify `PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt`.**
4. Do not rename/remove existing public types.
5. Do not change runtime behavior, dependency analysis, propagation, EF behavior, diagnostics, or concurrency.
6. Do not add a NuGet package.
7. Do not add a source generator in the first comparison wave.
8. Do not change existing dogfood scenarios to use a candidate API.
9. Candidate APIs must live in a disposable concept project and translate to the existing public builder API where executable behavior is needed.
10. Do not implement four independent engines. There is one existing engine. Candidate syntax is only a front-end/facade experiment.
11. Do not use `dynamic`, reflection tricks, or untyped `object` APIs to make a pretty mock compile.
12. Do not hide capabilities that are awkward. Awkward cases are the point of the experiment.
13. Do not declare a winner based only on the shortest happy-path example.
14. If a candidate cannot express a required dogfood case without an escape hatch, record that as a result. Do not silently fall back to the old API in the middle of a candidate declaration.
15. Keep current mutation/runtime APIs (`Change`, `MutationSet`, `Apply`, `Prepare`, `Plan`, EF saves) out of scope unless a candidate model-building syntax forces a clear conflict.

The RC/release workflow must not consume concept-project output. This experiment is pre-release design evidence, not release evidence.

---

# 2. Deliverables

Create one disposable project:

```text
experiments/Raffinert.Consistency.ApiV2Concept/
    Raffinert.Consistency.ApiV2Concept.csproj
    README.md
    Common/
        Domain.cs
        ScenarioExpectations.cs
    Current/
        CurrentApi.cs
    VariantA.ProviderPipeline/
        Api.cs
        Models.cs
    VariantB.TypedDomainPipeline/
        Api.cs
        Models.cs
    VariantC.HybridBuilderPipeline/
        Api.cs
        Models.cs
    VariantD.ContextDsl/
        Api.cs
        Models.cs
```

Do not add the project to package/release workflows.

It may be added to the solution only if that does not make it part of packaging. If solution inclusion complicates release verification, leave it outside the solution and document the explicit build command.

Also create the final comparison document:

```text
docs/consistency-api-v2-concept-results.md
```

Do not create this results file until all required variants compile and all required scenarios have been attempted.

---

# 3. Reference facts the agent must understand first

Before writing candidate code, inspect these existing files:

```text
README.md
src/Raffinert.Consistency/Model/ConsistencyModelBuilder.cs
samples/Raffinert.Consistency.DependencyMaintenanceSample/Program.cs
samples/Raffinert.Consistency.OrderFulfillmentSample/Scenarios/DependencyPropagationScenarios.cs
samples/Raffinert.Consistency.OrderFulfillmentSample/Scenarios/AllocationIntegrityScenarios.cs
samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Program.cs
docs/anemic-model-dependency-maintenance-example.md
```

Write a short `README.md` section called `Existing semantic inventory` containing at least:

```text
ObjectSet<T>
Relation<TLeft, TRight>
Derived<TSource, TValue>
Invariant<TSource>
direct member dependencies
nested member dependencies / DependsOn
relation-backed derived values
incremental Count / Any / Sum
upstream derived dependencies
projected cross-object derived dependencies
Dirty vs Invalid impact policy
value-transition severity
repair scheduling
stable definition names
EF Map / Materialize / Enforce
DiscoverConsumers
ConsistencyScope.Complete
```

If the candidate syntax does not have an obvious representation for one of these, do not ignore it. Mark it `UNRESOLVED` in that variant's notes.

---

# 4. Shared dogfood scenarios — every candidate must express the same semantics

Do not invent different examples for different APIs. Each candidate must be evaluated against the same scenarios.

## Scenario S1 — trivial source-derived value

Existing semantic shape:

```csharp
var remaining = builder.Derived(lines)
    .Compute(x => x.OrderedQuantity - x.FulfilledQuantity);
```

Required proof:

```text
one object set
one derived scalar per source object
automatically analyzable direct dependencies
no explicit DependsOn required
```

This is the readability baseline. If an API makes this significantly worse, record it.

## Scenario S2 — nested dependency / UnitRate dogfood

Use the existing neutral domain:

```text
Association.SourceItem.UnitValue
Association.TargetItem.UnitValue
    -> UnitRateCalculator.Calculate(...)
    -> Association.UnitRate persisted mirror
```

Required semantics:

```text
only Association is a Raffinert root set
SourceItem and TargetItem remain ordinary navigation targets
opaque calculator may require explicit dependency declaration
retargeting SourceItem/TargetItem changes dependency routing
EF can materialize UnitRate
```

This scenario is mandatory because it tests whether a pretty pipeline accidentally forces fake endpoint object sets.

## Scenario S3 — relation + incremental aggregate

Use order fulfillment:

```text
OrderLine + Fulfillment
    relation predicate:
        same OrderNumber
        same ItemNumber
        !Cancelled
    -> Sum(Fulfillment.Quantity)
    -> FulfilledQuantity
```

Required semantics:

```text
binary relation remains explicit
membership add/remove is distinguishable from item change
Sum can compile to the existing incremental plan
API must not require the user to write “incremental” merely to get the optimized recognized Sum plan unless the candidate intentionally argues for that choice
```

## Scenario S4 — derived -> derived composition

```text
FulfilledQuantity
OrderLine.OrderedQuantity
    -> RemainingQuantity
```

Required semantics:

```text
DAG edge is obvious in declaration
source object is still available to the computation
upstream derived input is strongly typed
```

## Scenario S5 — projected cross-object derived composition

Use existing allocation scenario:

```text
Allocation.OrderLine
    -> RemainingQuantity(OrderLine)
    -> UnitRate(OrderLine)
    -> AllocationValidity
```

Required semantics:

```text
projection from Allocation to OrderLine is explicit and typed
reverse routing remains possible
candidate must not pretend ordinary Combine alone solves source mapping
```

This is a critical discriminator. A candidate that looks excellent until cross-object projection is needed is not sufficient.

## Scenario S6 — Dirty vs Invalid policy

Express both:

```text
FulfilledQuantity:
    MembershipAdded -> Dirty
    MembershipRemoved -> Invalid
    ItemChanged -> Invalid

RemainingQuantity.OrderedQuantity:
    increase -> Dirty
    decrease -> Invalid
```

Required semantics:

```text
policy remains attached to the dependency/impact it classifies
value-transition policy can see old and new values
syntax must make it difficult to confuse computation semantics with invalidation severity
```

## Scenario S7 — invariant + deferred repair

```text
AllocationValidity
    -> invariant Must(valid)
    -> ScheduleRepairWith(...)
```

Required semantics:

```text
invariant is not disguised as another derived bool unless the candidate deliberately distinguishes rule definition from rule reaction
repair remains downstream policy/effect, not part of pure Select computation
```

## Scenario S8 — EF persistence boundary

Use both:

```text
Materialize(UnitRate -> Association.UnitRate)
Enforce(nonNegativeRemaining)
```

Required semantics:

```text
model declaration and persistence mapping remain separable
candidate must show whether materialization/enforcement belong in the pipeline or remain EF adapter configuration
```

Do not move EF into Core merely to make fluent syntax look uniform.

## Scenario S9 — definition naming / durable identity

Every meaningful graph node used by diagnostics/durable work must be nameable.

Test whether naming reads naturally:

```csharp
.Named("remaining-quantity")
```

versus constructor/property naming or another candidate mechanism.

Do not sacrifice stable explicit definition identity for variable-name magic.

---

# 5. Variant A — Roslyn/Guacamole-style generic provider pipeline

Goal: test the strongest provider abstraction.

This variant intentionally starts close to Roslyn/Guacamole:

```text
ValueProvider<TSource, TValue>
ValuesProvider<TSource, TValue>
Select
Where
Combine
Collect/Aggregate
```

Do not use the exact names if generic constraints make them misleading, but preserve the idea that computation nodes are providers composed into a DAG.

## A.1 Target happy-path syntax

Prototype something close to:

```csharp
var lines = model.Objects<OrderLine>()
    .Key(x => x.Id)
    .Named("order-lines");

var ordered = lines.Value(x => x.OrderedQuantity);
var fulfilled = lines.Value(x => x.FulfilledQuantity);

var remaining = ordered
    .Combine(fulfilled)
    .Select((orderedQuantity, fulfilledQuantity) =>
        orderedQuantity - fulfilledQuantity)
    .Named("remaining-quantity");
```

Do not force this exact API if it creates nonsense. The purpose is to test generic providers.

## A.2 Relation syntax to test

Try:

```csharp
var matching = lines
    .RelateTo(fulfillments)
    .Where((line, fulfillment) =>
        line.OrderNumber == fulfillment.OrderNumber &&
        line.ItemNumber == fulfillment.ItemNumber &&
        !fulfillment.Cancelled)
    .Named("matching-fulfillments");

var fulfilledQuantity = matching
    .PerLeft()
    .Sum(x => x.Quantity)
    .Named("fulfilled-quantity");
```

`PerLeft()` is only a candidate name. Also test `GroupByLeft()`, `ForEachLeft()`, and no explicit grouping method if the relation type itself can expose left-keyed aggregate operators cleanly. Record which reads best.

## A.3 Cross-object projection syntax to test

Generic `Combine` is insufficient for this case. Prototype a typed projection operation, for example:

```csharp
var remainingForAllocation = remaining.For(
    allocations,
    allocation => allocation.OrderLine);

var rateForAllocation = unitRate.For(
    allocations,
    allocation => allocation.OrderLine);

var allocationValidity = remainingForAllocation
    .Combine(rateForAllocation)
    .Combine(allocations.Value(x => x.ReservedQuantity))
    .Select(...);
```

Also try the opposite direction:

```csharp
var allocationValidity = allocations
    .SelectWith(allocation => allocation.OrderLine, remainingQuantity)
    .CombineWith(allocation => allocation.OrderLine, unitRate)
    .Select(...);
```

Do not implement both fully if one is obviously unworkable; create compile-level sketches and explain the failure.

## A.4 Main question

Does a small generic provider vocabulary remain understandable once object identity, relation orientation, source projection, impact severity, invariant reactions, and EF mapping appear?

## A.5 Failure conditions

Mark Variant A as structurally weak if any of these happen:

```text
type signatures become dominated by 3+ generic type parameters
relation left/right orientation becomes invisible
cross-object projection needs unsafe/untyped escape hatches
Dirty/Invalid policy has no obvious dependency edge to attach to
object-set identity gets erased and later reconstructed
error messages become provider-type puzzles rather than domain concepts
```

---

# 6. Variant B — typed domain pipeline

Goal: keep pipeline composition, but retain Raffinert-specific semantic types instead of reducing everything to generic providers.

Candidate type families may look like:

```text
ObjectSet<T>
ObjectValue<TSource, TValue>
Relation<TLeft, TRight>
RelationAggregate<TLeft, TValue>
Derived<TSource, TValue>
Invariant<TSource>
```

Names are not frozen.

## B.1 Target syntax

Prototype:

```csharp
var lines = model.Objects<OrderLine>()
    .Key(x => x.Id)
    .Named("order-lines");

var fulfillments = model.Objects<Fulfillment>()
    .Key(x => x.Id)
    .Named("fulfillments");

var matching = lines
    .Relate(fulfillments)
    .Where((line, fulfillment) =>
        line.OrderNumber == fulfillment.OrderNumber &&
        line.ItemNumber == fulfillment.ItemNumber &&
        !fulfillment.Cancelled)
    .Named("matching-fulfillments");

var fulfilledQuantity = matching
    .SumByLeft(x => x.Quantity)
    .Named("fulfilled-quantity");

var remainingQuantity = lines
    .Select(x => x.OrderedQuantity)
    .Combine(fulfilledQuantity)
    .Select((ordered, fulfilled) => ordered - fulfilled)
    .Named("remaining-quantity");
```

Try both `SumByLeft` and `PerLeft().Sum`. Record whether explicit orientation is worth the extra word.

## B.2 Nested dependency syntax

Test whether ordinary `Select` can preserve expression analysis:

```csharp
var unitRate = associations
    .Select(a => UnitRateCalculator.Calculate(
        a.SourceItem.UnitValue,
        a.TargetItem.UnitValue))
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Named("association-unit-rate");
```

Also test an explicit input-first form:

```csharp
var unitRate = associations
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Select(a => UnitRateCalculator.Calculate(...));
```

Record which ordering makes `DependsOn` read as semantic input declaration rather than a patch after computation.

## B.3 Impact policy syntax

Prefer edge-local semantics. Prototype:

```csharp
var fulfilledQuantity = matching
    .WithImpact(impact => impact
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .SumByLeft(x => x.Quantity);
```

For source-member transitions:

```csharp
var remainingQuantity = ...
    .WhenSourceMemberChanges(
        x => x.OrderedQuantity,
        (oldValue, newValue) =>
            newValue < oldValue
                ? DependencySeverity.Invalid
                : DependencySeverity.Dirty);
```

Do not hide impact policy inside `Select` lambdas.

## B.4 Main question

Can Raffinert get Roslyn-like graph readability without losing the domain distinctions that make the runtime correct?

This is expected to be a strong candidate, but the agent must not bias the comparison in its favor.

---

# 7. Variant C — hybrid current builder + composable derived pipeline

Goal: minimize migration and public API churn.

Keep object sets, relations, invariants, impact policy, and EF mapping close to the current API. Only improve derived composition and recognized aggregate declaration.

## C.1 Target syntax

Example:

```csharp
var lines = model.Objects<OrderLine>()
    .Named("order-lines")
    .Key(x => x.Id);

var matching = model.Relation(lines, fulfillments)
    .Where(...)
    .Named("matching-fulfillments");

var fulfilledQuantity = model.Derived(lines)
    .From(matching)
    .Sum(x => x.Quantity)
    .Impact(...)
    .Named("fulfilled-quantity");

var remainingQuantity = model.Derived(lines)
    .From(x => x.OrderedQuantity)
    .Combine(fulfilledQuantity)
    .Select((ordered, fulfilled) => ordered - fulfilled)
    .Impact(...)
    .Named("remaining-quantity");
```

Alternative minimal form:

```csharp
var remainingQuantity = model.Derived(lines)
    .Using(fulfilledQuantity)
    .Compute((line, fulfilled) => line.OrderedQuantity - fulfilled);
```

but remove `.Incrementally()` by recognizing `Sum`/`Count`/`Any` from explicit aggregate operators.

## C.2 Questions to answer

```text
Can 80% of readability gain come from only 20% API change?
Is current model.Derived(source) valuable because it always states ownership explicitly?
Can recognized aggregates be first-class without turning all derived values into providers?
Can Combine be added only where it clarifies derived-to-derived DAG edges?
```

## C.3 Required migration comparison

For every shared scenario, show current syntax next to Variant C. Count conceptual operations, not just lines.

Example metric:

```text
Current: Derived + Using + Incrementally + Compute
Variant C: Derived + FromRelation + Sum
```

Do not count formatting-only line reductions as a design improvement.

---

# 8. Variant D — context DSL inspired by Guacamole, but Raffinert-specific

Goal: test whether named graph nodes as context properties improve discoverability and composition.

This is a syntax experiment only. **Do not implement a source generator in this wave.** Write the shape manually.

Candidate:

```csharp
public sealed class OrderConsistencyModel : ConsistencyModelContext
{
    public ObjectSet<OrderLine> Lines { get; }
    public ObjectSet<Fulfillment> Fulfillments { get; }
    public Relation<OrderLine, Fulfillment> MatchingFulfillments { get; }
    public Derived<OrderLine, decimal> FulfilledQuantity { get; }
    public Derived<OrderLine, decimal> RemainingQuantity { get; }

    public OrderConsistencyModel()
    {
        Lines = Objects<OrderLine>().Key(x => x.Id);
        Fulfillments = Objects<Fulfillment>().Key(x => x.Id);

        MatchingFulfillments = Lines
            .Relate(Fulfillments)
            .Where(...);

        FulfilledQuantity = MatchingFulfillments
            .SumByLeft(x => x.Quantity);

        RemainingQuantity = Lines
            .Select(x => x.OrderedQuantity)
            .Combine(FulfilledQuantity)
            .Select((ordered, fulfilled) => ordered - fulfilled);
    }
}
```

Also sketch, but do not implement, a possible future generated form:

```csharp
[ConsistencyModel]
public partial class OrderConsistencyModel
{
    public partial ObjectSet<OrderLine> Lines { get; }
    public partial ObjectSet<Fulfillment> Fulfillments { get; }
}
```

The results document must clearly separate:

```text
property-based context organization — evaluated now
source-generated registration/naming — future possibility only
```

## D.1 Main questions

```text
Do model properties make graph nodes easier to discover and reuse?
Can property names safely become default diagnostic names while still allowing explicit stable names?
Does inheritance/context lifecycle add ceremony without semantic benefit?
Does this accidentally couple model definition to DI/runtime lifetime?
```

Do not let “looks like Guacamole” count as a benefit by itself.

---

# 9. Explicitly rejected pseudo-variant — LINQ/IQueryable illusion

Do not implement an API that pretends the entire consistency model is ordinary `IQueryable<T>` or `IEnumerable<T>`.

Reason to record in the experiment README:

```text
Raffinert needs semantic distinctions that ordinary LINQ does not encode:
- stable object-set identity
- relation orientation and pair deltas
- reverse routing
- Dirty vs Invalid impact
- invariant reaction
- repair scheduling
- durable definition identity
- authoritative scope requirements
```

LINQ vocabulary may inspire operator names, but the candidate types must not lie about semantics.

---

# 10. Implementation strategy for the concept facades

The candidate APIs should compile to the existing builder, not reimplement the engine.

Use one internal adapter per variant.

Conceptually:

```text
Candidate API declaration
        ↓
variant-specific typed facade
        ↓
existing ConsistencyModelBuilder / RelationBuilder / DerivedBuilder / InvariantBuilder
        ↓
existing CompiledConsistencyModel
        ↓
existing ConsistencyRuntime
```

For syntax that cannot be translated without new Core capability:

1. do not modify Core;
2. leave a compile-safe concept descriptor if possible;
3. mark the exact missing capability in that variant's `Models.cs` comment and experiment README;
4. classify it as one of:

```text
SYNTAX-ONLY GAP       — engine can already do it; adapter is awkward
PUBLIC-FACADE GAP     — engine can do it but current public builder cannot expose enough metadata
SEMANTIC GAP          — engine itself cannot express required semantics
TYPE-SYSTEM GAP       — desired fluent shape loses required static type information
```

Never classify a gap as semantic without verifying current API cannot express the scenario.

---

# 11. Task sequence

## Task 264 — Freeze baseline and create experiment skeleton

Before changes:

```bash
git status --short
git rev-parse HEAD
```

Record the actual SHA in the experiment README. If it differs from the reviewed baseline, inspect changes before proceeding.

Create the project/folders from section 2.

The project may reference:

```text
src/Raffinert.Consistency/Raffinert.Consistency.csproj
src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj
```

No other product implementation references.

Acceptance:

```bash
dotnet build experiments/Raffinert.Consistency.ApiV2Concept/Raffinert.Consistency.ApiV2Concept.csproj -c Release
```

passes with zero warnings.

Commit suggestion:

```text
experiments: scaffold Consistency API v2 concept
```

## Task 265 — Capture current API baseline

Implement `Current/CurrentApi.cs` with S1–S9 declarations copied/adapted from the actual dogfood samples.

Do not improve current syntax in this file.

Record for each scenario:

```text
lines of declaration code
number of distinct API concepts used
whether ownership/source is visually obvious
whether dependency edge is visually obvious
whether relation orientation is visually obvious
whether impact policy is adjacent to the dependency it classifies
whether execution strategy leaks into declaration
```

This is the baseline for comparison.

## Task 266 — Implement Variant A facade and declarations

Implement enough typed facade code for S1–S9 to compile or to produce an explicit recorded gap.

At least S1, S2, S3, S4, and S5 must execute through the existing runtime if translation is possible.

Do not skip S5.

Commit suggestion:

```text
experiments: prototype generic provider API
```

## Task 267 — Implement Variant B facade and declarations

Same requirements as Task 266.

Keep semantic types distinct. Do not reuse Variant A provider types and merely rename them.

Commit suggestion:

```text
experiments: prototype typed domain pipeline API
```

## Task 268 — Implement Variant C facade and declarations

Same scenarios. Optimize for smallest public-surface delta, not maximum fluent novelty.

Commit suggestion:

```text
experiments: prototype hybrid builder pipeline API
```

## Task 269 — Implement Variant D manual context declarations

Do not add source generation.

Execute through existing runtime where practical. The context is organizational syntax, not a new engine/lifetime.

Commit suggestion:

```text
experiments: prototype context-style consistency API
```

## Task 270 — Dogfood all variants against identical runtime expectations

Create `Common/ScenarioExpectations.cs` so executable candidates share assertions.

For translated candidates, verify at least:

```text
S1 remaining quantity changes correctly
S2 source mutation fans out to all affected associations
S2 retargeting updates reverse dependency routing
S3 cancellation/removal invalidates expected aggregate source
S4 derived-to-derived propagation works
S5 projected Allocation -> OrderLine dependency routes correctly
S6 Dirty vs Invalid states match current sample behavior
S7 repair request occurs exactly where current behavior requests it
S8 materialized/enforced EF behavior remains current behavior
```

Candidate facade code must not contain manual invalidation logic to make tests pass.

## Task 271 — Produce API usability matrix

Create `docs/consistency-api-v2-concept-results.md`.

Use this exact evaluation matrix with factual notes rather than a single numeric winner:

| Criterion | Current | Variant A | Variant B | Variant C | Variant D |
|---|---|---|---|---|---|
| S1 simple derived readability | | | | | |
| Relation readability | | | | | |
| Relation orientation obvious | | | | | |
| Derived DAG visually obvious | | | | | |
| Cross-object projection clarity | | | | | |
| Nested dependency clarity | | | | | |
| Dirty/Invalid policy locality | | | | | |
| Invariant/repair clarity | | | | | |
| EF separation | | | | | |
| Stable naming | | | | | |
| Type inference quality | | | | | |
| IntelliSense discoverability | | | | | |
| Compiler error quality | | | | | |
| Generic type noise | | | | | |
| Runtime semantics leakage | | | | | |
| Incremental-strategy leakage | | | | | |
| Migration cost | | | | | |
| New public types required | | | | | |
| New engine capability required | | | | | |

For each row, write short evidence. Do not use arbitrary 1–10 scores.

## Task 272 — Run “new user reading” checks

For each candidate, put the S3–S7 model declaration alone in a separate Markdown code block without explanatory prose.

Then answer these questions using only what is visible in the declaration:

```text
What are the root object sets?
Which objects are related?
What is calculated per OrderLine?
What depends on FulfilledQuantity?
How does Allocation reach OrderLine-derived state?
Which changes are Dirty versus Invalid?
Which rule schedules repair?
```

If the answer requires knowing hidden conventions, record that as an API readability cost.

## Task 273 — Stress the type system and diagnostics

Add intentional compile-failure snippets as comments/docs; do not break the build.

Test expected API guidance for:

```text
Combine values from different source object sets without projection
aggregate a relation from the wrong orientation
project through a navigation of the wrong target type
use a derived value owned by another model
attach membership impact to a non-relation dependency
attach source-member transition policy to unrelated source type
materialize a derived value onto the wrong entity type
create a cycle between derived nodes
```

For each candidate, state whether the desired failure can be caught at compile time or only by model Build().

Prefer candidates that make illegal graphs unrepresentable, but do not contort the API solely to eliminate every runtime validation.

## Task 274 — Separate semantic API from optimization API

Inspect every candidate for words such as:

```text
Incremental
Cache
Invalidate
Recompute
Scan
Index
```

For each occurrence, classify:

```text
required semantic contract
optional tuning
implementation leakage
```

Specifically test whether `.Incrementally()` can disappear from normal recognized `Count`, `LongCount`, `Any`, and `Sum` declarations while the compiler still selects the current incremental plan.

Do not remove current production `.Incrementally()` in this experiment.

## Task 275 — Human decision gate

Stop after the results document.

Do **not** modify production API.

The final results document must end with these headings:

```text
## Facts established by dogfooding
## Trade-offs that remain subjective
## Capabilities every viable API must preserve
## Smallest viable production change
## Largest coherent production change
## Open questions requiring maintainer decision
```

It may identify which variants survived or failed specific requirements, but implementation of a production API requires explicit maintainer approval after reviewing the examples.

---

# 12. Candidate syntax details the agent must explore, not silently decide

For each item below, show at least two alternatives in the experiment README and record which one was used in executable code.

## Object-set creation

```csharp
model.Objects<OrderLine>()
model.Set<OrderLine>()
```

## Relation creation

```csharp
lines.RelateTo(fulfillments)
lines.Relate(fulfillments)
model.Relation(lines, fulfillments)
```

## Relation aggregation orientation

```csharp
matching.PerLeft().Sum(x => x.Quantity)
matching.SumByLeft(x => x.Quantity)
matching.Left.Sum(x => x.Quantity)
```

Do not use a plain `.Sum(...)` if it is ambiguous whether the result is global, per-left, or per-right.

## Direct object member as value node

```csharp
lines.Value(x => x.OrderedQuantity)
lines.Select(x => x.OrderedQuantity)
```

Record whether `Select` creates confusion with collection projection.

## Derived composition

```csharp
ordered.Combine(fulfilled).Select(...)
lines.Derive(fulfilled).Select(...)
model.Derived(lines).Using(fulfilled).Compute(...)
```

## Cross-object projection

```csharp
remaining.For(allocations, x => x.OrderLine)
allocations.Use(x => x.OrderLine, remaining)
allocations.SelectWith(x => x.OrderLine, remaining)
```

This naming decision is more important than `Select` vs `Map`. Give it serious attention.

## Invariant declaration

```csharp
allocationValidity.Must(x => x)
allocations.Invariant(allocationValidity).Must((_, valid) => valid)
model.Invariant(allocations).Using(allocationValidity).Must(...)
```

Do not allow the source ownership of the invariant to become ambiguous.

## Naming

```csharp
.Named("remaining-quantity")
.WithName("remaining-quantity")
model.Define("remaining-quantity", ...)
```

Explicit stable strings must remain possible regardless of property/variable names.

---

# 13. What not to copy from Roslyn/Guacamole

Do not cargo-cult these characteristics:

### Do not call everything `Incremental*`

Incrementality is an execution property. Raffinert's user-facing semantics are dependencies, relations, derived state, validity, and consequences.

### Do not erase domain ownership

A Roslyn provider can often be understood as a stream/value. Raffinert must know “this value exists per `OrderLine`” or “this relation is `OrderLine -> Fulfillment`”. Keep that in the type system where practical.

### Do not make source generation mandatory

Guacamole can generate source accessors/context plumbing. Raffinert currently works with ordinary objects and explicit mutation reporting / EF tracking. API-v2 syntax must not require generated domain wrappers just to declare a model.

### Do not conflate reactive outputs with repair policy

`RegisterOutput`-style callbacks are not automatically equivalent to Raffinert invariants, durable policy work, transaction/outbox planning, or post-commit dispatch.

### Do not inherit Guacamole concurrency semantics

This experiment is about model declaration syntax only. `ConsistencyRuntime` remains non-thread-safe under its current contract. Do not add reader/writer locking while working on API syntax.

---

# 14. Strong design hypotheses to test

These are hypotheses, not conclusions.

### H1

`Select`/`Combine` can make derived-to-derived DAG edges easier to see than `.Using(...).Compute(...)`.

### H2

Relations need Raffinert-specific typed operators; generic `ValuesProvider<T>` is probably insufficient to express left/right ownership and membership impact clearly.

### H3

Recognized aggregates should normally imply the optimized incremental plan, making `.Incrementally()` unnecessary in the common API.

### H4

`DependsOn(...)` remains useful for opaque method calls even in a pipeline API because dependency completeness and computation expression are separate concerns.

### H5

Cross-object projected dependencies are the hardest API problem and should drive the design more than trivial `Select` examples.

### H6

Invariant and repair syntax should remain semantically distinct from pure derived computation.

### H7

EF `Map`, `Materialize`, `Enforce`, `DiscoverConsumers`, and authoritative `ConsistencyScope` should probably remain adapter-level concepts rather than being folded into the core computation pipeline.

### H8

A hybrid API may deliver most usability improvement with much less public-surface churn than a universal provider abstraction.

The results document must explicitly say whether each hypothesis was supported, contradicted, or unresolved by the dogfood prototypes.

---

# 15. Acceptance criteria for the entire concept wave

The concept wave is complete only when all are true:

```text
[ ] production src code unchanged
[ ] public API baselines unchanged
[ ] existing samples unchanged
[ ] Current baseline declarations captured
[ ] Variant A attempted for S1-S9
[ ] Variant B attempted for S1-S9
[ ] Variant C attempted for S1-S9
[ ] Variant D attempted for S1-S9
[ ] S5 projected cross-object case was not skipped
[ ] S6 Dirty/Invalid case was not simplified away
[ ] S7 repair case was not simplified away
[ ] executable candidate translations use existing runtime
[ ] no candidate contains manual invalidation to fake correctness
[ ] results matrix completed with evidence
[ ] type-system misuse cases documented
[ ] optimization leakage reviewed
[ ] hypotheses H1-H8 evaluated
[ ] no production winner implemented without maintainer approval
```

Run at minimum:

```bash
dotnet build experiments/Raffinert.Consistency.ApiV2Concept/Raffinert.Consistency.ApiV2Concept.csproj -c Release
dotnet build Raffinert.Consistency.sln -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build
```

If the concept project is intentionally outside the solution, state that fact in the results document.

---

# 16. What a later production plan must contain

Do not execute this section during the concept wave.

After human selection, create a separate implementation plan that covers:

```text
public type names and namespaces
compatibility/deprecation strategy for current builder API
whether API v2 wraps or replaces current builders
PublicAPI baseline changes
XML documentation
README rewrite
migration guide with before/after examples
all sample migrations
package-consumer tests
compile-time misuse tests
runtime semantic-equivalence tests
EF adapter compatibility
performance/allocations of model construction
release-candidate baseline reset
```

If the chosen design requires changing the engine rather than only the public front-end, that engine change must be isolated and justified separately.

---

# 17. Final instruction to the coding agent

Your job in this roadmap is **not to design by taste**.

Your job is to make four competing API ideas concrete enough that the same difficult Raffinert scenarios can be read, compiled, and where possible executed side-by-side.

Do not optimize for the prettiest three-line example.

The API must survive this graph:

```text
Fulfillment mutation
    -> relation membership/item impact
    -> FulfilledQuantity
    -> RemainingQuantity
    -> projected AllocationValidity
    -> invariant
    -> deferred repair
```

and this graph:

```text
SourceItem.UnitValue / TargetItem.UnitValue
    -> nested dependency routing through Association
    -> UnitRate
    -> EF materialized mirror
```

If a candidate makes those graphs easier to understand without weakening their semantics, record the evidence. If it only makes `x => x.A + x.B` prettier, record that limitation and move on.
