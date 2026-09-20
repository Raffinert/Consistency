# Allocation dogfood fifth pass: EF preview cost, validation strategy, and multi-step repair convergence

## Purpose

This is a **literal step-by-step implementation plan for a weak agent**. Follow the order. Do not redesign unrelated APIs. Do not promote `ConsistencyPreview` to a public API. Do not delete the current full-runtime reseed baseline. Do not claim the fourth-pass preview performance result is representative of EF until the EF path itself is measured.

The repository is still in release-candidate stage. Backward compatibility is not a goal, but this pass is intentionally narrow: **do not make breaking API changes unless a failing test or benchmark in this plan proves they are required**.

This pass has four goals:

1. Measure the actual EF rejected-save preview path rather than only the core `Runtime.CreatePreview(plan)` path.
2. Determine whether EF stale-state validation is accidentally O(graph) on every preview query.
3. Prove multi-step / second-order repair convergence without rebuilding a full runtime.
4. Document the exact staleness guarantees of the internal preview so we do not overclaim snapshot semantics for arbitrary unreported POCO changes.

Expected branch: `plan/allocation-dogfood`.

Expected starting implementation: commit `ed15e30b40ac9e100ea318b6e041c381561b5b6f` or later if only documentation/plans were added afterwards.

---

# 0. Do not touch code until you can explain the current flow

Read these files first:

- `src/Raffinert.Consistency/Runtime/ConsistencyPreview.cs`
- `src/Raffinert.Consistency/Runtime/MutationCommit.cs`
- `src/Raffinert.Consistency/Runtime/RelationRuntimeState.cs`
- `src/Raffinert.Consistency/Runtime/ObjectSetRuntime.cs`
- `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreSession.cs`
- `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs`
- `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyPersistencePolicy.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/EfScenarios.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/Repair.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/ProposedStateBenchmark.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md`
- `tests/Raffinert.Consistency.Tests/ProposedStatePreviewTests.cs`
- relevant EF integration tests around rejected save / injected runtime / session lifetime.

Before changing anything, write down the current rejected-save flow in your own notes:

```text
tracked EF mutation
    -> EF session captures current unit of work
    -> Raffinert prepares speculative plan against committed runtime
    -> enforced invariant is violated
    -> ConsistencyInvariantViolationException
    -> EF session retains rejected plan + fingerprint + baseline revision
    -> application calls CreateRejectedPreview(exception)
    -> preview queries proposed state
    -> application mutates tracked repair target
    -> preview becomes stale
    -> application calls SaveChangesAsync again
    -> EF re-plans from unchanged committed runtime baseline
    -> eventually SQL succeeds
    -> runtime commits final plan
```

Also write down current preview validation:

```text
preview.Related / Evaluate / GetState
    -> preview.Validate()
    -> runtime plan validation
    -> EF external validation (for rejected EF preview)
    -> CaptureFingerprint(...)
```

Do not proceed until you can point to the exact methods implementing these arrows.

---

# 1. Preserve fourth-pass fixes before starting

Do not regress these already-correct behaviors:

- `RepairRequestInfo.Reason` comes from current propagated severity.
- `Invalid` dominates `Dirty` for one invariant/source in one operation.
- repair request deduplication is operation-scoped.
- `RepairWhenViolated()` eagerly evaluates affected repair-enabled invariants.
- non-repair invariants remain lazy unless another policy requires evaluation.
- rejected EF save does not advance committed runtime state.
- preview does not install proposed state into committed runtime.
- preview is still internal/experimental.
- full-runtime reseed repair path remains available as a baseline.

Run the existing relevant tests before any modification. If they are red, stop and fix the branch before continuing.

Minimum pre-change commands:

```powershell
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
```

Record counts. Do not rely on remembered counts.

---

# 2. Add explicit instrumentation for EF rejected-preview validation cost

## 2.1 Problem to prove or disprove

