# Allocation Consistency Dogfood — implementation plan

## Purpose

Build a deliberately small but demanding neutral-domain dogfood for Raffinert.Consistency. The sample must exercise the parts that distinguish the library from a computed-property helper or ordinary validation framework:

1. relation discovery;
2. relation membership changes;
3. derived state;
4. transitive derived dependencies;
5. invariant validation;
6. directional/asymmetric impact (`Dirty` vs `Invalid` where the current API supports it);
7. invalidation of an existing relationship;
8. deferred repair/reallocation;
9. EF Core persistence integration;
10. causal/explanation diagnostics where already supported;
11. comparison of an anemic mutation model with a richer domain model.

This is dogfood, not a showcase. Do not hide awkward API usage. Do not add new core abstractions merely to make the sample pretty. If the current public API cannot express a scenario cleanly, record the gap explicitly.

## Non-goals

Do NOT:

- copy invoice, purchase-order, goods-receipt terminology;
- implement pricing, money, discounts, units, conversion rates, or accounting;
- build a UI or HTTP API;
- introduce MediatR, AutoMapper, CQRS infrastructure, repositories, event buses, or unrelated architecture;
- redesign Raffinert.Consistency before the sample proves a concrete gap;
- make repair magically happen inside entity property setters;
- turn Raffinert into the owner of business decisions;
- add generic `Required`, string-length, email-address, or other ordinary input validation;
- fake `Dirty`/`Invalid` behavior in sample code if the library does not support the required semantics;
- make a broad README rewrite in this task.

## Neutral domain

Use these names unless an existing sample convention makes a tiny naming adjustment necessary:

- `Demand` — a request for a quantity of a resource in a time bucket.
- `Supply` — available capacity for that resource/time bucket.
- `Allocation` — an explicit link assigning a `Demand` to a `Supply` with an allocated quantity.
- `Fulfillment` — quantity already consumed/fulfilled against a `Supply`.

The conceptual mapping is intentionally generic:

```text
Demand ---- Allocation ----> Supply ---- Fulfillment
```

The sample must make sense without knowing the real-world domain that motivated it.

## Minimal entity model

Start with ordinary POCOs. Keep IDs simple and stable (for example `int`). Use `decimal` quantities unless existing sample conventions strongly prefer another numeric type.

Suggested shape:

```csharp
public sealed class Demand
{
    public int Id { get; set; }
    public string ResourceCode { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal RequestedQuantity { get; set; }
}

public sealed class Supply
{
    public int Id { get; set; }
    public string ResourceCode { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Capacity { get; set; }

    // Physical mirrors only if the dogfood intentionally exercises MaterializeTo.
    public decimal FulfilledQuantity { get; set; }
    public decimal AllocatedQuantity { get; set; }
    public decimal RemainingCapacity { get; set; }
}

public sealed class Allocation
{
    public int Id { get; set; }
    public int DemandId { get; set; }
    public Demand Demand { get; set; } = null!;
    public int SupplyId { get; set; }
    public Supply Supply { get; set; } = null!;
    public decimal Quantity { get; set; }
}

public sealed class Fulfillment
{
    public int Id { get; set; }
    public int SupplyId { get; set; }
    public Supply Supply { get; set; } = null!;
    public decimal Quantity { get; set; }
}
```

Do not add properties unless a scenario below requires them.

## Business semantics

### Candidate supply relation

A `Supply` is a candidate for a `Demand` when both match:

```text
Demand.ResourceCode == Supply.ResourceCode
Demand.Date         == Supply.Date
```

Represent this with a Raffinert relation, not with hand-written sample orchestration.

Expected conceptual definition:

```csharp
var candidateSupplies = model.Relation(demands, supplies)
    .Where((demand, supply) =>
        demand.ResourceCode == supply.ResourceCode &&
        demand.Date == supply.Date);
```

Use the actual current API after inspecting repository source/tests. Do not invent an API because this document contains pseudocode.

