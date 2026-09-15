# Codex Implementation Plan — Dogfood Pre-Commit Guard Validation

Baseline: `0e3a712cf912fd2d8ff33367c6f5a13376db04db`

This plan continues the Ghost Matching dogfood after the first production-shaped sample wave.
The first wave proved that several final-state procurement rules can be represented with ordinary
relations, derived values, incremental aggregates, and one Boolean invariant. It also exposed an
important missing integration contract: the production guard runs before persistence, while the
sample mostly evaluates already-bootstrapped or already-committed runtime state.

The goal of this roadmap is to make the sample prove the actual guard workflow:

```text
application mutates domain objects
        ↓
Capture / Prepare the mutation batch
        ↓
Plan against the mutated final domain state
        ↓
evaluate affected invariants while the plan is temporarily installed
        ↓
known violation?
    yes -> reject persistence and discard the plan
    no  -> persist database work
        ↓
Commit(plan) without rerunning semantic model logic
        ↓
Dispatch
```

This roadmap MUST NOT invent production quantity equations that are not present in the inspected
Ghost Matching guard implementation. The existing No-GRN "PO line must be Modified" rule and GRN
"POLGR must be Modified" rule remain explicit transition-proxy gaps until either the real bookkeeping
equations are supplied or we deliberately decide to model mutation-batch constraints.

---

## 0. Non-negotiable constraints

The implementing agent must preserve these rules throughout the wave:

1. Do not add EF `EntityState`, `WasModified`, `WasTouched`, or similar fake persistence flags to sample domain entities.
2. Do not replace semantic relations with scenario-side dictionaries or manual lookups.
3. Do not make `PlanDetailed` commit runtime state.
4. Do not execute policy callbacks while planning or evaluating planned invariants.
5. Do not rerun relation predicates, impact classifiers, derived propagation, or invariant predicates in `Commit(plan)`.
6. Do not silently treat a domain `Unknown` result as `Violation`; for the Ghost Matching sample, `Unknown` means "cannot conclude, do not block".
7. Do not strengthen the current production rules without documenting the stronger semantic assumption.
8. Do not add a generic mutation-batch DSL in this roadmap.
9. Do not change package version, publish NuGet packages, create a tag, or create a GitHub release.
10. Public API added by this roadmap stays in `PublicAPI.Unshipped.txt`.

STOP if an implementation requires violating one of these constraints. Record the blocker instead of working around it.

---

# Task 87 — Turn the procurement sample into an executable regression gate

## Why

The sample project is compiled because it is in the solution, but CI does not execute it. A green CI run therefore does not prove that the dogfood scenarios actually pass.

The current sample also contains one fake assertion after D5:

```csharp
runner.Check(
    "D5 LGR and POLGR removed in one MutationSet",
    RuleEvaluation.Valid,
    RuleEvaluation.Valid);
```

That assertion must disappear.

## Files

- `.github/workflows/ci.yml`
- `.github/workflows/release-candidate.yml`
- `samples/Raffinert.Relations.PurchaseOrderSample/Program.cs`
- `samples/Raffinert.Relations.PurchaseOrderSample/Support/ScenarioRunner.cs`
- `samples/Raffinert.Relations.PurchaseOrderSample/Scenarios/GhostMatchingScenarios.cs`

## Steps

### 87.1 Add an explicit sample execution step to CI

After the Release build and before packaging, execute:

```bash
dotnet run --project samples/Raffinert.Relations.PurchaseOrderSample/Raffinert.Relations.PurchaseOrderSample.csproj \
  -c Release --no-build
```

Do the same in `release-candidate.yml`.

The workflow must fail if any scenario assertion throws.

### 87.2 Make ScenarioRunner incapable of fake assertions

Keep `Check(name, expected, actual, impact?)`, but do not add any overload that accepts only an expected value.

Every scenario must calculate `actual` from one of:

- `runtime.Get(...)`
- `runtime.Evaluate(...)`
- `runtime.GetState(...)`
- `PreparedImpactPlan.InvariantEvaluations`
- `RuntimeApplyResult`

Never use a constant as the `actual` value.