The current EF preview uses an external validation callback. `ConsistencyPreview.Validate()` runs for every public preview read. The EF callback calls `ValidateRejected(...)`, which captures a fresh mutation fingerprint from the `DbContext`.

Therefore one repair decision such as:

```text
Evaluate invariant
Related current allocations
Related candidate supplies
Evaluate remaining capacity for candidate A
Evaluate remaining capacity for candidate B
```

may cause multiple full fingerprint captures.

Do not optimize yet. First measure it.

## 2.2 Add internal diagnostics, not public API

Add a narrowly scoped internal diagnostic counter to the EF session or fingerprint capture path so tests/benchmarks can answer:

```text
How many rejected-preview fingerprint validations happened?
```

Acceptable shapes:

```csharp
internal long RejectedPreviewValidationCount { get; }
```

or a test-only diagnostic object.

Rules:

- do not expose this in the public package surface;
- do not add a generic metrics subsystem;
- do not alter runtime semantics;
- increment exactly once per expensive EF fingerprint validation, not once per trivial local check.

## 2.3 Add a regression test proving current behavior

Before optimization, add a test that:

1. creates rejected EF preview;
2. performs multiple preview queries;
3. records validation/fingerprint count;
4. proves the count is greater than one under current implementation.

This test is diagnostic evidence and may be changed later when optimization is implemented.

Do not fake graph size yet; this is only call-count proof.

---

# 3. Build a real EF end-to-end preview benchmark

The existing `ProposedStateBenchmark` compares:

```text
A. CreateRuntime(seed full graph)
B. Runtime.CreatePreview(plan)
```

That is useful for core preview cost, but it does **not** include EF session validation/fingerprinting.

Keep the existing benchmark unchanged as the **core preview benchmark**.

Add a second benchmark specifically for EF rejected preview.

Suggested name:

```text
RejectedEfPreviewBenchmark
```

or keep it in `ProposedStateBenchmark` but label output clearly as separate measurements.

## 3.1 Required benchmark paths

Measure at least these paths separately:

### Baseline A — full runtime reseed

```text
CreateRuntime(seed all tracked objects)
+ run repair-decision query sequence
```

### Preview B — core preview only

Existing measurement:

```text
Runtime.CreatePreview(plan)
+ run repair-decision query sequence
```

### Preview C — actual EF rejected preview

Real path:

```text
DbContext tracked graph
-> rejected SaveChanges
-> session.CreateRejectedPreview(exception)
-> run same repair-decision query sequence
```

The third path is the one that matters for EF conclusions.

## 3.2 Use the same logical query workload for all paths

Do not benchmark only one `RemainingCapacity` lookup.

Use a small but realistic repair-decision workload approximating `ReallocateDemand`:

```text
1. Evaluate violated capacity invariant.
2. Related(SupplyAllocations, badSupply).
3. Pick one affected allocation.
4. Related(CandidateSupplies, allocation.Demand).
5. Evaluate RemainingCapacity for candidates until a suitable replacement is found.
6. Optionally evaluate CompatibilityInvariant for the allocation if this is part of the same real path.
```

Make the candidate ordering deterministic.

The exact same logical workload must be used for baseline and preview comparisons.

## 3.3 Graph sizes

At minimum:

```text
100 allocations
1,000 allocations
10,000 allocations
```

Optionally add 50,000 if runtime is reasonable and memory permits.

Do not reduce graph size because EF path is slower. Record the result.

## 3.4 Measure separately

For each graph size report:

```text
reseed setup ms
reseed repair-query ms
reseed allocated bytes

core-preview setup ms
core-preview repair-query ms
core-preview allocated bytes

EF-preview creation ms
EF-preview repair-query ms
EF-preview allocated bytes
EF fingerprint validations per repair decision
```

If practical also record:

```text
tracked entity count
mutation count
candidate relation size
number of preview reads in repair decision
```

Do not present one combined number that hides where cost is coming from.

## 3.5 Benchmark hygiene

