# PR review blockers: added-baseline relationship evidence and mutable EF fingerprints

## Purpose

This is a deliberately narrow merge-blocker fix plan for PR #1 on branch `plan/allocation-dogfood`.

Starting implementation head before this plan: `ff59be1b4f45627abad03a5d1f135b59b3f77f2d`.

Do **not** turn this into another architecture/dogfood pass. Do not redesign preview, persistence policy, repair, relation indexing, or the public API. Fix exactly the two active Codex review findings, prove them with focused regressions, rerun the existing merge gates, reply to the two review threads, and stop.

The two open review findings are:

1. **P1 – original relationship indexes incorrectly include `Added` tracked entries.**
   - Review comment: `discussion_r4057559772`
   - File: `src/Raffinert.Consistency.EntityFrameworkCore/TrackedGraphSnapshot.cs`
2. **P2 – EF mutation fingerprints retain mutable property-value references instead of immutable EF-comparer snapshots.**
   - Review comment: `discussion_r4057559774`
   - Files centered around `EfMutationFingerprint.cs`, fingerprint capture in `ConsistencySave.cs`, and rejected/pending validation in `ConsistencyEfCoreSession.cs`.

No backward-compatibility shims are required. This is still pre-1.0/RC. Prefer the correct API/internal design over preserving an internal implementation mistake.

---

# 0. Guardrails for the agent

Before editing anything:

1. Confirm the current branch is `plan/allocation-dogfood`.
2. Confirm `git status` is clean.
3. Record current HEAD.
4. Read, do not skim:
   - `src/Raffinert.Consistency.EntityFrameworkCore/TrackedGraphSnapshot.cs`
   - `src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs`
   - `src/Raffinert.Consistency.EntityFrameworkCore/EfMutationFingerprint.cs`
   - `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs`
   - `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreSession.cs`
   - `tests/Raffinert.Consistency.EntityFrameworkCore.Tests/TrackedGraphSnapshotTests.cs`
   - `tests/Raffinert.Consistency.EntityFrameworkCore.Tests/InjectedRuntimeMaterializationTests.cs`
5. Search the EF test project for:
   - `CreateRejectedPreview`
   - `RejectedPreviewValidationCount`
   - `Fingerprint`
   - `byte[]`
   - `ValueComparer`
   - one-to-one principal-side capture tests
6. Run the starting tests before changes and record counts:
   - Core net8
   - Core net10
   - EF net10
   - allocation dogfood
7. Do **not** modify public API baselines unless a genuinely public API change becomes unavoidable. It should not be necessary.
8. Do **not** solve either finding with type-specific hacks such as `if (value is byte[])`.
9. Do **not** reintroduce any `ChangeTracker.Entries()` call inside a per-property/per-navigation hot loop. Previous passes explicitly removed those O(N²) paths.
10. Do **not** weaken tests or change expected semantics merely to make a new test pass.

Expected result: both fixes should be internal EF adapter corrections.

---

# 1. P1: prove the `Added`-entry original-index bug before fixing it

## Problem

`TrackedGraphSnapshot.BuildIndex(...)` currently iterates all tracked entries and, for `original == true`, reads `OriginalValue` but does not exclude `EntityState.Added`.

That is semantically wrong: an `Added` entity did not exist in the persisted/runtime baseline graph even if EF exposes an original FK value equal to its current FK value.

This affects both principal and dependent original-state indexes because `BuildIndex` is shared.

The rule to implement is:

```text
original relationship index = entries that existed in the baseline
Added entries                 = never members of that original graph
```

Do not narrowly patch only one-to-one code. Fix this at the index-population boundary.

## 1.1 Add a low-level regression first

Add a focused `TrackedGraphSnapshotTests` regression that constructs/tracks:

- an existing principal,
- a newly `Added` dependent,
- the dependent FK already points at the existing principal,
- EF reports the added dependent's FK `OriginalValue` equal (or effectively equal) to its current FK.

Assert the test precondition where practical so the regression documents the EF behavior that makes the bug possible.

Then assert:

```text
FindDependents(... original: true)  => does NOT contain the Added dependent
FindDependents(... original: false) => DOES contain the Added dependent
```