### 87.3 Fix D5

For the coordinated removal scenario:

```text
remove LGR
remove matching POLGR
same MutationSet
```

Do not query the removed LGR after commit because it is no longer registered.

Instead prove all of the following:

1. the mutation batch applies successfully;
2. relation impact contains the expected removals;
3. no surviving source is left with a bookkeeping-existence violation;
4. the runtime version advances exactly once;
5. causal output attributes the removals to the batch when `Causal` detail is requested.

If the rule is later represented as a planned invariant, D5 should additionally prove that no violated planned invariant is returned.

## Acceptance

- CI actually runs the sample.
- RC workflow actually runs the sample.
- no `Check(... Valid, Valid)` style constant assertion remains.
- a deliberately broken sample assertion causes CI failure.

## Commit

`test: execute procurement dogfood scenarios in ci`

---

# Task 88 — Convert semantic dogfood rules into real invariants and mutation-driven scenarios

## Why

The current sample models three important guard rules as `Derived<..., RuleEvaluation>` values only:

- POIL/LGR quantity balance;
- POLGR quantity conservation;
- LGR -> matching POLGR existence.

The scenario runner manually queries those values. That proves the expressions are correct, but does not prove that Raffinert can treat them as guard rules affected by mutations.

The sample should preserve the tri-state domain result while also expressing the guard decision as a Boolean invariant:

```text
Violation -> invariant false
Valid     -> invariant true
Unknown   -> invariant true  // skip; do not block
```

This mirrors the production guard's "skip when required information cannot be resolved" behavior.

## Files

- `samples/Raffinert.Relations.PurchaseOrderSample/Scenarios/GhostMatchingScenarios.cs`
- optionally split into focused scenario files if the existing file becomes hard to follow;
- `samples/Raffinert.Relations.PurchaseOrderSample/Support/ScenarioRunner.cs`

Do not change core runtime code in this task.

## 88.1 Add invariants on top of existing tri-state derived values

Add named invariants:

```csharp
var poilBalanceInvariant = model.Invariant(poils)
    .Using(poilBalance)
    .Must((_, result) => result != RuleEvaluation.Violation)
    .Named("poil-linked-quantity-balance-invariant");

var polgrBalanceInvariant = model.Invariant(polgrs)
    .Using(polgrBalance)
    .Must((_, result) => result != RuleEvaluation.Violation)
    .Named("polgr-bookkeeping-balance-invariant");

var lgrBookkeepingInvariant = model.Invariant(linkedReceipts)
    .Using(lgrBookkeeping)
    .Must((_, result) => result != RuleEvaluation.Violation)
    .Named("lgr-bookkeeping-existence-invariant");
```

Keep the existing `RuleEvaluation` derived values. They are still needed to distinguish `Valid` from `Unknown` in the sample output.

Do NOT encode Unknown as `Violation`.

## 88.2 Replace mostly-static A scenarios with mutations

Use an initially valid invoice + POIL graph, then exercise at least these transitions:

```text
A-M1 InvoiceLine.IsDeleted false -> true while POIL stays active
     expected planned/current invariant = violated

A-M2 InvoiceLine.IsDeleted false -> true
     POIL.IsDeleted false -> true
     same MutationSet
     expected = valid

A-M3 deleted invoice has one deleted and one active POIL;
     soft-delete surviving active POIL
     expected violated -> valid transition
```

Assert relation membership, derived count state, invariant state, and causal origins where useful.

## 88.3 Add POIL/LGR mutation scenarios

Start from a valid `LinkedQuantity == Sum(LGR.Quantity)` state.

Required transitions:

```text
B-M1 LGR quantity 5 -> 4             valid -> violation
B-M2 POIL LinkedQuantity 10 -> 9     restores valid state
B-M3 add LGR and adjust LinkedQuantity in one MutationSet -> valid
B-M4 remove LGR without adjustment   -> violation
B-M5 remove LGR + adjust POIL in one MutationSet -> valid
B-M6 GRN Enabled -> Unknown while currently inconsistent -> Unknown / invariant does not block
B-M7 GRN Unknown -> Enabled on inconsistent state -> violation
```

