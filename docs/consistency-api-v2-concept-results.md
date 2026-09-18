# Consistency API v2 concept results

Status: design evidence only. No production API is selected or implemented.

The disposable project at
`experiments/Raffinert.Consistency.ApiV2Concept` compiled with zero warnings and
executed Current plus Variants A-D against one shared S1-S9 harness. Every
candidate translated to the existing builder, compiled model, runtime, and EF
adapter; none implemented runtime behavior.

## Usability matrix

| Criterion | Current | Variant A | Variant B | Variant C | Variant D |
|---|---|---|---|---|---|
| S1 simple derived readability | One owner plus one computation; shortest direct form | `Value + Combine + Select` exposes graph but creates two intermediate nodes | `Select + Combine + Select` is graph-like but `Select` can imply collection projection | Direct `Derived(set).Select` stays short; composed form uses `From` | Named properties aid later use; constructor helper calls add ceremony |
| Relation readability | Explicit `model.Relation(left,right).Where` | `left.RelateTo(right).Where` reads directionally | `left.Relate(right).Where` is compact and domain-specific | Keeps current relation form | `Relate(Lines,Fulfillments,name,predicate)` is explicit but argument-heavy |
| Relation orientation obvious | Constructor order plus `Using` source | `PerLeft().Sum` is unambiguous | `SumByLeft` is unambiguous in one operator | Source-owned `Derived(lines).From(relation).Sum` makes left ownership visible | Helper name `SumByLeft` plus typed relation property |
| Derived DAG visually obvious | `Using(upstream)` exposes edges but computation-oriented builder vocabulary dominates | `Combine` makes edges prominent | `ObjectValue.Combine` makes edges prominent while retaining ownership | `From(first,second)` states source and edges | Property references make node reuse prominent |
| Cross-object projection clarity | Typed `Using(selector, first, second)` is precise but dense | `remaining.For(allocations, selector).Combine(rate)` reads naturally; generic type is complex | `allocations.SelectWith(selector, remaining, rate)` places ownership most clearly | `Derived(allocations).From(selector, remaining, rate)` is explicit and close to current | `Project(Allocations,...,selector,values,...)` is clear but positional |
| Nested dependency clarity | `DependsOn` before `Compute` clearly augments opaque code | Same input-first declaration under `SourceProvider` | Same input-first declaration under a typed set | Nearly identical to current, ending in `Select` | Helper chain retains explicit paths; property organization adds no special benefit |
| Dirty/Invalid policy locality | Impact sits between input selection and computation | Relation impact is on `PerLeft`; transition impact is on the member value node | Relation impact is on `DomainRelation`; transition impact is on the object value edge | Relation impact follows `From(relation)`; direct impact follows source selection | Impact is an explicit helper argument and can be far from a long computation |
| Invariant/repair clarity | Distinct `Invariant + Using + Must + ScheduleRepairWith` | Distinct invariant provider and repair policy | Distinct domain invariant and repair policy | Keeps current concepts | Distinct context property, but reaction is constructed in one helper call |
| EF separation | Separate adapter is established and executable | Raw handles are retained internally; mappings remain separate | Same | Same with smallest adapter mismatch | Context exposes internal handles; mappings remain separate |
| Stable naming | Explicit `.Named(string)` everywhere | Explicit `.Named`; no variable magic | Explicit `.Named`; no variable magic | Explicit `.Named`; no variable magic | Explicit constructor strings; property names are not silently durable identity |
| Type inference quality | Strong for current supported arities | Works, but projected/combined providers produce long inferred types | Works with types named after domain semantics | Works and stays close to current builder types | Works inside helper calls; constructor diagnostics can be positional |
| IntelliSense discoverability | Builders stage valid next operations | Small provider vocabulary is easy initially, then stage types multiply | Domain node methods expose relevant operations | Familiar current stages plus a few operations | Context base helpers are discoverable during construction; properties aid consumers |
| Compiler error quality | Errors mention `Derived`, `Relation`, and owner types | Errors can become provider-type puzzles | Errors mention `ObjectValue`, relation orientation, and owner types | Errors remain close to current domain concepts | Generic helper errors are precise but can identify argument positions rather than a fluent stage |
| Generic type noise | Moderate, mostly hidden in locals | Highest: projection/combine stages carry up to four semantic type parameters | Moderate and domain-labelled | Lowest new generic noise | Properties show concise final types; helper implementations remain generic |
| Runtime semantics leakage | Mutation APIs remain separate | None in declaration | None in declaration | None in declaration | None in declaration |
| Incremental-strategy leakage | `.Incrementally()` is visible | Hidden behind `PerLeft().Sum` | Hidden behind `SumByLeft` | Hidden behind `From(relation).Sum` | Hidden inside `SumByLeft` helper |
| Migration cost | None | Largest declaration rewrite | Large declaration rewrite and new node vocabulary | Smallest; preserves builder ownership and policy shapes | Large organizational rewrite into context classes |
| New public types required | None | Provider, combined, projected, relation-group, and invariant stages | Typed set/value/relation/projected/invariant stages | A small number of derived/aggregate stages or methods | Context base plus node/property types; source generation is not required |
| New engine capability required | None | None for S1-S9 | None for S1-S9 | None for S1-S9 | None for S1-S9 |

