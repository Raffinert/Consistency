# Allocation dogfood follow-up: core fixes and second dogfood pass

## Purpose

Implement the concrete framework changes exposed by the allocation dogfood in
`experiments/Raffinert.Consistency.AllocationDogfood` and then rerun the exact same domain scenarios against the
cleaner API.

This repository is still pre-1.0 / release-candidate quality. **Backward compatibility is NOT a goal for this
work.** Breaking public API changes are allowed and preferred when they remove semantic ambiguity or reduce the
chance of incorrect use.

Do not add compatibility shims, obsolete forwarding methods, legacy aliases, duplicate public APIs, or adapters
whose only purpose is to preserve the current API shape. Change the API cleanly, update all repository usages,
update `PublicAPI.Shipped.txt`, update docs/samples/tests, and remove obsolete concepts when the new model makes
them unnecessary.

The goal is not to make the existing 29 dogfood scenarios pass by any means necessary. The goal is to improve the
framework semantics and then prove that the original dogfood domain becomes simpler and more truthful.

---

# Non-negotiable architectural decisions

The implementation must preserve these boundaries:

```text
Domain model
    owns business meaning and local admissibility

Raffinert.Consistency
    owns dependency discovery, impact propagation, derived freshness,
    invariant freshness/evaluation, and structured repair requirements

Application/domain handlers
    own business repair decisions and actions

EF Core adapter
    translates tracked changes and provides the persistence boundary
```

Do NOT move reallocation ranking or replacement selection into Raffinert.

Do NOT make Raffinert a workflow engine.

Do NOT make domain methods call `ConsistencyRuntime`, `IConsistencyRuntime`, `Change.Property`, or repair APIs.

Do NOT solve the problem by teaching the dogfood domain about Raffinert.

---

# Problems confirmed by the first dogfood

The first allocation dogfood is intentionally the source of truth for these problems.

## Problem A — relation item changes cannot be direction-sensitive

Current relation aggregate policy supports:

```csharp
.ItemChanged(DependencySeverity.Invalid)
```

but cannot express:

```text
Fulfillment.Quantity increases  -> Invalid
Fulfillment.Quantity decreases  -> Dirty

Allocation.Quantity increases   -> Invalid
Allocation.Quantity decreases   -> Dirty
```

The runtime already supports old/new classification for direct source members through
`SourceMemberChanged(...)`. Relation items need the equivalent capability.

## Problem B — repair scheduling currently changes freshness semantics

Today an invariant configured with `ScheduleRepairWith(...)` escalates inherited `Dirty` impact to `Invalid` and
emits repair work before the invariant is known to be violated.

This makes a safe transition such as:

```text
Supply.Capacity: 10 -> 15
```

behave as though urgent corrective work is required.

This is conceptually wrong. The framework must separate:

```text
dependency impact
    !=
invariant evaluation state
    !=
actual invariant violation
    !=
repair requirement
```

## Problem C — callback-based repair is the wrong production contract

`ScheduleRepairWith(Action<TSource>)` embeds application behavior/state in the compiled model. The EF integration
registers the compiled model as singleton while runtimes are scoped. Capturing mutable queues or scoped services
inside compiled model callbacks is therefore an unsafe production boundary.

The primary framework contract must be structured repair data owned by the runtime/apply/save result, not an
arbitrary callback captured by the compiled model.

## Problem D — relation semantics must be reusable as a single source of truth

The dogfood currently defines candidate compatibility twice:

```text
Demand -> Supply candidate relation
Allocation -> Supply compatibility relation
```

Both repeat ResourceCode/Date matching semantics.

A later rule change can therefore update candidate matching but forget allocation compatibility. That is exactly
the kind of duplicated consistency knowledge this library is supposed to eliminate.

The framework needs a way for a derived value or invariant to consume membership in an already-defined relation
for object references projected from another source object.

---

# Target end-state

The second dogfood pass should be able to express the model approximately like this:

```csharp
var candidateSupplies = model.Relation(demands, supplies)
    .Where((demand, supply) =>
        demand.ResourceCode == supply.ResourceCode &&
        demand.Date == supply.Date)
    .Named("candidate-supplies");

var fulfilledQuantity = model.Derived(supplies)
    .From(supplyFulfillments)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Invalid)
        .MembershipRemoved(DependencySeverity.Dirty)
        .ItemMemberChanged(
            x => x.Quantity,
            (oldValue, newValue) => newValue > oldValue
                ? DependencySeverity.Invalid
                : DependencySeverity.Dirty))
    .Sum(x => x.Quantity)
    .Named("fulfilled-quantity");

var allocatedQuantity = model.Derived(supplies)
    .From(supplyAllocations)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Invalid)
        .MembershipRemoved(DependencySeverity.Dirty)
        .ItemMemberChanged(
            x => x.Quantity,
            (oldValue, newValue) => newValue > oldValue
                ? DependencySeverity.Invalid
                : DependencySeverity.Dirty))
    .Sum(x => x.Quantity)
    .Named("allocated-quantity");

var remainingCapacity = model.Derived(supplies)
    .From(fulfilledQuantity)
    .From(allocatedQuantity)
    .Impact(policy => policy.SourceMemberChanged(
        x => x.Capacity,
        (oldValue, newValue) => newValue < oldValue
            ? DependencySeverity.Invalid
            : DependencySeverity.Dirty))
    .Select((supply, fulfilled, allocated) =>
        supply.Capacity - fulfilled - allocated)
    .Named("remaining-capacity");

var capacityInvariant = model.Invariant(supplies)
    .From(remainingCapacity)
    .Must((_, remaining) => remaining >= 0m)
    .RepairWhenViolated()
    .Named("supply-capacity-valid");

var hasCompatibleSupply = model.Derived(allocations)
    .FromMembership(
        candidateSupplies,
        allocation => allocation.Demand,
        allocation => allocation.Supply)
    .Named("allocation-has-compatible-supply");

var compatibilityInvariant = model.Invariant(allocations)
    .From(hasCompatibleSupply)
    .Must((_, compatible) => compatible)
    .RepairWhenViolated()
    .Named("allocation-compatible");
```

Exact public names may be adjusted only if the resulting naming is materially clearer. Do not invent multiple
parallel APIs for the same concept.

---

# Required semantic model

## Derived values

Keep the existing meaning:

```text
Fresh   = evaluated from accepted current dependencies
Dirty   = may be stale, but previously known state is not known unsafe solely because of this impact
Invalid = cached state must not be relied upon before successful recomputation
```

## Invariants

Keep or refine the existing states so they represent evaluation/freshness only:

```text
Unknown
Valid
Violated
Dirty
Invalid
```

Important: `Dirty` and `Invalid` describe confidence/freshness, NOT whether repair work exists.

## Repair

Repair must be a consequence of an **evaluated violation**, not a synonym for invalidation.

Required conceptual flow:

```text
mutation
   -> dependency impact
   -> invariant becomes Dirty or Invalid
   -> invariant evaluation occurs when policy/save/runtime requires it
   -> predicate returns true
        -> Valid
        -> no repair requirement
   -> predicate returns false
        -> Violated
        -> structured RepairRequest is emitted if invariant is repairable
```

A safe dirty transition must not emit repair work merely because the invariant has a repair policy.

---

# Breaking API policy for this task

The agent MUST follow these rules:

1. Breaking public API changes are allowed.
2. Do not preserve `ScheduleRepairWith(Action<TSource>)` just for compatibility.
3. Do not add `[Obsolete]` wrappers unless there is a strong internal reason unrelated to compatibility.
4. If `InvariantReaction.ScheduleRepair` no longer fits the model, remove it or replace the reaction model.
5. Update every repository usage rather than keeping old API surface alive.
6. Update `PublicAPI.Shipped.txt` to represent the new RC API.
7. Update README/docs/samples/experiments/tests in the same change.
8. Do not create `V2`, `New`, `Ex`, `Advanced`, `Legacy`, or similarly duplicated APIs.

---

# Phase 0 — baseline and guardrails

Before editing production code:

1. Checkout `plan/allocation-dogfood`.
2. Confirm HEAD contains commit `cdfe88a` or a descendant.
3. Run the complete solution build.
4. Run all existing tests.
5. Run the allocation dogfood executable.
6. Record baseline counts in the implementation report:
   - existing test count;
   - allocation dogfood scenario count;
   - warnings/errors.
7. Read these files fully before making changes:
   - `src/Raffinert.Consistency/DependencyImpact.cs`
   - `src/Raffinert.Consistency/Derived/DerivedBuilders.cs`
   - invariant definition/runtime implementation files
   - repair request/runtime apply result files
   - `src/Raffinert.Consistency/Relations.cs`
   - relation runtime/index implementation files
   - EF integration save-planning files
   - `experiments/Raffinert.Consistency.AllocationDogfood/AllocationConsistencyModel.cs`
   - `experiments/Raffinert.Consistency.AllocationDogfood/CoreScenarios.cs`
   - `experiments/Raffinert.Consistency.AllocationDogfood/EfScenarios.cs`
   - `experiments/Raffinert.Consistency.AllocationDogfood/Repair.cs`
   - `experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md`

Do not start by editing the dogfood. Fix framework semantics first.

---

# Phase 1 — write failing tests for directional relation-item impact

Add focused core tests before changing production code.

The tests must cover a relation-backed incremental `Sum` where the right-side item has a numeric `Quantity`.

Required tests:

## 1.1 Increase classifier

```text
old Quantity = 3
new Quantity = 7
classifier returns Invalid
expected derived state = Invalid
```

## 1.2 Decrease classifier

```text
old Quantity = 7
new Quantity = 3
classifier returns Dirty
expected derived state = Dirty
```

## 1.3 Unrelated member change

If the item changes another property not selected by the item-member classifier, the existing default
`ItemChanged(...)` behavior must remain in force.

## 1.4 Multiple configured item-member classifiers

If more than one changed item member has configured rules, the strongest severity wins:

```text
Dirty + Invalid -> Invalid
```

## 1.5 Relation membership changes remain separate

`MembershipAdded` and `MembershipRemoved` behavior must not be changed by the item-member classifier.

## 1.6 Invalid classifier return

Invalid enum values must fail deterministically at configuration/runtime boundary using the same style as
`SourceMemberChanged`.

Do not alter existing dogfood expectations yet. First prove the framework API independently.

---

# Phase 2 — implement directional relation-item member classification

Extend relation-backed derived impact configuration.

Preferred public shape:

```csharp
.ItemMemberChanged<TValue>(
    Expression<Func<TItem, TValue>> member,
    Func<TValue, TValue, DependencySeverity> classify)
```

Because the current impact builder is typed only by `TSource`, you may need to introduce a relation-specific impact
builder typed by both source and item, for example:

```csharp
DerivedRelationImpactPolicyBuilder<TSource, TItem>
```

If that is necessary, do the clean breaking refactor now. Do NOT force the feature into an awkward weakly typed API
just to preserve the current builder type.

Implementation requirements:

1. Only a direct item member expression is required for this task.
2. Normalize unary conversion the same way `SourceMemberChanged` does.
3. Store the member metadata plus old/new classifier in compiled model policy data.
4. During `Change.Property` on a relation item, inspect configured item-member rules.
5. Apply the matching classifier using the normalized old/new values carried by the change.
6. If multiple relevant changes exist, take the maximum severity.
7. If no specific item-member classifier applies, fall back to configured/default `ItemChanged` severity.
8. Do not disable incremental aggregate execution plans.
9. Do not require sample code to manually invalidate anything.
10. Extend compiled-model diagnostics to expose that directional item-member rules exist. Exact delegate bodies need
    not be printable, but member names should be diagnosable.

Add focused tests for the compiled diagnostic representation.

---

# Phase 3 — define repair semantics independently of callbacks

Before changing API code, add tests that describe the required semantics.

Create tests around one invariant fed by a derived value.

Required behavior:

## 3.1 Dirty but safe change

Setup:

```text
invariant currently Valid
upstream becomes Dirty
new evaluated predicate is still true
invariant configured as repairable
```

Expected:

```text
state after impact may be Dirty
NO repair request yet
Evaluate(invariant) -> true
state -> Valid
NO repair request
```

## 3.2 Invalid but still valid after re-evaluation

Expected:

```text
state becomes Invalid
NO repair request merely because of invalidation
Evaluate(invariant) -> true
state -> Valid
NO repair request
```

## 3.3 Actual violation

Expected:

```text
impact -> Dirty or Invalid
Evaluate(invariant) -> false
state -> Violated
exactly one structured repair request for invariant/source
```

## 3.4 Repeated evaluation of same violation

Do not continuously duplicate repair requests for the same invariant/source within one apply/plan lifecycle.
Existing deduplication semantics should be preserved or clarified.

## 3.5 Valid -> violated -> valid

After repair/mutation makes the predicate true again, subsequent evaluation must return `Valid`; no stale repair
requirement should be regenerated from old state.

## 3.6 Save-time enforcement

An enforced invariant that evaluates false at save must still reject persistence exactly as today.

Repairability must not weaken enforcement.

---

# Phase 4 — replace `ScheduleRepairWith(Action<TSource>)` as the primary model

Refactor invariant repair configuration so compiled model definitions describe policy only; they do not capture
arbitrary application callbacks.

Preferred public API:

```csharp
.RepairWhenViolated()
```

The invariant definition should store something equivalent to:

```text
RepairPolicy.None
RepairPolicy.WhenViolated
```

Do not store an `Action<TSource>` in the compiled definition.

If `InvariantReaction.ScheduleRepair` exists only to support callback scheduling, remove/rework it. A cleaner model
is preferred over preserving the enum shape.

Keep separate concepts for:

```text
EvaluateImmediately
MarkDirty
MarkInvalid
repairability
```

Do not overload one enum if that makes the state machine unclear.

Possible shape:

```text
InvariantImpactPolicy / reaction = how affected invariant freshness is handled
RepairPolicy = whether evaluated violation creates structured repair work
```

Choose the simplest coherent implementation that enforces this separation.

Update all existing repository tests/usages that currently depend on `ScheduleRepairWith(...)`.

Do NOT add a compatibility alias.

---

# Phase 5 — make structured repair requests the first-class result

Inspect the existing `RepairRequest`, durable repair info, apply result, plan result, dispatch model, and EF save
work.

The target is:

```text
runtime/apply/save evaluates a repairable invariant
predicate is false
    -> state = Violated
    -> result contains structured RepairRequest
```

A repair request must provide enough stable information for application code to route it without a captured
callback.

At minimum preserve/provide:

- invariant identity / stable definition key when named;
- invariant definition id if internal routing needs it;
- source object for in-process core runtime results;
- source stable key when available/appropriate for durable work;
- causal information already available in the plan/result where practical.

Do not put application command types into the core framework.

Do not make core reference the allocation dogfood's `RepairRequirementKind`.

Do not store scoped service instances in the compiled model.

---

# Phase 6 — remove callback dispatch from the allocation dogfood

Only after phases 3–5 pass core tests, update the dogfood.

Delete the model-owned `RepairQueue` from `AllocationConsistencyModel`.

Replace:

```csharp
.ScheduleRepairWith(Repairs.RequireCapacityRepair)
```

and:

```csharp
.ScheduleRepairWith(Repairs.RequireCompatibilityRepair)
```

with the new repairable-invariant configuration.

Refactor `ReallocateDemand` so it accepts/consumes structured repair requests returned by the runtime/application
plan rather than a callback-populated queue.

The application may translate generic framework requests into application concepts. Keep that translation outside
core.

Example conceptual flow:

```csharp
var application = runtime.ApplyDetailed(...);
var result = reallocator.Process(runtime, application.Result.RepairRequests);
```

If repairs are produced after explicit invariant evaluation, pass the resulting repair work from the operation
that performed evaluation. Do not reintroduce a hidden global queue.

Delete unused queue/callback scaffolding from `Repair.cs`.

Add a test proving that two independent runtime instances using the same compiled model cannot leak repair work to
one another.

Add a test proving concurrent/scoped EF usage has no shared mutable repair queue in compiled model state.

