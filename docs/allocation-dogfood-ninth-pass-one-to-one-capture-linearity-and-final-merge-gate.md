# Allocation dogfood ninth pass: one-to-one capture linearity and final merge gate

## Status and intent

This is a **small, final implementation pass** for `plan/allocation-dogfood`.

Starting point:

- branch: `plan/allocation-dogfood`
- expected starting commit: `8839ddafc77975d93cf139cbc66e49f462642e0c`
- Core net8 baseline reported: `395/395`
- Core net10 baseline reported: `395/395`
- EF baseline reported: `245/245`
- Allocation dogfood baseline reported: `32/32`

The previous pass fixed two correctness gaps:

1. tracked relationship indexes now use EF `IEntityType` assignability instead of CLR-type-only membership;
2. FK-only one-to-one retargeting now captures both dependent-side and principal-side navigation changes by preserving original principal-side evidence before `DetectChanges()` and reading stabilized current navigation values afterward.

The one-to-one correctness fix introduced a new scalability risk in the implementation:

```csharp
foreach (var evidence in principalReferences)
{
    changes.RemoveAll(change =>
        change is PropertyChange property &&
        ReferenceEquals(property.Instance, evidence.Owner.Entity) &&
        property.Member == evidence.Member);

    // add post-fixup principal-side change
}
```

This may become `O(P * C)` where:

- `P` = number of principal-side reference evidences;
- `C` = number of already captured navigation changes.

In a one-to-one-heavy graph with thousands of FK-only retargets, both can scale with `N`, giving an avoidable `O(N²)` cleanup phase.

The purpose of this pass is to:

1. prove whether the cleanup path scales super-linearly;
2. remove the provisional principal-side change/cleanup design;
3. keep the exact one-to-one correctness semantics from the eighth pass;
4. prove approximately linear work for 100 / 1,000 / 10,000 FK-only one-to-one retargets;
5. rerun all prior correctness/performance/package gates;
6. perform a final merge gate;
7. **stop extending this dogfood branch after this pass unless a test reveals a real correctness defect.**

This is **not** another architecture-design pass.

---

# Non-negotiable rules

The agent must obey all of these.

1. **Do not redesign Preview.** `ConsistencyPreview` stays internal and experimental.
2. **Do not add a public API** for this optimization.
3. **Do not add compatibility wrappers, obsolete aliases, V2 shims, or duplicate APIs.** The project is RC/pre-1.0 and breaking cleanup is allowed when needed.
4. **Do not weaken one-to-one correctness** to improve performance.
5. **Do not remove or skip the FK-only one-to-one tests added in the eighth pass.**
6. **Do not move `DetectChanges()` back to the old broken position** unless a failing regression proves a different ordering is required.
7. **Do not disable `TreatWarningsAsErrors`.**
8. **Do not reduce benchmark sizes** just to make the run faster.
9. **Do not claim linear behavior from elapsed time alone.** Add deterministic work-count evidence.
10. **Do not optimize unrelated EF paths.** If another hotspot is observed, record it under Remaining limitations unless it blocks correctness or causes the exact benchmark in this plan to fail catastrophically.
11. **Do not promote internal diagnostics to public API.** Internal counters are acceptable.
12. **Do not change domain repair ranking or convergence behavior.**
13. **Do not reintroduce a full temporary runtime reseed.**
14. **Do not change `RepairWhenViolated()` semantics.**
15. **Do not change `FromMembership()` semantics.**
16. **Do not change `ItemMemberChanged()` semantics.**
17. **Do not rewrite the branch history.** Add one normal commit on the current branch when complete.
18. **Do not create another follow-up implementation plan from inside this work.** If something genuinely new is found, report it instead of silently expanding scope.

---

# Required starting verification

Before changing code, run and record the exact current state.

From repository root:

```powershell
git status --short
git branch --show-current
git rev-parse HEAD
```

Expected:

```text
branch = plan/allocation-dogfood
HEAD   = 8839ddafc77975d93cf139cbc66e49f462642e0c
working tree clean
```

If the branch has advanced legitimately, record the actual HEAD and inspect the diff before continuing.

Do not reset other people's changes.

Then run focused baseline tests first:

```powershell
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release --no-restore
```

If normal output is locked by a stale process, use an isolated artifacts/output path. Do not modify project configuration merely to work around a file lock.

Record:

- test count;
- pass/fail;
- warnings/errors.

---

# Phase 1 — prove the current cleanup complexity

## 1.1 Inspect the exact implementation

Find `CaptureStabilizedNavigationSnapshot` in:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs
```

Confirm the current shape is equivalent to:

```csharp
snapshot = TrackedGraphSnapshot.Create(...);
changes = CaptureNavigationChangesCore(..., principalReferences).ToList();

changeTracker.DetectChanges();

foreach (var evidence in principalReferences)
{
    changes.RemoveAll(...owner/member...);

    var currentValue = evidence.Owner
        .Reference(evidence.Navigation.Name)
        .CurrentValue;

    if (!ReferenceEquals(evidence.OriginalValue, currentValue))
        changes.Add(...);
}
```

Do not change anything until the baseline work-count test exists.

## 1.2 Add deterministic internal diagnostics

Extend the existing internal EF diagnostics type only if needed.

Preferred counters:

```csharp
internal long PrincipalReferenceEvidenceCaptured { get; set; }
internal long PrincipalReferenceCleanupScans { get; set; }
internal long PrincipalReferenceChangesEmitted { get; set; }
```

Names may vary, but semantics must be explicit.

`PrincipalReferenceCleanupScans` must count the amount of cleanup work, not merely the number of `RemoveAll` calls.

For the old implementation, increment it once per inspected change inside the cleanup predicate, e.g. conceptually:

```csharp
changes.RemoveAll(change =>
{
    diagnostics?.PrincipalReferenceCleanupScans++;
    ...
});
```

Keep diagnostics internal.

Do not alter production semantics to collect diagnostics.

## 1.3 Add a focused mass one-to-one fixture

Add a test fixture that models exactly this shape:

```text
Principal P1  1 <-> 1 Detail D1
Principal P2
Principal P3  1 <-> 1 Detail D2
Principal P4
...
```

Each detail starts attached to one principal, then is retargeted to another principal **by FK only**:

```csharp
detail.ParentId = replacement.Id;
```

Do not assign:

```csharp
detail.Parent = replacement;
oldPrincipal.Detail = null;
replacement.Detail = detail;
```

because that would bypass the exact bug/fixup path we need to measure.

Use sizes:

```text
100
1,000
10,000
```

The fixture must keep key values unique and avoid accidental relationship ambiguity.

## 1.4 Baseline test must prove old cleanup growth

Before implementing the optimization, run the focused capture with diagnostics.

Record for each N:

```text
N
principal evidences
navigation changes before cleanup if available
cleanup inspections
final principal-side emitted changes
elapsed time
allocated bytes if easy to measure
```

Do not require an exact quadratic ratio because JIT/EF fixup behavior can vary.

But the evidence should show that cleanup inspections materially exceed O(N), e.g. by growing roughly with both number of evidences and number of captured changes.

If the counter unexpectedly shows cleanup is already effectively linear, stop and explain why before rewriting production code. Do not implement an optimization for a nonexistent path.

---

# Phase 2 — replace provisional principal-side changes with a two-phase capture

## 2.1 Required semantic model

Keep this conceptual split:

```text
DEPENDENT-SIDE REFERENCE
old/current can be derived from tracked FK values in the pre-fixup snapshot

