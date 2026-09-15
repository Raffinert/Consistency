# Codex Implementation Plan — Pre-Commit Guard Proof Completion

Baseline: `c334572e80fc1328e4251496bd7e887953a6d6bf`

Current verified baseline:

- Tasks 87–93 introduced planned affected-invariant evaluation, EF `RelationUnitOfWork` exposure,
  executable procurement dogfood scenarios, production-inspired regression tests, and boundary documentation.
- CI run #156 for the baseline completed successfully.
- `PlannedInvariantEvaluationMode.Affected` is public but remains intentionally Unshipped.
- The previous roadmap is still marked active because several of its explicit acceptance cases were not implemented.

This roadmap finishes that proof. It is intentionally conservative: **do not invent another framework feature unless a
required test demonstrates that the current implementation is incorrect or cannot express the required behavior.**

The desired end state is:

```text
mutated application / EF domain state
        ↓
Capture + Prepare
        ↓
PlanDetailed(..., Affected)
        ↓
evaluate only affected invariant sources in planned final state
        ↓
violations?
   yes -> reject external durability; runtime remains unchanged
   no  -> persist/commit database work
        ↓
Commit(plan) installs exact planned state without semantic rerun
        ↓
Dispatch
```

The two production transition proxies remain outside this roadmap unless authoritative domain equations are supplied:

```text
No-GRN: POIL add/delete requires corresponding PO-line bookkeeping adjustment
GRN:    LGR add/delete requires corresponding POLGR bookkeeping adjustment
```

Do **not** implement these by adding `WasModified`, EF `EntityState`, guessed quantity fields, or a generic mutation-rule DSL.

---

## 0. Hard constraints for the implementing agent

1. Keep all existing shipped API signatures unchanged.
2. Keep the new planned-invariant API in `PublicAPI.Unshipped.txt` for this roadmap.
3. Do not add a new public API merely to make a test easier.
4. Do not scan all invariant sources when `Affected` is requested; keep source selection propagation-scoped.
5. `PlanDetailed` must leave runtime version and runtime-owned state unchanged when it returns or throws.
6. `Commit(plan)` must not rerun relation predicates, impact classifiers, propagation, derived computations that were
   captured by the plan, or invariant predicates evaluated during planning.
7. Planning must never dispatch repair callbacks or immediate-policy callbacks.
8. A violating plan is data, not an exception. The application decides whether violation blocks persistence.
9. `Unknown` in the procurement sample remains non-blocking. Do not silently convert it to `Violated`.
10. Do not use EF InMemory as evidence for a database durability boundary where this plan explicitly requires SQLite.
11. Do not change package version, publish packages, create a tag, or create a GitHub release.
12. Do not move the new API from Unshipped to Shipped in this roadmap.
13. Preserve the existing generated-key rule: capture before the first save when necessary, obtain store-generated keys
    inside the transaction, then Prepare/Plan once the final key exists.
14. Keep commits small and bisectable. Run the focused tests after every task before touching the next task.

STOP if a required case reveals a real semantic defect in the core runtime. Add a failing test first, then make the
smallest correction that fixes that test. Do not redesign the dependency engine speculatively.

---

# Task 94 — Complete the core contract tests for evaluated binding plans

## Why this task exists

`PlannedInvariantEvaluationTests` proves the basic path, but the previous roadmap explicitly required more than the
current file covers. Before relying on the API from EF, prove the lifecycle and policy boundaries directly in core.

## Primary files

- `tests/Raffinert.Relations.Tests/PlannedInvariantEvaluationTests.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs` only if a new failing test exposes a defect
- `src/Raffinert.Relations/Runtime/RelationRuntime.cs` only if a new failing test exposes a defect
- `src/Raffinert.Relations/Derived/InvariantRuntimeState.cs` only if a new failing test exposes a defect

Do not edit public API files in this task unless a core semantic correction truly changes public surface. The expected
outcome is tests only.

## 94.1 Add stale-version protection for evaluated plans

Create a test with two prepared mutations from the same runtime version:

```text
prepare + plan A with Affected
commit another mutation B
attempt Commit(plan A)
```

Expected:

- stale plan commit throws the same stale-version exception/contract as an ordinary binding plan;
- invariant predicate for plan A is not rerun during the rejected commit attempt;
- runtime remains at B's committed version/state;
- plan A remains uncommitted.

Suggested test name:

```text
Evaluated_plan_remains_subject_to_stale_version_validation
```

## 94.2 Add domain-drift protection for evaluated plans

Plan a valid mutation with affected evaluation, then modify one of the prepared domain members again before
`Commit(plan)`.

Expected:

- commit fails domain-assumption validation;
- invariant predicate is not rerun;
- forward patch is not installed;
- runtime version/state is unchanged;
- plan remains uncommitted.

Suggested name:

```text
Evaluated_plan_remains_subject_to_domain_drift_validation
```

## 94.3 Prove planning does not dispatch `ScheduleRepair`

Create an invariant configured with `ScheduleRepairWith` and a callback counter/list.

Plan a mutation that affects and violates the invariant with `Affected` mode.

Expected immediately after planning:

```text
HasInvariantViolations == true
repair callback count == 0
runtime version unchanged
```

If the plan is intentionally discarded, the callback count must stay zero.

Suggested name:

```text
Planning_affected_violation_does_not_dispatch_repair_callback
```

## 94.4 Prove planning does not dispatch immediate policy callbacks

Use an invariant configured for the existing immediate-evaluation reaction if that public/model API is available.
Record predicate and callback/evaluation counters separately where possible.

The planned explicit evaluation may execute the invariant predicate exactly once because the caller requested
`Affected`. It must **not** dispatch the post-commit policy action during planning.

If the existing immediate reaction is internal-only or cannot be configured publicly, test the nearest existing public
immediate-policy path rather than creating API for the test.

Suggested name:

```text
Planning_affected_evaluation_does_not_dispatch_immediate_policy_action
```

## 94.5 Prove direct invariant source-member dependencies are included

The current implementation uses dependency propagation to choose affected invariant sources. Add a case where the
invariant predicate itself reads a source member in addition to an upstream derived value, and only that direct member
changes.

Example shape:

```csharp
var projected = model.Derived(items).Compute(x => x.Value).Named("value");
var invariant = model.Invariant(items).Using(projected)
    .Must((item, value) => item.Enabled ? value >= 0 : true)
    .Named("enabled-positive");
```

Change only `Enabled`.

Expected:

- the source appears once in `InvariantEvaluations`;
- the predicate evaluates in planned final state;
- no global invariant-source scan is needed.

Suggested name:

```text
Plan_affected_includes_direct_invariant_source_member_dependency
```

## 94.6 Prove deterministic ordering with multiple invariants and sources

Construct at least:

```text
2 invariant definitions
2 affected sources
```

Request `Affected` repeatedly from equivalent fresh runtimes.

Compare the sequence of:

```text
InvariantId
DefinitionKey
SourceIdentity.DurableIdentity
State
```

Expected ordering is deterministic and follows the existing contract:

```text
InvariantId
then durable/source identity
then encounter order only as final fallback
```

Do not assert ordering using object hash codes.

## 94.7 Prove `Summary` vs `Causal` does not change invariant truth

Plan the same logical mutation on equivalent fresh runtimes using:

```text
Summary + Affected
Causal + Affected
```

Expected `InvariantEvaluations` and `HasInvariantViolations` are semantically identical. Only causal impact detail may
differ.

## Acceptance

Task 94 is done only when all of the following are proven:

- stale version rejection;
- domain drift rejection;
- no repair dispatch during planning;
- no immediate-policy dispatch during planning;
- direct invariant member changes are selected as affected;
- deterministic multi-source ordering;
- Summary/Causal parity for invariant truth;
- existing Task 89 tests remain green on .NET 8 and .NET 10.

## Commit sequence

Prefer:

```text
test: complete evaluated binding plan safety matrix
```

If a real defect is exposed, split into:

```text
test: expose <specific evaluated-plan defect>
fix: preserve <specific contract>
```