## Runtime evidence

For each of the five declarations, the executable harness verified:

- direct and composed values produce the same results;
- the relation-backed `Sum` reports
  `IncrementalSum(Fulfillment.Quantity)` in the compiled debug view even though
  candidate syntax does not expose `.Incrementally()`;
- cancellation produces Invalid aggregate state;
- quantity increase produces Dirty state and decrease produces Invalid state;
- `Allocation -> OrderLine` reverse routing reaches allocation validity and its
  repair policy;
- one detailed repair request is emitted and dispatch invokes the configured
  callback;
- a shared nested `SourceItem` mutation updates every associated mirror;
- retargeting an association moves reverse routing to the new source;
- EF materializes `Association.UnitRate` and blocks an invariant-violating save;
- explicit definition keys survive compilation.

The experiment did not add manual invalidation or special-case assertions per
variant.

## New-user reading checks

### Current

```csharp
var matching = model.Relation(lines, fulfillments).Where(Matches).Named("matching-fulfillments");
var fulfilledQuantity = model.Derived(lines).Using(matching)
    .Impact(p => p.MembershipAdded(Dirty).MembershipRemoved(Invalid).ItemChanged(Invalid))
    .Incrementally().Compute((_, rows) => rows.Sum(x => x.Quantity)).Named("fulfilled-quantity");
var remainingQuantity = model.Derived(lines).Using(fulfilledQuantity)
    .Impact(p => p.SourceMemberChanged(x => x.OrderedQuantity, IncreaseDirtyDecreaseInvalid))
    .Compute((line, fulfilled) => line.OrderedQuantity - fulfilled).Named("remaining-quantity");
var allocationValidity = model.Derived(allocations)
    .Using(x => x.OrderLine, remainingQuantity, unitRate)
    .Compute((allocation, remaining, rate) => allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate)
    .Named("allocation-validity");
var allocationInvariant = model.Invariant(allocations).Using(allocationValidity)
    .Must((_, valid) => valid).Named("allocation-validity-invariant")
    .ScheduleRepairWith(RepairAllocation);
```

### Variant A