PRINCIPAL-SIDE REFERENCE
old must be captured before DetectChanges/fixup
current must be read after DetectChanges/fixup
```

The implementation must therefore avoid generating a provisional principal-side mutation before stabilization.

## 2.2 Required implementation shape

Refactor `CaptureNavigationChangesCore` so that for a principal-side reference it:

1. resolves the original relationship;
2. stores `PrincipalReferenceEvidence`;
3. does **not** resolve/add the current principal-side `PropertyChange` yet;
4. continues to the next navigation.

Target shape:

```csharp
var oldValue = ResolveReference(
    snapshot,
    owner,
    navigation,
    original: true);

if (!navigation.IsOnDependent)
{
    principalReferences.Add(new PrincipalReferenceEvidence(
        owner,
        navigation,
        member,
        mapping,
        oldValue));

    continue;
}

var newValue = ResolveReference(
    snapshot,
    owner,
    navigation,
    original: false);

if (!ReferenceEquals(oldValue, newValue))
{
    changes.Add(mapping is null
        ? Change.Property(owner.Entity, member, oldValue, newValue)
        : mapping.Property(owner.Entity, member, oldValue, newValue));
}
```

After restoring `AutoDetectChangesEnabled`, call:

```csharp
changeTracker.DetectChanges();
```

Then emit principal-side changes exactly once:

```csharp
foreach (var evidence in principalReferences)
{
    var currentValue = evidence.Owner
        .Reference(evidence.Navigation.Name)
        .CurrentValue;

    if (ReferenceEquals(evidence.OriginalValue, currentValue))
        continue;

    changes.Add(evidence.Mapping is null
        ? Change.Property(
            evidence.Owner.Entity,
            evidence.Member,
            evidence.OriginalValue,
            currentValue)
        : evidence.Mapping.Property(
            evidence.Owner.Entity,
            evidence.Member,
            evidence.OriginalValue,
            currentValue));
}
```

There must be no `changes.RemoveAll(...)` or equivalent scan-by-owner/member cleanup.

## 2.3 Keep original evidence stable

Do not rebuild original relationship evidence after `DetectChanges()`.

The entire point is:

```text
old principal relationship = pre-fixup truth
new principal relationship = post-fixup truth
```

The pre-fixup `TrackedGraphSnapshot` must remain the source for original dependent-index lookup.

## 2.4 Preserve collection behavior

Do not accidentally defer collection reset capture unless necessary.

Existing collection behavior from prior passes must remain:

- move resets old and new owner once each;
- partial-null composite FK does not create phantom owners;
- partial-to-partial relationship-null transitions do not create fake resets;
- self-reference collection evidence remains correct;
- resets remain deduplicated by owner/member.

If the principal-side refactor changes collection results, treat that as a regression and fix it before proceeding.

---

# Phase 3 — correctness regression matrix

All of the following must pass after the refactor.

## 3.1 FK-only one-to-one exact evidence

For:

```text
D.Parent   = P1
P1.Detail  = D
P2.Detail  = null

