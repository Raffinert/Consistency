# Allocation dogfood third pass: build verification, EF repair contract, and explicit repair semantics

## Status and intent

This is a **follow-up implementation plan** for branch `plan/allocation-dogfood`, starting from commit `086c4df` (`Implement allocation dogfood core fixes`).

The previous pass introduced the right high-level concepts (`ItemMemberChanged`, `FromMembership`, `RepairWhenViolated`, structured repair requests) but the implementation must **not be accepted yet** because the allocation dogfood executable currently fails to build locally while the agent reported a successful solution build.

The important discovery is that `experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj` is **not part of `Raffinert.Consistency.sln`**. Therefore `dotnet build Raffinert.Consistency.sln` did not verify the dogfood project at all.

This plan has four goals:

1. reproduce and fix the real allocation-dogfood build failure without hiding it;
2. make it impossible for repository verification to miss the dogfood again;
3. make structured repair information available on the EF rejected-save path, not only the core `ApplyDetailed` path;
4. make the eager-evaluation semantics implied by `RepairWhenViolated()` explicit, tested, documented, and internally coherent.

The library is still release-candidate quality. **Do not preserve backward compatibility for an API shape that turns out to be wrong.** Prefer the cleanest final API and update all usages/tests/docs in one change.

---

# Non-negotiable rules for the implementing agent

Read this section before touching code.

1. **Do not start with framework changes.** First reproduce the allocation dogfood build failure and save the exact compiler errors in your working notes.
2. **Do not claim a build passed unless you ran the exact project build command listed below.**
3. Do not comment out, delete, skip, or weaken any dogfood scenario merely to obtain a green run.
4. Do not turn off `TreatWarningsAsErrors`.
5. Do not change target frameworks to avoid a compiler/package error.
6. Do not remove project references to make the executable compile.
7. Do not replace a failing scenario with an easier scenario.
8. Do not move business reallocation policy into Raffinert Core. `ReallocateDemand` remains application code.
9. Do not reintroduce callback-based repair APIs or mutable repair queues into the compiled consistency model.
10. Do not add compatibility aliases/obsolete wrappers for changed RC APIs. Update callers directly.
11. Do not claim `29/29` until the executable was built separately and run with `--no-build` from that exact output.
12. Keep ordinary EF usage ordinary: the dogfood must continue to mutate tracked objects and call normal `SaveChangesAsync()`.
13. Keep `Supply.ChangeCapacity(...)` Raffinert-free.
14. If the existing semantic model turns out to be wrong, change it explicitly and update documentation. Do not preserve an accidental behavior just because tests currently encode it.
15. Every new public API requires API tests/public API files according to existing repository conventions.

---

# Phase 0 — reproduce the actual dogfood build failure

## 0.1 Start clean

From repository root:

```powershell
git status

dotnet clean

dotnet build `
  experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj `
  -c Release
```

Do **not** run only the solution build.

## 0.2 Record all errors before editing

In your implementation notes, record:

```text
AllocationDogfood direct build before changes
---------------------------------------------
command:
<exact command>

errors:
<full compiler error list, including file, line, column, error code>
```

If more than one independent compiler error exists, classify each one:

```text
A. stale API usage after 086c4df
B. missing namespace/reference
C. accessibility/public API mismatch
D. type inference/generic API regression
E. package/framework mismatch
F. other
```

Do not guess the root cause before reading the compiler message.

## 0.3 Fix the minimum real cause

Fix the actual build issue.

Allowed examples:

- update dogfood code to the intended new public API;
- fix an accidental accessibility mismatch in a newly public contract;
- fix a broken generic signature introduced by the previous pass;
- update public API declarations if a newly public type is required.

Not allowed:

- weakening dogfood behavior;
- deleting `RepairRequestInfo` use;
- replacing structured repair handling with hard-coded repair triggers;
- removing projected membership from the sample;
- removing EF scenarios.

## 0.4 Immediate checkpoint

Before doing any other phase, these must pass:

```powershell
dotnet build `
  experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj `
  -c Release

dotnet run `
  --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj `
  -c Release `
  --no-build