If there is a corresponding principal-index scenario where an added principal can be resolved by an existing dependent, add the symmetric low-level assertion as well. Since the fix belongs in shared `BuildIndex`, both principal and dependent original indexes should obey the same baseline rule.

The test must fail against the pre-fix implementation for the intended reason.

## 1.2 Add a public capture-path one-to-one regression

Use the existing one-to-one test model/fixture if possible; do not invent a parallel model unless the existing fixture cannot express the case cleanly.

Cover at least this scenario:

### Scenario A: no old dependent, add a new dependent

Baseline:

```text
P.Detail = null
```

Mutation:

```text
D2 is Added
D2.ParentId = P.Id
```

Expected captured navigation evidence:

```text
P.Detail : null -> D2
D2.Parent: null/baseline-none -> P   (according to existing dependent-side capture semantics)
```

Most important assertion: the principal-side old value must be `null`, never `D2`.

### Scenario B: replace existing dependent with an Added dependent

Baseline:

```text
P.Detail = D1
```

Proposed tracked state:

```text
D1 no longer owns P
D2 is Added with FK = P.Id
```

Expected principal-side evidence:

```text
P.Detail : D1 -> D2
```

There must be:

- no ambiguity exception,
- no omitted principal-side mutation,
- no duplicate `(instance, member)` navigation mutation.

Exercise the same public capture path used in production (`CreateChangeSet` and/or `CaptureUnitOfWork`, following the existing test conventions). Do not test only the internal index.

## 1.3 Implement the minimal semantic fix

The expected implementation shape is at `TrackedGraphSnapshot.BuildIndex(...)`:

```csharp
if (original && entry.State == EntityState.Added)
    continue;
```

Place this before extracting index values.

Important:

- This is intentionally a rule for **all original indexes**, not only dependents.
- Do not special-case one-to-one.
- Do not change metadata assignability filtering.
- Preserve partial-null composite FK handling.
- Preserve EF key comparer semantics.
- Do not automatically add a symmetric `Deleted` exclusion for current indexes unless a focused failing regression proves that current behavior is wrong. That is outside this review finding.

## 1.4 Re-run focused P1 tests

Run:

- all new P1 tests,
- all `TrackedGraphSnapshotTests`,
- existing one-to-one replacement/FK-only/owned/ambiguity regressions,
- generated-FK scale tests.

Do not proceed to P2 until P1 is green.

---

# 2. P2: prove mutable fingerprint aliasing before designing the fix

## Problem

`EfMutationFingerprint` currently captures `PropertyChange.OldValue` / `NewValue` as ordinary `object?` references and later compares them with `object.Equals`.

That is unsafe for mutable reference values.

Example:

```csharp
var bytes = entity.Hash;
// rejected/pending fingerprint stores `bytes` as NewValue
bytes[0] = 42; // in-place mutation
```

The old fingerprint now observes the modified object too because it retained the same reference. A later freshly captured fingerprint can therefore compare equal even though semantic EF state changed.

This is not limited to rejected preview. The same fingerprint is also used to decide whether `_pending` plans can be reused in `ConsistencyEfCoreSession`.

Required semantic rule:

```text
fingerprint evidence must represent value-at-capture-time,
not a live reference to a mutable object.
```

And equality must follow EF property comparison semantics, not generic `object.Equals`.

---

# 3. P2 tests first: low-level fingerprint regression

Add a focused EF test that proves the current bug before the fix.

## 3.1 Structural `byte[]` regression

Create/use an EF mapped semantic property of type `byte[]` that participates in consistency tracking.

Required sequence:

1. Establish baseline.
2. Mutate the property so a `PropertyChange` exists.
3. Capture fingerprint F1.
4. Mutate the **same current array instance in place** without replacing the property reference.
5. Capture fingerprint F2.
6. Assert `F1.Equals(F2) == false`.

The test must ensure the difference is caused by content mutation of the same instance, not assignment of a second array.

If EF's default comparer for that property is structural, use it. Do not add application-level cloning to make the test pass.

## 3.2 Custom mutable value/comparer regression

Do not allow a `byte[]`-specific implementation.