mutation:
D.ParentId = P2.Id only
```

capture must contain exactly:

```text
D.Parent   : P1   -> P2
P1.Detail  : D    -> null
P2.Detail  : null -> D
```

No duplicate `PropertyChange` for any of those navigation members.

Run against both:

```text
ChangeTrackerAdapter.CreateChangeSet(...)
ChangeTrackerAdapter.CaptureUnitOfWork(...)
```

## 3.2 No-op one-to-one

If FK remains unchanged, no dependent or principal navigation mutation is emitted.

## 3.3 FK-only removal

For optional one-to-one:

```text
D.ParentId = null
```

expect:

```text
D.Parent  P1 -> null
P1.Detail D  -> null
```

No unrelated principal receives a change.

## 3.4 FK-only addition

For initially unassigned detail:

```text
D.ParentId = P1.Id
```

expect:

```text
D.Parent   null -> P1
P1.Detail  null -> D
```

## 3.5 Explicit navigation replacement still works

Preserve the existing replacement scenario where application code explicitly assigns navigation properties.

Do not make the optimized FK-only path regress explicit navigation mutation.

## 3.6 Ambiguous original relationship still fails closed

The previous ambiguity tests must still throw rather than choosing an arbitrary dependent/principal.

## 3.7 Shared CLR type isolation still passes

Keep the eighth-pass test:

```text
same CLR type
same key
separate IEntityType metadata
```

A relationship targeting one metadata type must never resolve the other.

## 3.8 EF inheritance still passes

A relationship targeting an EF base entity type must still resolve a tracked derived principal via metadata assignability.

## 3.9 Partial-null composite FK matrix still passes

Keep all of:

```text
[null, null] -> no relationship
[1, null]    -> no relationship
[null, "A"]  -> no relationship
[1, "A"]     -> complete relationship
```

and complete↔partial / partial↔partial collection behavior.

## 3.10 EF comparer semantics still pass

Keep:

- structural `byte[]` key test;
- converted/custom case-insensitive key test;
- composite equality/hash behavior.

## 3.11 Generated-FK fixup remains O(1) lookup

Keep the existing 100 / 1,000 / 10,000 generated-FK test:

```text
principal lookups = N
full tracker scans = 0
```

---

# Phase 4 — mass one-to-one linearity proof

## 4.1 Required deterministic work counts

After the refactor, rerun 100 / 1,000 / 10,000 FK-only one-to-one retargets.

Expected structural shape:

```text
PrincipalReferenceEvidenceCaptured ~= number of principal-side refs visited
PrincipalReferenceCleanupScans      = 0
PrincipalReferenceChangesEmitted    = O(N)
```

If there are two principal-side references per logical pair because of model shape, report that clearly. The requirement is proportional growth, not an arbitrary exact N.

## 4.2 Time/allocation measurement

Record:

```text
N
capture ms
allocated bytes
reference navigations visited
reference index lookups
reference candidate checks
principal reference evidences
principal cleanup scans
principal emitted changes
```

Do not set a fragile hard millisecond assertion in unit tests.

Use deterministic work-count assertions for regression protection.

## 4.3 Acceptance threshold

The 10k case must not show:

- `O(N²)` cleanup work;
- hundreds of millions of change comparisons;
- a return to multi-second relationship capture caused by this code path.

Expected algorithmic behavior should be approximately:

```text
snapshot/index construction  O(N)
reference capture            O(N)
EF DetectChanges/fixup       EF-owned cost
principal post-fixup emit    O(N)
```

If EF's own `DetectChanges()` becomes the dominant cost at 10k, report it separately. Do not attempt to replace EF fixup in this pass.

---

# Phase 5 — preserve allocation dogfood behavior

Run the complete dogfood executable after focused EF tests.

Required behavior remains:

- 32 scenarios pass;
- single-step repair succeeds;
- multi-step repair performs the expected rejected plans before success;
- second-order repair produces the fresh `S2` repair request;
- runtime version does not advance during rejected attempts;
- runtime version advances exactly once after successful persistence;
- no full temporary runtime reseed is used;
- rejected preview remains internal/experimental;
- repair ranking and convergence remain application-owned.

Do not change the dogfood domain merely to make tests pass.

---

# Phase 6 — performance regression checks from prior passes

## 6.1 Navigation/fingerprint benchmark

Rerun the existing 100 / 1,000 / 10,000 indexed capture benchmark.

Verify:

- no return to full tracked-entry candidate scanning;
- reference candidate checks remain at zero or the previously expected bounded value;
- reference index lookups scale with navigations;
- 10k capture remains in the same broad order of magnitude as the seventh/eighth-pass result, not seconds.

Do not fail the pass for ordinary machine-to-machine timing noise.

Report both structural counters and elapsed values.

## 6.2 Rejected-preview benchmark

Rerun the unchanged workload:

```text
rejected save
CreateRejectedPreview
five repair-decision reads
```

for:

```text
100
1,000
10,000 allocations
```

Keep reporting:

```text
Rejection + preview
Repair query
Allocated bytes
Reads
Validations
```

Expected validation count remains:

```text
5 reads
6 validations
```

Do not introduce validation leases or weaken authority checks in this pass.

## 6.3 Generated-FK scalability

Rerun 100 / 1,000 / 10,000 modified FKs and report:

```text
principal lookups
full tracker scans
```

Expected:

```text
lookups = N
scans   = 0
```

---

# Phase 7 — final merge-readiness audit

This is a review/checklist phase, not an excuse to implement more features.

## 7.1 Public API audit

Inspect both:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
```