- warm up each path;
- use the same graph and same mutation semantics;
- use medians from at least 5 measured iterations;
- clearly label that this is an in-process development benchmark, not a formal BenchmarkDotNet result;
- do not mix debug and release results;
- do not claim sub-millisecond precision as statistically meaningful beyond the experiment.

---

# 4. Decide whether per-query EF fingerprinting is a real problem

After the end-to-end benchmark, classify the current behavior.

Choose one:

```text
A. ACCEPTABLE
B. OPTIMIZE
C. INCONCLUSIVE
```

Do not optimize before this decision.

Use practical criteria, not arbitrary microbenchmarks.

`OPTIMIZE` is justified if one or more are clearly true:

- EF repair-query cost scales badly with graph size primarily because fingerprint capture repeats per preview read;
- one repair decision performs many duplicate fingerprint captures;
- repeated validation materially erases the preview advantage over full reseed;
- allocated bytes or `DetectChanges` work is obviously excessive.

If results are acceptable, document them and skip Phase 5 implementation work. Still keep the tests and benchmark.

---

# 5. If justified, optimize validation without weakening stale detection

Only execute this phase if Phase 4 concludes `OPTIMIZE`.

## 5.1 Non-negotiable rule

Do not replace stale detection with wishful thinking.

A preview must still fail deterministically when:

- committed runtime baseline revision changes;
- relevant tracked EF state changes after rejection;
- prepared domain assumptions no longer hold;
- the retained rejected plan is no longer the current rejected plan for that EF session;
- the preview is disposed.

## 5.2 Preferred direction

The likely problem is **recomputing the same EF fingerprint for every preview read**, not validation itself.

Explore a validation lease / snapshot token for one stable preview query session.

Conceptually:

```text
CreateRejectedPreview
    -> validate rejected plan once
    -> capture validation token / mutation fingerprint revision
    -> preview performs many reads cheaply
    -> any relevant EF tracked change invalidates token/view
```

However, EF does not emit a perfect "all relevant property changed" event for arbitrary POCO mutation, so do not invent a guarantee you cannot prove.

Possible safe strategies to investigate:

### Strategy A — validate once at preview creation only

Only acceptable if relevant tracked mutation after preview creation can be detected by another reliable mechanism before any stale read.

If not provable, reject this strategy.

### Strategy B — explicit query batch / repair decision scope

Example concept:

```csharp
using var preview = session.CreateRejectedPreview(error);
preview.ValidateCurrentTrackedState(); // expensive once

// reads rely on the validated batch until caller mutates tracked state
```

This is internal experimental API only.

The caller must not mutate tracked consistency-relevant state while continuing to use the same preview.

### Strategy C — cheap tracker version plus fallback fingerprint

Only if a reliable EF/session-local revision can be maintained for all relevant state changes. Do not implement this if it requires fragile property-change instrumentation or silent gaps.

## 5.3 Do not solve arbitrary POCO observability here

The core library cannot detect arbitrary property changes that were never reported.

The EF adapter may use `DetectChanges`, but do not build a second change tracker.

If safe cheap invalidation cannot be implemented cleanly, keep per-read validation and document the cost. Correctness wins.

## 5.4 Tests required for any optimization

If validation behavior changes, add tests for:

1. multiple reads without mutation remain valid;
2. relevant scalar tracked mutation invalidates preview;
3. relevant navigation retarget invalidates preview;
4. add/remove tracked entity invalidates preview;
5. runtime baseline advancement invalidates preview;
6. irrelevant tracked mutation behaves according to current semantic-member filtering contract;
7. disposed preview always fails;
8. a newly rejected save supersedes the older rejected plan.

Do not remove existing stale-state tests.

---

# 6. Add a true multi-step repair dogfood scenario

The current rejected-save repair scenario only needs one reallocation. That does not prove second-order repair handling.

Add a new scenario that requires **at least two repair mutations** before the save can succeed.

Suggested name:

```text
EfRejectedSave_MultiStepRepair_ReplansUntilConsistent
```

