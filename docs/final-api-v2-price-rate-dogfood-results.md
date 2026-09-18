# Final API v2 PriceRate / UnitRate dogfood results

Status: focused spike complete. The isolated executable passes; no production public API or runtime implementation was changed.

The prior experiments remain the baseline: Variant C had the lowest migration and generic-stage cost; a universal provider facade added complexity without runtime capability; semantic aggregates can hide `.Incrementally()`; cross-object projection is the hardest declaration; opaque calculators still need explicit dependencies; derived computation remains separate from invariants and repair; `Evaluate` is logical-only; targeted and object `Materialize` synchronize configured mirrors; object materialization is not graph-wide repair; and logical edges must use derived handles rather than mirror properties. This spike did not contradict those findings.

## 1. Complete candidate API

```csharp
var priceRate = model
    .Derived(links)
    .DependsOn(
        x => x.InvoiceLine.Price,
        x => x.PurchaseOrderLine.Price)
    .Impact(p => p.SourceChanged(DependencySeverity.Invalid))
    .Select(PurchaseOrderInvoiceLine.CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");

var unitRate = model
    .Derived(links)
    .From(priceRate)
    .DependsOn(
        x => x.InvoiceLine.Quantity,
        x => x.PurchaseOrderLine.OrderedQuantity)
    .Impact(p => p.SourceChanged(DependencySeverity.Invalid))
    .Select((link, rate) => PurchaseOrderInvoiceLine.CalculateUnitRate(link, rate))
    .MaterializeTo(x => x.UnitRate)
    .Named("unit-rate");

var matchingReceipts = model
    .Relation(poLines, receipts)
    .Where((line, receipt) =>
        line.Id == receipt.PurchaseOrderLineId && !receipt.Cancelled)
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
    .Select((link, remaining, rate) => /* calculation */)
    .Named("actual-quantity");

var linkInvariant = model
    .Invariant(links)
    .From(linkValidity)
    .Must((_, valid) => valid)
    .Named("link-validity-invariant")
    .ScheduleRepairWith(Rematch);

var logicalRate = runtime.Evaluate(priceRate, link);
var synchronizedRate = runtime.Materialize(priceRate, link);
runtime.Materialize(link);
Use(link.PriceRate, link.UnitRate);
```

A new reader can distinguish the three roles without engine knowledge: handles in `From` are logical DAG inputs, paths in `DependsOn` are tracked opaque reads, and `MaterializeTo` properties are physical mirrors.

## 2. PriceRate to UnitRate lifecycle

The executable starts with logical and mirrored PriceRate/UnitRate at 6. Changing invoice price from 60 to 55 makes PriceRate and UnitRate Invalid while both properties remain 6. `Evaluate(priceRate, link)` returns 5.5 and leaves `link.PriceRate` at 6. `Evaluate(unitRate, link)` also returns 5.5, proving that it consumes the current logical PriceRate through the handle rather than reading the stale mirror.

`Materialize(link)` then writes both mirrors to 5.5, touches no other object, and does not dispatch repair. Dispatching the mutation result invokes the one pending repair exactly once. Targeted `Materialize(priceRate, link)` returns 5.5 and repairs a deliberately corrupted PriceRate mirror without changing UnitRate.

## 3. `From` versus `DependsOn`

`From(priceRate)` creates a typed value edge and supplies PriceRate as a delegate argument. `DependsOn(x => x.InvoiceLine.Price)` adds change tracking only; it does not add a delegate argument. Mutating either declared nested price caused the opaque `CalculatePriceRate` method to recompute. Omitting those paths made current `Build()` reject the opaque calculator for incomplete dependency tracking.

The concepts did not overlap in the scenario. The one production wrinkle is C# conversion: a method group does not convert directly to `Expression<Func<...>>`. The spike's post-`DependsOn` `Select` accepts `Func<TSource,TValue>` because dependencies are explicit. Production should make this overload/policy intentional rather than relying on a surprising compiler error.

## 4. Cross-object projection

This shape remained readable:

```csharp
.From(x => x.PurchaseOrderLine, remainingQuantity)
.From(unitRate)
.Select((link, remaining, rate) => ...)
```