Run:

```bash
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0
```

---

# Task 95 — Complete the procurement pre-commit mutation matrix in the executable sample

## Why

The current `PrecommitGuardScenarios` proves the core happy/rejection paths, but it implements only a subset of the
explicit mutation matrix from Task 88. The executable sample should demonstrate the real incremental transitions, not
leave most of them only as static bootstrap fixtures.

## Primary files

- `samples/Raffinert.Relations.PurchaseOrderSample/Scenarios/PrecommitGuardScenarios.cs`
- `samples/Raffinert.Relations.PurchaseOrderSample/Scenarios/GhostMatchingScenarios.cs`
- `samples/Raffinert.Relations.PurchaseOrderSample/Support/ScenarioRunner.cs` only if a small assertion helper is useful

Do not change core runtime code in this task.

## 95.1 Complete orphan transitions

Keep the existing:

```text
A-P1 invoice delete only -> violation
A-P2 invoice + POIL delete in one batch -> valid
```

Add a repair transition equivalent to the missing A-M3:

```text
start with deleted invoice + one deleted POIL + one active POIL
plan soft-delete of final active POIL
expected planned invariant state = Valid
commit accepted plan
assert final invariant state = Valid
```

The actual assertion must come from plan/runtime state, never a constant `Valid` value.

## 95.2 Complete POIL/LGR quantity transitions

Add all missing cases:

```text
B-P4 add LGR without LinkedQuantity correction -> violation
B-P5 add LGR + LinkedQuantity correction same batch -> valid
B-P6 remove LGR without LinkedQuantity correction -> violation
B-P7 remove LGR + LinkedQuantity correction same batch -> valid
B-P8 inconsistent state + Enabled -> Unknown transition -> non-blocking and derived result visibly Unknown
B-P9 same inconsistent state + Unknown -> Enabled -> violation
```

Important:

- prove incremental `Sum` changes;
- do not recreate the runtime between every transition unless object lifecycle makes it necessary;
- where a rejected plan leaves application objects mutated, explicitly restore/reconcile those objects before the next
  independent scenario and comment why.

## 95.3 Complete POLGR conditional transitions

Add:

```text
C-P3 normal unbalanced row + IsServiceItemLine false -> true -> non-blocking Valid/not-applicable
C-P4 service true -> null -> derived RuleEvaluation.Unknown, invariant non-blocking
C-P5 null -> false while still unbalanced -> violation
C-P6 GrnMode Enabled -> Unknown while unbalanced -> Unknown/non-blocking
C-P7 Unknown -> Enabled while unbalanced -> violation
```

The sample must show both:

```text
RuleEvaluation.Unknown
```

and:

```text
plan.HasInvariantViolations == false
```

for the Unknown cases. Do not call the domain result `Valid` in the printed output.

## 95.4 Complete LGR/POLGR existence transitions

Keep the current remove-surviving-LGR and coordinated removal cases, then add:

```text
D-P3 add LGR + matching POLGR in one batch -> valid
D-P4 add LGR without matching POLGR -> violation
D-P5 GoodsReceiptId retarget without replacement POLGR -> violation
D-P6 same retarget + matching replacement POLGR in one batch -> valid
D-P7 remove matching POLGR then add replacement for same logical key in same batch -> valid
```

D-P5/D-P6 must use the real composite relation, not a scenario dictionary.

## 95.5 Make rejected-plan reconciliation explicit

At the top of `PrecommitGuardScenarios` or in a helper comment, document:

> `PlanDetailed` rolls back Raffinert runtime state, not the already-mutated application objects. A rejected scenario
> must restore/reload/reconcile its domain objects before reusing them for another independent plan.

Where a scenario reuses objects after rejection, make that restoration visible in code.

## Acceptance

- every final-state semantic family has a valid -> violation transition;
- every family has a repair/restore or coordinated-valid transition;
- POIL/LGR add and remove are both tested;
- service-item and GRN Unknown transitions are mutation-driven;
- composite key retargeting is visible in the executable sample;
- Unknown is printed as Unknown but is non-blocking;
- no fake actual-value assertions remain;
- sample still runs successfully from `dotnet run`.