This must prove incremental `Sum` propagation rather than only bootstrap evaluation.

## 88.4 Add POLGR mutation scenarios

Start from a balanced normal line.

Required transitions:

```text
C-M1 QuantityMatched changes and breaks conservation -> violation
C-M2 second field change restores conservation       -> valid
C-M3 IsServiceItemLine false -> true on unbalanced POLGR -> valid/not applicable
C-M4 true -> null                                  -> Unknown / invariant does not block
C-M5 null -> false                                 -> violation
C-M6 GrnMode Enabled -> Unknown                    -> Unknown / invariant does not block
```

## 88.5 Add LGR -> POLGR mutation scenarios

Required transitions:

```text
D-M1 remove matching POLGR while LGR survives -> violation
D-M2 add matching POLGR back                  -> valid
D-M3 add LGR + matching POLGR in one batch    -> valid
D-M4 add LGR without matching POLGR           -> violation
D-M5 remove LGR + POLGR in one batch          -> no surviving violation
D-M6 change GoodsReceiptId so existing POLGR no longer matches -> violation
D-M7 add replacement POLGR for new key in same batch -> valid
```

The `D-M6/D-M7` pair is important because it proves composite relation reindexing, not only lifecycle add/remove.

## Acceptance

- every final-state semantic rule has both a `Derived<..., RuleEvaluation>` and a named `Invariant`;
- every rule has at least one valid -> violation mutation scenario;
- every rule has at least one repair/restore scenario;
- Unknown is visible as Unknown in sample output but does not count as an invariant violation;
- no scenario-side dictionary matching is introduced.

## Commit

`sample: dogfood guard invariants through real mutations`

---

# Task 89 — Add planned-final-state invariant evaluation to binding plans

## Problem to solve

`PlanDetailed` already executes a prepared mutation reversibly, produces the exact forward patch, and restores runtime state before returning.

However `RuntimeApplyResult.InvariantImpacts` means only:

```text
this invariant source became Dirty / Invalid
```

It does NOT mean:

```text
this invariant predicate evaluates to valid / violated in the planned final state
```

The Ghost Matching guard needs the second meaning before database durability.

## Required public API

Keep the existing overload unchanged:

```csharp
public PreparedImpactPlan PlanDetailed(
    PreparedMutation prepared,
    RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary);
```

Add:

```csharp
public enum PlannedInvariantEvaluationMode
{
    None,
    Affected
}
```

Add an overload:

```csharp
public PreparedImpactPlan PlanDetailed(
    PreparedMutation prepared,
    RuntimeImpactDetailLevel detailLevel,
    PlannedInvariantEvaluationMode invariantEvaluationMode);
```

Add immutable plan result data:

```csharp
public sealed record PlannedInvariantEvaluation(
    int InvariantId,
    object Source,
    InvariantEvaluationState State)
{
    public string? DefinitionKey { get; init; }
    public SourceIdentity? SourceIdentity { get; init; }
}
```

Extend `PreparedImpactPlan`:

```csharp
public IReadOnlyList<PlannedInvariantEvaluation> InvariantEvaluations { get; }

public bool HasInvariantViolations =>
    InvariantEvaluations.Any(value => value.State == InvariantEvaluationState.Violated);
```

If `InvariantEvaluationState` is not currently part of the approved public surface, first verify its existing accessibility because `RelationRuntime.GetState(Invariant<...>)` already exposes it. Reuse the existing type; do not create a duplicate planned-only Valid/Violated enum unless compilation/public API constraints force it.

## Exact semantics

### `None`

Must preserve today's behavior exactly:

- no invariant predicate is executed merely because planning occurs;
- `InvariantEvaluations` is empty;
- no additional derived recomputation;
- no policy dispatch.

### `Affected`

Evaluate each distinct affected invariant source exactly once while the reversible planned final state is installed.

An affected source is obtained from the actual dependency propagation result, not by rescanning all registered invariant sources.

Skip a source if the planned mutation removed it from its object set.

For every evaluated source capture:

- invariant id;
- definition key;
- source object;
- source identity;
- final `InvariantEvaluationState`.