Ownership and projection direction are visible, and a PO-line quantity change propagated back to the link. A decrease produced Invalid and an increase Dirty according to the member-specific impact policy. The current engine needed an internal projected bridge to combine one projected and one local upstream; this is facade composition, not a new runtime semantic.

The compact alternative `.From(selector, remainingQuantity, unitRate)` was rejected: `remainingQuantity` belongs to the selected PO line while `unitRate` belongs to the link, so putting both in one projected call obscures ownership.

## 5. Recognized aggregates

The declaration contains no `.Incrementally()`. Compiled debug output identified:

```text
IncrementalSum(GoodsReceipt.Quantity)
IncrementalCount
IncrementalLongCount
IncrementalAny
```

All four operators therefore have syntax and existing optimized execution support now. Adding a receipt followed the configured additive/Fresh incremental path. Cancelling it followed the Invalid path and the subsequent evaluation recomputed Sum, Count, LongCount, and Any correctly. No optimized plan was simulated by the facade.

Impact reads most clearly immediately after the relation-valued `From`. Direct impact is less edge-local when several `DependsOn` paths exist; `SourceMemberChanged(path, classifier)` removes the ambiguity, but the production stage should not imply that a bare `Impact` belongs only to the immediately preceding path.

## 6. `MaterializeTo`, `Evaluate`, and `Materialize`

`MaterializeTo` records sink metadata and does not create a source edge. `Evaluate` delegates to logical runtime `Get` and never writes a property. Targeted `Materialize` evaluates and synchronizes one configured representation. Object `Materialize` prepares every configured target physically owned by that registered source object and applies the writes with physical rollback on setter failure; it is not graph traversal or repair dispatch.

The same facade metadata is intentionally bridged into `ConsistencyEfCoreMappings.Materialize`. An in-memory SQLite check showed both PriceRate and UnitRate become EF-modified after object materialization. Production needs a public compiled materialization descriptor/bridge so ordinary declarations do not expose raw handles.

Runtime-only definitions omit `MaterializeTo`. Target type and source ownership are generic constraints; the target parser additionally requires one direct writable property. A second definition targeting the same set/property is rejected at declaration.

## 7. Mirror-property dependency rejection

After PriceRate is registered as a target, `.DependsOn(x => x.PriceRate)` is rejected during `Build()` with a message directing the consumer to `From(price-rate)`. Build-time enforcement sees the complete model and is sufficient for correctness; immediate rejection or an analyzer can later shorten feedback, but an analyzer must not be the only guard.

## 8. Invariant and repair behavior

The invariant remains a separate declaration with explicit `From`, `Must`, and `ScheduleRepairWith` stages. Price mutation produced one repair request. Evaluating and materializing PriceRate/UnitRate did not consume or dispatch it. The normal dispatch boundary ran it exactly once. Pure value synchronization therefore did not acquire hidden business-repair behavior.

## 9. Type-system, diagnostics, and IntelliSense

| Case | Observed/preferred failure |
|---|---|
| T1 incompatible-owner `From` without projection | Generic type failure for different CLR types; declaration-time owner check for distinct same-CLR sets |
| T2 projection selector returns wrong owner type | Generic inference/type failure against the handle owner CLR type; same-CLR set identity remains a model-level concern |
| T3 wrong `MaterializeTo` value type | Compile-time expression type failure |
| T4 target on the wrong source object | Compile-time expression root type failure, reinforced by direct-property parsing |
| T5 unrelated `DependsOn` path | Non-source roots are API-rejected; a valid but semantically unnecessary source path is not preventable |
| T6 registered mirror used in `DependsOn` | Model `Build()` validation with a `From(handle)` fix |
| T7 handle from another model | Declaration-time facade validation |
| T8 same CLR type, different object-set identity | Declaration-time facade validation for direct `From` |

The useful conceptual IntelliSense surface is small:

| Stage | Relevant next operations |
|---|---|
| `Derived(set)` | `DependsOn`, `From`, `Select` |
| `Derived(set).DependsOn(...)` | `Impact`, `Select` |
| `Derived(set).From(value)` | `DependsOn`, `Impact`, `Select` |
| `Derived(set).From(relation)` | `Impact`, `Sum`, `Count`, `LongCount`, `Any` |
| completed derived value | `MaterializeTo`, `Named` |
| `Invariant(set).From(value)` | `Must` |
| completed invariant | `Named`, `ScheduleRepairWith` |