## 6.1 Construct a deterministic graph

Use the existing neutral domain.

Example shape:

```text
Demand D1 -> Allocation A1 quantity 4 -> Supply S1
Demand D2 -> Allocation A2 quantity 4 -> Supply S1
Fulfillment on S1 = 2
S1 capacity initially = 12

S2 compatible, enough for exactly one moved allocation
S3 compatible, enough for the second moved allocation

Mutation:
S1 capacity 12 -> 2
```

After the mutation, S1 must release at least two allocations before its invariant is valid.

Choose capacities so:

- one reallocation is insufficient;
- second reallocation makes graph valid;
- candidate selection is deterministic by current lowest-Id rule;
- no accidental single move can fix the problem.

Do not change business ranking just for the test.

## 6.2 Required flow

The scenario must prove:

```text
Save attempt 1
    -> rejected
    -> preview 1
    -> repair mutation 1

Save attempt 2
    -> still rejected
    -> preview 2
    -> repair mutation 2

Save attempt 3
    -> succeeds
```

Assert exact attempt count.

Do not use `for (attempt < 3)` and then merely assert success. Use an application convergence limit larger than required (for example 10) and separately assert the real number of attempts was exactly the expected value.

## 6.3 Verify no full-runtime reseed

Instrument or structure the test so the preview path cannot silently call:

```csharp
Compiled.CreateRuntime(seed => ...)
```

The baseline method may remain in the codebase but must not be used by this scenario.

## 6.4 Verify committed runtime semantics

Before final successful save:

```text
runtime.Version remains unchanged
```

After final successful save:

```text
runtime.Version advances once for the successful final plan
```

Do not let rejected intermediate plans install partial runtime state.

---

# 7. Add a second-order repair scenario on another source

The multi-step capacity scenario proves repeated repair of the same violated source. Add a separate scenario proving a repair can create a new problem elsewhere.

Suggested shape:

```text
S1 is over capacity.
A1 is moved from S1 to S2.
That move makes S2 over capacity.
A second rejected plan now produces a repair request for S2.
A1 or another allocation then moves from S2 to S3.
Final graph becomes valid.
```

The scenario must assert:

- first rejected save repair request identifies S1;
- after repair mutation, first preview becomes stale;
- second save is rejected for S2 (not S1 only);
- second preview reflects the new proposed graph;
- second repair is application-owned;
- final save succeeds;
- final persisted allocations are deterministic;
- final derived `RemainingCapacity` mirrors/totals are correct;
- no temporary runtime was rebuilt from the complete graph.

Do not cheat by manually creating the second repair request. It must come from Raffinert planning the new tracked state.

---

# 8. Make repair convergence explicit in application code

Current application code returns after one proposed-state mutation because the current preview becomes stale. That is acceptable, but the convergence model must be explicit.

Refactor the dogfood orchestration so the outer application workflow owns:

```text
maximum save/repair attempts
save attempt count
rejected-plan count
repair mutation count
unresolved repair termination
successful convergence
```

Keep preview itself immutable/read-only.

Do **not** add staged mutable preview unless the fifth-pass experiment proves repeated re-plan is unusably expensive.

Recommended application-level pseudocode:

```csharp
for (var attempt = 1; attempt <= maxAttempts; attempt++)
{
    try
    {
        await db.SaveChangesAsync();
        return Success(attempt);
    }
    catch (ConsistencyInvariantViolationException error)
    {
        using var preview = session.CreateRejectedPreview(error);
        var repair = repairService.ApplyOneStep(preview, error.RepairRequests);
        if (!repair.Changed)
            return Unresolved(attempt, error.RepairRequests);
    }
}

throw new RepairDidNotConvergeException(...);
```

This is dogfood application code, not a new core workflow engine.

---

# 9. Document exact preview staleness guarantees

The fourth-pass report currently risks implying more than the implementation can know.

Update `DOGFOOD.md` and, if appropriate, internal architecture docs with a precise table.