Add a small test-only mutable reference/value-object type with a configured EF `ValueComparer` that:

- compares by meaningful content,
- snapshots by creating an independent copy.

Repeat the same F1 -> mutate same object -> F2 assertion.

This proves the implementation is based on EF metadata/comparer semantics rather than hardcoded array handling.

## 3.3 Equal-but-distinct value regression

Add the opposite check:

- F1 contains a snapshotted value,
- current property is replaced with a distinct instance that is equal according to the configured EF comparer,
- the relevant evidence should compare equal if the semantic mutation set is otherwise the same.

The fingerprint must not devolve into pure reference equality for scalar EF properties.

---

# 4. P2 integration regression: rejected preview must become stale

Add an integration test in the existing rejected-preview/session test area (prefer `InjectedRuntimeMaterializationTests.cs` or the existing file that already owns rejected preview behavior).

Build the smallest model where a mutable semantic EF property influences a derived value and/or enforced invariant.

Required sequence:

1. Create a valid runtime/EF baseline.
2. Make a change that causes an enforced invariant rejection.
3. Obtain the rejected preview through the existing supported internal test path.
4. Evaluate/read something from the preview once so it has a concrete pre-mutation proposed-state result.
5. Mutate the same mutable property value instance in place (e.g. modify `byte[]` contents).
6. Attempt another preview read/evaluation.
7. Assert it throws the existing stale-preview `InvalidOperationException` rather than returning cached pre-mutation data.
8. Assert the runtime version/baseline did not advance merely because validation detected staleness.

Do not change preview public visibility. `ConsistencyPreview` remains internal/experimental.

---

# 5. P2 integration regression: pending-plan reuse must also detect the mutation

Because the same fingerprint is used for `_pending` reuse, add a separate regression for that path.

Preferred scenario:

- mutable semantic input feeds a materialized derived value,
- first materialization/preparation creates a pending plan,
- mutate the same input object in place,
- invoke the materialization/preparation path again,
- verify the previous pending plan is **not** reused as authoritative,
- verify the result reflects the new input value.

A concrete form is acceptable:

```text
Token = byte[] { 1 }
derived/materialized value = Token[0]
prepare/materialize => 1
Token[0] = 2 in place
prepare/materialize again => 2
```

The exact model can follow existing `InjectedRuntimeMaterializationTests` patterns.

If directly observing pending-plan reuse is easier through internal counters/test hooks, use existing internal diagnostics only. Do not add public diagnostics for this test.

---

# 6. P2 implementation design: use EF `ValueComparer` snapshot/equality

## 6.1 Do not use generic cloning

Forbidden fixes:

```csharp
value is byte[] ? ((byte[])value).ToArray() : value
```

or JSON serialization, reflection deep-copy, `ICloneable`, etc.

EF already owns the value semantics. Use its metadata.

## 6.2 Fingerprint creation needs EF property metadata

Current `EfMutationFingerprint.Create(IReadOnlyList<RuntimeMutation>)` does not know which `IProperty` produced a scalar `PropertyChange`.

Refactor internally so fingerprint creation can resolve EF metadata for scalar property mutations.

A reasonable shape is one of:

```csharp
EfMutationFingerprint.Create(ChangeTracker tracker, IReadOnlyList<RuntimeMutation> mutations)
```

or

```csharp
EfMutationFingerprint.Create(
    IReadOnlyList<RuntimeMutation> mutations,
    IEfFingerprintValueResolver resolver)
```

Prefer the simplest internal design that keeps metadata lookup linear and testable.

Do not expose this publicly.

## 6.3 Avoid reintroducing O(N²)

Do **not** resolve every `PropertyChange.Instance` with:

```csharp
changeTracker.Entries().Single(...)
```

inside the mutation loop.

Build one reference-identity map per fingerprint capture:

```text
entity object reference -> EntityEntry
```

Cost must be O(tracked entries + mutations), not O(entries × mutations).

Reusing an internal `TrackedGraphSnapshot`/equivalent identity map is fine if it does not distort responsibilities. A small dedicated resolver is also fine.

## 6.4 Resolve scalar EF property metadata carefully

For a `PropertyChange`:

1. Resolve the `EntityEntry` by `ReferenceEquals` identity.
2. Resolve the EF scalar `IProperty` corresponding to `change.Member`.
3. Verify you are not accidentally treating a navigation/property-like member as an EF scalar property.
4. Obtain the EF `ValueComparer` used for that property's model semantics.
5. Snapshot `OldValue` and `NewValue` at fingerprint construction time.
6. Store enough comparer/equality information in the evidence so later fingerprint equality uses EF semantics.

For non-scalar runtime mutations/navigation-style property changes where no EF scalar `IProperty` applies, preserve the existing appropriate identity/value semantics. Do not force a scalar comparer onto navigation entities.

## 6.5 Use the EF comparer for both snapshot and equality

The desired conceptual evidence is:

```text
member metadata identity
old snapshot = comparer.Snapshot(oldValue)
new snapshot = comparer.Snapshot(newValue)
comparer     = EF value comparer for that property
```

And equality becomes conceptually:

```text
same mutation kind/set/instance/member/etc
AND comparer.Equals(oldSnapshotA, oldSnapshotB)
AND comparer.Equals(newSnapshotA, newSnapshotB)
```

Use the exact EF Core 10 APIs available in the project. Do not guess method signatures. Compile against the actual referenced EF version.

If `IProperty.GetValueComparer()` can be null/default for some properties, resolve the effective comparer according to EF Core's actual metadata/type-mapping API rather than falling back blindly to `object.Equals` for a mutable type. The agent must inspect the API available in EF Core 10 and add a test proving default `byte[]` semantics work.

For key values, do not disturb the existing `GetKeyValueComparer()` behavior in `TrackedGraphSnapshot`; this fingerprint fix concerns semantic scalar property evidence and has different metadata needs.

## 6.6 Keep fingerprint equality deterministic

Do not rely on mutation list object identity.

Preserve the existing ordering contract unless a failing test shows ordering instability. This review finding is about value snapshotting, not fingerprint canonicalization.

---

# 7. Update every fingerprint creation call consistently

Search for every call to:

```text
EfMutationFingerprint.Create(...)
```

At minimum inspect/update:

- normal sync prepare,
- async prepare,
- `CaptureFingerprint`,
- `CaptureFingerprintAsync`,
- `CaptureTrackedStateFingerprint`,
- any test helper/direct construction.

Do not leave one path using old raw-reference evidence while another uses snapshotted evidence.

After the refactor, there should be exactly one semantic rule for EF mutation fingerprint values.

---

# 8. Preserve all previous EF capture guarantees

After P1/P2 changes, explicitly rerun and verify these previously fixed areas:

1. Partial-null composite FK means no relationship.
2. Shared CLR entity types do not cross-resolve equal keys.
3. Base EF target resolves tracked derived entity type.
4. Structural `byte[]` key comparison remains correct.
5. Configured converted/custom key comparers remain correct.
6. Generated-FK capture stays indexed with zero full tracker scans.
7. FK-only one-to-one retarget still emits:
   - dependent old -> new,
   - old principal dependent -> null,
   - new principal null -> dependent.
8. Principal-side capture has no provisional-change `RemoveAll` cleanup scan.
9. Owned-reference/replacement/ambiguity regressions remain green.
10. Rejected-save runtime version still stays unchanged until successful persistence.
11. Preview authority validation remains per-read and still makes no snapshot-isolation claim.

---

# 9. Performance regression gates

Do not accept a correctness fix that quietly restores quadratic tracker scanning.

Run the existing scale checks after implementation:

## 9.1 Generated FK scale

Expected structural property remains:

```text
100 modified FKs   -> 100 indexed lookups, 0 full scans
1,000              -> 1,000 indexed lookups, 0 full scans
10,000             -> 10,000 indexed lookups, 0 full scans
```

## 9.2 One-to-one retarget scale

Keep the ninth-pass guarantees:

```text
principal cleanup scans = 0
candidate scans          = 0 for the indexed scenario
work grows approximately linearly with N
```

## 9.3 Rejected-preview benchmark

Rerun the existing 100 / 1,000 / 10,000 rejected-preview benchmark.