### Derived fulfilled quantity

For each `Supply`:

```text
FulfilledQuantity = Sum(Fulfillment.Quantity for that Supply)
```

This must be relation-backed/incremental if the existing API supports the exact aggregate plan.

### Derived allocated quantity

For each `Supply`:

```text
AllocatedQuantity = Sum(Allocation.Quantity for that Supply)
```

### Derived remaining capacity

Use this definition consistently:

```text
RemainingCapacity = Capacity - FulfilledQuantity - AllocatedQuantity
```

This is deliberately a downstream derived value consuming other derived values. It must exercise transitive dependency propagation rather than independently re-reading all underlying collections.

### Capacity invariant

The core cross-object invariant is:

```text
FulfilledQuantity + AllocatedQuantity <= Capacity
```

Equivalent `RemainingCapacity >= 0` is mathematically valid, but prefer the explicit business expression if the API permits it because the failure is easier to understand.

Do not replace this with ordinary setter validation. The purpose is to prove that Raffinert identifies which invariant is affected when upstream state changes.

### Allocation compatibility invariant

An existing allocation must also remain semantically compatible with its linked supply:

```text
Allocation.Demand.ResourceCode == Allocation.Supply.ResourceCode
Allocation.Demand.Date         == Allocation.Supply.Date
```

Prefer expressing this through the candidate relation/current relation machinery if the API supports it cleanly. Do not duplicate matching logic in three places merely to satisfy the sample. If the current API makes relation membership difficult to consume as an invariant, record that as a dogfood finding.

## Repair semantics

Raffinert must determine that repair is required; the repair handler owns the business decision.

Create a deliberately simple repair operation named approximately:

```text
ReallocateDemand
```

It should:

1. receive enough identity/context to locate the affected demand/allocation;
2. find candidate supplies using the same business compatibility definition;
3. exclude the currently invalid supply when appropriate;
4. choose a deterministic replacement using a boring rule (for example lowest `Supply.Id` with sufficient available capacity);
5. replace/update the allocation;
6. fail explicitly or leave a clearly represented unresolved state if no replacement exists.

Do NOT put ranking sophistication into this sample. The point is repair scheduling and boundary ownership, not matching quality.

Important architectural rule:

```text
Raffinert: "this allocation now requires repair"
Application/domain repair code: "this is how we choose the replacement"
```

Do not move the second responsibility into the consistency engine.

## Phase 0 — inspect before changing anything

Before implementation:

1. Read the root `README.md` sections for relations, derived values, invariants, repair, EF integration, diagnostics, and dogfood guidance.
2. Inspect `samples/`, `experiments/`, and relevant tests.
3. Search for current usages of:
   - `Relation(`
   - `Derived(`
   - `.From(`
   - `.DependsOn(`
   - `.Impact(`
   - `Invariant(`
   - `ScheduleRepairWith`
   - `MaterializeTo`
   - `AddRaffinertConsistency`
   - `SaveChangesConsistentlyAsync`
   - `ConsistencyScope`
4. Reuse public API patterns already proven by tests/samples.
5. Do not depend on internal types from the sample just because they make implementation easier.
6. Decide whether this belongs under `samples/` or `experiments/` based on existing repository conventions. Prefer a sample only if it can remain understandable and stable; otherwise put initial dogfood under `experiments/`.

Write down any mismatch between this plan and the actual current API before attempting framework changes.

## Phase 1 — create the smallest runnable domain

Create one runnable project and its tests. Suggested project name:

```text
Raffinert.Consistency.Allocation.Sample
```

or, if placed under experiments:

```text
Raffinert.Consistency.Allocation.Dogfood
```

Requirements:

- target the same supported .NET version used by the relevant existing sample;
- reference projects/packages consistently with repository conventions;
- keep all domain classes small;
- provide deterministic seed data;
- build before adding Raffinert definitions;
- add the project to the solution if repository conventions require it.

Do not proceed until the new project builds.