Confirm the intended branch additions remain public where appropriate:

- `ItemMemberChanged(...)`;
- `FromMembership(...)`;
- `RepairWhenViolated()`;
- structured `RepairRequestInfo` / runtime repair request surface;
- EF violation exception `RepairRequests`.

Confirm these remain internal/not shipped:

- `ConsistencyPreview`;
- `TrackedGraphSnapshot`;
- `EfFingerprintDiagnostics`;
- rejected-plan preview plumbing/session details;
- benchmark-only helpers.

Do not promote any internal experimental type.

## 7.2 Dead/provisional code audit

Search the production projects for:

```text
RemoveAll(
principalReferences
repair callback
callback queue
CreateRuntime(seed
full reseed
ConsistencyPreview
```

Interpret results carefully.

Required conclusions:

- no principal-reference cleanup scan remains;
- no old mutable repair callback queue exists;
- full-runtime reseed may remain in dogfood benchmark baseline only, not the production repair path;
- `ConsistencyPreview` remains intentionally internal and used only by supported internal experiment/test plumbing;
- stale wording that says callbacks where the product now uses structured repair requests is removed from current-product docs/comments.

Do not rewrite historical plan documents merely because they describe prior stages.

## 7.3 Solution/project boundary

Confirm allocation dogfood remains in `Raffinert.Consistency.sln` so normal solution build compiles it.

## 7.4 Documentation wording

Current docs must continue to state:

- preview authority validation is stale-state detection, not DB snapshot isolation;
- another transaction can change DB state after validation;
- final `SaveChanges` planning/enforcement is the durability boundary;
- core arbitrary unreported POCO drift is not guaranteed detectable;
- authoritative scope remains host-asserted;
- preview remains internal/experimental;
- repair ranking/convergence remain application-owned.

Do not claim the experimental Preview is production-ready.

---

# Phase 8 — exact verification sequence

Run these in this order so failures are attributable.

## 8.1 Focused EF tests

```powershell
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release
```

Record exact pass count.

## 8.2 Core tests net8

Use the repository's existing command/script to run the Core test suite under net8 Release.

Record exact pass count.

## 8.3 Core tests net10

Run the same suite under net10 Release.

Record exact pass count.

## 8.4 Direct dogfood build

```powershell
dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
```

Expected:

```text
0 warnings
0 errors
```

If ordinary output is locked by a stale process, use an isolated output/artifacts path and report the lock honestly.

## 8.5 Dogfood executable

```powershell
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build
```

Expected current scenario count:

```text
32/32
```

If this pass intentionally adds a new executable dogfood scenario rather than only EF unit tests, update the expected count and explain exactly why.

Prefer adding the mass one-to-one scalability proof to EF tests/benchmark infrastructure rather than inflating the allocation dogfood scenario count unnecessarily.

## 8.6 Full solution build

```powershell
dotnet build Raffinert.Consistency.sln -c Release
```

Verify the allocation dogfood project appears in build output.

## 8.7 Formatting

```powershell
dotnet format Raffinert.Consistency.sln --verify-no-changes
```

## 8.8 Diff whitespace

```powershell
git diff --check
```

## 8.9 Package/release verification

Run the repository's existing RC/package verification flow for the current version.

Expected current version unless repository changed legitimately:

```text
0.2.0-rc.2
```

Do not bump version in this pass.

## 8.10 Isolated package consumers

Use a fresh/isolated NuGet cache so same-version global-cache artifacts cannot create a false failure or false pass.