```

Expected executable result:

```text
Allocation dogfood scenarios passed: 29.
```

If the project builds but one scenario fails, stop and diagnose that scenario before proceeding.

---

# Phase 1 — close the verification hole permanently

The previous report said "Solution build: 0 warnings, 0 errors" even though the dogfood project was not part of the solution. This must not be possible again.

## 1.1 Choose one canonical repository verification strategy

Preferred strategy for this branch: **add the allocation dogfood project to `Raffinert.Consistency.sln`** under an `experiments` solution folder if one exists, otherwise create/use a suitable solution folder.

Reason: the dogfood has become architecture-level regression coverage. A normal solution build should compile it.

If repository structure strongly rejects experiment projects in the solution, the only acceptable alternative is to add an existing canonical repository verification script/CI step that explicitly builds and runs it. Do not invent a second ad-hoc verification system if the repository already has one.

## 1.2 Required behavior after the change

This command must compile the allocation dogfood transitively:

```powershell
dotnet build Raffinert.Consistency.sln -c Release
```

Then the executable must still be independently runnable:

```powershell
dotnet run `
  --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj `
  -c Release `
  --no-build
```

## 1.3 Verification-report contract

Future agent reports must contain separate lines, not one aggregated claim:

```text
Solution build: PASS
Allocation dogfood direct build: PASS
Allocation dogfood executable: 29/29 PASS
Core net8 tests: PASS
Core net10 tests: PASS
EF tests: PASS
Package consumers: PASS
Formatting/diff checks: PASS
```

If any command was not run, say `NOT RUN`; never infer it from another command.

---

# Phase 2 — preserve the good parts from commit 086c4df

Before changing semantics, protect the parts that are correct.

The following behaviors must remain:

## 2.1 Directional relation-item severity

For fulfillment/allocation quantity:

```text
increase  -> Invalid
decrease  -> Dirty
```

via typed member-specific configuration, e.g.:

```csharp
.ItemMemberChanged(
    x => x.Quantity,
    (oldValue, newValue) => newValue > oldValue
        ? DependencySeverity.Invalid
        : DependencySeverity.Dirty)
```

Membership add/remove policies remain independent from item-member changes.

Required regression tests:

- member-specific rule overrides generic `ItemChanged` fallback for that member;
- two changed item members merge with `Invalid` dominance;
- unrelated changed member uses `ItemChanged` fallback;
- membership addition/removal does not incorrectly call the item-member classifier;
- invalid enum returned from classifier fails deterministically.

## 2.2 Single semantic authority for allocation compatibility

Keep:

```csharp
var candidateSupplies = model.Relation(demands, supplies)
    .Where(...);

var hasCompatibleSupply = model.Derived(allocations)
    .FromMembership(
        candidateSupplies,
        allocation => allocation.Demand,
        allocation => allocation.Supply);
```

Do not restore a second duplicated compatibility predicate.

## 2.3 Application-owned repair

Keep replacement ranking and convergence in the application:

```text
Raffinert -> what invariant is violated / what source needs attention
Application -> which allocation moves, where it moves, retry/convergence policy
```

Do not move `OrderBy`, replacement choice, or "no replacement" workflow into Raffinert Core.

## 2.4 No callback state in compiled model

Compiled definitions must contain no application repair callback and no mutable repair queue.

Two runtimes sharing the same compiled model must produce isolated structured request data.

---

# Phase 3 — decide and formalize `RepairWhenViolated()` evaluation semantics

The current implementation makes a repair-enabled invariant eager for affected sources because the runtime must evaluate it in order to know whether a repair request should exist.

That is logically defensible, but it must be an explicit contract, not an accidental side effect.

## 3.1 Adopt this contract unless code proves it impossible

`RepairWhenViolated()` means:

> For every source affected by the invariant's dependency graph during a mutation/plan, evaluate the invariant before producing the operation result. Emit repair data only if the evaluated predicate is false.

Consequences:

```text
repair-disabled invariant
    affected -> Dirty/Invalid according to normal lazy semantics

repair-enabled invariant
    affected -> evaluate now
             -> upstream values required by the predicate are made Fresh
             -> invariant becomes Valid or Violated
             -> RepairRequest only for Violated
```

This is intentionally eager **only because repair creation requires proof of violation**.

## 3.2 Do not conflate dependency severity and violation state

Keep these concepts separate:

```text
Dirty / Invalid
    = freshness/usability of dependency result

Valid / Violated
    = evaluated invariant truth

RepairRequested
    = policy output created after evaluated Violated
```

Do not add `Violated` to `DependencySeverity`.
Do not represent `RepairRequired` as another freshness state.