```csharp
var matching = lines.RelateTo(fulfillments).Where(Matches).Named("matching-fulfillments");
var fulfilledQuantity = matching.PerLeft()
    .WithImpact(p => p.MembershipAdded(Dirty).MembershipRemoved(Invalid).ItemChanged(Invalid))
    .Sum(x => x.Quantity).Named("fulfilled-quantity");
var orderedQuantity = lines.Value(x => x.OrderedQuantity,
    p => p.SourceMemberChanged(x => x.OrderedQuantity, IncreaseDirtyDecreaseInvalid));
var remainingQuantity = orderedQuantity.Combine(fulfilledQuantity)
    .Select((_, ordered, fulfilled) => ordered - fulfilled).Named("remaining-quantity");
var allocationValidity = remainingQuantity.For(allocations, x => x.OrderLine)
    .Combine(unitRate).Select((allocation, remaining, rate) =>
        allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate)
    .Named("allocation-validity");
var allocationInvariant = model.Invariant(allocations, allocationValidity)
    .Must((_, valid) => valid).Named("allocation-validity-invariant")
    .ScheduleRepairWith(RepairAllocation);
```

### Variant B

```csharp
var matching = lines.Relate(fulfillments).Where(Matches).Named("matching-fulfillments");
var fulfilledQuantity = matching
    .WithImpact(p => p.MembershipAdded(Dirty).MembershipRemoved(Invalid).ItemChanged(Invalid))
    .SumByLeft(x => x.Quantity).Named("fulfilled-quantity");
var orderedQuantity = lines.Select(x => x.OrderedQuantity,
    p => p.SourceMemberChanged(x => x.OrderedQuantity, IncreaseDirtyDecreaseInvalid));
var remainingQuantity = orderedQuantity.Combine(fulfilledQuantity)
    .Select((_, ordered, fulfilled) => ordered - fulfilled).Named("remaining-quantity");
var allocationValidity = allocations.SelectWith(x => x.OrderLine, remainingQuantity, unitRate)
    .Select((allocation, remaining, rate) =>
        allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate)
    .Named("allocation-validity");
var allocationInvariant = model.Invariant(allocations, allocationValidity)
    .Must((_, valid) => valid).Named("allocation-validity-invariant")
    .ScheduleRepairWith(RepairAllocation);
```

### Variant C

```csharp
var matching = model.Relation(lines, fulfillments).Where(Matches).Named("matching-fulfillments");
var fulfilledQuantity = model.Derived(lines).From(matching)
    .Impact(p => p.MembershipAdded(Dirty).MembershipRemoved(Invalid).ItemChanged(Invalid))
    .Sum(x => x.Quantity).Named("fulfilled-quantity");
var orderedQuantity = model.Derived(lines).Select(x => x.OrderedQuantity,
    p => p.SourceMemberChanged(x => x.OrderedQuantity, IncreaseDirtyDecreaseInvalid));
var remainingQuantity = model.Derived(lines).From(orderedQuantity, fulfilledQuantity)
    .Select((_, ordered, fulfilled) => ordered - fulfilled).Named("remaining-quantity");
var allocationValidity = model.Derived(allocations)
    .From(x => x.OrderLine, remainingQuantity, unitRate)
    .Select((allocation, remaining, rate) =>
        allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate)
    .Named("allocation-validity");
var allocationInvariant = model.Invariant(allocations, allocationValidity)
    .Must((_, valid) => valid).Named("allocation-validity-invariant")
    .ScheduleRepairWith(RepairAllocation);
```

### Variant D

```csharp
MatchingFulfillments = Relate(Lines, Fulfillments, "matching-fulfillments", Matches);
FulfilledQuantity = SumByLeft(MatchingFulfillments, "fulfilled-quantity", x => x.Quantity,
    p => p.MembershipAdded(Dirty).MembershipRemoved(Invalid).ItemChanged(Invalid));
var orderedQuantity = Select(Lines, "ordered-quantity", x => x.OrderedQuantity,
    p => p.SourceMemberChanged(x => x.OrderedQuantity, IncreaseDirtyDecreaseInvalid));
RemainingQuantity = Combine(Lines, "remaining-quantity", orderedQuantity, FulfilledQuantity,
    (_, ordered, fulfilled) => ordered - fulfilled);
AllocationValidity = Project(Allocations, "allocation-validity", x => x.OrderLine,
    RemainingQuantity, UnitRate,
    (allocation, remaining, rate) => allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate,
    p => p.SourceChanged(Invalid));
AllocationInvariant = Invariant(Allocations, AllocationValidity,
    "allocation-validity-invariant", RepairAllocation);
```