---

# Phase 7 — update capacity-increase semantics

Change the dogfood scenario currently named similar to:

`CapacityIncrease_DoesNotCreateInvariantViolation`

The new required result is:

```text
Capacity 10 -> 15
RemainingCapacity = Dirty
CapacityInvariant = Dirty (or remains lazily affected according to final invariant freshness design)
RepairRequests = 0
Evaluate(CapacityInvariant) = true
CapacityInvariant = Valid
RepairRequests still = 0
```

The scenario must NOT expect `Invalid` merely because repair is configured.

This scenario is a release-blocking acceptance test for the new repair semantics.

---

# Phase 8 — update fulfillment/allocation decrease semantics

Modify the dogfood model to use the new directional item-member classifier.

Required outcomes:

## Fulfillment increase

```text
3 -> 7
FulfilledQuantity -> Invalid
CapacityInvariant becomes correctness-sensitive
re-evaluation may produce violation
```

## Fulfillment decrease

```text
7 -> 3
FulfilledQuantity -> Dirty
no urgent invalid classification solely from the decrease
no repair request unless later actual evaluation proves some different invariant violated
```

## Allocation increase

```text
5 -> 8
AllocatedQuantity -> Invalid
```

## Allocation decrease

```text
5 -> 2
AllocatedQuantity -> Dirty
```

Rename old scenarios that mention "conservative invalid impact" because that workaround should no longer be the
expected behavior.

Do not weaken the invariant to make these pass.

---

# Phase 9 — design relation-membership projection API with tests first

The goal is to reuse an existing relation as semantic authority.

We need to answer:

> For source object `Allocation`, is the pair `(allocation.Demand, allocation.Supply)` currently a member of
> relation `candidateSupplies : Relation<Demand, Supply>`?

Required properties:

1. No duplicate matching predicate.
2. Dependency tracking follows both selectors:
   - `allocation.Demand`
   - `allocation.Supply`
3. Changes to fields used by the underlying relation predicate propagate through relation membership impacts.
4. Reassigning `allocation.Demand` or `allocation.Supply` affects membership.
5. The derived result must be boolean and cacheable like other derived state.
6. It must participate in downstream invariant propagation.
7. It must use the existing relation's authoritative predicate/index semantics.
8. It must not compile/copy/rewrite the predicate into a second relation definition.

Preferred conceptual public shape:

```csharp
model.Derived(allocations)
    .FromMembership(
        candidateSupplies,
        allocation => allocation.Demand,
        allocation => allocation.Supply)
```

A reasonable fluent continuation can directly return `Derived<Allocation, bool>` or a small builder if impact
configuration is needed.

Do not introduce a generic "query DSL" in this task.

---

# Phase 10 — implement projected relation membership

Implement only what the dogfood and general reusable semantics require.

Likely internal needs:

1. New derived definition type representing projected pair membership.
2. Tracked dependencies for left/right selectors.
3. Runtime evaluation:
   - resolve selected left/right objects;
   - ask the existing relation runtime/index whether that exact pair is a member;
   - do not scan a duplicated predicate unless the underlying relation itself legitimately uses scan fallback.
4. Reverse impact propagation:
   - when underlying relation membership for the selected pair is added/removed, affect the owning source object;
   - when owner changes selected left/right reference, affect that source object;
   - when nested member changes cause the underlying relation to change, propagate via existing relation impact data.
5. Diagnostics should identify:
   - upstream relation key/id;
   - projected left selector path;
   - projected right selector path.

Be conservative for ambiguous selector analysis, but do not silently claim exact propagation if it is not proven.

Add focused unit tests before touching the dogfood compatibility model.

---

# Phase 11 — remove duplicate compatibility relation from dogfood

Delete `CompatibleAllocatedSupplies` completely.

Delete its duplicated ResourceCode/Date predicate.

Replace `HasCompatibleSupply` with projected membership from `CandidateSupplies`.

Required dogfood proof:

1. Existing compatible allocation evaluates true.
2. Supply `ResourceCode` change invalidates/affects the allocation compatibility derived value.
3. Demand `Date` change invalidates/affects it.
4. Reassigning allocation to another candidate supply restores true.
5. Add a new test that modifies the candidate rule in one place and proves allocation compatibility follows it.

For the last test, add one additional candidate criterion if necessary in fixture/model, but do not permanently make
the sample domain more complicated than needed. The purpose is to demonstrate single semantic authority.

There must be exactly one candidate-matching predicate in `AllocationConsistencyModel.cs` after this phase.

---

# Phase 12 — verify DDD/rich-domain boundary again

Keep `Supply.ChangeCapacity(...)` local and Raffinert-free.

Required EF scenario:

```csharp
supply.ChangeCapacity(6m);
await db.SaveChangesAsync();
```

Expected:

- no explicit `runtime.Apply` in application/EF path;
- derived state is detected via EF tracking;
- capacity invariant is evaluated/enforced;
- invalid persistence is rejected;
- application may repair and retry.

Required core-runtime documentation/scenario:

Explicitly retain and document that dependency-free core requires reported mutations:

```csharp
supply.ChangeCapacity(6m);
runtime.Apply(Change.Property(...));
```

Do not pretend the core can observe arbitrary POCO mutation without an observer/change source.

Add/adjust README wording so the distinction is explicit:

```text
Core runtime: mutate + report changes.
EF integration: ordinary tracked mutation + SaveChanges boundary.
```

---

# Phase 13 — repair workflow acceptance tests

The repaired architecture must cover these exact scenarios.

## 13.1 Compatibility violation

- mutate supply so current allocation is no longer a candidate;
- compatibility invariant evaluates false;
- exactly one structured repair request identifies that invariant/source;
- application reallocator chooses replacement;
- runtime relation/derived/invariant state becomes valid.

## 13.2 Capacity violation

- decrease capacity below current fulfillment + allocation;
- capacity invariant evaluates false;
- structured repair request is emitted;
- application moves allocation;
- old and replacement supplies both end valid.

## 13.3 Safe capacity increase

- no repair request.

## 13.4 Safe fulfillment decrease

- no repair request purely due to decrease.

## 13.5 Safe allocation decrease

- no repair request purely due to decrease.

## 13.6 No replacement available

- repair request remains unresolved in application result;
- framework invariant remains observably violated;
- no silent acceptance.

## 13.7 Repair convergence guard

Keep an application-level convergence/attempt guard if useful. Do not move this concern into core.

---

# Phase 14 — EF transaction and lifetime regression tests

Rerun and preserve all existing EF guarantees from the first dogfood.

Must still pass:

1. ordinary `SaveChangesAsync` enforces configured invariant;
2. save-time mirror materialization works;
3. explicit pre-read `Materialize` works;
4. SQL failure does not commit runtime state;
5. retry rebuilds/commits correctly;
6. late tracking invalidates stale pending plan;
7. incomplete authoritative scope is rejected;
8. rejected save can be repaired and retried.

Add new regression tests:

## 14.1 No singleton callback state

Resolve two DI scopes sharing the same singleton compiled model. Produce different violations in each scope. Verify
repair work is isolated to each scoped operation/runtime result.

## 14.2 Concurrent model safety

At minimum, prove compiled model repair metadata is immutable and contains no mutable application queue or scoped
callback state.

Do not solve concurrency by putting locks around a singleton application queue. Remove the queue architecture.

---

# Phase 15 — diagnostics improvements limited to what this refactor needs

Do not build a giant observability subsystem in this task.

Required diagnostics only:

1. directional item-member impact rules expose configured member names;
2. projected relation membership diagnostics expose source relation and selector paths;
3. repair request identifies invariant/source cleanly;
4. causal traces continue to connect mutation -> derived -> invariant.

Optional only if straightforward:

- include evaluated invariant input values in an evaluation result/diagnostic record.

Do not delay the core fixes to build full business-expression explanation machinery.

---

# Phase 16 — update public API surface and docs

Because breaking changes are allowed, clean the API rather than layering it.

Required repository-wide search/update for:

```text
ScheduleRepairWith
InvariantReaction.ScheduleRepair
Repair scheduler callbacks
ItemChanged
relation compatibility duplicates introduced only due missing membership projection
```