## 3.3 Simplify internal reaction logic if current behavior is confusing

Review `InvariantRuntimeState.ApplyImpact` and `EvaluateAffectedInvariants`.

The current code contains special behavior equivalent to:

```text
EvaluateImmediately + RepairWhenViolated
```

being routed differently from `EvaluateImmediately` without repair.

Refactor if needed so that there is one understandable rule:

1. propagation marks the invariant affected (`Dirty`/`Invalid`);
2. evaluation policy selects which affected invariants are evaluated now;
3. evaluation sets `Valid`/`Violated`;
4. violation policy optionally emits structured repair request.

Avoid hidden special cases such as "do not enqueue immediate evaluation when repair policy is set" unless they are necessary and covered by tests.

## 3.4 Required tests

Add focused Core tests for all of these:

### A. safe Dirty transition with repair policy

```text
initial invariant = Valid
source change -> Dirty impact
RepairWhenViolated configured
predicate remains true

expected:
upstream derived = Fresh after operation
invariant = Valid
RepairRequests = 0
```

### B. unsafe Dirty transition with repair policy

Use a model where a Dirty-classified transition can still make the predicate false.

Expected:

```text
invariant = Violated
RepairRequests = exactly 1
```

This test proves repair is based on evaluation, not severity.

### C. unsafe Invalid transition with repair policy

Expected:

```text
upstream derived = Fresh after eager re-evaluation
invariant = Violated
RepairRequests = exactly 1
```

### D. affected but repair-disabled invariant stays lazy

Expected:

```text
no RepairWhenViolated
source change -> Dirty
upstream remains Dirty until explicit Evaluate/materialize/save policy requires freshness
invariant remains Dirty
RepairRequests = 0
```

### E. no duplicate repair request for same invariant/source within one operation

Multiple causes in the same mutation set may reach one invariant.

Expected:

```text
one invariant/source -> at most one repair request per operation result
```

### F. valid -> violated -> valid across separate operations

Expected:

```text
op1 violation -> request exists
op2 repair/safe mutation -> invariant Valid, no stale request leaks
```

---

# Phase 4 — make EF rejected-save repair information first-class

This is the most important functional gap after the build failure.

Today core usage has:

```csharp
var application = runtime.ApplyDetailed(...);
application.Result.RepairRequests
```

but ordinary EF `SaveChangesAsync()` rejects enforced invariant violations with `ConsistencyInvariantViolationException` exposing only `Violations`.

That creates an undesirable split:

```text
Core mutation -> structured repair information available
EF mutation   -> only violation list available
```

The production EF path must not throw away repair information that the prepared impact plan already computed.

## 4.1 Desired public exception contract

Use the smallest clean contract. Preferred shape:

```csharp
public sealed class ConsistencyInvariantViolationException : Exception
{
    public IReadOnlyList<PlannedInvariantEvaluation> Violations { get; }
    public IReadOnlyList<RepairRequestInfo> RepairRequests { get; }
}
```

If implementation architecture strongly suggests a dedicated value object, use:

```csharp
public sealed record ConsistencyViolationResult(
    IReadOnlyList<PlannedInvariantEvaluation> Violations,
    IReadOnlyList<RepairRequestInfo> RepairRequests);
```

and expose that on the exception.

Do **not** expose internal `RuntimePolicyActions`.
Do **not** expose callbacks.
Do **not** expose a mutable collection.

Since this is RC, choose the cleaner public API and update API baseline files directly.

## 4.2 Preserve filtering semantics

The exception should expose:

- `Violations`: only violated invariants that are actually enforced by the EF mappings for this save;
- `RepairRequests`: structured repair requests corresponding to violated repair-enabled invariants relevant to the rejected plan.

Do not blindly return unrelated repair requests for non-enforced invariants if that would make the save exception misleading.

If the current plan can contain both enforced and non-enforced invariant requests, filter explicitly by invariant ID/definition.

## 4.3 No runtime commit on rejection

When planning finds an enforced violation:

```text
SQL must not execute
runtime version must not advance
materialized mirrors must not remain partially changed
repair data must still be available to the caller
```

The repair payload must be data copied/projected from the plan/result; it must not require committing runtime state.

## 4.4 Required EF test

Add a focused test like:

```csharp
var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
    () => db.SaveChangesAsync());

var request = Assert.Single(error.RepairRequests);
Assert.Equal("supply-capacity-valid", request.DefinitionKey);
Assert.Same(supply, request.Source);
Assert.Contains(error.Violations, v => v.DefinitionKey == "supply-capacity-valid");
Assert.Equal(versionBefore, runtime.Version);
```

Also verify database values remain unchanged.

## 4.5 Required EF negative case

For an enforced invariant that is violated but **not** configured with `RepairWhenViolated()`:

```text
save rejected
Violations contains invariant
RepairRequests is empty
```

This prevents "every violation implies repair" from leaking into the API.

---

# Phase 5 — dogfood EF flow must actually consume repair information

The existing EF retry scenario currently demonstrates:

```text
save fails
application already knows what to change
application directly moves allocation
retry save
```

That does not dogfood the new structured repair contract.

Change/add a scenario so the path is:

```text
1. mutate Supply via Supply.ChangeCapacity(...)
2. ordinary SaveChangesAsync()
3. catch ConsistencyInvariantViolationException
4. inspect exception.RepairRequests
5. translate request to application repair requirement
6. perform application-owned reallocation
7. retry ordinary SaveChangesAsync()
8. verify persisted graph and mirrors
```

Important: do not make `SaveChangesAsync()` itself perform reallocation. The first save must still reject invalid state.

## 5.1 Prefer reusing the same repair translator

If practical, refactor `ReallocateDemand` so it can process `IEnumerable<RepairRequestInfo>` from either:

- Core `RuntimeApplyResult.RepairRequests`, or
- EF `ConsistencyInvariantViolationException.RepairRequests`.

The application should not need two incompatible adapters for the same repair request type.

## 5.2 Preserve rejected-plan semantics

Because runtime state has not committed when EF rejects the save, ensure the application repair code uses a supported runtime/current-object path.

Do not assume a rejected plan has become committed runtime state.

If the current `ReallocateDemand.Process(...)` depends on runtime indexes that would only be updated by committing the rejected mutation, explicitly identify that issue and solve it correctly rather than forcing the runtime version forward.

This is an important test: repair data from a rejected EF plan is useful only if application repair can operate against the tracked current domain graph safely.

If a new API is required for "evaluate against planned/tracked current state", document the gap before adding anything. Prefer using existing EF/session planning facilities if possible.

---

# Phase 6 — projected relation membership hardening

`FromMembership(...)` removes the duplicated predicate, which is good. Now prove its routing is exact enough for production use.

Add/retain focused tests for:

1. relation left object's join key changes;
2. relation right object's join key changes;
3. source's left selector retargets (`allocation.Demand` changes);
4. source's right selector retargets (`allocation.Supply` changes);
5. selected left object removal;
6. selected right object removal;
7. unrelated relation pair changes do not affect unrelated projected sources;
8. multiple allocations projecting the same `(Demand, Supply)` pair are all affected;
9. one allocation retarget does not affect another allocation that retains the old pair;
10. causal diagnostics report the underlying relation definition and both selector paths.

For each scenario verify:

```text
correct affected source set
correct derived state
correct invariant state after evaluation policy
no global invalidation of every Allocation
```

Do not fall back to duplicating the candidate predicate in tests or implementation.

---

# Phase 7 — documentation corrections

The dogfood documentation is now stale in two directions.

## 7.1 Update allocation README

The README currently talks about deliberately retained behaviors that the second pass claims to have fixed (directional aggregate behavior, repair escalation, duplicate compatibility relation).

Rewrite that paragraph.

The README should now explain:

```text
- experiment exercises directional impact classification;
- allocation compatibility projects membership from the candidate relation;
- repair-enabled invariants are evaluated when affected so repair requests represent proven violations;
- Core reports mutations explicitly;
- EF observes tracked changes at SaveChanges;
- closed-world scope remains a host assertion.
```

## 7.2 Correct DOGFOOD.md claim about EF

Do not write:

> "the EF save result exposes RepairRequestInfo"

unless the final implementation actually exposes repair requests through the rejected-save exception/result.

After Phase 4, document the exact API:

```text
Core: ApplyDetailed(...).Result.RepairRequests
EF rejected save: ConsistencyInvariantViolationException.RepairRequests
```

## 7.3 Explicitly document eager semantics

Add a short section:

```text
RepairWhenViolated is not merely a label on a future handler.
To prove whether repair is needed, Raffinert evaluates affected repair-enabled invariants during mutation planning/application. Upstream derived values required by that evaluation may therefore become Fresh even when their original dependency impact was Dirty or Invalid.
```

