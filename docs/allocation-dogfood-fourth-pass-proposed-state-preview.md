# Allocation dogfood fourth-pass plan: repair reason correctness and proposed-state preview spike

## Purpose

This is a **step-by-step implementation plan for a weak agent**. Follow it literally. Do not redesign unrelated APIs, do not simplify scenarios, and do not claim success from partial verification.

This repository is still release-candidate quality. **Backward compatibility is not a goal.** Breaking API changes are allowed when they make the model more correct or substantially clearer. Do not add obsolete aliases, compatibility shims, duplicate APIs, or migration wrappers unless this plan explicitly asks for them.

The fourth pass has two goals:

1. Fix a concrete correctness bug in `RepairRequestInfo.Reason`.
2. Dogfood a **proposed-state query/preview** concept for EF repair after a rejected save, so repair code does not need to rebuild a second full runtime from the tracked graph.

The second goal is intentionally an experiment first. Do **not** promote preview to a polished public API until the dogfood and measurements justify it.

---

# 0. Read this before touching code

Current branch: `plan/allocation-dogfood`

Expected starting commit: `2411eccc34539e77ef8dadcda06070029ba293dd` or later if only this plan was added.

Relevant files to inspect first:

- `src/Raffinert.Consistency/Derived/InvariantRuntimeState.cs`
- `src/Raffinert.Consistency/Runtime/MutationCommit.cs`
- `src/Raffinert.Consistency/Policies/PolicyActions.cs`
- `src/Raffinert.Consistency/Runtime/*` related to `PreparedMutation`, `PreparedImpactPlan`, forward patches, rollback journals and planning
- `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyPersistencePolicy.cs`
- `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/Repair.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/EfScenarios.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md`
- `tests/Raffinert.Consistency.Tests/InvariantPolicyTests.cs`
- `tests/Raffinert.Consistency.EntityFrameworkCore.Tests/*`

Before changing anything, write down in your own notes the current flow:

```text
mutation
  -> dependency propagation
  -> invariant impact severity
  -> repair-enabled invariant eagerly evaluates
  -> false predicate creates RepairRequestInfo
```

and the current rejected EF save flow:

```text
tracked mutation
  -> EF captures mutation set
  -> Raffinert prepares speculative plan
  -> invariant violation
  -> save rejected
  -> committed runtime remains unchanged
  -> dogfood rebuilds a second runtime from DbSet.Local
  -> repair uses second runtime
  -> retry SaveChanges
  -> main runtime plans again
```

Do not proceed until you can locate the code implementing both flows.

---

# 1. Reproduce the `RepairRequestInfo.Reason` bug with a failing test first

## 1.1 Problem

Current invariant repair logic infers request severity from the invariant's **previous cached evaluation state**.

Conceptually current logic behaves like:

```csharp
var previous = GetState(source);
var valid = Evaluate(source);

if (!valid && definition.RepairPolicy == InvariantRepairPolicy.WhenViolated)
{
    var reason = previous == InvariantEvaluationState.Invalid
        ? DependencyImpactKind.Invalid
        : DependencyImpactKind.Dirty;

    policyActions.AddRepairRequest(definition, source, reason);
}
```

This is wrong when the invariant has never been evaluated.

Example:

```text
invariant state = Unknown
incoming dependency impact = Invalid
predicate evaluates false
```

Because `Unknown` was never stored as `Invalid`, request reason can become `Dirty`, even though the actual dependency impact was `Invalid`.

The repair reason must represent **the current operation's propagated severity**, not a guess based on prior cache state.

## 1.2 Add failing tests before implementation

Add core tests covering all of these cases.

### Test A — Unknown invariant + Invalid impact

- Create derived value whose source change is configured as `Invalid`.
- Create `RepairWhenViolated()` invariant over it.
- Add source.
- **Do not evaluate the invariant before mutation.**
- Mutate source so predicate becomes false.
- Apply detailed mutation.
- Assert exactly one repair request.
- Assert request reason is `DependencySeverity.Invalid`.

### Test B — Unknown invariant + Dirty impact

Same setup, but dependency impact is `Dirty`.

Expected request reason: `DependencySeverity.Dirty`.

### Test C — previously valid + Invalid

Keep an existing test or add one proving pre-evaluating the invariant does not change the correct result.

Expected: `Invalid`.

### Test D — multiple causes, Dirty + Invalid

One operation affects the same invariant/source through at least two paths:

```text
Dirty cause
Invalid cause
```

Expected:

```text
one repair request
reason = Invalid
```

Invalid must dominate Dirty.

### Test E — request deduplication remains operation-scoped

Two impacts in one operation must not produce duplicate invariant/source requests.

A later independent operation may produce a new request.

Do not change implementation until these tests demonstrate the bug.

---

# 2. Fix repair reason at the dependency/policy boundary

## 2.1 Required design rule

`InvariantRuntimeState` owns:

- cached invariant evaluation state;
- predicate evaluation;
- freshness/evaluation state transitions.

It should **not infer operation impact severity from its previous cached state**.

The dependency propagation layer already knows whether an affected invariant/source arrived as:

```text
Dirty
Invalid
```

That actual operation severity must reach structured repair request creation.

## 2.2 Preferred implementation direction

Prefer separating evaluation from repair-request construction.

Conceptually:

```csharp
var state = _invariants[definition];
var isValid = state.EvaluateValue(source);

if (!isValid && definition.RepairPolicy == InvariantRepairPolicy.WhenViolated)
{
    policyActions.AddRepairRequest(
        definition,
        source,
        actualImpactKind);
}
```

Do not make `EvaluateValue()` guess the impact reason.

If the implementation architecture strongly requires passing reason into the state, this is acceptable:

```csharp
state.EvaluateValue(source, impactKind, policyActions);
```

but document why. The preferred design is that policy consequence construction stays outside invariant state.

## 2.3 Preserve severity dominance

If the same invariant/source is affected multiple times in one operation, merge severities so:

```text
Invalid > Dirty
```

Do not rely on enumeration order.

## 2.4 Preserve current semantics

Do not break:

- `RepairWhenViolated()` eager evaluation;
- repair-disabled lazy invariants;
- `EvaluateImmediately` behavior;
- request isolation between runtimes;
- request isolation between operations;
- deterministic result ordering.

---

# 3. Verify the reason fix before starting preview work

Run at minimum:

```powershell
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
```

Then run the allocation dogfood directly:

```powershell
dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build
```

Do not begin the preview spike if these are red.

---

# 4. Document the proposed-state problem before implementing anything

Add a short section to `DOGFOOD.md` titled something like:

```text
## Proposed-state repair gap
```

Describe the current behavior accurately:

```text
1. EF tracked graph contains proposed values.
2. Raffinert plans them against the committed runtime.
3. Save is rejected because an invariant is violated.
4. Committed runtime intentionally stays unchanged.
5. Repair code needs relation/derived/invariant queries against the proposed tracked state.
6. Current dogfood solves this by creating and seeding a second runtime from the whole tracked graph.
```

State explicitly that this is the baseline implementation to compare against, not the desired final API.

Do not call it merely "ceremony". It may be an O(graph) cost and duplicate-index construction problem.

---

# 5. Define preview experiment constraints

Create an **internal/experimental** abstraction first. Do not add polished public API docs yet.

The experiment must answer this question:

> Can Raffinert query the same proposed state that caused an EF plan to fail, without installing that state into the committed runtime and without rebuilding the entire runtime from scratch?

## 5.1 Minimum query surface

The preview needs only what repair currently requires:

```csharp
Evaluate(derived, source)
Evaluate(invariant, source)
Related(relation, source)
GetState(derived/invariant, source) // optional if easy, but useful
```

Do **not** add:

- `Materialize`
- `Dispatch`
- persistence APIs
- arbitrary mutation APIs
- workflow APIs
- repair handler registration
- event bus behavior
- transaction abstraction

## 5.2 Non-negotiable isolation

Preview operations must not mutate committed runtime state.

After preview reads, assert all of these remain unchanged:

- `runtime.Version`
- committed relation membership/indexes
- committed navigation indexes
- committed projection indexes
- committed derived cached values/states
- committed invariant cached states
- committed coverage state

No cleanup/rollback should be required after simply disposing the preview.

## 5.3 Domain objects

The EF tracked entities already physically contain proposed CLR property values.

Preview must account for that fact. Be explicit about which data comes from:

```text
committed runtime structural/index state
+
prepared mutation / forward patch
+
current object scalar values
```

Do not accidentally query committed indexes while reading proposed object values in a way that mixes incompatible worlds.

---

# 6. Inspect existing prepared-plan machinery before inventing new state structures

Before writing preview code, inspect and write comments/notes around:

- `PreparedMutation`
- `PreparedImpactPlan`
- runtime forward patch
- rollback journal
- plan validation
- dependency graph state capture
- relation deltas
- navigation/projection refresh planning