Evaluation is read-only from the application's perspective but may populate derived/invariant runtime caches. Those cache writes are part of the planned runtime state and therefore MUST be captured in the forward patch.

## Internal implementation order

Target files:

- `src/Raffinert.Relations/Runtime/RelationRuntime.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- `src/Raffinert.Relations/Derived/InvariantRuntimeState.cs` only if a small internal helper is required;
- `src/Raffinert.Relations/Policies/PolicyActions.cs` or the existing file containing `PreparedImpactPlan`;
- `src/Raffinert.Relations/PublicAPI.Unshipped.txt`

### 89.1 Extend PreparedMutationExecution

Add an internal immutable collection for planned invariant evaluations.

Do not store callbacks in the plan.

### 89.2 Evaluate before capturing the forward patch

The required order inside reversible planning is:

```text
CaptureRollbackJournal
CommitMutations into temporary runtime state
EvaluateAffectedInvariants (optional)
CaptureForwardPatch
Create public plan/result data
RestoreRollbackJournal
```

Do NOT evaluate after `CaptureForwardPatch`, otherwise derived/invariant cache state produced by validation will not be installed by `Commit(plan)`.

### 89.3 Determine affected sources from propagation evidence

Use `RuntimeCommitResult.DependencyPropagation.InvariantImpacts`.

Group by invariant definition and source reference.

Do not scan `_invariants.Values` × all object-set instances.

### 89.4 Evaluate deterministically

For each distinct `(invariant definition, source reference)`:

1. check the source is registered in the planned source set;
2. call the invariant runtime state's evaluation once;
3. read/capture final state;
4. create source identity while temporary planned state is installed.

Ordering of public `InvariantEvaluations` must be stable:

```text
InvariantId
then durable/source identity when available
then stable encounter order as final fallback
```

Do not order by object hash code.

### 89.5 Ensure `Commit(plan)` does not reevaluate

A binding plan with affected evaluations must install the already-computed forward patch and return the already-computed evaluations.

No invariant predicate call is allowed in `Commit(plan)`.

## Core tests

Create a focused test file, for example:

`tests/Raffinert.Relations.Tests/PlannedInvariantEvaluationTests.cs`

Required tests:

1. `Plan_affected_evaluation_reports_violation_without_advancing_runtime_version`
2. `Plan_affected_evaluation_reports_valid_final_state`
3. `Plan_none_does_not_execute_invariant_predicate`
4. `Plan_affected_evaluates_only_impacted_sources`
5. `Plan_affected_deduplicates_same_invariant_source`
6. `Plan_affected_skips_source_removed_by_same_batch`
7. `Plan_affected_can_evaluate_source_added_by_same_batch_with_stable_key`
8. `Plan_affected_restores_all_runtime_state_after_return`
9. `Plan_affected_predicate_exception_restores_runtime_state`
10. `Commit_of_evaluated_plan_does_not_rerun_predicate`
11. `Commit_of_evaluated_plan_installs_evaluated_invariant_cache_state`
12. `Discarded_violating_plan_leaves_runtime_unchanged`
13. `Evaluated_plan_remains_subject_to_stale_version_and_domain_drift_checks`
14. `Planning_does_not_dispatch_immediate_or_repair_callbacks`

Use a deliberately stateful predicate counter in tests 3 and 10 so semantic reruns are detectable.

## Acceptance

- existing `PlanDetailed` behavior is unchanged;
- affected mode evaluates only affected invariant sources;
- violation is inspectable before runtime commit;
- runtime version/state after planning is identical to pre-plan state;
- `Commit(plan)` does not rerun predicate/propagation;
- new public surface is in `PublicAPI.Unshipped.txt` only.

## Commit sequence

1. `test: specify planned invariant evaluation semantics`
2. `feat: evaluate affected invariants in binding plans`

STOP after commit 1 if the proposed API conflicts with an existing public contract; adjust the API explicitly before implementation rather than hiding behavior behind internal helpers.

---

# Task 90 — Extend the EF unit-of-work binding-plan API

## Goal

Allow the existing EF adapter to request affected-invariant evaluation without bypassing `RelationUnitOfWork`.

## File

- `src/Raffinert.Relations.EntityFrameworkCore/ChangeTrackerAdapter.cs`
- `src/Raffinert.Relations.EntityFrameworkCore/PublicAPI.Unshipped.txt`
- EF tests

## API

Keep the current method:

```csharp
public PreparedImpactPlan? PlanDetailed(
    RelationRuntime runtime,
    RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary)