| Visible question | Current | A | B | C | D |
|---|---|---|---|---|---|
| Root sets | Must read earlier declarations | Must read earlier declarations | Must read earlier declarations | Must read earlier declarations | Capitalized properties imply nodes, but root status still requires declarations |
| Related objects | `Relation(lines, fulfillments)` | `lines.RelateTo(fulfillments)` | `lines.Relate(fulfillments)` | `Relation(lines, fulfillments)` | `Relate(Lines, Fulfillments)` |
| Calculated per OrderLine | Ownership is explicit in each `Derived(lines)` | `PerLeft`, then provider ownership is inferred | `SumByLeft` and typed values | Explicit in every `Derived(lines)` | Helper source argument/property type |
| Depends on FulfilledQuantity | `Using(fulfilledQuantity)` | `Combine(fulfilledQuantity)` | `Combine(fulfilledQuantity)` | `From(...fulfilledQuantity)` | `Combine(...FulfilledQuantity)` |
| Allocation reaches OrderLine | Typed `Using` selector | `remaining.For(allocations, selector)` | `allocations.SelectWith(selector,...)` | `Derived(allocations).From(selector,...)` | `Project(Allocations,...selector,...)` |
| Dirty versus Invalid | Visible next to relation/source dependency | Visible on relation/member providers | Visible on domain relation/member value | Visible directly after `From`/source selection | Visible, but policy is a helper argument |
| Rule schedules repair | Explicit final method | Explicit final method | Explicit final method | Explicit final method | Hidden inside the `Invariant` helper convention; readability cost |

The blocks show one convention cost shared by every fluent option: root-set
creation is not recoverable from S3-S7 declarations alone. Variant D adds a
second hidden convention: its helper performs both `Must` and repair scheduling.
That compactness should not be copied without making reactions visible.

## Type-system and diagnostics findings

Detailed misuse examples are recorded in
`experiments/Raffinert.Consistency.ApiV2Concept/TYPE-SYSTEM-DIAGNOSTICS.md`.
Different CLR source types, wrong projection targets, unrelated transition
members, and most wrong materialization targets fail at compile time. Same-CLR
type/different-set ownership and cross-model handles are rejected by the current
builder during declaration. Cycles remain a `Build()` concern if a future API
introduces forward references.

Variant A has the least helpful errors because stage types describe providers
rather than domain roles. B and C preserve owner and relation concepts in error
types. Eliminating all runtime identity checks would require generated nominal
types per set and was not justified by this experiment.

## Gap classification

| Gap | Classification | Evidence |
|---|---|---|
| Hide `.Incrementally()` behind recognized aggregates | SYNTAX-ONLY | Every facade calls the current builder and gets the existing incremental Sum plan. |
| Preserve same-CLR-type object-set identity in C# generics | TYPE-SYSTEM | Generic owner type cannot encode runtime set identity; builder validation remains necessary. |
| Deliberate EF interop from candidate wrappers | PUBLIC-FACADE | The concept can retain raw handles internally; a production wrapper needs a supported adapter bridge. |
| Forward references/cycles with fluent construction | TYPE-SYSTEM | Executable prototypes are one-pass; descriptors would defer cycle validation to `Build()`. |
| S1-S9 runtime meaning | No gap | Existing engine expressed and executed every required scenario. |

## Optimization-language review

`Dirty` and `Invalid` are required semantic contracts. `Incrementally` is an
optional execution request in the current API and implementation leakage in the
candidate declaration. `Cache`, `Recompute`, `Scan`, and `Index` do not appear in
candidate declarations. Candidate `Sum` selects the current optimized plan
through its adapter. The same facade principle can cover recognized `Count`,
`LongCount`, and `Any`; production work would need tests for each operator before
claiming full coverage.

## Hypotheses