Also state that non-repair invariants retain their configured lazy reaction semantics unless another policy (e.g. EF enforcement / explicit immediate reaction) requests evaluation.

## 7.4 Keep limitations honest

Keep these limitations:

- `ConsistencyScope.Complete(...)` is host asserted;
- Core arbitrary POCO mutation requires explicit `Change` reporting;
- replacement ranking/convergence/unresolved workflow are application concerns;
- diagnostic traces do not yet explain evaluated numeric/business operands.

Do not present these as fixed.

---

# Phase 8 — direct build + full regression verification

Run these commands from a clean working tree state after implementation.

## 8.1 Dogfood direct build first

```powershell
dotnet clean

dotnet build `
  experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj `
  -c Release

dotnet run `
  --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj `
  -c Release `
  --no-build
```

Do not proceed if this fails.

## 8.2 Solution

```powershell
dotnet build Raffinert.Consistency.sln -c Release
```

Verify from MSBuild output that `Raffinert.Consistency.AllocationDogfood` is actually built. Do not infer this only from success code.

## 8.3 Core tests per target framework

Run according to existing repository conventions. At minimum verify both target frameworks explicitly if the test project multi-targets:

```powershell
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build
```

If repository build layout requires build during test, remove `--no-build`; do not fake compatibility.

## 8.4 EF tests

```powershell
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release
```

## 8.5 Package consumer tests

Run the existing repository commands for:

```text
CoreNet8
CoreNet10
EfNet10
```

Do not replace these with unit tests.

## 8.6 Formatting / diff checks

Run repository-standard formatting and diff verification.

Then:

```powershell
git status --short
git diff --check
```

Expected working tree after commit: clean.

---

# Required new acceptance matrix

The final implementation must satisfy this matrix.

| Scenario | Expected dependency state | Expected invariant result | Repair request |
|---|---|---|---|
| Capacity 10 -> 15, still valid | evaluated to Fresh for repair-enabled path | Valid | none |
| Capacity 10 -> 9, still valid | evaluated to Fresh | Valid | none |
| Capacity 10 -> 6, over capacity | evaluated to Fresh | Violated | exactly one |
| Fulfillment 7 -> 3 | directional Dirty, then Fresh because repair-enabled invariant is checked | Valid | none |
| Fulfillment 3 -> 7 causing overflow | directional Invalid, then Fresh during check | Violated | exactly one |
| Allocation 5 -> 2 | directional Dirty, then Fresh during check | Valid | none |
| Allocation 5 -> 8 causing overflow | directional Invalid, then Fresh during check | Violated | exactly one |
| Candidate pair removed for selected allocation | membership-derived value evaluated | Violated | exactly one compatibility request |
| Unrelated candidate pair change | unrelated projected source stays unaffected | unchanged | none |
| Same dependency change on invariant without RepairWhenViolated | remains normal lazy Dirty/Invalid until separately evaluated | Dirty/Invalid | none |
| EF save with enforced repair-enabled violation | plan rejected, runtime uncommitted | Violated reported | exception exposes request |
| EF save with enforced non-repair violation | plan rejected, runtime uncommitted | Violated reported | exception request list empty |

---

# Required review of public API shape

Before finishing, inspect the resulting IntelliSense surface.

The intended public conceptual chain should remain small:

```csharp
model.Derived(set)
    .From(relation)
    .Impact(...)
    .Sum(...)
    .MaterializeTo(...)
    .Named(...);

model.Derived(sourceSet)
    .FromMembership(relation, leftSelector, rightSelector)
    .Named(...);

model.Invariant(set)
    .From(derived)
    .Must(...)
    .RepairWhenViolated()
    .Named(...);