The facade uses focused stages, but not Variant-A-style provider proliferation. Aggregate methods do not leak onto scalar stages.

The logical debug graph omitted adapter-only bridge nodes and rendered the intended vocabulary directly:

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
  from unit-rate
  projection x => x.PurchaseOrderLine -> remaining-quantity
```

## 10. `Select` versus `Compute`

```csharp
.From(priceRate).Select((link, rate) => ...)
.From(priceRate).Compute((link, rate) => ...)
```

`Select` reads naturally after `From`, matches the successful Variant C, and keeps runtime `Evaluate` as the distinct consumption verb. It can suggest collection projection in isolation, but its stage-restricted scalar use and direct `Derived(set).Select(...)` remain understandable. `Compute` is slightly clearer for a direct calculator but introduces a second computation verb beside `Evaluate`. Evidence is not strong enough to rename; retain `Select`.

## 11. `From` versus `Using`

`From` communicates a visible DAG value flow and gives `.From(selector, handle)` a readable projection direction. `Using` is more general but does not say whether the dependency supplies a value or merely influences freshness. Retaining `DependsOn` for opaque reads makes the distinction crisp. Keep `From` for typed logical inputs.

## 12. Production blockers and readiness gates

No runtime-semantic blocker was found. A production implementation still needs deliberate work in four API/compiler areas:

- define the expression-versus-delegate `Select` overload after `DependsOn`;
- support composed `From` plus `DependsOn`, and mixed projected/local `From`, without exposing adapter token/bridge nodes;
- make materialization descriptors and the EF bridge first-class compiled metadata;
- settle exact `Impact` placement and projected same-CLR object-set validation.

These are API/metadata gaps, not requests for a new cache, traversal, aggregate, repair, or materialization engine.

| Gate | Result |
|---|---|
| G1 concise PriceRate declaration with explicit correctness dependencies | Pass |
| G2 UnitRate consumes PriceRate through `From` | Pass |
| G3 stale mirror cannot corrupt UnitRate | Pass |
| G4 materialization metadata drives object materialization | Pass |
| G5 object materialization does not execute repair | Pass |
| G6 Sum selects incremental strategy without `.Incrementally()` | Pass |
| G7 Count/LongCount/Any classified honestly | Pass: all three are optimized today |
| G8 cross-object syntax understandable/type-safe enough | Pass, with same-CLR set validation noted above |
| G9 `DependsOn` distinct from `From` | Pass |
| G10 mirror misuse rejected no later than Build | Pass |
| G11 invariant and repair remain explicit | Pass |
| G12 no universal provider/stage explosion | Pass |
| G13 intentional EF bridge exists | Pass |
| G14 no new engine capability required for syntax | Pass; production metadata/stage adapters remain |

## 13. Recommendation and maintainer decisions

Proceed to a production implementation plan for this Variant-C-derived vocabulary; another broad syntax spike is not warranted. Preserve the semantic split among `From`, `DependsOn`, and `MaterializeTo`.

The remaining decisions are narrow:

1. Keep `Select` (recommended by this evidence) or rename it `Compute`?
2. Keep `From` (recommended) or use `Using` for explicit derived-value flow?
3. Confirm chained `.From(selector, projected).From(local)` as the cross-object shape.
4. Place `Impact` on the derived stage as dogfooded, or introduce dependency-scoped configuration where ambiguity remains?
5. Keep mirror misuse enforcement at Build, optionally adding immediate/analyzer feedback?
6. Put `MaterializeTo` on the derived value as dogfooded, while compiling it into a shared mapping descriptor, or use a separate mapping layer?
7. Ship Sum, Count, LongCount, and Any as first-class recognized operators in the initial API v2 surface?
8. Document a definitions context with properties only as an optional organization pattern?

Run evidence:

```powershell
dotnet run --project experiments/Raffinert.Consistency.FinalApiDogfood/Raffinert.Consistency.FinalApiDogfood.csproj
```