| Hypothesis | Outcome | Evidence |
|---|---|---|
| H1: `Select`/`Combine` clarify derived DAG edges | Supported with qualification | A/B make edges visually prominent; C's `From` is similarly clear with less provider ceremony. |
| H2: relations need Raffinert-specific typed operators | Supported | `PerLeft`/`SumByLeft` preserve orientation and a place for membership impact; generic `Combine` alone cannot. |
| H3: recognized aggregates can hide strategy selection | Supported for Sum | All facades omit `.Incrementally()` while the compiled model reports `IncrementalSum`. Count/LongCount/Any were not executed. |
| H4: `DependsOn` remains useful for opaque code | Supported | The opaque UnitRate calculator required explicit nested dependency paths in every syntax. |
| H5: cross-object projection is the hardest API case | Supported | It required dedicated `For`, `SelectWith`, `From(selector,...)`, or `Project` stages; ordinary `Combine` was insufficient. |
| H6: invariant/repair should remain distinct from pure computation | Supported | Separate invariant nodes and reactions executed cleanly; Variant D's compressed helper reduced readability. |
| H7: EF and authoritative scope should remain adapter-level | Supported | Materialization, enforcement, discovery, and completeness executed without entering any core pipeline syntax. |
| H8: a hybrid may deliver much of the gain with less churn | Supported as an implementation fact, not a winner | C removed aggregate strategy leakage and exposed composition while retaining current ownership/policy concepts and requiring the fewest new shapes. |
| H9: candidates benefit from an explicit semantic IR | NOT TESTED | Optional Task 276 was not executed. |
| H10: stable keys plus bindings enable portability | NOT TESTED | Optional Task 276 was not executed. |
| H11: harmful YAML pressure should be rejected | NOT TESTED | Optional Task 276 was not executed. |

## Facts established by dogfooding

The existing engine and public builder can express S1-S9 without a semantic
extension. Typed facade composition can hide aggregate execution strategy while
retaining the current incremental plan. Relations, projected dependencies,
impact policy, invariants, and EF boundaries cannot be collapsed into one honest
generic value-provider abstraction. Explicit stable strings remain necessary.

All candidates compiled and executed. That establishes feasibility, not API
quality or compatibility.

## Trade-offs that remain subjective

`Value` versus `Select`, `PerLeft().Sum` versus `SumByLeft`, and provider-owned
`For` versus consumer-owned `SelectWith` remain naming/readability choices.
Whether context properties justify constructor ceremony also remains subjective.
The experiment provides examples but does not select a winner.

## Capabilities every viable API must preserve

A viable API must retain typed object-set ownership, relation orientation and
pair impacts, nested dependency completeness, derived DAG edges, typed projected
reverse routing, Dirty/Invalid and transition policies, distinct invariants and
repair reactions, explicit durable names, and separate EF mapping/discovery/scope
configuration.

## Smallest viable production change

Keep the current builder and add explicit recognized aggregate operators after a
relation input, plus narrowly scoped typed composition aliases where they make
DAG edges clearer. This corresponds to the Variant C pressure test and would
need a separate production plan, API baselines, XML docs, migration examples,
operator tests for Sum/Count/LongCount/Any, and package-consumer coverage.

## Largest coherent production change

Introduce a Raffinert-specific typed domain pipeline comparable to Variant B,
with object values, oriented relation aggregates, projected consumer operations,
and explicit invariant types. A universal provider layer larger than this did
not add semantic capability and produced less domain-oriented diagnostics.

## Open questions requiring maintainer decision

- Should the first public API optimize for minimum migration (C) or a complete
  typed vocabulary (B)?
- Is `SelectWith` clearer than `For` for projected cross-object dependencies?
- Should explicit aggregate operators imply optimized planning unconditionally,
  with tuning available only as an advanced override?
- How should a production facade expose handles to EF adapters without leaking
  implementation wrappers?
- Should context/property organization be offered independently of model syntax?
- Is an explicit semantic IR worth a separate experiment before any public API
  is selected?
