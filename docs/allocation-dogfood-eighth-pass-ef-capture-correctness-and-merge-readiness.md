# Allocation dogfood eighth pass: EF capture correctness cleanup and merge-readiness

## Purpose

This is a **literal step-by-step implementation plan for a weak agent**. Follow the order. Do not redesign unrelated APIs. Do not add a ninth architectural experiment unless a failing correctness test in this plan proves it is necessary.

This pass has two narrow correctness goals and one consolidation goal:

1. Make tracked-graph indexes honor exact EF `IEntityType` identity/assignability rather than CLR type only.
2. Prove and, if necessary, fix FK-only one-to-one retarget capture when principal-side navigations have not yet been fixup-stabilized.
3. Perform a final merge-readiness audit so the allocation dogfood branch stops accumulating experiments and only intentional production code/docs remain.

Expected branch:

```text
plan/allocation-dogfood
```

Expected starting commit:

```text
652758eef2ca03c76bfe08249a7d9965ac6e82d8
```

Backward compatibility is not required while the package is still pre-1.0 RC, but this pass is intentionally small. Do not introduce breaking public API changes unless a failing test requires one.

---

# 0. Establish the starting state before changing code

Run and record the exact counts/results:

```powershell
dotnet build Raffinert.Consistency.sln -c Release

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release

dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
```

Expected approximate baseline from the previous pass:

```text
Core net8: 395
Core net10: 395
EF: 242
Dogfood: 32
```

Do not rely on remembered counts. Record what actually runs.

If ordinary build output is locked by another process, use an isolated `--artifacts-path`. Do not weaken warnings, tests, or project inclusion to bypass a lock.

---

# 1. Read the exact code paths before editing

Read these files first:

```text
src/Raffinert.Consistency.EntityFrameworkCore/TrackedGraphSnapshot.cs
src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs
src/Raffinert.Consistency.EntityFrameworkCore/EfFingerprintDiagnostics.cs

tests/Raffinert.Consistency.EntityFrameworkCore.Tests/TrackedGraphSnapshotTests.cs
experiments/Raffinert.Consistency.AllocationDogfood/EfMutationCaptureBenchmark.cs
experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md
docs/ef-core-consistency.md
```

Before modifying anything, be able to explain these current facts:

```text
TrackedGraphSnapshot
    -> captures EntityEntry[] once
    -> lazily builds principal/dependent indexes
    -> index descriptor contains IEntityType metadata
    -> index population currently filters by target.ClrType.IsInstanceOfType(...)

CaptureNavigationChanges
    -> captures old/current relationship evidence from snapshot
    -> principal-side current one-to-one may read owner.Reference(...).CurrentValue
    -> some callers run DetectChanges before capture
    -> some public adapter helpers currently capture navigation evidence before DetectChanges
```

Do not proceed until the relevant methods are identified exactly.

---

# 2. Fix index membership to use EF entity-type semantics

## 2.1 Problem

The current snapshot index descriptor is metadata-aware, but index population is only CLR-type-aware.

Current shape:

```csharp
if (!target.ClrType.IsInstanceOfType(entry.Entity))
    continue;
```

That is insufficient for:

```text
shared CLR types
multiple IEntityType metadata nodes backed by the same CLR type
EF inheritance hierarchies where base-target lookup must include valid derived metadata
```

The index must answer:

```text
"Is this tracked entry an instance of this EF entity type in the model?"
```

not merely:

```text
"Is this CLR object assignable to this CLR class?"
```

## 2.2 Required implementation

Use EF metadata assignability for index population.

Preferred shape:

```csharp
if (!target.IsAssignableFrom(entry.Metadata))
    continue;
```

If the exact EF API in the referenced version differs, use the equivalent metadata-level assignability API. Do not fall back to CLR-only checks.

Rules:

- exact entity type matches must be included;
- valid EF-derived entity types must be included when target is an EF base type;
- unrelated shared-type metadata with the same CLR type must be excluded;
- do not change key comparer behavior from the seventh pass;
- do not change relationship null semantics.

## 2.3 Add a regression test for same CLR type / distinct IEntityType

Add a focused test that creates two EF entity types backed by the same CLR type but representing different metadata identities.

Acceptable approaches include:

```text
shared-type Dictionary<string, object> entities
or
explicit model configuration using the same CLR type under different entity-type metadata where EF permits it
```

The important assertion is:

```text
relationship targeting EF entity type A
must never resolve an entry belonging only to EF entity type B
when the key values are equal
```

The test must fail under CLR-only filtering and pass after metadata filtering.

Do not make the test merely inspect `BuildIndex`; exercise relationship resolution through the adapter/snapshot if practical.

## 2.4 Add an EF inheritance regression

Add a base/derived model:

```text
BasePrincipal
DerivedPrincipal : BasePrincipal
Dependent -> BasePrincipal
```

Assert that lookup targeting the base EF entity type can still resolve the valid tracked derived principal.

This prevents an overcorrection from exact metadata equality.

Required assertions:

```text
base-target + derived tracked entry -> resolves
unrelated same-CLR/shared metadata -> does not resolve
```

---

# 3. Prove FK-only one-to-one retarget behavior before changing ordering

## 3.1 The suspected gap

The adapter currently captures navigation evidence before an internal `DetectChanges()` in some paths.

Dependent-side resolution derives current relationship from current FK values, so FK-only changes can still be observed.

Principal-side one-to-one current resolution currently uses the principal navigation's current CLR value.

Therefore this sequence is suspicious:

```text
P1.Detail = D
P2.Detail = null
D.ParentId = P1
baseline accepted

D.ParentId = P2      // FK only; do NOT set D.Parent or P1/P2.Detail manually

capture immediately
```

Before EF relationship fixup runs, principal CLR navigations may still represent the old relationship.

## 3.2 Add the failing-or-proving regression first

Add a test named approximately:

```text
Fk_only_one_to_one_retarget_captures_both_dependent_and_principal_navigation_changes
```

Construct:

```text
Parent P1
Parent P2
Detail D
D.ParentId = P1.Id
P1.Detail = D
```

Save/accept baseline.

Then mutate only:

```csharp
D.ParentId = P2.Id;
```

Do not manually set:

```text
D.Parent
P1.Detail
P2.Detail
```

Call the same public capture surface that is expected to work independently, for example:

```csharp
ChangeTrackerAdapter.CreateChangeSet(...)
```

and separately the public `CaptureUnitOfWork(...)` path if their ordering differs.

Expected semantic evidence after capture:

```text
D.Parent:   P1 -> P2
P1.Detail:  D  -> null
P2.Detail:  null -> D
```

If the existing implementation already passes, keep the regression test and do not redesign ordering.

If it fails, proceed to Phase 4.

---

# 4. If FK-only one-to-one capture fails, normalize DetectChanges/capture ordering carefully

Only execute this phase if the test from Phase 3 fails.

## 4.1 Do not blindly move DetectChanges

The current adapter deliberately captures original/current relationship evidence, including navigation changes, added/deleted entities, and generated-key/fixup cases.

A naive ordering change can destroy old-value evidence or change generated-value semantics.

Before modifying code, classify each public/internal capture path:

```text
CreateChangeSet
CaptureUnitOfWork(public)
CaptureUnitOfWork(internal fingerprint path)
CapturePolicyAwareSnapshot
ConsistencyCoordinator Prepare/CaptureFingerprint paths
```

Record whether each caller already invokes `DetectChanges()` before entering the adapter.

## 4.2 Preferred direction

Where the adapter itself promises to capture current EF tracked state, normalize the graph before constructing `TrackedGraphSnapshot`:

```text
DetectChanges()
    -> create one TrackedGraphSnapshot
    -> capture relationship evidence from EF-stabilized current state
    -> capture scalar/lifecycle evidence from the same logical tracked state
```

But preserve original values from EF entry state.

Do not call `DetectChanges()` repeatedly inside loops.

## 4.3 Preserve AutoDetectChanges behavior

If temporary disabling of `AutoDetectChangesEnabled` is still required while enumerating relationship metadata, restore it in `finally` exactly as today.

Do not leave caller state changed.

## 4.4 Required regression matrix after ordering change

Re-run and explicitly verify all of these still pass:

```text
dependent reference retarget
null transition
partial-null composite FK
composite complete <-> partial-null
one-to-one principal-side replacement
FK-only one-to-one retarget
added/deleted dependent
collection add/remove/move
self-reference
ambiguous principal resolution fails closed
ambiguous dependent resolution fails closed
byte[] structural key comparer
converted custom comparer key
generated-FK fixup evidence
repeated fingerprint stability
```

Do not delete or weaken any previous tests to make the ordering change pass.

---