Update:

- `src/Raffinert.Consistency/PublicAPI.Shipped.txt`
- root `README.md`
- relevant docs under `docs/`
- existing samples
- old experiments that are expected to compile as part of solution
- tests
- allocation dogfood README/DOGFOOD findings

If archived docs intentionally describe historical APIs, do not rewrite history unless they are compiled/validated by
repo tooling. But current docs must not teach removed APIs.

Update `DOGFOOD.md` into a second-pass report with four sections:

```text
Fixed by framework changes
Still awkward
Fundamental limitations
Architectural result after second pass
```

Explicitly note whether each original gap is resolved.

---

# Phase 17 — do NOT "fix" ConsistencyScope completeness

`ConsistencyScope.Complete(...)` is a host assertion, not proof that all DB rows were fetched.

Do not attempt to infer arbitrary database completeness from the in-memory runtime.

Do not add fake safety heuristics.

Keep the existing authoritative-scope behavior and documentation clear.

This is a fundamental limitation, not part of this core-fix implementation.

---

# Phase 18 — final code-quality pass

Before declaring success:

1. Search for duplicate compatibility predicate text in the allocation dogfood.
2. Search for `ScheduleRepairWith` repository-wide.
3. Search compiled model definitions for arbitrary `Action<T>` repair callback storage.
4. Search for model-owned mutable repair queues.
5. Confirm new item-member classifiers are strongly typed.
6. Confirm relation membership projection uses existing relation definition/runtime state.
7. Confirm no dogfood-specific types leaked into core.
8. Confirm no domain type references leaked into generic framework tests.
9. Confirm no new public API exists only to preserve the removed RC API.
10. Run formatter/analyzers.
11. Build solution with zero warnings.
12. Run all tests.
13. Run allocation dogfood.

---

# Exact dogfood outcome changes expected

The agent must explicitly compare old and new behavior in its completion report.

## Old

```text
Capacity increase
    -> RemainingCapacity Dirty
    -> invariant escalated Invalid
    -> repair request emitted
```

## New

```text
Capacity increase
    -> RemainingCapacity Dirty
    -> invariant affected but not falsely violated
    -> evaluation true
    -> no repair request
```

## Old

```text
Fulfillment decrease
    -> ItemChanged Invalid
    -> urgent repair request
```

## New

```text
Fulfillment decrease
    -> directional item-member classifier Dirty
    -> no repair request unless evaluated invariant actually fails
```

## Old

```text
Candidate predicate duplicated in CandidateSupplies and CompatibleAllocatedSupplies
```

## New

```text
CandidateSupplies is the only matching predicate
Allocation compatibility consumes membership in CandidateSupplies
```

## Old

```text
compiled model callback -> mutable RepairQueue
```

## New

```text
compiled model contains immutable repair policy
runtime/apply/save result contains structured repair request
application consumes request
```

---

# Tests that must not be weakened

Do not change these truths merely to get green tests:

1. capacity cannot be less than fulfilled + allocated when capacity invariant is enforced;
2. incompatible allocation must remain invalid/violated until repaired;
3. no replacement must remain explicit, not silently accepted;
4. failed SQL save must not advance committed runtime state;
5. incomplete scope must not be treated as authoritative;
6. candidate matching must remain relation-driven;
7. derived mirrors must still distinguish `Evaluate` from `Materialize`;
8. rich domain method must remain free from Raffinert calls;
9. application owns replacement ranking;
10. core runtime requires explicit mutation reporting unless an adapter observes mutations.

---

# Things the agent must NOT do

Do NOT:

- preserve old repair callback API for compatibility;
- add legacy aliases;
- create `ScheduleRepairWith2`, `RepairWhenViolatedEx`, etc.;
- move allocation/reallocation business algorithms into core;
- make invariants automatically query the database;
- weaken `ConsistencyScope` safety;
- remove Dirty/Invalid distinction;
- classify every item change as Invalid to avoid implementing directional rules;
- keep the duplicated compatibility relation after projected membership exists;
- hide repair work in global/static/singleton state;
- make compiled model mutable per request;
- add service-provider access to core model definitions;
- make domain entities depend on Raffinert;
- bypass existing relation indexes by recompiling the predicate in projected membership;
- disable incremental `Sum` just to simplify propagation;
- change dogfood business rules to fit the framework;
- skip repository-wide API cleanup because the old API was public;
- declare completion while old and new repair concepts coexist without a strong semantic reason.