The prototype should reuse existing speculative-plan machinery where possible.

The weak agent must not create a parallel implementation of:

- relation indexing;
- dependency propagation;
- derived state cache;
- navigation tracking;
- projection tracking.

If the only way to implement preview appears to be copying half of `ConsistencyRuntime`, stop and record that as a negative result before proceeding.

---

# 7. Implement the smallest useful preview prototype

Name is not final. Internally, names such as these are acceptable:

```csharp
ConsistencyPreview
PreparedConsistencyView
ProposedStateView
```

Do not bikeshed the public name yet.

## 7.1 Desired conceptual use

The target usage should be approximately:

```csharp
var prepared = runtime.Prepare(mutations);
var plan = runtime.PlanDetailed(prepared, ...);

using var preview = runtime.CreatePreview(plan);

var remaining = preview.Evaluate(model.RemainingCapacity, supply);
var candidates = preview.Related(model.CandidateSupplies, demand);
var valid = preview.Evaluate(model.CapacityInvariant, supply);
```

This is conceptual. Fit it to current runtime architecture rather than forcing these exact signatures.

## 7.2 Preview must represent final proposed state

If one atomic mutation contains:

```text
allocation.SupplyId old -> new
allocation.Supply old -> new
```

preview must expose the **final projected state**, not an intermediate one.

Likewise for:

- relation join-key changes;
- selector retargets;
- object add/remove;
- multiple changes to the same relation in one mutation set.

## 7.3 Preview query consistency

Within one preview instance:

```text
Repeated Evaluate -> same result
Repeated Related -> same membership
Derived queried before relation -> same as relation queried first
Invariant queried before derived -> same final truth
```

Read order must not affect results.

---

# 8. Add focused core preview tests

Create tests independent of EF first.

Required scenarios:

### Preview A — scalar derived value

Committed:

```text
Capacity = 10
Remaining = 2
```

Prepared mutation:

```text
Capacity 10 -> 6
```

Preview:

```text
Remaining = -2
```

Committed runtime remains:

```text
Remaining = 2
```

### Preview B — relation membership change

Prepared join-key change removes a candidate pair.

Preview `Related(...)` must show removed pair.
Committed runtime must still show original pair.

### Preview C — projected membership

Prepared candidate relation change makes an existing allocation incompatible.

Preview invariant must evaluate false.
Committed invariant state/value remains baseline.

### Preview D — retarget allocation

Prepared allocation moves from Supply A to Supply B.

Preview must show:

```text
A allocated total decreases
B allocated total increases
A capacity invariant updates
B capacity invariant updates
```

Committed runtime stays unchanged.

### Preview E — add/remove lifecycle

Preview must correctly query proposed state when an object is added or removed in the prepared mutation.

### Preview F — disposal

Create preview, query it, dispose it.

Assert no runtime version/state changes and no rollback call is necessary.

### Preview G — stale prepared plan

If runtime advances after plan creation, preview creation or use must fail deterministically rather than silently query stale state.

Reuse existing stale-plan validation semantics where possible.

---

# 9. Integrate preview into EF planning without committing runtime state

Do not change normal successful `SaveChangesAsync` semantics.

For a rejected enforced invariant plan, the application needs enough information to query the proposed state that produced the violation.

Explore the smallest safe integration.

Possible shapes include:

```csharp
ConsistencyInvariantViolationException
    .RepairRequests
    .<preview handle or plan handle>
```

or a scoped EF session API that can expose the rejected prepared plan.

Do **not** serialize runtime internals into the exception.

Do **not** make exceptions own long-lived unmanaged resources.

Do **not** create a preview that remains valid after relevant tracked state changes.

A reasonable design may be:

```text
exception carries immutable violation/repair data
EF scoped session retains rejected plan until tracked state changes
application asks scoped consistency session for proposed-state preview
```

This is only an example. Choose the smallest design that preserves lifetime and staleness rules.

---

# 10. Rework only the dogfood rejected-save repair scenario

Current dogfood does this:

```csharp
new ReallocateDemand(model).ProcessCurrentGraph(
    error.RepairRequests,
    db.Demands.Local,
    db.Supplies.Local,
    db.Allocations.Local,
    db.Fulfillments.Local);
```

That method rebuilds and seeds another full runtime.

Keep this implementation temporarily as **baseline**.

Add a second repair path using preview.

Desired flow:

```text
Supply.ChangeCapacity(6)
  -> SaveChangesAsync
  -> rejected invariant
  -> exception.RepairRequests
  -> obtain proposed-state preview for rejected plan
  -> application reallocation logic queries preview
  -> application mutates tracked Allocation
  -> retry SaveChangesAsync
  -> success
```

The new path must not call:

```csharp
Compiled.CreateRuntime(seed => ...)
```

for repair querying.

Do not delete the baseline path until benchmark comparison is complete.

---

# 11. Repair application boundary must remain application-owned

Preview does not choose a replacement.

Keep this responsibility in dogfood application logic:

```text
which candidate is acceptable
candidate ordering/ranking
what to do when none exists
how many repair attempts are allowed
convergence policy
```

Raffinert supplies:

```text
proposed relation membership
proposed derived values
proposed invariant truth
repair request identity
```

Do not add a generic reallocation workflow engine to core.

---

# 12. Handle repair-induced second-order effects

This is critical.

Suppose rejected proposed state creates repair request for Supply A.

Application preview finds replacement Supply B and mutates allocation A -> B.

That repair can create new consequences:

```text
Supply A becomes valid
Supply B allocation increases
Supply B may become invalid
compatibility may change
another repair may be required
```

The experiment must define how the application obtains consistency information after each repair mutation.

At minimum support one of these explicitly:

### Approach 1 — recreate preview from current tracked mutation set

After repair mutation, re-plan against unchanged committed baseline and obtain a new preview.

### Approach 2 — preview supports staged mutation application

Only do this if it falls naturally out of existing prepared-plan machinery.

Do not invent a mutable mini-runtime just to satisfy this requirement.

For the dogfood, prefer re-planning after each repair step if that is much simpler and still avoids full graph reseeding.

Add a convergence limit as the application already does.

---

# 13. Benchmark baseline reseed vs preview

This is not a nanosecond benchmark exercise. We need order-of-magnitude evidence.

Create an experiment/benchmark that compares:

```text
A. Full CreateRuntime(seed entire graph) repair query
B. Proposed-state preview from prepared plan
```

Use at least these graph sizes:

```text
100 allocations
1,000 allocations
10,000 allocations
```

If 10,000 is trivial and memory permits, optionally include 50,000 or 100,000.

Measure at minimum:

- elapsed time to create usable repair-query context;
- allocations/allocated bytes if readily available;
- relation/index rebuild work if diagnostics expose it;
- number of objects seeded/copied/touched;
- preview creation cost;
- first repair query cost.

Do not optimize benchmark code before correctness.

Do not claim preview wins unless measurements show it.

If preview is not materially better or is much more complex, record that result honestly.

---

# 14. Decide whether preview remains experimental or graduates

After dogfood and benchmarks, write a short decision section in `DOGFOOD.md`.

Choose exactly one outcome:

## Outcome A — preview justified

State why:

- removes full graph reseed;
- reuses prepared-plan machinery;
- query semantics are understandable;
- isolation/staleness are testable;
- measurable improvement is material.

Then propose a public API separately, but do not over-polish it in the same commit unless trivial.

## Outcome B — preview not justified yet

State why:

- complexity too high;
- unsafe mixed-state semantics;
- negligible performance benefit;
- current reseed approach acceptable for intended scope;
- better architecture is different.

Do not force a feature merely because this plan proposed it.

---

# 15. Required regression tests for EF rejected save

Regardless of preview outcome, keep/add tests proving:

### EF A — rejected save does not commit runtime

```text
Version unchanged
committed relations unchanged
committed derived cache unchanged
```

### EF B — repair-enabled violation exposes repair request

Exactly the enforced violated invariant/source pair.

### EF C — non-repair violation exposes no repair requests

### EF D — preview/rejected-plan handle invalidates after tracked graph changes

If the application changes relevant tracked state after rejection, old proposed-state view must not silently remain usable.

### EF E — retry succeeds after application repair

### EF F — SQL failure after a valid re-plan still preserves runtime transaction semantics

Do not conflate invariant rejection with SQL persistence failure.

---

# 16. Documentation updates

Update only after implementation behavior is final.

## `DOGFOOD.md`

Document:

- repaired `Reason` semantics;
- proposed-state problem;
- baseline full runtime reseed;
- preview experiment/result;
- final repair-after-rejection workflow;
- remaining limitations.

## `docs/architecture.md`

If preview survives, add one concise diagram:

```text
Committed Runtime
      |
      +-- Prepare mutation --> Proposed-State View
      |                         |
      |                         +-- Evaluate
      |                         +-- Related
      |                         +-- invariant truth
      |
      +-- unchanged until durable commit
```