## Commit

```text
sample: complete precommit procurement mutation matrix
```

Run:

```bash
dotnet run --project samples/Raffinert.Relations.PurchaseOrderSample/Raffinert.Relations.PurchaseOrderSample.csproj -c Release
```

---

# Task 96 — Add real SQLite stable-key pre-durability guard tests

## Why

The previous roadmap explicitly required SQLite evidence. The current EF guard test uses EF InMemory, which proves
adapter flow but not a real relational durability boundary.

The current repository already has `EntityFrameworkCoreSqliteTests.cs`; extend it instead of creating another fixture.

## Primary file

- `tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreSqliteTests.cs`

Reuse existing `SqliteFixture`, test DbContext, and model entities where practical.

## 96.1 Invalid stable-key path

Create a stable application-assigned-key entity scenario with an invariant that can be violated by an update.

Required sequence:

```text
seed DB with valid row
seed runtime from same authoritative entity
prime invariant as Valid
mutate tracked entity to invalid value
CaptureUnitOfWork
Prepare(runtime)
PlanDetailed(runtime, Causal, Affected)
assert HasInvariantViolations == true
DO NOT call SaveChanges
DO NOT Commit(plan)
query database from a fresh context/AsNoTracking
```

Assertions:

- DB still contains original persisted value;
- runtime version unchanged;
- runtime invariant/cache state remains pre-plan state;
- no repair callback or other policy callback executed;
- tracked application object is still mutated/Modified, demonstrating that rejection does not magically rollback EF;
- after explicit `Reload` or equivalent reconciliation, tracked object returns to DB value.

Suggested name:

```text
Sqlite_violating_evaluated_plan_is_rejected_before_database_durability
```

## 96.2 Valid stable-key path

Use a valid coordinated update.

Required sequence:

```text
mutate tracked entity/entities
CaptureUnitOfWork
Prepare
PlanDetailed(..., Affected)
assert no violations
SaveChanges
Commit(plan)
Dispatch
```

Assertions:

- DB contains final value;
- runtime version advances exactly once;
- installed invariant state equals the state captured by the plan;
- invariant predicate does not rerun in `Commit(plan)`;
- if dispatch has no relevant callback, no extra semantic execution occurs.

Suggested name:

```text
Sqlite_valid_evaluated_plan_persists_then_installs_exact_runtime_plan
```

## 96.3 Database failure after valid planning

Plan a valid mutation, then force `SaveChanges` to fail with an existing SQLite uniqueness/concurrency mechanism.

Expected:

- do not commit the plan;
- runtime version/state remain unchanged;
- DB transaction/result is not durable;
- planned invariant evaluation remains merely detached result data.

Suggested name:

```text
Sqlite_database_failure_after_valid_evaluated_plan_does_not_advance_runtime
```

## Acceptance

These tests must use SQLite, not EF InMemory.

Run:

```bash
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

## Commit

```text
test: prove sqlite precommit invariant guard boundary
```

---

# Task 97 — Prove store-generated-key ordering with affected invariant evaluation

## Why

Generated-key binding/outbox behavior is already well tested, but the new affected-invariant mode has not yet been
combined with that ordering. The new API must not tempt callers to plan before a database-generated identity exists.

## Primary file

- `tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreSqliteTests.cs`

Reuse existing generated-key test entities and transaction helpers.

## 97.1 Valid generated-key path

Required ordering:

```text
context.Add(entity)
CaptureUnitOfWork BEFORE first SaveChanges
BeginTransaction
SaveChanges                      // database assigns key, transaction not committed
assert entity.Id != default
unit.Prepare(runtime)            // only now prepare key-dependent runtime mutation
plan = unit.PlanDetailed(runtime, Causal, Affected)
assert no invariant violations
assert PlannedInvariantEvaluation.SourceIdentity uses final generated key
persist any additional/outbox work if scenario uses it
transaction.Commit
unit.Commit(runtime)
unit.Dispatch(runtime)
```

Assertions:

- runtime version remains unchanged until DB commit + runtime commit;
- plan uses final generated identity;
- `Commit(plan)` does not reevaluate predicate;
- runtime registers entity under final key.

Suggested name:

```text
Generated_key_evaluated_plan_uses_identity_assigned_inside_transaction
```

## 97.2 Violating generated-key path rolls back external durability

Use an added entity that violates its invariant after key assignment.

Sequence:

```text
CaptureUnitOfWork
BeginTransaction
SaveChanges                // assigns key but is still rollback-able
Prepare
PlanDetailed(..., Affected)
assert violation
Rollback transaction
DO NOT Commit(plan)
```

Then verify from a fresh context:

- row does not exist;
- runtime did not advance/register entity;
- generated ID may remain on the in-memory entity and must not be mistaken for database durability;
- caller/EF reconciliation requirement is explicit in test comments.

Suggested name:

```text
Generated_key_violating_evaluated_plan_rolls_back_database_and_leaves_runtime_uncommitted
```

## 97.3 Prevent accidental early planning in the documented workflow

Do not add artificial runtime rejection if current key validation already provides the right behavior.

Instead add/retain documentation and, if practical, a focused test proving that planning an added store-generated-key
entity before the key exists either:

- fails under the existing key contract, or
- produces no durable identity and therefore is explicitly unsupported for this persistence workflow.

Choose the behavior the existing runtime already specifies. Do not invent a second key lifecycle.

## Acceptance

- both valid and violating generated-key paths use a real SQLite transaction;
- plan evaluation happens after the first save assigns the key;
- violating path rolls DB back and never commits runtime;
- valid path commits DB before installing runtime plan;
- no new generated-key API is added.

## Commit

```text
test: prove generated key precommit invariant workflow
```

---

# Task 98 — Exercise the new public API from packed packages and clarify its scope

## Why

The planned-invariant API is new public surface. It currently compiles inside the repository, but this wave should prove
that both packed core and EF packages expose the intended call shape before the API is ever moved to Shipped.

## Files

- `tests/package-consumers/CoreNet8/Program.cs`
- `tests/package-consumers/CoreNet10/Program.cs`
- `tests/package-consumers/EfNet10/Program.cs`
- `docs/architecture.md`
- `docs/ghost-matching-dogfooding-findings.md`
- `src/Raffinert.Relations/PublicAPI.Unshipped.txt` only for audit, not for moving entries
- `src/Raffinert.Relations.EntityFrameworkCore/PublicAPI.Unshipped.txt` only for audit

## 98.1 Core packed consumers

Add a minimal invariant and mutation, then call:

```csharp
var plan = runtime.PlanDetailed(
    prepared,
    RuntimeImpactDetailLevel.Summary,
    PlannedInvariantEvaluationMode.Affected);