```

Add:

```csharp
public PreparedImpactPlan? PlanDetailed(
    RelationRuntime runtime,
    RuntimeImpactDetailLevel detailLevel,
    PlannedInvariantEvaluationMode invariantEvaluationMode)
```

The old overload delegates to `None`.

Do not create a second independent plan field. `RelationUnitOfWork` still owns exactly one `_plan`.

## Required behavior

- empty unit of work returns `null` exactly as today;
- attempting a second binding plan still throws;
- detail level mismatch at commit still throws;
- evaluated plan is committed through the same `Commit(plan)` path;
- adapter itself does not interpret violations or throw a guard exception;
- application code decides whether a violation blocks persistence.

## Tests

Add stable-key SQLite tests:

### invalid pre-save path

```text
mutate tracked entity
CaptureUnitOfWork
Prepare
PlanDetailed(... Affected)
assert HasInvariantViolations
assert DB row still has original value
assert runtime version/state unchanged
do not Commit(plan)
```

### valid pre-save path

```text
mutate tracked entity
CaptureUnitOfWork
Prepare
PlanDetailed(... Affected)
assert !HasInvariantViolations
SaveChanges
commit DB transaction if one exists
Commit(plan)
Dispatch
assert DB + runtime agree
```

### generated-key transactional path

For a store-generated key, preserve the existing proven ordering:

```text
CaptureUnitOfWork before first SaveChanges
begin DB transaction
first SaveChanges -> generated key
Prepare
PlanDetailed(... Affected)
violation -> rollback DB transaction; discard plan
valid -> persist any durable work; DB commit; Commit(plan)
```

Do not pretend the in-memory EF entity automatically rewinds after DB rollback. Tests/documentation must state that the caller must reconcile/reload the tracked domain graph before reusing it.

## Commit

`feat: expose planned invariant evaluation through ef unit of work`

---

# Task 91 — Rework the Ghost Matching sample into a pre-persistence guard workflow

## Goal

The sample should no longer prove only that invalid final states are queryable. It should demonstrate rejection of a bad planned mutation before runtime commit/persistence.

## Sample helper

Add a small sample-only helper such as:

```csharp
private static PreparedImpactPlan PlanGuard(
    RelationRuntime runtime,
    MutationSet mutations)
{
    var prepared = runtime.Prepare(mutations);
    return runtime.PlanDetailed(
        prepared,
        RuntimeImpactDetailLevel.Causal,
        PlannedInvariantEvaluationMode.Affected);
}
```

Do not add this helper to the library API.

## Scenario shape

Each guard scenario should use an isolated fixture/runtime or explicitly undo rejected domain mutations before continuing. A rejected plan rolls back Raffinert runtime state, not application object property values.

Preferred pattern:

```text
Arrange valid graph
prime only caches needed by the scenario
mutate domain objects
build MutationSet
PlanGuard
inspect tri-state derived result where useful
inspect plan.InvariantEvaluations
if violated:
    assert runtime version unchanged
    discard plan
else:
    Commit(plan)
