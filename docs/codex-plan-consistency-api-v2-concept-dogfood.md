# Codex concept plan — Consistency API v2 dogfooding

Status: **DESIGN / CONCEPT PROOF ONLY — DO NOT SHIP ANY API FROM THIS PLAN**

Reviewed baseline: `2e5f77e68f6c12fb75d5900a8c192e792a9ab26a`

Purpose: compare several possible public model-building APIs for Raffinert.Consistency before the first public API is treated as settled.

This plan is deliberately written for a weak coding agent. Do not improvise architecture. Do not choose a winner early. Do not modify production API until the comparison is complete and a human explicitly approves one direction.

> **Optional design pressure:** after the four C# API variants have been dogfooded, perform the YAML/portable-model spike in section 18. YAML is explicitly **not** a release requirement and must not distort the C# API merely to make serialization easy.

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
16. YAML/serialization is optional. Do not add a production YAML dependency, loader, serializer, expression language, or public serialization API in this concept wave.

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
    OptionalYaml/
        README.md
        model.example.yaml
        PortableModelSketch.cs
```

`OptionalYaml/**` is created only if Task 276 is executed.

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

Use the S1–S9 scenarios already described by this plan: trivial source-derived value; nested UnitRate dependency; relation plus incremental aggregate; derived-to-derived composition; projected cross-object composition; Dirty vs Invalid policy; invariant plus deferred repair; EF persistence boundary; and stable definition naming.

Do not invent different semantics per API variant. The full declarations and acceptance requirements remain authoritative from the original plan baseline.

---

# 5. Variant A — Roslyn/Guacamole-style generic provider pipeline

Implement and evaluate the generic provider concept described in the original plan. Preserve object-set identity and test relation orientation, `Combine`, relation aggregation, cross-object projection, impact policy, invariant reaction, and EF separation. Mark the variant structurally weak if generic provider composition erases domain ownership or requires unsafe/untyped recovery.

---

# 6. Variant B — typed domain pipeline

Implement and evaluate typed Raffinert-specific graph nodes such as `ObjectSet<T>`, typed object values, typed relations, relation aggregates, derived values, and invariants. Test `Select`/`Combine`, explicit left/right aggregation, nested `DependsOn`, cross-object projection, and edge-local impact semantics.

---

# 7. Variant C — hybrid current builder + composable derived pipeline

Keep object sets, relations, invariants, impact policy, and EF mapping close to the current API. Improve only the portions where composition provides clear value: recognized aggregate operators and derived-to-derived DAG composition. Compare conceptual operations rather than line counts.

---

# 8. Variant D — context DSL inspired by Guacamole, but Raffinert-specific

Test property-based context organization without source generation. Graph nodes may be exposed as model properties, but explicit stable definition names must remain possible. Do not couple model declaration to DI/runtime lifetime.

---

# 9. Explicitly rejected pseudo-variant — LINQ/IQueryable illusion

Do not implement an API that pretends the entire consistency model is ordinary `IQueryable<T>` or `IEnumerable<T>`. LINQ vocabulary may inspire operator names, but Raffinert-specific types must retain object-set identity, relation orientation, reverse routing, impact severity, invariant reaction, durable identity, and authoritative-scope semantics.

---

# 10. Implementation strategy for the concept facades

Candidate APIs compile to the existing builder, not independent engines:

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

Classify untranslatable ideas as `SYNTAX-ONLY GAP`, `PUBLIC-FACADE GAP`, `SEMANTIC GAP`, or `TYPE-SYSTEM GAP`. Never call something a semantic gap before verifying the current API cannot express it.

---

# 11. Task sequence

Execute Tasks 264–275 from the original plan intent in order: freeze baseline; capture current API; implement variants A–D; dogfood identical runtime expectations; produce the usability matrix; run new-user readability checks; stress type-system diagnostics; separate semantic API from optimization API; then stop at the human decision gate.

The mandatory concept wave ends at Task 275. Task 276 below is optional and may be performed before or after the human review, but it must not select or modify the production API.

## Task 276 — OPTIONAL: portable model / YAML serialization pressure test

**This task is optional. Failure is not a release blocker. Do not execute it until S1–S9 have been attempted for all four C# API variants.**

### 276.1 Purpose

Test whether the candidate API designs naturally separate:

```text
C# declaration syntax
        ↓
semantic model / intermediate representation (IR)
        ↓
compiled dependency DAG + executable delegates
```

The question is **not** “can arbitrary C# lambdas be serialized to YAML?”. Assume they cannot and should not be.

The useful question is:

> Is there a stable, portable description of the graph's semantic structure that could be represented outside C#, while executable behavior remains explicitly bound to code?

Use YAML only as a human-readable pressure test. The architectural result should not depend on YAML specifically; JSON or another representation should be possible from the same portable model.

### 276.2 Hard boundaries

Do **not**:

```text
add YamlDotNet or another YAML package
add a production YAML loader
add a production serializer
invent a general expression language
serialize arbitrary delegates
serialize arbitrary Expression<TDelegate> trees
use AssemblyQualifiedName as durable domain identity
resolve arbitrary type/method names from untrusted YAML
execute code named by untrusted YAML
change Core to satisfy this spike
judge an API worse merely because its C# syntax itself is not serializable
```

This is a model-shape experiment, not a configuration feature.

### 276.3 Sketch a portable semantic IR

Create `OptionalYaml/PortableModelSketch.cs` containing **concept-only DTOs/records**. They do not need to compile into Core and must not become public product types.

Explore whether the portable portion can represent at least:

```text
model schema/version
stable definition key
node kind: object-set / relation / derived / invariant
stable object-set logical type key
object-set key member paths
relation left/right definition keys
relation dependency member paths
relation semantic predicate reference, if externalized
source-member dependency paths
upstream derived definition keys
projected dependency path + upstream definition key
recognized aggregate kind: Count / LongCount / Any / Sum
recognized aggregate value member path where applicable
impact policy categories: membership-added / membership-removed / item-changed
Dirty / Invalid severity
invariant dependency edges
repair-policy stable reference, if externalized
EF materialization target member path as adapter metadata, if deliberately included
```

Do not force every item into the portable IR. For each one classify it:

```text
PORTABLE STRUCTURE
CODE-BOUND BEHAVIOR
ADAPTER-SPECIFIC METADATA
RUNTIME-ONLY STATE
UNRESOLVED
```

### 276.4 Explicitly separate structure from executable behavior

The spike must demonstrate a representation similar in spirit to:

```yaml
schema: raffiniert-consistency-model/v0

objectSets:
  order-lines:
    type: OrderLine
    key: Id

  fulfillments:
    type: Fulfillment
    key: Id

relations:
  matching-fulfillments:
    left: order-lines
    right: fulfillments
    predicate: matching-fulfillments-predicate
    dependsOn:
      left:
        - OrderNumber
        - ItemNumber
      right:
        - OrderNumber
        - ItemNumber
        - Cancelled

derived:
  fulfilled-quantity:
    source: order-lines
    fromRelation: matching-fulfillments
    aggregate:
      kind: Sum
      member: Quantity
    impact:
      membershipAdded: Dirty
      membershipRemoved: Invalid
      itemChanged: Invalid

  remaining-quantity:
    source: order-lines
    dependsOn:
      members:
        - OrderedQuantity
      derived:
        - fulfilled-quantity
    computation: remaining-quantity-computation

invariants:
  non-negative-remaining:
    source: order-lines
    dependsOn:
      derived:
        - remaining-quantity
    predicate: non-negative-remaining-predicate
    repair: rematch-order-line
```

This is illustrative, not a schema to adopt.

The identifiers such as:

```text
matching-fulfillments-predicate
remaining-quantity-computation
non-negative-remaining-predicate
rematch-order-line
```

must be treated as **stable behavior binding keys**, not reflection method names.

Sketch the corresponding C# binding concept separately, for example:

```csharp
bindings.RelationPredicate<OrderLine, Fulfillment>(
    "matching-fulfillments-predicate",
    (line, fulfillment) => ...);

bindings.Computation<OrderLine, decimal>(
    "remaining-quantity-computation",
    ...);
```

Do not implement a production binding registry. The goal is to expose the architectural boundary.

### 276.5 Dogfood YAML against difficult scenarios, not only S1

Create `OptionalYaml/model.example.yaml` covering at least:

```text
S2 nested UnitRate dependency
S3 relation + Sum aggregate
S4 derived -> derived dependency
S5 projected Allocation -> OrderLine dependency
S6 Dirty vs Invalid policy
S7 invariant + repair binding
S9 stable definition identity
```

S5 is mandatory for this optional spike if the spike is executed. If projected dependencies cannot be represented without leaking C# implementation details, record exactly why.

Do not require the YAML to be executable.

### 276.6 Round-trip thought experiment

For each API variant A–D, answer separately:

```text
Can C# declaration produce a semantic IR without parsing source code?
Can portable IR recreate the same graph structure before behavior binding?
Which information exists only inside lambdas today?
Would supporting portable IR require a new semantic concept, or only exposing metadata already known by the builder?
Can stable definition keys identify nodes without variable/property-name magic?
Can behavior binding be validated for source/value types?
Can the compiled model remain immutable after binding?
```

Do not claim round-trip support unless both directions are actually represented by the sketch.

### 276.7 Security note

Add a short section to `OptionalYaml/README.md` stating that a future external model format must not become arbitrary code execution.

At minimum record:

```text
external model input is data, not trusted code
behavior keys must resolve only through an application-supplied allow-listed registry
no arbitrary reflection type activation
no arbitrary method invocation
schema/version validation is required
unknown node/operator/binding kinds fail closed
```

No security implementation is required in this spike.

### 276.8 Evaluation output

Add an **Optional YAML / portable representation pressure test** section to `docs/consistency-api-v2-concept-results.md` only if Task 276 is executed.

Use this table:

| Question | Current | Variant A | Variant B | Variant C | Variant D |
|---|---|---|---|---|---|
| Graph structure naturally maps to IR | | | | | |
| Object ownership survives serialization | | | | | |
| Relation orientation survives | | | | | |
| Cross-object projection survives | | | | | |
| Dirty/Invalid policy survives | | | | | |
| Stable node identity survives | | | | | |
| Behavior can be separated from structure | | | | | |
| Requires parsing C# expressions to work | | | | | |
| Requires new Core semantic capability | | | | | |

Do not score 1–10. Give factual notes.

### 276.9 Decision rule

YAML portability is a **tie-breaker / architectural signal only**.

Do not reject an otherwise superior C# API because it is less convenient to serialize.

However, record a positive architectural signal if a candidate naturally yields:

```text
typed C# facade
    ↓
explicit semantic IR
    ↓
compiler
    ↓
immutable compiled DAG/runtime metadata
```

That separation can improve diagnostics, visualization, tooling, model diffing, future persistence, and external configuration even if YAML support is never shipped.

### 276.10 Acceptance for optional spike

If Task 276 is executed, it is complete only when:

```text
[ ] no production dependency added
[ ] no production source changed
[ ] no arbitrary C# expression serialization attempted
[ ] portable-vs-code-bound classification written
[ ] example YAML includes S2-S7 and S9 concerns
[ ] projected dependency S5 explicitly addressed
[ ] stable behavior-binding-key concept sketched
[ ] security boundary documented
[ ] all API variants evaluated against the same IR questions
[ ] results explicitly say YAML is optional and non-blocking
```

Suggested commit:

```text
experiments: pressure-test API v2 with portable YAML model
```

---

# 12. Candidate syntax details the agent must explore, not silently decide

Preserve the original plan's candidate comparisons for object-set creation, relation creation, relation aggregation orientation, direct member value nodes, derived composition, cross-object projection, invariant declaration, and explicit stable naming. Cross-object projection naming remains more important than superficial `Select` versus `Map` naming.

---

# 13. What not to copy from Roslyn/Guacamole

Do not call everything `Incremental*`; do not erase domain ownership; do not make source generation mandatory; do not conflate reactive outputs with repair policy; and do not inherit Guacamole concurrency semantics. This experiment is model-declaration syntax only.

---

# 14. Strong design hypotheses to test

Preserve hypotheses H1–H8 from the original plan and add:

### H9

A viable API may benefit from compiling first into an explicit semantic IR rather than directly mutating builder definitions, even if that IR is never serialized.

### H10

Stable definition keys plus typed application-supplied behavior bindings may make part of a consistency model portable without requiring serialization of C# lambdas or expression trees.

### H11

If YAML representation requires inventing semantics that the C# model does not otherwise need, YAML is exerting harmful design pressure and should remain unsupported.

If Task 276 is not executed, mark H9–H11 `NOT TESTED`, not supported or contradicted.

---

# 15. Acceptance criteria for the entire concept wave

Mandatory completion remains Tasks 264–275 and the original S1–S9/API-comparison acceptance criteria. Task 276 is explicitly optional and non-blocking.

If Task 276 is executed, additionally require its section 276.10 checklist. Do not let an incomplete optional YAML spike block the mandatory API-v2 comparison or RC work.

---

# 16. What a later production plan must contain

After human selection, create a separate implementation plan covering public types/namespaces, compatibility strategy, API baselines, docs, samples, misuse tests, semantic-equivalence tests, EF compatibility, performance, and RC baseline reset.

If portable model representation is later selected as a production feature, create a **separate** roadmap for it. That roadmap must address schema versioning, binding validation, security, migrations, diagnostics, and compatibility. Do not smuggle it into the API-v2 implementation plan.

---

# 17. Final instruction to the coding agent

Your job in this roadmap is **not to design by taste**.

Make four competing API ideas concrete enough that the same difficult Raffinert scenarios can be read, compiled, and where possible executed side-by-side. Do not optimize for the prettiest three-line example.

The API must survive graphs such as:

```text
Fulfillment mutation
    -> relation membership/item impact
    -> FulfilledQuantity
    -> RemainingQuantity
    -> projected AllocationValidity
    -> invariant
    -> deferred repair
```

and:

```text
SourceItem.UnitValue / TargetItem.UnitValue
    -> nested dependency routing through Association
    -> UnitRate
    -> EF materialized mirror
```

If a candidate makes those graphs easier to understand without weakening semantics, record the evidence. If it only makes `x => x.A + x.B` prettier, record that limitation and move on.

---

# 18. Optional portability principle

Treat YAML as a lens, not a target.

The potentially valuable architectural idea is not a `.yaml` file. It is a clean boundary:

```text
human/API declaration
       ↓
semantic consistency model
       ↓
behavior binding
       ↓
compiled execution model
       ↓
runtime state
```

If that boundary emerges naturally, preserve it as design evidence. If it requires weakening type safety, duplicating semantics, or inventing a configuration language, reject the YAML direction without penalizing the C# API.