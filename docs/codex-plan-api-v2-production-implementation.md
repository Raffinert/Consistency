# Codex plan — API v2 production implementation

Status: **APPROVED DIRECTION — PRODUCTION IMPLEMENTATION PLAN**

Audience: coding agent. Follow this document literally and in order. Do not redesign the API while implementing it.

Baseline experiment: `e48072579c09da0a88536a758b23f16332f5a3db` (`experiments: dogfood final PriceRate API v2`).

Primary evidence document:

```text
docs/final-api-v2-price-rate-dogfood-results.md
```

The broad syntax exploration is finished. Do **not** create another A/B/C/D experiment. The production direction is Variant-C-derived and is fixed by this plan except for the explicitly listed implementation details.

---

# 1. Target developer experience

The production API must support this style:

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

var matchingReceipts = model
    .Relation(poLines, receipts)
    .Where(Matches)
    .Named("matching-receipts");

var receivedQuantity = model
    .Derived(poLines)
    .From(matchingReceipts)
    .Impact(p => p
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Sum(x => x.Quantity)
    .Named("received-quantity");

var actualQuantity = model
    .Derived(links)
    .From(x => x.PurchaseOrderLine, remainingQuantity)
    .From(unitRate)
    .Select((link, remaining, rate) => CalculateActualQuantity(link, remaining, rate))
    .Named("actual-quantity");

var linkInvariant = model
    .Invariant(links)
    .From(linkValidity)
    .Must((_, valid) => valid)
    .Named("link-validity-invariant")
    .ScheduleRepairWith(Rematch);
```

Runtime consumption must support:

```csharp
var logicalRate = runtime.Evaluate(priceRate, link);

var synchronizedRate = runtime.Materialize(priceRate, link);

runtime.Materialize(link);
Use(link.PriceRate, link.UnitRate);
```

Do not rename `From`, `Select`, `MaterializeTo`, `Evaluate`, or `Materialize` during implementation.

---

# 2. Fixed semantic vocabulary

These meanings are requirements, not suggestions.

## `From(...)`

Means:

> explicit logical value flow from another derived/relation value into this definition.

A handle passed through `From` is part of the logical DAG and its current logical value is supplied to the downstream calculator/operator.

Example:

```csharp
.From(priceRate)
.Select((link, rate) => ...)
```

`rate` comes from the logical PriceRate definition. It must never be obtained by reading `link.PriceRate`.

## `DependsOn(...)`

Means:

> tracked source/member dependency for a value read opaquely inside ordinary calculator code.

Example:

```csharp
.DependsOn(x => x.InvoiceLine.Price)
.Select(CalculatePriceRate)
```

`DependsOn` affects invalidation/change tracking. It does **not** add a calculator argument.

## `MaterializeTo(...)`

Means:

> optional physical representation of a logical derived value on the source object.

It is metadata/sink configuration. It is **not** a logical source edge.

## `Impact(...)`

Means:

> change-severity policy for dependency edges.

The underlying model must treat impact as dependency/edge semantics even where fluent syntax uses a group shorthand.

## `Invariant(...).Must(...).ScheduleRepairWith(...)`

Remains separate from pure derived computation and materialization.

Materialization must not dispatch repair.

---

# 3. Read the repository before editing

Before making any production change, inspect these areas and write down the actual production types/files that correspond to each concept:

```text
src/Raffinert.Consistency/
src/Raffinert.Consistency.EntityFrameworkCore/
tests/
experiments/Raffinert.Consistency.FinalApiDogfood/
docs/final-api-v2-price-rate-dogfood-results.md
```

Locate at minimum:

```text
- current model/builder entry point
- object-set abstraction and stable set identity
- relation builder/definition
- derived definition/handle
- dependency descriptors
- projected/cross-object dependency implementation
- aggregate operators and incremental plans
- invariant builder/definition
- repair scheduling
- compiled model Build path
- runtime Get/evaluation path
- current materialization implementation
- EF Core materialization mapping
- diagnostics/debug graph support
```

Do not copy experiment adapters blindly. Map each facade concept onto the existing production primitive.

Before coding, create a short implementation note in the commit/PR description or working notes containing:

```text
candidate concept -> existing production primitive -> required production change
```

---

# 4. Migration strategy: additive first

Do not break the existing public API at the start.

Implement API v2 additively over the existing engine.

Phases:

```text
Phase A: shared semantic metadata/primitives
Phase B: v2 declaration facade/builders
Phase C: recognized operators
Phase D: materialization metadata/runtime API
Phase E: validation/diagnostics
Phase F: EF bridge
Phase G: parity tests and examples
Phase H: deprecate/reduce old syntax only after parity is proven
```

Existing tests must continue passing throughout Phases A-G.

Do not remove `.Using`, `.Compute`, `.Incrementally`, existing materialization mappings, or other old public members merely because v2 has replacements.

If old and new syntax can target the same compiled primitive, they should.

---

# 5. Phase A — introduce/normalize semantic metadata

Goal: make the compiled model capable of representing v2 concepts without caring which fluent syntax produced them.

## A1. Logical dependency descriptor

Ensure the compiled model can distinguish at least:

```text
Direct member dependency
Derived-value dependency
Projected derived-value dependency
Relation-valued dependency
```

Do not encode these distinctions only in fluent builder generic types.

The compiled model/debug model must know which kind of edge it is.

## A2. Edge impact descriptor

Represent impact on the dependency edge or dependency descriptor.

Required capabilities:

```text
source/member changed -> Dirty/Invalid/custom classifier
relation membership added -> severity
relation membership removed -> severity
relation item changed -> severity
```

Do not store one undifferentiated `Impact` field on the derived node if that prevents different dependencies from having different policies.

A group shorthand may apply one policy to multiple dependencies by copying/attaching the policy to each relevant edge.

## A3. Materialization descriptor

Create or normalize a first-class compiled descriptor conceptually containing:

```text
source object-set identity
source CLR type
value type
target member identity/name
compiled getter for current physical target value
compiled setter for physical target value
equality/comparison strategy if one already exists
owning derived definition identity
```

Use existing production primitives where possible.

Do not make EF Core own this descriptor. Core/runtime owns materialization metadata; EF consumes it.

## A4. Stable definition identity

Preserve `.Named("...")` as explicit stable identity.

Do not infer durable identity from local variable names.

## A5. Tests

Add low-level compiled-model tests proving all four dependency kinds and materialization descriptors survive Build with correct set/definition identity.

Commit checkpoint suggested:

```text
core: normalize v2 dependency and materialization metadata
```

---

# 6. Phase B — production v2 declaration facade

Goal: implement the successful final dogfood vocabulary using focused staged builders, without Variant-A provider/generic explosion.

Use experiment code only as behavioral reference.

## B1. `Derived(set)` entry

Production entry:

```csharp
model.Derived(links)
```

must expose relevant operations such as:

```text
DependsOn
From
Select
```

Do not expose relation aggregate operators until a relation-valued stage exists.

## B2. `DependsOn`

Support one or multiple source-rooted member paths:

```csharp
.DependsOn(
    x => x.InvoiceLine.Price,
    x => x.PurchaseOrderLine.Price)
```

Requirements:

```text
- every path is rooted in TSource;
- nested paths are tracked correctly;
- each dependency receives a compiled descriptor;
- DependsOn does not alter calculator argument shape;
- multiple calls compose rather than replace previous dependencies.
```

If nested dependency tracking already has a production primitive, reuse it.

## B3. `From(localDerived)`

Support:

```csharp
.From(priceRate)
```

where upstream and downstream share the same owner set/source identity.

Requirements:

```text
- validate object-set identity, not only CLR type;
- create a logical derived-handle edge;
- downstream calculator receives upstream current logical value;
- runtime evaluation follows the handle, never a materialized mirror;
- multiple From calls preserve deterministic calculator argument ordering.
```

Same CLR type but different object set must be rejected.

## B4. projected `From`

Support:

```csharp
.From(x => x.PurchaseOrderLine, remainingQuantity)
```

Meaning:

```text
for each downstream link
select its PurchaseOrderLine
consume remainingQuantity owned by that selected object
propagate changes back to the link
```

Requirements:

```text
- selector result CLR type matches upstream handle owner type;
- object-set identity is validated where multiple same-CLR sets exist;
- projection direction is explicit;
- downstream value argument is the logical upstream value;
- changes on projected upstream propagate to downstream owners;
- adapter/internal bridge nodes must not leak into public debug vocabulary.
```

Do not implement the rejected compact syntax that mixes projected and local handles in one projected call.

Use chaining:

```csharp
.From(x => x.PurchaseOrderLine, remainingQuantity)
.From(unitRate)
```

## B5. `Select`

Keep the name `Select`.

Support the calculator shapes required by the number of explicit `From` inputs.

Examples:

```csharp
.Derived(links)
.Select(link => ...)

.Derived(links)
.From(priceRate)
.Select((link, rate) => ...)

.Derived(links)
.From(a)
.From(b)
.Select((link, aValue, bValue) => ...)
```

Do not create an unbounded hand-written overload explosion if the repository already has a tuple/composition primitive. Prefer the smallest typed mechanism consistent with existing architecture.

However, do not sacrifice useful compile-time types by falling back to `object[]` in the public API.

## B6. `Expression` versus `Func` policy

This is a required deliberate design.

Support opaque method groups after explicit dependencies:

```csharp
.DependsOn(...)
.Select(CalculatePriceRate)
```

This must compile naturally.

Do not require users to manually wrap every method group in a lambda merely to satisfy `Expression<Func<...>>`.

Recommended rule:

```text
- expression-based Select may be used where production can extract/use structural expression information;
- Func-based Select is allowed when dependencies are explicit or when the engine does not require expression inspection;
- both compile into the same logical calculation primitive where semantics are equivalent.
```

Do not attempt to infer opaque `Func` dependencies magically.

If a calculator is opaque and dependency declarations are insufficient according to current correctness rules, Build must reject it.

## B7. stage discoverability

Keep the IntelliSense surface approximately:

```text
Derived(set): DependsOn, From, Select
After DependsOn: DependsOn, From, Impact, Select
After scalar From: DependsOn, From, Impact, Select
After relation From: Impact, Sum, Count, LongCount, Any
Completed value: MaterializeTo, Named
```

Do not expose every extension method at every stage.

Commit checkpoint suggested:

```text
api: add v2 Derived From DependsOn Select builders
```

---

# 7. Phase C — recognized aggregate operators

Goal: semantic operators choose existing optimized execution plans without `.Incrementally()`.

Implement first-class:

```csharp
.Sum(selector)
.Count()
.LongCount()
.Any()
```

on the correct relation-valued stage.

## C1. `Sum`

Compile directly to the existing incremental Sum primitive/plan.

No `.Incrementally()` call should be required.

## C2. Count family

Map:

```text
Count -> existing IncrementalCount
LongCount -> existing IncrementalLongCount
Any -> existing IncrementalAny
```

Do not simulate optimization in the facade.

## C3. impact behavior

Prove:

```text
membership add can follow Dirty/Fresh incremental path according to configured policy
membership removal can become Invalid
item change can become Invalid
subsequent Evaluate recomputes when required
```

## C4. debug plan

Debug/diagnostic representation must expose semantic/compiled plan information such as:

```text
received-quantity
  from matching-receipts
  operator IncrementalSum(GoodsReceipt.Quantity)
```

## C5. old syntax parity

For each recognized operator, write parity tests comparing old declaration syntax and v2 syntax against the same mutation timeline and outputs.

Commit checkpoint suggested:

```text
api: add recognized incremental aggregate operators
```

---

# 8. Phase D — dependency-scoped Impact

Goal: remove ambiguity without overcomplicating the common case.

This phase may require a small API design choice, but the semantic model is fixed: **Impact belongs to dependency edges**.

## D1. relation-valued shorthand

Keep readable syntax such as:

```csharp
.From(matchingReceipts)
.Impact(p => p
    .MembershipAdded(Dirty)
    .MembershipRemoved(Invalid)
    .ItemChanged(Invalid))
.Sum(...)
```

Here the relevant preceding relation edge is unambiguous.

## D2. direct dependency group shorthand

Allow this if it can be made unambiguous:

```csharp
.DependsOn(
    x => x.InvoiceLine.Price,
    x => x.PurchaseOrderLine.Price)
.Impact(p => p.SourceChanged(Invalid))
```

Meaning: apply this policy to the dependency group created/configured by that stage.

Do not make it mean “whichever dependency happened to be added last”.

## D3. member-specific impact

Provide a clear way to express asymmetric policy, conceptually one of:

```csharp
.DependsOn(
    x => x.OrderedQuantity,
    impact => impact.SourceChanged(IncreaseDirtyDecreaseInvalid))
```

or an equivalent existing builder shape.

The exact spelling may follow repository conventions, but it must identify the member/edge explicitly.

Do not add three competing public spellings.

## D4. tests

Required:

```text
- two DependsOn members with shared policy
- two DependsOn members with different policies
- projected From impact
- relation membership add/remove/item change
- increase Dirty / decrease Invalid classifier
```

Commit checkpoint suggested:

```text
api: scope impact policies to dependency edges
```

---

# 9. Phase E — `MaterializeTo` in Core

Goal: make materialization a first-class optional representation of a derived value.

## E1. public declaration

Support:

```csharp
.MaterializeTo(x => x.PriceRate)
```

on a completed derived value.

Requirements:

```text
- TValue matches target property type at compile time where possible;
- expression root is TSource;
- target is one direct writable property;
- descriptor records source object-set identity;
- runtime-only values simply omit MaterializeTo;
- MaterializeTo does not create a dependency edge.
```

## E2. duplicate targets

Reject two definitions targeting the same:

```text
object-set identity + target member
```

Do not deduplicate only by CLR type/property name.

Prefer declaration-time rejection when enough information is already known; Build-time rejection is acceptable where complete-model context is required.

## E3. mirror dependency validation

Given:

```csharp
priceRate.MaterializeTo(x => x.PriceRate)
```

reject:

```csharp
.DependsOn(x => x.PriceRate)
```

when the property is a registered materialization target.

Correct usage:

```csharp
.From(priceRate)
```

Correctness enforcement must happen no later than `Build()`.

Use an actionable error message similar to:

```text
'PriceRate' is a materialization target of derived definition 'price-rate'.
Depend on the logical definition with From(priceRate) instead of treating the mirror property as an independent source dependency.
```

An analyzer may be added later for faster feedback, but is not required for correctness.

## E4. shared descriptor

Do not create separate runtime and EF materialization metadata.

Compile one descriptor consumed by both.

Commit checkpoint suggested:

```text
core: add MaterializeTo compiled descriptors and validation
```

---

# 10. Phase F — runtime `Evaluate` and `Materialize`

The previous experiments selected these semantics. Implement them in production without reopening naming.

## F1. `Evaluate`

Public shape conceptually:

```csharp
TValue runtime.Evaluate<TSource, TValue>(
    Derived<TSource, TValue> definition,
    TSource source);
```

Semantics:

```text
Fresh -> return cached logical value
Dirty/Invalid -> evaluate/recompute according to existing runtime semantics -> Fresh -> return
never write MaterializeTo target
```

`Evaluate != ForceRecompute`.

Use existing runtime Get/evaluation machinery internally rather than duplicating cache logic.

## F2. targeted `Materialize`

Conceptually:

```csharp
TValue runtime.Materialize<TSource, TValue>(
    Derived<TSource, TValue> definition,
    TSource source);
```

Semantics:

```text
Evaluate definition as needed
write exactly this definition's configured target
return logical value
```

If definition has no materialization target, reject with an actionable error.

Preserve the selected targeted T1 behavior: do not automatically materialize upstream materialized definitions.

## F3. object-level `Materialize(source)`

Conceptually:

```csharp
void runtime.Materialize<TSource>(TSource source);
```

Meaning:

> synchronize every configured materialized derived representation physically located on this registered source object.

It does not mean:

```text
repair graph
materialize dependencies' objects
materialize downstream objects
evaluate unrelated runtime-only values
```

## F4. source lookup

Do not scan the entire model.

Build an index approximately:

```text
object-set/source identity -> materialization descriptors
```

Lookup should be O(1) plus O(k), where k is materialized definitions for the applicable source membership(s).

Do not resolve only by CLR type.

## F5. two-phase object materialization

Use the selected O2 strategy:

```text
Phase 1: evaluate/prepare all required logical values
Phase 2: write physical mirrors
```

If evaluation fails in phase 1, no target property should have been written by this call.

## F6. physical rollback

Use selected F2 semantics for multi-target setter failure:

```text
capture previous physical values
apply writes deterministically
if one write fails:
    restore already-written physical targets
    rethrow
```

Do not roll back logical cache solely because physical writes failed.

A retry should reuse Fresh logical values where possible.

## F7. avoid redundant assignment

If target already equals logical value, avoid setter invocation where the descriptor/equality policy can safely determine equality.

This reduces EF/property side effects.

## F8. no-target source

`runtime.Materialize(source)` is a no-op when a valid registered source has zero materialized targets.

Targeted `Materialize(definition, source)` still rejects a runtime-only definition because the request is explicit.

## F9. cross-object physical scope

Evaluation may traverse projected/logical dependencies.

Physical writes remain on the requested source object only.

## F10. repair separation

Materialize must not dispatch/consume repair requests.

Commit checkpoint suggested:

```text
runtime: add Evaluate and targeted/object Materialize APIs
```

---

# 11. Phase G — invariants and repair v2 facade

Do not redesign invariant semantics.

Support the selected readable shape:

```csharp
model
    .Invariant(links)
    .From(linkValidity)
    .Must((_, valid) => valid)
    .Named("link-validity-invariant")
    .ScheduleRepairWith(Rematch);
```

Requirements:

```text
- From consumes logical handle;
- Must remains explicit;
- repair scheduling remains explicit;
- materialization does not trigger repair;
- normal mutation completion/dispatch preserves exactly-once behavior.
```

If current production invariant API already has equivalent semantics, implement this as a thin facade.

Do not create convenience helpers that merge Must and repair scheduling.

Commit checkpoint suggested:

```text
api: add v2 invariant value-flow facade
```

---

# 12. Phase H — EF Core bridge

Goal: EF consumes Core materialization descriptors; EF must not define a second materialization model.

## H1. descriptor bridge

Adapt existing `ConsistencyEfCoreMappings.Materialize` or equivalent so it can consume compiled `MaterializeTo` metadata.

Do not expose raw internal runtime handles in normal user declarations.

## H2. persistence orchestration

Keep the previous decision:

```text
application API -> object-level Materialize(source) is ergonomic
persistence adapter -> targeted affected-definition materialization is preferable when EF knows exactly which definitions are affected
```

Do not force EF to call object-level Materialize for every changed source if that causes unnecessary visits/evaluations.

Both paths must share the same lower-level materialization descriptor/write primitive.

## H3. EF tracking tests

Use SQLite/in-memory relational tests where appropriate.

Required:

```text
- Evaluate does not mark materialized property modified;
- targeted Materialize marks only changed target;
- object Materialize marks changed PriceRate and UnitRate targets;
- already-equal target is not needlessly modified where assignment is skipped;
- unrelated scalar properties remain untouched;
- setter/write failure leaves physical object rolled back according to Core semantics.
```

## H4. SaveChanges parity

Existing persistence behavior must remain correct for old API declarations during migration.

Commit checkpoint suggested:

```text
efcore: consume compiled MaterializeTo descriptors
```

---

# 13. Phase I — debug graph and diagnostics

The public model should be inspectable in logical vocabulary.

Target debug output approximately:

```text
price-rate [Derived<PurchaseOrderInvoiceLine, Decimal?>]
  depends-on InvoiceLine.Price
  depends-on PurchaseOrderLine.Price
  materializes-to PriceRate

unit-rate [Derived<PurchaseOrderInvoiceLine, Decimal?>]
  from price-rate
  depends-on InvoiceLine.Quantity
  depends-on PurchaseOrderLine.OrderedQuantity
  materializes-to UnitRate

actual-quantity [Derived<PurchaseOrderInvoiceLine, Decimal>]
  from remaining-quantity
  projection PurchaseOrderLine -> remaining-quantity
  from unit-rate

received-quantity [Derived<PurchaseOrderLine, Decimal>]
  from matching-receipts
  operator IncrementalSum(GoodsReceipt.Quantity)
```

Do not expose facade-only tuple/token/bridge implementation nodes unless a low-level diagnostic mode explicitly asks for them.

Diagnostics must include definition stable names where available.

Add actionable errors for:

```text
- wrong set identity
- projected owner mismatch
- cross-model handle
- duplicate materialization target
- mirror property used as source dependency
- runtime-only targeted Materialize
- unknown/unregistered source
- incomplete opaque calculator dependencies under current correctness rules
```

Commit checkpoint suggested:

```text
diagnostics: expose v2 logical graph and validation messages
```

---

# 14. Phase J — full PriceRate/UnitRate production tests

Create a production-level test fixture based on the final dogfood scenario. Do not depend on experiment facade classes.

Required timeline:

## J1 initial

```text
InvoiceLine.Price = 60
POL.Price = 10
PriceRate logical = 6 Fresh
UnitRate logical = 6 Fresh
mirrors = 6
```

## J2 mutation

```text
InvoiceLine.Price = 55
PriceRate stale/Invalid according to configured impact
UnitRate stale/Invalid transitively
mirrors remain 6
```

## J3 Evaluate PriceRate

```csharp
var rate = runtime.Evaluate(priceRate, link);
```

Assert:

```text
rate = 5.5
logical PriceRate Fresh
link.PriceRate still 6
```

## J4 Evaluate UnitRate

```csharp
var unit = runtime.Evaluate(unitRate, link);
```

Assert UnitRate uses logical PriceRate 5.5, never stale mirror 6.

## J5 targeted Materialize

Corrupt PriceRate mirror and call:

```csharp
runtime.Materialize(priceRate, link);
```

Assert only PriceRate target is synchronized.

## J6 object Materialize

```csharp
runtime.Materialize(link);
```

Assert both configured mirrors are synchronized, unrelated objects untouched, repair not dispatched.

## J7 repair

Run normal dispatch/completion and assert pending repair executes exactly once.

## J8 rogue mirror

Set both mirrors to incorrect values while logical caches remain Fresh. Object Materialize must restore both without recomputation.

## J9 failure

Inject UnitRate target setter failure. Assert PriceRate physical write is rolled back, logical caches remain reusable, retry succeeds without unnecessary recompute.

---

# 15. Phase K — aggregate/linking production tests

Create production tests for relation-backed quantity behavior.

Required:

```text
matchingReceipts relation
receivedQuantity Sum
receipt Count
receipt LongCount
Any receipt
remainingQuantity downstream derived value
```

Mutation timeline:

```text
add receipt -> configured additive path
increase quantity -> configured policy
remove/cancel receipt -> Invalid
quantity decrease -> Invalid where configured
Evaluate after Invalid -> correct recomputation
```

Prove old API and v2 produce equivalent logical values/states/repair requests for equivalent declarations.

---

# 16. Phase L — type-system and misuse tests

Add explicit tests/compile samples for:

```text
L1 From incompatible owner without projection
L2 projected From selector returns incompatible owner
L3 same CLR type but wrong object-set identity
L4 handle from another model
L5 MaterializeTo wrong value type
L6 MaterializeTo non-direct/non-writable member
L7 duplicate target
L8 registered mirror used in DependsOn
L9 opaque Func calculator with missing required dependency declarations
L10 targeted Materialize on runtime-only definition
L11 object Materialize unknown/unregistered source
```

For each, prefer the earliest useful failure point without introducing excessive generic complexity:

```text
compile time > fluent declaration > Build > runtime
```

But do not contort the public API solely to move every error to compile time.

Error quality matters more than theoretical maximal static typing.

---

# 17. Phase M — compatibility and migration tests

The existing API must remain functional while v2 is introduced.

For representative definitions, declare the same semantics twice:

```text
old API declaration
v2 declaration
```

Feed identical mutations and compare:

```text
logical value
Fresh/Dirty/Invalid state
repair scheduling
incremental/recompute behavior
materialized result
EF persistence result
```

At minimum cover:

```text
simple direct derived value
multiple dependencies
transitive derived dependency
projected dependency
relation Sum
Count/LongCount/Any
invariant + repair
materialized derived value
```

Do not deprecate old syntax until these parity tests pass.

---

# 18. Phase N — documentation after implementation

Only after production tests pass, update README/docs.

Document the conceptual split before presenting advanced syntax:

```text
From          = logical value flow
DependsOn     = tracked opaque read
MaterializeTo = physical representation
Evaluate      = make/read logical value current
Materialize   = synchronize physical representation
Invariant     = required truth
Repair        = consequence handling
```

Use PriceRate/UnitRate as the primary materialization example.

Use received quantity as the recognized aggregate example.

Show:

```csharp
runtime.Evaluate(priceRate, link);
runtime.Materialize(link);
```

side by side to make logical versus physical semantics explicit.

Do not describe `.Incrementally()` as the preferred v2 aggregate API.

Do not claim direct property reads are automatically fresh before Materialize.

Document that current runtime concurrency/thread-safety contract remains unchanged.

Commit checkpoint suggested:

```text
docs: document API v2 value flow and materialization
```

---

# 19. Do not implement concurrency in this plan

Concurrency remains a separate release decision/workstream.

Do not add:

```text
locks
concurrent collections
per-source synchronization
async materialization
```

merely because object Materialize mutates several properties.

Preserve/document the current runtime synchronization contract.

Physical rollback in one single-threaded Materialize call is not a claim of cross-thread atomicity.

---

# 20. Do not implement YAML persistence in this plan

Potential YAML model persistence/serialization is optional future work.

While designing descriptors, avoid needless runtime-only closures where stable declarative metadata can be retained, but do not distort API v2 or add serializers now.

A future persisted representation may care about:

```text
stable names
object-set identities
member paths
operator kinds
impact policy identifiers
materialization target member
```

Opaque calculator delegates obviously cannot be reconstructed from YAML without an external registry. This is out of scope.

---

# 21. Suggested commit sequence

Prefer small reviewable commits in this order:

```text
1. core: normalize v2 dependency and materialization metadata
2. api: add v2 Derived From DependsOn Select builders
3. api: add recognized incremental aggregate operators
4. api: scope impact policies to dependency edges
5. core: add MaterializeTo compiled descriptors and validation
6. runtime: add Evaluate and targeted/object Materialize APIs
7. api: add v2 invariant value-flow facade
8. efcore: consume compiled MaterializeTo descriptors
9. diagnostics: expose v2 logical graph and validation messages
10. tests: add PriceRate UnitRate v2 production scenarios
11. tests: add aggregate linking and old-v2 parity coverage
12. docs: document API v2 value flow and materialization
```

Every commit must build and tests must pass before moving to the next where practical.

Do not squash unrelated architecture changes into these commits.

---

# 22. Mandatory implementation rules for the agent

1. **Do not redesign names.** `From`, `DependsOn`, `Select`, `MaterializeTo`, `Evaluate`, `Materialize` are selected.
2. **Do not create another experimental facade** as the final deliverable. Implement production types after reading existing primitives.
3. **Do not remove old API yet.** v2 is additive until parity is proven.
4. **Do not make mirror properties logical dependencies.** Use derived handles.
5. **Do not make `MaterializeTo` EF-specific.** It belongs to Core compiled metadata.
6. **Do not make `Evaluate` write properties.**
7. **Do not make object `Materialize` repair the graph.**
8. **Do not make object `Materialize` scan the whole model.** Index materializers by source/object-set identity.
9. **Do not materialize dependency objects or downstream objects** when materializing one source.
10. **Do not expose `.Incrementally()` in the preferred v2 recognized aggregate path.**
11. **Do not hide repair inside derived/invariant convenience helpers.**
12. **Do not use CLR type alone as object-set identity.**
13. **Do not create a universal provider abstraction** like rejected Variant A.
14. **Do not add concurrency/YAML/source generation in this implementation.**
15. **Do not silently weaken validation to make the facade compile.** Add actionable Build/declaration errors.

---

# 23. Stop conditions — ask maintainer instead of guessing

Stop implementation and report evidence if any of these occurs:

```text
S1. Existing engine cannot represent From(handle) without a new runtime semantic.
S2. Projected From requires graph behavior materially different from the successful dogfood.
S3. Existing aggregate primitives do not actually support Sum/Count/LongCount/Any parity.
S4. MaterializeTo cannot be represented in Core without coupling Core to EF.
S5. O2 evaluate-then-write object materialization conflicts with existing correctness semantics.
S6. F2 physical rollback cannot be implemented without corrupting logical runtime state.
S7. Same-CLR multiple object-set membership cannot be resolved with existing runtime identity.
S8. Supporting Func method groups after DependsOn would silently lose required correctness metadata.
S9. Dependency-scoped Impact cannot be represented without changing current engine semantics.
S10. Old API and v2 produce different states/repairs for an equivalent declaration.
```

Do not work around a stop condition with reflection hacks, `dynamic`, `object[]`, global scans, or duplicated runtime engines.

---

# 24. Definition of done

API v2 production implementation is done only when all items below are true:

```text
[ ] production Derived(set).DependsOn(...) exists
[ ] production Derived(set).From(localHandle) exists
[ ] production projected From(selector, handle) exists
[ ] production Select supports required typed value-flow shapes
[ ] opaque method-group calculator after explicit DependsOn works intentionally
[ ] edge-scoped Impact metadata exists
[ ] Sum is recognized and uses existing incremental plan
[ ] Count is recognized and uses existing incremental plan
[ ] LongCount is recognized and uses existing incremental plan
[ ] Any is recognized and uses existing incremental plan
[ ] MaterializeTo exists in Core declaration API
[ ] duplicate materialization target is rejected
[ ] mirror-property dependency is rejected by Build or earlier
[ ] runtime.Evaluate exists and never writes mirrors
[ ] targeted runtime.Materialize exists
[ ] object runtime.Materialize exists
[ ] object Materialize uses source/set indexed descriptors, not whole-model scan
[ ] object Materialize uses evaluate-first/write-second semantics
[ ] object Materialize rolls back prior physical writes on setter failure
[ ] logical caches are not rolled back solely for physical setter failure
[ ] runtime-only unrelated definitions are not evaluated by object Materialize
[ ] cross-object dependencies do not expand physical materialization scope
[ ] materialization does not dispatch repair
[ ] v2 invariant From/Must/ScheduleRepairWith works
[ ] EF consumes shared Core materialization descriptors
[ ] Evaluate does not create EF property modifications
[ ] Materialize creates only expected EF modifications
[ ] PriceRate stale-mirror test passes
[ ] UnitRate consumes logical PriceRate handle test passes
[ ] additive/subtractive linking impact tests pass
[ ] aggregate old-v2 parity tests pass
[ ] invariant/repair old-v2 parity tests pass
[ ] materialization old-v2 parity tests pass
[ ] same-CLR different-set misuse test passes
[ ] cross-model handle misuse test passes
[ ] debug graph uses logical v2 vocabulary
[ ] actionable validation messages exist
[ ] existing old API tests remain green
[ ] final API v2 docs/README updated
[ ] no concurrency/YAML/source-generation scope creep introduced
```

---

# 25. Final architecture to preserve

Keep this mental model throughout implementation:

```text
                     logical graph

source members ──DependsOn──┐
                            │
upstream derived ──From─────┼──> Derived value
                            │        │
relation/operator ──From────┘        │
                                     │
                         ┌───────────┴───────────┐
                         │                       │
                    Evaluate                MaterializeTo
                         │                       │
                  logical current           physical mirror
                                                 │
                                      runtime.Materialize(source)


                     correctness consequences

change ──Impact──> Dirty / Invalid
                         │
                         ▼
                     Invariant
                         │
                         ▼
                       Repair
```

The important boundaries are:

```text
logical value != physical mirror
materialization != repair
semantic operator != execution-strategy declaration
opaque dependency tracking != logical value flow
CLR type != object-set identity
```

If an implementation shortcut violates one of these boundaries, do not take the shortcut.

After all phases and tests pass, stop and report the implementation commits, remaining compatibility concerns, and any old API members that are now candidates for a separate deprecation/removal plan. Do not remove them in this plan.