assert runtime behavior
```

## Required guard scenarios

### Orphaned POIL

1. deleting invoice only -> planned violation; plan discarded;
2. deleting invoice + POIL in same batch -> no violation; plan commit succeeds;
3. deleting final surviving active POIL from already-deleted invoice -> no violation.

### POIL/LGR quantity balance

1. LGR quantity change without POIL adjustment -> planned violation;
2. LGR quantity + POIL LinkedQuantity coordinated batch -> valid;
3. GRN Unknown on inconsistent values -> tri-state Unknown, invariant valid/non-blocking;
4. Unknown -> Enabled on same inconsistent values -> planned violation.

### POLGR conservation

1. quantity mutation breaks equation -> planned violation;
2. coordinated field changes preserve equation -> valid;
3. service item true -> unbalanced equation does not block;
4. unresolved service status -> Unknown/non-blocking;
5. null -> false on unbalanced row -> planned violation.

### LGR/POLGR existence

1. remove POLGR while LGR survives -> planned violation;
2. add LGR without POLGR -> planned violation;
3. add LGR + POLGR same batch -> valid;
4. retarget GoodsReceiptId without replacement POLGR -> planned violation;
5. retarget + add matching replacement same batch -> valid;
6. remove LGR + POLGR same batch -> valid with no fake assertion.

## Causal proof

For at least one failing scenario from each rule family, assert that the corresponding invariant evaluation is present and print/render the causal `RuntimeApplyResult` trace.

Do not make exact prose text of the renderer a brittle assertion. Assert semantic cause kinds/definition keys where tests need precision.

## Commit

`sample: dogfood precommit ghost matching guard plans`

---

# Task 92 — Add a reusable regression test for the sample's guard semantics

## Why

Executable samples are useful, but critical production-inspired semantics should also have deterministic test coverage that is easy to run and diagnose.

## Preferred implementation

Add a core test file mirroring the sample's four implemented semantic families without copying all sample setup code verbatim.

Suggested file:

`tests/Raffinert.Relations.Tests/GhostMatchingDogfoodTests.cs`

Use small test fixtures/builders shared inside the test file.

At minimum test:

- coordinated delete invoice + POIL final-state validity;
- POIL/LGR Sum propagation and planned violation;
- POLGR conservation planned violation;
- composite `(PurchaseOrderLineId, GoodsReceiptId)` existence and retarget;
- Unknown -> Enabled transition;
- discarded invalid plan leaves version and relation/runtime caches unchanged;
- valid planned batch commits exactly once.

Do not assert private implementation structures.

The purpose is to pin the public dogfood contract.

## Commit

`test: lock procurement precommit guard dogfood semantics`

---

# Task 93 — Record the remaining production parity boundary honestly

## Goal

After Tasks 87–92, update the dogfood findings and architecture/docs so they distinguish three categories precisely.

## Category A — covered as final-state semantic invariants

Expected:

- deleted invoice has no active POIL;
- POIL LinkedQuantity vs active LGR quantity sum, subject to the documented stronger-invariant assumption;
- POLGR quantity conservation for known non-service rows;
- LGR has corresponding `(PO line, goods receipt)` POLGR;
- GRN/service Unknown skip behavior;
- pre-persistence planned evaluation and rejection.

## Category B — still not covered because the production equation is missing

Keep explicit:

### No-GRN companion adjustment

Current production proxy:

```text
POIL added / soft-deleted
AND net LinkedQuantity delta for PurchaseOrderLineId != 0
=> corresponding PurchaseOrderLine must be modified
```

Netting key:

```text
PurchaseOrderLineId
```

InvoiceLineId must NOT participate in netting.

### GRN companion adjustment

Current production proxy:

```text
LGR added / removed with non-zero quantity
AND net delta for (PurchaseOrderLineId, GoodsReceiptId) != 0
=> corresponding POLGR must be modified
```

Netting key:

```text
(PurchaseOrderLineId, GoodsReceiptId)
```

Zero-quantity LGRs remain exempt exactly as production currently does.

Do not mark these covered merely because `activePoilsByLine` / `polgrsByLine` relations exist.

## Category C — candidate future framework capability

Only if no stronger final-state equations exist, record a separate future roadmap candidate:

```text
mutation-batch invariant / transition constraint
```

It would need to reason over normalized mutation provenance, grouped old/new deltas, and required companion mutations.

Do NOT implement this generic capability in Tasks 87–93.

## Docs to update

- `docs/ghost-matching-dogfooding-findings.md`
- `docs/architecture.md` only if planned invariant evaluation changes the documented binding-plan contract;
- README dogfood/example section if one exists and the new workflow materially helps users.

Document the new binding-plan guarantee carefully:

> When `PlannedInvariantEvaluationMode.Affected` is requested, affected invariant predicates are evaluated while the reversible planned final state is installed. The resulting evaluation data and any cache state produced by that evaluation are bound into the plan. `Commit(plan)` installs that precomputed state without rerunning invariant predicates.

Also document:

> Rejecting/discarding a plan restores Raffinert runtime state but does not undo mutations already made to application objects or an EF ChangeTracker. The application must rollback/reload/reconcile its domain state as appropriate.

## Commit

`docs: record precommit guard dogfood boundary`

---

# Validation commands after every implementation task

Run at minimum:

```bash
dotnet restore Raffinert.Relations.sln