Required distinction:

| Drift type | Detected? | Mechanism |
| --- | --- | --- |
| committed runtime version/baseline revision changes | yes | runtime plan validation |
| prepared mutation member changes | yes | prepared domain assumptions |
| relevant EF tracked mutation after rejected save | yes | EF mutation fingerprint / validation |
| EF add/remove/navigation changes represented in tracked mutation capture | yes | EF fingerprint |
| arbitrary unreported core POCO mutation outside prepared mutation | **not guaranteed** | core has no arbitrary mutation observer |

Do not say “preview rejects domain drift” without qualification.

Use wording such as:

> Preview detects runtime-plan drift and prepared-mutation drift. The EF rejected-preview integration also detects changes visible to the adapter's relevant tracked-state fingerprint. The dependency-free core cannot detect arbitrary POCO mutations that were never reported as changes.

This is a fundamental integration boundary, not a bug to hide.

---

# 10. Add a test documenting unreported core POCO drift limitation

This test is documentation-by-executable-specification.

Create a core preview fixture with at least two objects.

1. Prepare a mutation affecting object A.
2. Create preview.
3. Mutate consistency-relevant object B directly **without reporting a Change**.
4. Demonstrate current behavior explicitly.

Do not force a desired outcome.

If preview reads B's current CLR value and therefore reflects the unreported mutation, document that this is not a validated snapshot guarantee.

If it fails for another reason, document that.

The test name should make the limitation obvious, for example:

```text
Preview_DoesNotGuaranteeDetectionOfUnreportedCorePocoMutation
```

Do not turn this into a test that expects Raffinert to detect magic mutation observation.

---

# 11. Do not optimize preview relation scans in this pass unless measurements demand it

Current preview relation queries scan proposed right-side candidates rather than maintaining a second hash index.

This is known and acceptable for the experiment.

Only consider indexed overlay work if the new **real EF repair-query benchmark** proves relation scanning is now the dominant cost after validation behavior is understood.

If scan cost is not dominant, record it and stop.

Do not build:

- persistent preview indexes;
- mutable shadow runtime;
- copy-on-write full relation runtime;
- generic query planner changes;

without separate evidence and a separate plan.

---

# 12. Update benchmark conclusions honestly

Update `experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md` with three clearly separated conclusions:

## Core preview construction

Report the existing core benchmark.

## Actual EF rejected-preview repair query

Report the new end-to-end numbers including fingerprint-validation count.

## Multi-step repair convergence

Report:

```text
number of rejected saves
number of preview instances created
number of repair mutations
number of final successful saves
whether any full runtime reseed was used
```

Do not generalize from one machine to universal production performance.

Correct language:

```text
On this development machine and graph shape, ...
```

Incorrect language:

```text
Preview is O(1) in production.
```

Preview setup may be nearly independent of graph size while EF validation or relation queries are not.

---

# 13. Fifth-pass API decision

Preview remains **internal/experimental** unless the user explicitly asks for public API work later.

At the end of this pass, choose one of these outcomes for EF validation:

```text
EF VALIDATION: KEEP CURRENT
EF VALIDATION: OPTIMIZED EXPERIMENTALLY
EF VALIDATION: NEEDS SEPARATE DESIGN
```

And one outcome for multi-step repair:

```text
MULTI-STEP REPAIR: PROVEN
MULTI-STEP REPAIR: FUNCTIONALLY CORRECT BUT TOO EXPENSIVE
MULTI-STEP REPAIR: FAILED / ARCHITECTURE GAP
```

Do not invent a fourth vague outcome.

---

# 14. Mandatory regression tests

At minimum the final branch must contain tests proving:

1. Repair reason `Unknown + Invalid -> Invalid` still passes.
2. Repair reason `Unknown + Dirty -> Dirty` still passes.
3. Mixed `Dirty + Invalid` still deduplicates to one `Invalid` request.
4. Core preview does not mutate committed runtime caches/indexes.
5. Core preview rejects runtime advancement.
6. EF rejected preview rejects relevant tracked mutation after rejection.
7. EF rejected preview uses the retained rejected plan for the matching exception/session only.
8. Multiple preview reads are valid before application mutation.
9. Multi-step repair requires at least two repair mutations and converges.
10. Repair-induced violation on a second supply/source is detected on re-plan.
11. Rejected intermediate saves do not advance runtime version.
12. Final successful save commits final repaired graph.
13. Preview path does not call full-runtime reseed in the new EF scenarios.
14. Unreported core POCO mutation limitation is explicitly documented by test.
15. Existing 30 dogfood scenarios remain valid or are intentionally expanded; do not silently delete coverage.

---

# 15. Verification commands

Run these commands explicitly and report each result separately.

```powershell
dotnet build Raffinert.Consistency.sln -c Release
```

```powershell
dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
```

```powershell
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build
```

```powershell
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
```

```powershell
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
```

```powershell
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release
```

Run the existing package-consumer verification using the repository-standard commands and an isolated NuGet cache if the repository uses same-version local packages.

Also run:

```powershell
dotnet format --verify-no-changes
```

or the repository's actual formatting command.

And:

```powershell
git diff --check
```

Working tree must be clean after commit.

Do not report “all green” without command-specific counts/results.

---

# 16. Required final agent report

The implementation report must use this structure.

## 1. Starting state

- starting commit;
- pre-change test counts;
- existing dogfood scenario count.

## 2. EF validation measurement

For each graph size:

```text
full reseed setup/query/bytes
core preview setup/query/bytes
EF rejected preview setup/query/bytes
preview reads per repair decision
EF fingerprint validations per repair decision
```

## 3. Validation decision

Exactly one:

```text
KEEP CURRENT
OPTIMIZED EXPERIMENTALLY
NEEDS SEPARATE DESIGN
```

Explain why in concrete measured terms.

## 4. Multi-step repair

Report:

```text
save attempts
rejections
preview instances
repair mutations
final success/failure
runtime version before/after
full reseed used: yes/no
```

## 5. Second-order repair

State which repair caused which new invariant violation and how the next re-plan discovered it.

## 6. Staleness contract

State precisely what preview detects and what arbitrary core POCO drift it cannot detect.

## 7. Remaining limitations

Do not hide:

- host-asserted authoritative scope;
- explicit reporting in dependency-free core;
- application-owned ranking/convergence;
- preview relation scan cost;
- any retained EF fingerprint cost;
- any unresolved preview lifetime issue.

## 8. Verification

Give exact command results and test counts.

## 9. Commit

Give pushed commit SHA and branch name.

---

# Definition of Done

This fifth pass is done only when all of the following are true:

- The fourth-pass repair reason fix remains correct.
- The existing core preview benchmark remains available and is clearly labeled as core-only.
- A real EF rejected-preview end-to-end benchmark exists.
- We know how many EF fingerprint validations one repair decision performs.
- Any validation optimization is backed by measurements and stale-state tests; otherwise current safe behavior remains.
- A multi-step repair scenario requires at least two repair mutations and converges without full graph reseeding.
- A repair-induced violation on another source is discovered by a subsequent re-plan.
- Rejected plans never advance committed runtime state.
- The final successful save commits the final repaired graph.
- Preview remains internal/experimental.
- The staleness documentation distinguishes EF tracked-state guarantees from arbitrary unreported core POCO mutations.
- The dogfood documentation no longer implies that core-preview benchmark numbers are equivalent to EF repair cost.
- Solution, dogfood, core tests, EF tests, package consumers, formatting, and `git diff --check` all pass.
- No scenario/test was deleted merely to make the branch green.

The goal of this pass is not to make Preview look fast. The goal is to determine **what the real EF repair workflow costs, whether repeated re-planning actually converges for realistic repairs, and exactly which correctness guarantees the experimental preview can honestly make**.