```

Assert:

```text
InvariantEvaluations accessible
HasInvariantViolations accessible
violating plan leaves runtime version unchanged
```

At least one consumer should also commit a valid evaluated plan.

Do not expand the package consumer into a full dogfood scenario.

## 98.2 EF packed consumer

Exercise:

```csharp
unit.PlanDetailed(runtime, detailLevel, PlannedInvariantEvaluationMode.Affected)
```

with a small stable-key tracked update. It is sufficient to prove callability and plan inspection; SQLite durability is
already covered by Task 96.

## 98.3 Clarify public semantics in docs

Document explicitly:

- `HasInvariantViolations` means **violations among invariant sources evaluated for this plan**, not a global scan of all
  registered runtime invariants;
- `InvariantEvaluations` contains only sources selected as affected plus applicable newly added sources;
- `Source` is an in-process object reference; use identity data when durable identification is needed;
- planning restores runtime-owned state but not application-object/EF state;
- callers must reject persistence themselves when violations are blocking;
- `Unknown` is a domain modeling concern; the Boolean invariant determines whether it blocks.

Do not claim the API performs transaction management or automatic SaveChanges interception.

## 98.4 Audit Unshipped API

Verify the only new public surface from Tasks 87–98 is intentional.

Do not move entries to `PublicAPI.Shipped.txt` here.

## Acceptance

- packed .NET 8 core consumer calls affected planning;
- packed .NET 10 core consumer calls affected planning;
- packed EF consumer calls the EF overload;
- docs state affected-only—not global—semantics;
- API remains Unshipped.

## Commit sequence

```text
test: exercise planned invariant api from packed packages
docs: clarify affected invariant plan semantics
```

---

# Task 99 — Close the dogfood proof wave honestly

## Goal

Close Tasks 87–93 and this completion wave only after the promised evidence actually exists.

## Verification commands

Run locally or in CI-equivalent environment:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore

dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Relations.PurchaseOrderSample/Raffinert.Relations.PurchaseOrderSample.csproj -c Release --no-build

dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Then run the existing packed consumers against the generated packages.

## Required evidence before closure

1. procurement sample executes in normal CI;
2. procurement sample remains in manual RC workflow;
3. complete mutation matrix from Task 95 is executable;
4. core evaluated-plan safety matrix from Task 94 is green on net8/net10;
5. stable-key SQLite invalid/valid/failure paths are green;
6. generated-key SQLite valid/rollback paths are green;
7. packed consumers exercise the new public overloads;
8. no callback is dispatched by planning;
9. `Commit(plan)` predicate-rerun protection remains proven;
10. no transition-proxy rules were fabricated;
11. no generic mutation-batch invariant DSL was introduced;
12. public additions remain Unshipped;
13. latest `main` CI is green.

## Roadmap bookkeeping

When all evidence is green:

- update `docs/roadmaps/README.md`:
  - mark Tasks 87–93 as implemented/completed by this proof wave;
  - mark Tasks 94–99 completed;
  - remove the active-plan marker unless a genuinely new roadmap already exists;
- update `docs/ghost-matching-dogfooding-findings.md` with the final coverage matrix;
- explicitly retain the two uncovered transition proxies:
  - No-GRN PO-line adjustment/net-zero rule;
  - GRN POLGR adjustment/net-zero rule;
- do **not** claim full `GhostMatchingDetectionService` replacement until those semantics are either modeled from
  authoritative equations or intentionally implemented as mutation-batch constraints.

## Release-candidate note

Do not run or claim the current `release-candidate.yml` as release-ready while the planned-invariant API is intentionally
present in `PublicAPI.Unshipped.txt` and the workflow requires an empty Unshipped surface.

This is not a failure of this roadmap. A future API-freeze/release wave may move the reviewed API to Shipped and execute
the release-candidate gate.

## Final commit

```text
docs: close precommit guard dogfood proof roadmap
```

---

# Recommended commit order

A weaker agent should follow this order exactly:

```text
1  test: complete evaluated binding plan safety matrix
2  sample: complete precommit procurement mutation matrix
3  test: prove sqlite precommit invariant guard boundary
4  test: prove generated key precommit invariant workflow
5  test: exercise planned invariant api from packed packages
6  docs: clarify affected invariant plan semantics
7  docs: close precommit guard dogfood proof roadmap
```

If any test exposes a defect, insert only:

```text
N  test: expose <specific defect>
N+1 fix: <smallest semantic correction>
```

before continuing the sequence.

---

# Definition of done

This wave is complete only when the repository proves this end-to-end contract:

```text
EF/application domain objects are already mutated
        ↓
Capture / Prepare
        ↓
PlanDetailed(Affected)
        ↓
only actually affected invariant sources evaluated
        ↓
Violation
  -> reject DB durability
  -> no runtime commit
  -> explicit application/EF reconciliation

No violation
  -> persist DB work
  -> DB durability succeeds
  -> Commit(plan) installs exact precomputed runtime state
  -> no invariant predicate rerun during commit
  -> Dispatch after commit
```

and it proves the same contract for both application-assigned keys and store-generated keys.

The wave does **not** claim that every production Ghost Matching guard is replaced. The remaining transition-proxy rules
stay documented and uncovered until their real domain semantics are available.