# 5. Audit snapshot comparer/key semantics one final time

Do not redesign them if tests are green. This is a proof step.

Verify by code review and tests:

```text
principal index values
    compared using principal key property GetKeyValueComparer()

dependent FK index values
    compared using corresponding principal key property GetKeyValueComparer()

hash code
    uses the same comparer semantics as equality

composite keys
    use one comparer per component

partial-null FK
    is not indexed as a relationship

entity lookup for generated fixup
    is reference identity, not entity Equals()
```

If equality and hashing use different semantics anywhere, fix it and add a regression.

Do not introduce generic custom comparer abstractions outside the EF adapter.

---

# 6. Keep the seventh-pass performance shape intact

Run the existing tracked-graph benchmark for:

```text
100
1,000
10,000
```

Record:

```text
capture time
allocated bytes
reference navigations visited
reference index lookups
reference candidate checks
collection index lookups
collection candidate checks
```

Acceptance criteria:

```text
no return to per-navigation full-tracker scans
ReferenceCandidateChecks remains 0 for the indexed reference path
10k remains in the same order of magnitude as seventh pass
no hundreds-of-millions candidate-count regression
```

Do not fail the pass because one local benchmark median shifts modestly. This is an algorithmic regression guard, not a nanosecond performance test.

Also re-run the generated-FK scalability test:

```text
100 modified FKs   -> 100 principal lookups, 0 full tracker scans
1,000              -> 1,000 lookups, 0 scans
10,000             -> 10,000 lookups, 0 scans
```

---

# 7. Re-run rejected-preview benchmark but do not optimize Preview in this pass

Run the unchanged workload:

```text
rejected SaveChanges
CreateRejectedPreview
five repair-decision reads
```

for:

```text
100
1,000
10,000
```

Record:

```text
rejection + preview ms
repair query ms
allocated bytes
reads
validations
```

Rules:

- keep validation count unchanged;
- keep authority discovery unchanged;
- do not add leases, cached authority tokens, or public Preview API;
- do not optimize relation scanning in this pass.

This benchmark is only a regression check after EF capture correctness changes.

---

# 8. Final dogfood repair regression

Run all allocation dogfood scenarios.

Explicitly confirm:

```text
single-step repair still succeeds
multi-step repair still requires the expected rejected plans
second-order repair still emits a fresh S2 repair request
runtime version stays unchanged across rejected plans
runtime version advances once after final successful persistence
full temporary runtime reseeds = 0
```

Do not alter application ranking or convergence policy in this pass.

---

# 9. Final merge-readiness audit: stop extending the branch

After the two correctness items are resolved, perform a repository audit of changes introduced across the dogfood passes.

This is not permission to redesign. It is cleanup/proof only.

## 9.1 Public API surface

Inspect public types/members added or changed by the branch.

Confirm:

```text
ConsistencyPreview remains internal/experimental
rejected-plan retention internals are not accidentally public
EfFingerprintDiagnostics is internal
TrackedGraphSnapshot is internal
repair request public API is intentional
RepairWhenViolated public API is intentional
FromMembership public API is intentional
ItemMemberChanged public API is intentional
```

If an experimental helper accidentally leaked public, make it internal before merge.

Do not hide APIs that are intentionally part of the branch's production design.

## 9.2 Remove stale callback-era terminology

Search branch files for stale concepts such as:

```text
ScheduleRepairWith
repair callback
model-owned repair queue
post-commit repair callback
```

If any remaining references describe removed behavior, update them.

Do not delete historical plan documents merely because they mention previous design phases; only current product/docs must be accurate.

## 9.3 Check baseline-only code

The full-runtime reseed repair path was intentionally kept as a benchmark/baseline during the Preview experiment.

Classify it now:

```text
A. still useful benchmark/reference -> keep clearly labeled experimental baseline
B. dead production-looking helper -> remove from production path
```

Do not remove benchmark evidence needed by DOGFOOD.md.

Ensure no normal EF repair scenario silently uses full reseed.

## 9.4 Verify dogfood project remains in solution

Confirm:

```text
Raffinert.Consistency.AllocationDogfood
```

is still present in the solution and therefore compiled by ordinary solution build.

Do not rely only on direct project build.

## 9.5 Documentation consistency

Review at least:

```text
README.md
src/public XML docs touched by this branch
docs/ef-core-consistency.md
experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md
```

Ensure current docs agree on these semantics:

```text
RepairWhenViolated eagerly proves affected repair-enabled invariants before emitting requests.
Repair request reason is propagated impact severity, not cached invariant state.
Rejected EF save does not advance committed runtime state.
Preview is internal/experimental.
Preview authority validation is detection, not snapshot isolation.
Core cannot detect arbitrary unreported POCO mutation.
External consumer discovery remains host-owned and may query the database.
Tracked navigation capture is indexed, not per-navigation full scan.
Partial-null composite FK means no relationship.
```

Remove stale contradictory statements from current docs.

---

# 10. Package and release-candidate verification

Run the full verification suite used by the previous pass.

At minimum:

```powershell
dotnet build Raffinert.Consistency.sln -c Release

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release

dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build

dotnet format --verify-no-changes
git diff --check
```

Then run the repository's existing pack/release verification scripts, including the isolated-cache package consumers used in previous passes.

If same-version NuGet cache causes stale package selection, use an isolated package cache. Do not bump the package version only to work around local cache contamination.

Record exact counts/results.

---

# 11. Git and branch hygiene

Before committing:

```text
git status --short
```

must show only intended changes.

Commit all implementation/tests/docs for this pass as one coherent commit unless repository policy requires otherwise.

Push to:

```text
origin/plan/allocation-dogfood
```

Do not create another branch.

Do not open or merge a PR unless explicitly asked.

---

# 12. Required final report

Report using this exact structure.

## 1. Starting state

```text
starting commit
baseline test counts
baseline dogfood count
```

## 2. IEntityType index correctness

State:

```text
old CLR-only behavior
new metadata assignability behavior
shared-CLR regression result
base/derived EF metadata regression result
```

## 3. FK-only one-to-one result

State one of:

```text
NO BUG — regression passed without implementation change
```

or:

```text
BUG CONFIRMED — normalized capture ordering
```

If fixed, describe exact ordering before/after and why original-value evidence remains correct.

## 4. Key/null semantics

Confirm:

```text
partial-null composite FK
structural key comparer
custom/converted comparer
composite comparer hashing/equality
```

## 5. Performance regression result

Provide 100/1k/10k tracked-capture table and generated-FK lookup counts.

## 6. Rejected-preview regression

Provide the unchanged 100/1k/10k benchmark table and validation counts.

## 7. Dogfood regression

State:

```text
scenario count
single-step result
multi-step result
second-order result
runtime version behavior
full reseed count
```

## 8. Merge-readiness audit

List:

```text
public API leaks found/fixed
stale docs found/fixed
baseline/dead-code decision
solution inclusion
remaining intentional experimental pieces
```

## 9. Remaining limitations

Do not invent new goals. List only real remaining limitations, expected to include some subset of:

```text
host-asserted authoritative scope
explicit core POCO mutation reporting
no arbitrary unreported POCO drift guarantee
preview authority checks are not DB snapshot isolation
preview relation queries still scan proposed candidates
repair ranking/convergence remain application-owned
Preview remains internal/experimental
```

## 10. Verification

Provide exact command/result summary.

## 11. Commit

Provide:

```text
commit SHA
branch
remote synchronized yes/no
working tree clean yes/no
```

---

# 13. Definition of Done

This pass is complete only when all are true:

- tracked relationship indexes use EF metadata assignability, not CLR-only membership;
- shared-CLR/unrelated entity-type collision is covered by regression test;
- base-target/derived-entry EF inheritance lookup is covered;
- FK-only one-to-one retarget behavior is explicitly proven;
- if ordering was wrong, it is fixed without regressing original/current navigation evidence;
- partial-null composite FK semantics remain correct;
- EF key comparer semantics remain correct for equality and hashing;
- generated-FK capture remains free of per-property full tracker scans;
- indexed navigation benchmark remains in the seventh-pass algorithmic shape;
- rejected-preview benchmark shows no major regression;
- all allocation dogfood repair scenarios pass without full runtime reseed;
- experimental Preview internals have not leaked into public API;
- current docs are internally consistent;
- solution build, Core net8/net10, EF tests, dogfood, format, diff check, package verification, and isolated package consumers all pass;
- working tree is clean after commit/push;
- no new architectural pass is started from this branch without a newly demonstrated correctness or performance defect.

After this Definition of Done is satisfied, treat `plan/allocation-dogfood` as ready for a separate final review/merge decision rather than continuing feature expansion.