## Phase 2 — encode candidate relation only

Implement object sets for `Demand` and `Supply` with stable keys.

Implement `candidateSupplies` from `ResourceCode` + `Date`.

Add tests before adding derived values:

1. matching code/date produces membership;
2. different resource does not match;
3. different date does not match;
4. changing demand resource removes old candidate and adds new candidate where appropriate;
5. changing supply resource/date updates reverse membership;
6. unrelated property changes do not change membership.

Where supported, assert diagnostics/execution plan indicates indexed/exact relation handling rather than relying only on final results. Do not assert implementation details that are intentionally unstable.

Checkpoint: candidate relation works independently.

## Phase 3 — fulfillment relation and aggregate

Define the relation between `Supply` and `Fulfillment` using stable identity/reference semantics supported by the library.

Define `fulfilledQuantity` as `Sum(Fulfillment.Quantity)`.

Test at minimum:

1. zero fulfillments => zero;
2. add fulfillment => sum increases;
3. increase fulfillment quantity => sum increases;
4. decrease fulfillment quantity => sum decreases;
5. remove fulfillment => sum decreases;
6. fulfillment moved from one supply to another affects both supplies;
7. unrelated fulfillment does not affect another supply.

If exact incremental `Sum` is expected, add a test/diagnostic assertion proving the intended execution plan is selected.

Checkpoint: one relation-backed aggregate works correctly.

## Phase 4 — allocation relation and aggregate

Define `Supply` -> `Allocation` relation.

Define `allocatedQuantity` as sum of allocation quantities.

Add the same add/change/remove/move tests as for fulfillment.

Also verify a single `Allocation` remains an explicit persisted/domain relationship; Raffinert must not replace it with an inferred relation.

Checkpoint: two independent aggregate inputs feed supply state.

## Phase 5 — transitive derived state

Define `remainingCapacity` from:

- `Supply.Capacity`;
- `fulfilledQuantity`;
- `allocatedQuantity`.

Do not calculate it by independently querying fulfillments/allocations again. This phase must prove derived-from-derived propagation.

Test:

1. capacity change affects remaining capacity;
2. fulfillment add/change/remove affects it transitively;
3. allocation add/change/remove affects it transitively;
4. unrelated supply remains unaffected;
5. repeated evaluation without changes returns stable results/caching behavior expected by current API.

If `MaterializeTo` is used, explicitly test logical-vs-physical behavior:

```text
Evaluate -> current logical value, mirror unchanged
Materialize -> configured mirror synchronized
```

Checkpoint: the graph contains a real multi-level dependency chain.

## Phase 6 — capacity invariant

Add the invariant:

```text
fulfilledQuantity + allocatedQuantity <= supply.Capacity
```

Use current invariant APIs, not a hand-written `if` in the save path.

Test these exact examples:

### Safe baseline

```text
Capacity = 10
Fulfilled = 3
Allocated = 5
Result: valid, Remaining = 2
```

### Capacity increase

```text
Capacity: 10 -> 15
Result: invariant remains valid
```

### Capacity decrease still valid

```text
Capacity: 10 -> 9
Fulfilled + Allocated = 8
Result: valid
```

### Capacity decrease invalid

```text
Capacity: 10 -> 6
Fulfilled + Allocated = 8
Result: invariant violation before durable inconsistent state is accepted
```

### Fulfillment increase invalid

```text
Capacity = 10
Allocated = 5
Fulfilled: 3 -> 7
Result: 12 > 10, invariant violation
```

### Allocation increase invalid

Use the symmetric scenario for allocation.

Checkpoint: invariant validation is dependency-driven and not manually called by every mutation path.

## Phase 7 — asymmetric impact (`Dirty` vs `Invalid`)

This phase is important. Do not fake it.

Desired semantics:

- increasing `Supply.Capacity` cannot make the capacity invariant worse;
- decreasing `Supply.Capacity` can make it false;
- increasing fulfillment consumes capacity and can make it false;
- decreasing fulfillment releases capacity and cannot make it worse;
- increasing allocation consumes capacity and can make it false;
- decreasing allocation releases capacity and cannot make it worse.

Map these onto the existing typed transition/source-member impact API if supported.

Conceptual matrix:

| Mutation | Desired urgency |
| --- | --- |
| Capacity increases | Dirty / lazy recomputation acceptable |
| Capacity decreases | Invalid / must revalidate before unsafe use/commit |
| Fulfillment increases | Invalid |
| Fulfillment decreases | Dirty |
| Allocation increases | Invalid |
| Allocation decreases | Dirty |

Tests must verify behavior, not only configuration metadata.

If current Raffinert cannot express one or more rows without awkward custom code:

1. do not redesign immediately;
2. create a dogfood finding document/section;
3. state the exact missing semantic capability;
4. keep the simplest correct conservative behavior (usually `Invalid`) until a separate decision is made.

Checkpoint: we know whether directional impact is genuinely usable in a realistic aggregate graph.

## Phase 8 — allocation compatibility and relinking trigger

Create an initial valid allocation:

```text
Demand D1: Resource=A, Date=day1, Quantity=5
Supply S1: Resource=A, Date=day1, Capacity=10
Supply S2: Resource=A, Date=day1, Capacity=10
Allocation A1: D1 -> S1, Quantity=5
```

Then mutate compatibility:

Scenario A:

```text
S1.ResourceCode: A -> B
```

Expected:

- `S1` disappears from D1 candidate relation;
- existing `A1` is no longer compatible;
- the consistency graph identifies the affected allocation/invariant;
- repair is scheduled/represented;
- repair can choose S2.

Scenario B:

```text
D1.Date: day1 -> day2
```

Expected equivalent invalidation/reallocation behavior.

Do not implement a sample-specific observer that manually says `if ResourceCode changed then repair`. The relation/dependency model must be the reason the consequence is discovered.

Checkpoint: relation membership changes can invalidate an already-persisted relationship.

## Phase 9 — capacity-driven reallocation

Test the harder case that motivated the dogfood.

Initial state:

```text
S1 Capacity = 10
D1 allocated to S1 for 5
S2 compatible and has enough free capacity
```

Mutation:

```text
S1 Capacity: 10 -> 3
```

Expected semantics:

1. capacity-derived state becomes unsafe/current allocation no longer fits;
2. affected invariant is identified;
3. repair requirement is produced;
4. repair handler selects S2 deterministically;
5. after repair, A1 points to S2 (or is replaced according to chosen domain model);
6. derived totals for both S1 and S2 become correct;
7. invariants pass after repair;
8. no unrelated demand/supply is recalculated/repaired unnecessarily.

Also test no-replacement case:

```text
S1 becomes insufficient
S2 also insufficient
```

Expected behavior must be explicit and safe. Do not silently keep an allocation that the model declares invalid.

Checkpoint: the sample proves invalid existing links + deferred business repair, not merely recalculation.

## Phase 10 — EF Core integration

Only after core runtime behavior works, persist the same model with EF Core using repository-standard provider/test setup.

Use the existing `Raffinert.Consistency.EntityFrameworkCore` integration and DI path where practical.

Test ordinary tracked mutation:

```csharp
supply.Capacity = 6;
await db.SaveChangesAsync(cancellationToken);
```

Verify configured invariants/derived mirrors behave according to current adapter guarantees.

Cover:

1. clean load -> mutation -> save;
2. materialized property read before save using `IConsistencyRuntime.Materialize(entity)` only where required;
3. saving without pre-read does not require manual materialization;
4. failed invariant prevents durable inconsistent save according to existing enforcement semantics;
5. failed SQL save does not commit runtime baseline/state;
6. retry rebuilds/uses a valid plan as current implementation specifies;
7. loading/tracking additional mapped objects after materialization follows existing pending-plan invalidation rules.