dotnet build Raffinert.Relations.sln -c Release --no-restore

dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj \
  -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj \
  -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj \
  -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Relations.PurchaseOrderSample/Raffinert.Relations.PurchaseOrderSample.csproj \
  -c Release --no-build

dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
```

After public API changes also run the normal pack/package-consumer checks used by CI.

---

# Recommended commit sequence

Keep commits small enough that a weaker implementation model can stop and diagnose one semantic change at a time.

```text
1  test: execute procurement dogfood scenarios in ci
2  sample: add boolean guard invariants over tri-state rules
3  sample: dogfood orphan rules through mutation batches
4  sample: dogfood poil lgr quantity mutations
5  sample: dogfood polgr conservation mutations
6  sample: dogfood composite lgr polgr mutations
7  test: specify planned invariant evaluation semantics
8  feat: evaluate affected invariants in binding plans
9  test: prove evaluated plans restore and install atomically
10 feat: expose planned invariant evaluation through ef unit of work
11 test: prove ef precommit invariant rejection path
12 sample: dogfood precommit ghost matching guard plans
13 test: lock procurement precommit guard dogfood semantics
14 docs: record precommit guard dogfood boundary
```

Do not combine commits 7–9. The tests must establish the contract before implementation and must separately prove rollback/install behavior.

---

# Definition of done

This roadmap is complete only when all of the following are true:

1. CI and RC workflows execute the purchase-order sample.
2. No sample scenario compares an expected constant to the same constant.
3. The four currently modeled Ghost Matching semantic families are represented as actual invariants.
4. The sample contains real valid -> violation and violation -> valid mutations.
5. Unknown remains observable and non-blocking.
6. `PlanDetailed(..., PlannedInvariantEvaluationMode.Affected)` reports final invariant truth without committing runtime state.
7. Planning a violating mutation leaves runtime version/state unchanged.
8. Discarding a violating plan invokes no callbacks.
9. A valid evaluated plan can be committed without rerunning the invariant predicate.
10. EF `RelationUnitOfWork` exposes the same evaluated-plan capability.
11. SQLite tests prove rejection before durability for stable-key mutations.
12. Generated-key transactional documentation/tests preserve the first-SaveChanges-inside-transaction ordering where required.
13. Public API additions remain Unshipped.
14. No fake EF persistence flags appear in sample domain entities.
15. No generic mutation-batch invariant DSL is introduced.
16. No-GRN PO-line adjustment and GRN POLGR adjustment are still explicitly marked uncovered unless authoritative final-state equations are supplied.
17. The dogfood findings document clearly says which production semantics are covered, stronger than production, skipped when Unknown, or still transition-only.

---

# Expected architectural result

After this roadmap, the useful production integration shape should be demonstrable as:

```text
EF / application domain mutation
        ↓
RelationUnitOfWork Capture + Prepare
        ↓
PlanDetailed(Affected invariant evaluation)
        ↓
PreparedImpactPlan
    ├── exact impact result
    ├── exact runtime forward patch
    ├── affected invariant evaluations
    └── durable policy work
        ↓
HasInvariantViolations?
    yes -> reject / rollback external unit of work; discard plan
    no  -> persist database changes / outbox
        ↓
database durable
        ↓
Commit(plan)
        ↓
Dispatch
```

That is the point at which the sample genuinely dogfoods the architectural role currently occupied by `GhostMatchingGuardInterceptor`, rather than only demonstrating that the same formulas can be queried after the fact.