## `docs/ef-core-consistency.md`

Explain rejected-save repair lifecycle without implying rejected plan is committed.

Avoid presenting experimental APIs as stable unless the decision section explicitly graduates them.

---

# 17. Verification commands — mandatory and separate

The final agent report must list each command separately.

Run:

```powershell
dotnet build Raffinert.Consistency.sln -c Release
```

Then direct dogfood build:

```powershell
dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
```

Then executable without rebuild:

```powershell
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build
```

Then core tests:

```powershell
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
```

Then EF tests:

```powershell
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release
```

Then existing package-consumer verification commands used by the repository.

Then formatting and diff checks:

```powershell
dotnet format Raffinert.Consistency.sln --verify-no-changes
git diff --check
```

If any command is blocked by an external file lock, report the exact process/error separately. Do not convert an infrastructure failure into a PASS.

---

# 18. Final report format

The weak agent must report exactly these sections.

## 1. Repair reason bug

- Reproduction test
- Root cause
- Exact implementation change
- Dirty/Invalid dominance behavior

## 2. Proposed-state experiment

- Baseline behavior
- Preview design attempted
- What runtime machinery was reused
- Isolation/staleness semantics

## 3. EF repair flow

Show exact flow before and after.

## 4. Dogfood result

- scenario count
- whether full runtime reseed remains
- whether preview is now used

## 5. Benchmark result

Table with graph size and baseline vs preview cost.

Do not omit negative results.

## 6. API decision

One of:

```text
KEEP EXPERIMENTAL
GRADUATE
REJECT
```

with reasons.

## 7. Remaining limitations

At minimum reconsider:

- host-asserted authoritative scope;
- explicit core POCO change reporting;
- diagnostics lacking business operands;
- application-owned ranking/convergence;
- preview lifetime/staleness if preview survives.

## 8. Verification

Separate PASS/FAIL row for every command from section 17.

## 9. Commit

Provide pushed commit SHA and branch.

---

# 19. Hard prohibitions

Do not do any of the following unless a failing test proves it is necessary:

- Do not redesign the whole runtime.
- Do not commit rejected EF plans into the main runtime merely to make repair easier.
- Do not mutate committed runtime then roll it back after repair queries.
- Do not rebuild all indexes in preview if existing prepared-plan/patch machinery can answer the query.
- Do not make preview a workflow engine.
- Do not move candidate ranking into Raffinert.
- Do not remove structured repair requests.
- Do not weaken invariant enforcement.
- Do not weaken authoritative-scope checks.
- Do not add backward-compatibility wrappers.
- Do not hide dogfood from solution/verification.
- Do not delete the full-reseed baseline before comparison.
- Do not claim performance improvement without measurement.
- Do not change tests simply because new behavior is easier to implement.

---

# 20. Definition of Done

This fourth pass is complete only when all statements below are true.

## Correctness

- An unevaluated repair-enabled invariant receiving `Invalid` impact emits `RepairRequestInfo.Reason == Invalid` when violated.
- An unevaluated invariant receiving `Dirty` impact emits `Dirty` when violated.
- Multiple causes merge with `Invalid` dominance.
- `RepairWhenViolated()` remains eager only for affected repair-enabled invariants.
- Repair-disabled invariants remain lazy unless another explicit policy evaluates them.

## Proposed-state experiment

- There is a documented baseline using full runtime reseed.
- There is either a working isolated preview prototype or an explicit documented negative result explaining why it was rejected.
- A working preview, if kept, can query derived values, relations and invariant truth for the proposed state without changing committed runtime state.
- Stale preview use is rejected deterministically.

## EF dogfood

- Rejected save exposes structured repair request.
- Application-owned repair uses proposed-state information.
- If preview is accepted, rejected-save repair no longer requires `Compiled.CreateRuntime(seed entire tracked graph)`.
- Retry persists the repaired graph and commits runtime state only after durable DB success.

## Verification

- Solution Release build passes.
- Direct AllocationDogfood Release build passes.
- AllocationDogfood executable passes all scenarios.
- Core net8 passes.
- Core net10 passes.
- EF tests pass.
- Package consumers pass.
- Formatting passes.
- `git diff --check` passes.
- Working tree is clean after commit.

The goal is not to prove Raffinert is correct by construction. The goal is to determine whether proposed-state repair can be expressed with a smaller, safer abstraction than rebuilding a complete temporary runtime, while fixing the known repair-severity correctness bug first.