Do not introduce a second manual change-tracking mechanism in the sample.

## Phase 11 — authoritative scope test

This is intentionally a stress test of the difficult part of EF integration.

Create a scenario where a supply has multiple allocations/fulfillments and only a subset is initially tracked.

Prove one of these explicitly:

- the operation is rejected because authoritative completeness is not proven; or
- the sample establishes `ConsistencyScope.Complete(...)` only after loading all required members.

Never let the sample imply that an in-memory sum is authoritative merely because EF currently tracks some rows.

Document the rule in sample comments/README:

```text
Tracked != complete.
```

Checkpoint: the dogfood does not accidentally demonstrate unsafe partial-graph behavior.

## Phase 12 — richer-domain variant

After the anemic version works, create a small variant or focused tests where local mutation authority moves into domain methods.

For example:

```csharp
public void ChangeCapacity(decimal newCapacity)
{
    if (newCapacity < 0)
        throw new ArgumentOutOfRangeException(nameof(newCapacity));

    Capacity = newCapacity;
}
```

Application code becomes:

```csharp
supply.ChangeCapacity(6);
await db.SaveChangesAsync(cancellationToken);
```

Keep local primitive validation (`capacity >= 0`) in the domain method.

Do NOT move the graph-dependent capacity invariant into the method by manually loading/summing every allocation and fulfillment merely to make the model look "DDD".

Evaluate whether Raffinert remains responsible for useful work:

- transitive derived state;
- identifying affected cross-object invariant;
- relation membership consequences;
- repair requirement;
- persistence synchronization.

The purpose is to test the architectural thesis:

```text
Rich domain model owns local business behavior.
Raffinert owns dependency/consequence knowledge.
```

If this split feels artificial in implementation, record that finding rather than hiding it.

## Phase 13 — diagnostics / explainability

Use existing public diagnostics APIs where available. Do not add a new diagnostics subsystem in this task.

For at least one failing capacity scenario, attempt to expose a causal explanation equivalent to:

```text
Supply.Capacity changed 10 -> 6
  -> RemainingCapacity affected
  -> CapacityInvariant affected
  -> Fulfilled(3) + Allocated(5) > Capacity(6)
  -> repair/rejection required
```

For compatibility invalidation:

```text
Supply.ResourceCode changed A -> B
  -> candidate relation membership D1/S1 removed
  -> Allocation A1 no longer compatible
  -> ReallocateDemand required
```

If current diagnostics cannot provide useful causal information, record the exact missing information as a finding. Do not parse private/internal state from the sample.

## Phase 14 — acceptance scenario suite

Create clearly named integration tests. At minimum cover:

1. `CandidateSupply_IsFound_WhenResourceAndDateMatch`
2. `CandidateSupply_IsRemoved_WhenResourceStopsMatching`
3. `FulfilledQuantity_TracksFulfillmentChanges`
4. `AllocatedQuantity_TracksAllocationChanges`
5. `RemainingCapacity_UsesDerivedFulfilledAndAllocatedValues`
6. `CapacityIncrease_DoesNotCreateInvariantViolation`
7. `CapacityDecrease_RevalidatesAndRejectsViolation`
8. `FulfillmentIncrease_CanInvalidateCapacityInvariant`
9. `FulfillmentDecrease_ReleasesCapacityWithoutUrgentRepair`
10. `AllocationIncrease_CanInvalidateCapacityInvariant`
11. `AllocationDecrease_ReleasesCapacityWithoutUrgentRepair`
12. `CompatibilityChange_InvalidatesExistingAllocation`
13. `InvalidAllocation_CanBeRepairedToAnotherCandidateSupply`
14. `InsufficientCapacity_CanTriggerReallocation`
15. `NoReplacementSupply_DoesNotSilentlyAcceptInvalidAllocation`
16. `UnrelatedSupplyMutation_DoesNotAffectOtherGraph`
17. `Evaluate_DoesNotWriteMaterializedMirror`
18. `Materialize_WritesConfiguredMirrors`
19. `EfSave_EnforcesConfiguredInvariant`
20. `PartialTrackedGraph_IsNotTreatedAsAuthoritativelyComplete`
21. `RichDomainMutation_StillTriggersConsistencyConsequences`