Some allocation increase from comparer snapshots is acceptable, especially for mutable properties, but there must be no new O(N²) behavior and no order-of-magnitude regression without explanation.

Report before/after values in the implementation report.

---

# 10. Full verification gate

Before reporting completion, run all of the following from a clean tree:

1. `dotnet restore Raffinert.Consistency.sln`
2. Release build, zero warnings/errors.
3. Core tests net8 — all pass.
4. Core tests net10 — all pass.
5. EF tests net10 — all pass, including new P1/P2 regressions.
6. Allocation dogfood — all scenarios pass.
7. Order fulfillment sample — passes.
8. EF Core consistency sample — passes.
9. Dependency maintenance sample — passes.
10. `dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes`
11. `git diff --check`
12. Pack `0.2.0-rc.3`.
13. Run RC verification script with empty unshipped requirement.
14. Restore CoreNet8/CoreNet10/EfNet10 consumers from the local package source.
15. Run `VerifyPackageConsumerAssets.ps1` and confirm all Raffinert packages resolve `0.2.0-rc.3`.
16. Run all three package consumers.

Do not bump to `rc.4`. `rc.3` has not been published; these review fixes belong in the same candidate.

---

# 11. Review-thread closure

Only after tests and full verification pass:

## P1 reply

Reply to review comment `4057559772` with a concise explanation containing:

- original indexes now exclude `EntityState.Added` at shared index construction,
- focused tests cover new dependent and replacement-with-added-dependent cases,
- existing one-to-one/scale tests remain green,
- implementation commit SHA.

## P2 reply

Reply to review comment `4057559774` with:

- fingerprints now snapshot scalar EF values at capture time using EF `ValueComparer` semantics,
- same comparer is used for equality,
- byte[] in-place mutation regression,
- custom comparer regression,
- rejected-preview stale validation regression,
- pending-plan reuse regression,
- implementation commit SHA.

If GitHub permissions/tooling allow resolving threads, resolve them only after the replies are posted and the PR CI run for the implementation commit is green. Otherwise leave the replies and report that thread resolution must be clicked manually.

Request a fresh Codex review after the implementation is pushed (`@codex review`) so the bot reviews the new head rather than the old `7b3fc589` commit.

---

# 12. Required implementation report

The agent's final report must contain exactly these sections:

## 1. Starting state

- starting commit
- test counts
- current RC version

## 2. P1 reproduction

- exact failing scenario before fix
- why Added OriginalValue polluted baseline lookup

## 3. P1 implementation

- exact code rule
- tests added

## 4. P2 reproduction

- exact mutable aliasing scenario
- proof F1 incorrectly equaled F2 before fix

## 5. P2 implementation

- how EF property metadata is resolved
- which comparer API is used
- how `Snapshot` is used
- how equality is performed
- how O(N²) lookup was avoided

## 6. Rejected-preview regression

- result of in-place mutable-value stale check

## 7. Pending-plan regression

- proof stale pending plan is not reused

## 8. Performance

- generated-FK scale
- one-to-one scale
- rejected-preview 100/1k/10k before/after

## 9. Full verification

- all test/sample/package results

## 10. PR review status

- replies posted to P1/P2
- fresh Codex review requested or reason it could not be requested
- CI status for the implementation head if available

## 11. Commit

- implementation commit SHA
- branch
- clean working tree confirmation

---

# Definition of Done

This micro-pass is complete only when all are true:

- `Added` entities cannot contaminate any original relationship index.
- Principal one-to-one add/replacement capture is correct and non-ambiguous.
- EF scalar fingerprint values are snapshots, not live mutable references.
- Fingerprint value equality follows EF `ValueComparer` semantics.
- In-place `byte[]` changes invalidate rejected preview validation.
- A configured custom mutable comparer behaves correctly.
- Pending-plan reuse cannot survive a relevant in-place mutable input change.
- No new O(N²) tracker/property scan is introduced.
- All previous EF correctness/performance regressions remain green.
- Full RC/package verification for `0.2.0-rc.3` passes.
- Both review comments have implementation replies.
- A fresh review is requested on the new head.

After that, stop. Do not create another dogfood pass unless the fresh review or CI produces a concrete new defect.