Run:

```text
CoreNet8
CoreNet10
EfNet10
```

and the repository's package asset verification script.

## 8.11 Final git checks

```powershell
git status --short
git diff --check
git log -1 --oneline
```

Working tree must be clean before commit/push report.

---

# Required acceptance matrix

The final report must explicitly mark every row PASS/FAIL.

| Area | Required result |
|---|---|
| FK-only dependent ref retarget | exact `P1 -> P2` |
| Old principal one-to-one ref | exact `D -> null` |
| New principal one-to-one ref | exact `null -> D` |
| One-to-one removal | exact dependent + old principal changes |
| One-to-one addition | exact dependent + new principal changes |
| No-op one-to-one | no navigation mutations |
| Duplicate principal mutation | none |
| Principal cleanup rescan | zero |
| 100 retargets | O(N) structural work |
| 1,000 retargets | O(N) structural work |
| 10,000 retargets | O(N) structural work |
| Shared CLR / separate IEntityType | no cross-resolution |
| EF base -> derived target | derived principal resolves |
| Partial-null composite FK | no relationship |
| Structural byte[] key | resolves with EF comparer |
| Converted/custom key | resolves with EF comparer |
| Generated FK 10k | N lookups, zero tracker scans |
| Allocation dogfood | all scenarios pass |
| Multi-step repair | expected convergence preserved |
| Second-order repair | fresh downstream request preserved |
| Rejected runtime version | unchanged until persistence |
| Preview public API | none |
| Solution build | PASS |
| Package consumers | PASS |
| Formatting | PASS |
| Working tree | clean |

---

# Explicit non-goals

Do **not** do any of the following in this pass:

- public Preview API;
- preview relation indexing redesign;
- validation lease/batch API;
- removal of per-read authority validation;
- transaction/snapshot-isolation abstraction;
- automatic repair execution;
- repair workflow engine;
- background dispatch;
- event bus;
- source generator;
- analyzer work;
- general arbitrary-POCO mutation interception;
- partition-aware authoritative scope;
- new persistence provider architecture;
- rewrite of `ConsistencyScope`;
- relation engine redesign;
- public diagnostics expansion;
- benchmark framework migration;
- unrelated README marketing rewrite;
- version bump;
- backward-compatibility shim creation.

If you notice any such opportunity, put it under `Remaining limitations / future work` and continue with this plan.

---

# Preferred implementation summary

The desired production flow after this pass is:

```text
Capture pre-fixup tracked graph snapshot
        |
        +--> dependent refs: derive old/current from FK snapshot and emit immediately
        |
        +--> principal refs: record ORIGINAL evidence only
        |
        +--> collection relationship evidence from indexed snapshot
        |
DetectChanges once
        |
        +--> EF performs relationship fixup
        |
        +--> read CURRENT principal refs directly from stabilized navigation
        |
        +--> emit exact principal-side changes once
        |
NO provisional principal changes
NO change-list cleanup scan
```

Expected complexity attributable to Raffinert relationship capture:

```text
tracked snapshot/indexes     ~ O(N)
navigation visit             ~ O(N)
principal evidence capture   ~ O(N)
post-fixup principal emit    ~ O(N)
```

EF's own `DetectChanges()` cost is outside Raffinert's indexing algorithm and should be reported separately if it dominates.

---

# Definition of Done

This pass is done only when all of the following are true:

1. The old `changes.RemoveAll(...)` principal cleanup path is gone.
2. Principal-side changes are emitted only after stabilization.
3. Original principal-side evidence is still captured before stabilization.
4. FK-only one-to-one exact three-change semantics pass.
5. Addition/removal/no-op one-to-one tests pass.
6. Shared-type metadata isolation passes.
7. EF base/derived metadata assignability passes.
8. Partial-null composite FK tests pass.
9. Structural/custom key comparer tests pass.
10. Generated-FK 100/1k/10k lookup tests pass with zero tracker scans.
11. Mass one-to-one 100/1k/10k deterministic counters show no cleanup scans and proportional work.
12. Previous navigation/fingerprint performance shape remains intact.
13. Rejected-preview benchmark remains intact with the same validation semantics.
14. Allocation dogfood passes all scenarios.
15. Core net8 passes.
16. Core net10 passes.
17. EF tests pass.
18. Full solution Release build passes with zero warnings/errors.
19. Formatting passes.
20. `git diff --check` passes.
21. RC/package verification passes.
22. Isolated package consumers pass.
23. Experimental Preview types remain internal.
24. Current docs contain no new false production-readiness claims.
25. Working tree is clean.
26. Changes are committed and pushed to `origin/plan/allocation-dogfood`.

After this Definition of Done is satisfied, **stop implementation work on this branch**.

The next activity is merge/PR review, not a tenth dogfood implementation pass.

Only a newly demonstrated correctness defect in the final diff is grounds for reopening implementation.

---

# Required final agent report

Return exactly these sections.

## 1. Starting state

Include:

- starting commit;
- baseline test counts;
- branch;
- clean/dirty state.

## 2. Quadratic-path evidence

Include baseline evidence from the old implementation:

```text
N
principal evidences
cleanup inspections
elapsed time
allocated bytes if measured
```

State whether the suspected super-linear cleanup was confirmed.

## 3. Implementation

Explain precisely:

- how dependent-side refs are captured;
- how principal-side original evidence is retained;
- when `DetectChanges()` runs;
- how current principal-side refs are read;
- confirmation that no `RemoveAll`/equivalent cleanup scan remains.

## 4. One-to-one correctness

Report PASS/FAIL for:

- FK-only retarget;
- FK-only removal;
- FK-only addition;
- no-op;
- explicit navigation replacement;
- ambiguity fail-closed.

## 5. Linearity result

Provide a table for 100 / 1,000 / 10,000 with at least:

```text
N
capture ms
allocated bytes
principal evidence count
principal cleanup scans
principal changes emitted
reference index lookups
candidate checks
```

## 6. Prior-regression preservation

Report:

- shared CLR metadata isolation;
- base/derived metadata;
- partial-null composite FK;
- byte[] comparer;
- converted/custom comparer;
- generated-FK lookup scalability.

## 7. Dogfood and rejected-preview regression

Report:

- allocation scenario count;
- single-step repair;
- multi-step repair;
- second-order repair;
- runtime version behavior;
- temporary reseed count;
- rejected-preview 100/1k/10k table.

## 8. Public API and merge gate

Report:

- intended public additions;
- confirmation Preview/snapshot/diagnostics remain internal;
- dead/provisional code findings;
- docs status;
- solution inclusion.

## 9. Verification

Provide an explicit command/result table for:

- solution build;
- Core net8;
- Core net10;
- EF;
- dogfood direct build;
- dogfood executable;
- formatting;
- `git diff --check`;
- RC/package verification;
- CoreNet8 consumer;
- CoreNet10 consumer;
- EfNet10 consumer;
- package assets;
- working tree.

Do not summarize unexecuted commands as PASS.

## 10. Remaining limitations

Only list genuine remaining known limitations. Do not invent another implementation roadmap.

At minimum preserve, if still true:

- authoritative scope is host-asserted;
- core POCO mutation reporting is explicit;
- arbitrary unreported POCO drift cannot be guaranteed;
- preview authority validation is not DB snapshot isolation;
- preview relation queries scan proposed candidates;
- repair ranking/convergence remain application-owned;
- Preview remains internal/experimental.

## 11. Commit

Include:

- final commit SHA;
- branch;
- push status;
- working tree status.

End with one of exactly:

```text
MERGE GATE: PASS
```

or

```text
MERGE GATE: FAIL
```

If FAIL, state the concrete failed acceptance criterion. Do not automatically start another implementation pass.