---

# Preferred implementation order

Use this exact order unless a compile dependency forces a small local adjustment:

1. Baseline build/tests/dogfood.
2. Directional relation-item failing tests.
3. Implement directional relation-item policy.
4. Repair semantic failing tests.
5. Separate repairability from invariant freshness reaction.
6. Remove callback-based repair as primary API.
7. Make structured repair requests authoritative.
8. Update dogfood repair consumption.
9. Update safe increase/decrease dogfood scenarios.
10. Projected relation-membership failing tests.
11. Implement projected relation membership.
12. Remove duplicate compatibility relation.
13. Re-run rich-domain and EF scenarios.
14. Add scope/lifetime/concurrency regressions.
15. Update diagnostics.
16. Clean public API/docs/samples.
17. Full verification.
18. Write second-pass dogfood report.

Do not implement all four changes at once before tests. Keep commits or working stages logically separable so a
regression can be localized.

---

# Definition of Done

This work is complete only when ALL of the following are true:

- [ ] Directional relation-item member impact exists as strongly typed public API.
- [ ] Fulfillment decrease is Dirty, not conservatively Invalid, in the dogfood.
- [ ] Allocation decrease is Dirty, not conservatively Invalid, in the dogfood.
- [ ] Capacity increase produces no false repair requirement.
- [ ] Repair requests are created only from evaluated violated repairable invariants.
- [ ] Compiled model no longer needs application-owned mutable repair callback queues.
- [ ] Allocation dogfood consumes structured repair requests.
- [ ] Two scoped runtimes sharing one compiled model cannot leak repair state.
- [ ] Candidate matching predicate exists only once in the dogfood.
- [ ] Allocation compatibility consumes membership of that existing relation.
- [ ] Rich `Supply.ChangeCapacity` remains Raffinert-free.
- [ ] EF application path remains `domain mutation -> SaveChangesAsync`.
- [ ] Core runtime documentation remains honest about explicit mutation reporting.
- [ ] Existing transaction/rollback/retry/scope guarantees still pass.
- [ ] Existing incremental aggregate plans remain incremental.
- [ ] All repository usages of removed RC APIs are updated.
- [ ] No compatibility shims were added solely to preserve the old API.
- [ ] `PublicAPI.Shipped.txt` matches the new intended RC API.
- [ ] Solution builds with zero warnings/errors.
- [ ] All existing tests pass.
- [ ] Updated allocation dogfood scenarios pass.
- [ ] `DOGFOOD.md` documents what improved and what limitations remain.

---

# Completion report required from the implementation agent

When finished, report exactly these sections:

## 1. Core API changes

List removed, added, and renamed public APIs.

## 2. Semantic changes

Explain:

- Dirty vs Invalid after relation item transitions;
- when repair requests are created;
- how projected relation membership propagates.

## 3. Dogfood before/after

Provide before/after behavior for:

- capacity increase;
- capacity decrease;
- fulfillment increase/decrease;
- allocation increase/decrease;
- compatibility invalidation;
- repair/reallocation.

## 4. DDD boundary

State whether `Supply.ChangeCapacity` or any other domain method gained Raffinert dependencies. The desired answer
is no; if not, explain why.

## 5. Remaining limitations

At minimum discuss:

- authoritative scope completeness remains host-asserted;
- core runtime still requires explicit mutation reporting;
- any diagnostic/business-value explanation gaps still present.

## 6. Verification

Report:

- build warnings/errors;
- total existing tests passed;
- dogfood scenarios passed;
- formatting/analyzer result;
- final commit SHA.

Do not claim "all gaps fixed" unless every original DOGFOOD.md gap was actually addressed or explicitly reclassified
as intentional/fundamental.