```

EF failure handling should read naturally:

```csharp
try
{
    await db.SaveChangesAsync();
}
catch (ConsistencyInvariantViolationException error)
{
    foreach (var repair in error.RepairRequests)
    {
        // application decides what to do
    }
}
```

If the implementation requires callers to reach through internal planning types or policy-action types, stop and redesign the public boundary.

---

# Things that must NOT be added in this pass

Do not expand scope into unrelated features.

Specifically do not add:

- workflow engine abstractions;
- automatic reallocation;
- background repair dispatch;
- event bus integration;
- new aggregate operators unrelated to this dogfood;
- automatic database completeness detection;
- source generators/analyzers;
- general mutation interception for arbitrary POCOs;
- rich business-operand diagnostics;
- new domain-event abstractions;
- compatibility wrappers for removed RC API.

Those may be future work. This pass is about making the current semantics trustworthy and verifiable.

---

# Definition of Done

Do not report completion until **every** item below is true.

## Build integrity

- [ ] The original direct dogfood build failure was reproduced and its exact cause was recorded before fixing.
- [ ] `Raffinert.Consistency.AllocationDogfood.csproj` builds directly in Release with warnings as errors.
- [ ] The allocation dogfood is now included in canonical repository verification (preferably solution build).
- [ ] Solution build cannot succeed while this dogfood project has a compiler error.

## Semantics

- [ ] Directional `ItemMemberChanged` behavior remains correct.
- [ ] `FromMembership` remains the single compatibility semantic authority.
- [ ] `RepairWhenViolated()` creates requests only after evaluated false predicate.
- [ ] Its eager affected-source evaluation semantics are explicit and covered by tests.
- [ ] Non-repair invariants retain lazy behavior unless another explicit policy requires evaluation.
- [ ] No callback-based repair state exists in the compiled model.

## EF contract

- [ ] Ordinary `SaveChangesAsync()` rejecting an enforced repair-enabled invariant exposes structured repair request data to the caller.
- [ ] Rejected save does not advance runtime version.
- [ ] Rejected save does not execute/persist unsafe SQL state.
- [ ] Enforced invariant without repair policy still rejects but exposes no repair request.
- [ ] Dogfood EF retry flow consumes the structured repair request instead of hard-coding the reason for repair.

## Dogfood behavior

- [ ] All original 29 allocation scenarios still pass, unless one was intentionally replaced by a stricter equivalent; any replacement must be documented.
- [ ] New EF repair-request scenario passes.
- [ ] Projected membership selector-retarget and underlying-relation routing tests pass.
- [ ] Application-owned reallocation remains outside Raffinert Core.
- [ ] `Supply.ChangeCapacity` remains free of Raffinert calls.

## Documentation

- [ ] allocation README no longer describes already-fixed old limitations as current behavior.
- [ ] DOGFOOD.md accurately describes Core and EF structured-repair surfaces.
- [ ] eager `RepairWhenViolated()` evaluation is documented.
- [ ] fundamental limitations remain documented honestly.

## Verification report

Final agent report must include the exact result of each command separately:

```text
Direct AllocationDogfood Release build: PASS/FAIL
AllocationDogfood executable: X/X PASS/FAIL
Solution Release build: PASS/FAIL
Core net8 tests: N/N PASS/FAIL
Core net10 tests: N/N PASS/FAIL
EF tests: N/N PASS/FAIL
Package consumer CoreNet8: PASS/FAIL
Package consumer CoreNet10: PASS/FAIL
Package consumer EfNet10: PASS/FAIL
Formatting: PASS/FAIL
git diff --check: PASS/FAIL
Working tree clean: YES/NO
```

Do not use phrases such as "all verification passed" without the command-by-command table above.

---

# Final implementation report format

Use exactly these sections in the completion report:

## 1. Reproduced build failure

- exact error(s)
- root cause
- exact fix

## 2. Verification boundary

- how the dogfood is now guaranteed to compile in canonical repository verification

## 3. Repair semantics

- exact meaning of `RepairWhenViolated()`
- proof that safe Dirty transitions do not produce repair work
- proof that false predicates do produce exactly one request

## 4. EF structured repair contract

- final public API
- behavior on rejected save
- behavior when invariant has no repair policy

## 5. Projected membership

- selector and relation-change routing coverage
- confirmation that compatibility predicate is not duplicated

## 6. DDD/application boundary

Confirm explicitly:

```text
Supply.ChangeCapacity: no Raffinert dependency
Raffinert: detects/evaluates consistency impact and produces repair data
Application: chooses replacement/reallocation policy
EF: observes tracked mutation and enforces at persistence boundary
```

## 7. Remaining limitations

Keep at least:

- host-asserted authoritative scope;
- explicit mutation reporting in Core;
- no evaluated business-operand explanation in diagnostics;
- application-owned repair convergence/ranking.

## 8. Verification

Paste the required command-by-command result table.

Only after all eight sections are complete should the agent commit and push to `plan/allocation-dogfood`.