Use names matching the actual behavior if API semantics require small changes.

## Phase 15 — performance sanity check

Do not prematurely benchmark microseconds. Add only a useful dogfood sanity scenario if benchmark infrastructure makes it cheap.

Suggested dataset:

```text
1,000 Demands
1,000 Supplies
5,000 Allocations
5,000 Fulfillments
```

Change one supply capacity and observe/measure:

- affected nodes/objects;
- number of invariant evaluations;
- number of repairs scheduled;
- whether unrelated graph regions are avoided.

The purpose is to detect accidental full-graph scans/revalidation. If existing diagnostics can report impact counts, prefer those over timing-only assertions.

Do not make CI depend on fragile wall-clock thresholds.

## Phase 16 — write a dogfood findings document

Add a short document next to the experiment/sample, for example `DOGFOOD.md`.

It must contain four sections:

### What was easy

List API pieces that mapped naturally to the problem.

### What was awkward

List required ceremony, confusing naming, duplicate definitions, or non-obvious lifecycle behavior.

### What could not be expressed cleanly

Especially note:

- directional impact gaps;
- relation-membership-as-invariant gaps;
- repair payload/context gaps;
- EF scope/completeness friction;
- diagnostics gaps.

### Architectural result

Answer explicitly:

1. Did Raffinert remain useful after introducing domain mutation methods?
2. Which invariants belong naturally inside the domain object/aggregate?
3. Which dependencies remain naturally external?
4. Did the consistency model create hidden coupling that was harder to understand than explicit orchestration?
5. Did the sample require business behavior to leak into Raffinert configuration?
6. Is the repair boundary clean?
7. Would a computed property, aggregate method, or domain event be simpler for any implemented piece?

Do not write marketing copy. Negative findings are valuable.

## Phase 17 — final quality gate

Before reporting completion:

1. run `dotnet build` for the solution;
2. run all existing tests, not only new tests;
3. run the new sample/dogfood tests;
4. run formatting/analyzers used by the repository;
5. inspect git diff for unrelated changes;
6. ensure no generated binaries, DB files, logs, or local artifacts are committed;
7. ensure public sample code does not use internals;
8. ensure comments explain business intent, not obvious syntax;
9. ensure all scenario names correspond to assertions that actually prove the claimed behavior;
10. ensure no test passes only because repair/invariant validation was manually invoked from the mutation method.

## Rules for framework changes discovered by dogfood

The agent MUST NOT casually modify core API while implementing this plan.

When a gap is found:

1. first prove it with a failing/minimal test or a precise DOGFOOD finding;
2. search existing API for the intended supported mechanism;
3. prefer using an existing public mechanism even if naming differs from this plan;
4. if no mechanism exists, document the gap;
5. only make a core change if it is small, obviously general, backwards-compatible where practical, and required to make the dogfood correct rather than prettier;
6. for any non-trivial API redesign, stop at a proposed follow-up plan instead of implementing it silently.

## Definition of done

This dogfood is complete only when it demonstrates, with executable tests, the full causal story:

```text
ordinary domain mutation
        |
        v
Raffinert discovers affected dependencies
        |
        +--> derived state becomes dirty/invalid
        |
        +--> relation membership can change
        |
        +--> only affected invariant(s) are revalidated
        |
        +--> invalid persisted relationship can require repair
        |
        v
business repair code chooses what to do
        |
        v
EF persistence commits only a consistency-valid result
```

And the richer-domain variant must answer whether this remains valuable when mutation is no longer anemic.

The goal is not to prove Raffinert is good. The goal is to make the library fail honestly if its abstraction does not hold up under a realistic neutral-domain consistency problem.