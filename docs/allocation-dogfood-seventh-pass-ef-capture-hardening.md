# Allocation dogfood seventh pass: EF capture hardening after tracked-graph indexing

## Purpose

This is a **literal step-by-step anti-dumb implementation plan for a weak agent**. Follow the order. Do not redesign unrelated APIs. Do not promote `ConsistencyPreview` to public API. Do not weaken rejected-preview validation. Do not remove external consumer discovery. Do not touch repair ranking or workflow semantics.

The sixth pass fixed the main quadratic navigation-resolution path by introducing `TrackedGraphSnapshot`. That fix is accepted and must not regress.

This seventh pass is intentionally smaller and focuses on the remaining correctness and scalability gaps around EF mutation capture:

1. fix **partial-null composite foreign-key semantics**;
2. remove the remaining `ChangeTracker.Entries()` scan in generated-FK fixup capture;
3. prove tracked-key comparison behaves correctly for configured EF key comparers / unusual key values;
4. document preview authority checks as **staleness detection**, not database snapshot isolation;
5. re-run the sixth-pass benchmarks to prove no regression.

Expected branch:

```text
plan/allocation-dogfood
```

Expected starting implementation: commit `662f76e3a8ce36373d6cec34cbdee6874f169fd6` or later if only this plan was added.

---

# 0. Read first. Do not edit code yet.

Read these files completely before making changes:

- `src/Raffinert.Consistency.EntityFrameworkCore/TrackedGraphSnapshot.cs`
- `src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs`
- `src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs`
- `src/Raffinert.Consistency.EntityFrameworkCore/EfFingerprintDiagnostics.cs`
- `tests/Raffinert.Consistency.EntityFrameworkCore.Tests/TrackedGraphSnapshotTests.cs`
- `tests/Raffinert.Consistency.EntityFrameworkCore.Tests/InjectedRuntimeMaterializationTests.cs`
- `tests/Raffinert.Consistency.EntityFrameworkCore.Tests/ExternalConsumerDiscoveryTests.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/EfMutationCaptureBenchmark.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/RejectedEfPreviewBenchmark.cs`
- `experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md`
- `docs/ef-core-consistency.md`

Before editing, write down the two currently known problem paths:

```text
Problem A: composite optional FK

FK values = [1, null]
current code checks values.All(x => x is null)
=> false
=> attempts principal lookup for [1, null]
=> can fail as ambiguous/not found

But EF relationship semantics treat composite FK as null when any component is null.
```

and:

```text
Problem B: generated FK fixup evidence

CapturePolicyAwareSnapshot
    -> for each modified property
        -> CaptureGeneratedFixupEvidence(...)
            -> tracker.Entries().SingleOrDefault(...principal...)

Many modified FK properties × all tracked entries
=> possible O(N^2)
```

Do not proceed until you can point to the exact current methods implementing both paths.

---

# 1. Establish a clean baseline

Run before any code changes:

```powershell
dotnet build Raffinert.Consistency.sln -c Release

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release

dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build
```

Record exact counts.

Expected approximate starting counts from previous report:

```text
Core net8: 395
Core net10: 395
EF: 232
Dogfood: 32
```

Do not hard-code these expectations into tests. Record actual counts from the branch.

If the baseline is red, fix baseline first. Do not mix unrelated failures into this pass.

---

# 2. Fix composite-FK null semantics with tests first

## 2.1 Add failing tests before implementation

Extend `TrackedGraphSnapshotTests` with explicit optional composite-FK cases.

Use an optional composite relationship with two FK components, for example:

```csharp
ParentPartition : int?
ParentCode      : string?
```

Required cases:

```text
[null, null] -> relationship is null
[1, null]    -> relationship is null
[null, "A"]  -> relationship is null
[1, "A"]     -> relationship can resolve normally
```

Also test transitions:

```text
[1, "A"] -> [1, null]
    old navigation = original parent
    new navigation = null

[1, null] -> [1, "A"]
    old navigation = null
    new navigation = matching parent
```

The tests must verify emitted `PropertyChange` old/new navigation values, not only that capture does not throw.

## 2.2 Do not implement an ad-hoc condition only in one call site

Create one internal helper for EF relationship-key null semantics.

Acceptable conceptual shape:

```csharp
private static bool IsNullRelationshipKey(
    IReadOnlyList<IProperty> properties,
    IReadOnlyList<object?> values)
```

The rule for an optional FK is:

```text
if any FK component is null, the relationship key is null
```

Do not use:

```csharp
values.All(value => value is null)
```

Do not invent SQL-style partial composite matching.

## 2.3 Apply the helper consistently

Inspect every place where an FK value tuple is interpreted as a relationship key.

At minimum inspect:

- dependent-side reference resolution;
- principal-side dependent lookup;
- collection relationship change detection;
- generated-FK fixup logic if it interprets FK tuples;
- any tracked snapshot index creation/lookups that could accidentally index a null relationship as a valid relationship key.

Do not change key semantics for ordinary entity primary keys. This rule is about **foreign-key relationship nullness**, not object-set identity.

## 2.4 Expected result

After the fix:

```text
partial-null composite FK
    -> no principal lookup
    -> relationship treated as null
    -> deterministic old/new navigation mutation
```

No ambiguous-resolution exception should be produced merely because one optional composite FK component is null.

---

# 3. Add direct-entry lookup to `TrackedGraphSnapshot`

The sixth pass already captures tracked entries once. Reuse that fact.

## 3.1 Add a reference-identity map

Inside `TrackedGraphSnapshot`, build or lazily build:

```text
object entity reference -> EntityEntry
```

Use reference equality, never value equality.

Conceptual field:

```csharp
private readonly Dictionary<object, EntityEntry> _entriesByReference;
```

Use `ReferenceEqualityComparer.Instance` or equivalent.

Populate it once from the same `Entries` array.

## 3.2 API shape

Add an internal method such as:

```csharp
internal EntityEntry? FindEntry(object entity)
```

or:

```csharp
internal bool TryFindEntry(object entity, out EntityEntry entry)
```

Do not expose this publicly.

Do not call `DbContext.Entry(entity)` as a substitute if doing so can create/attach state or otherwise change semantics.

## 3.3 Required tests

Add tests proving:

```text
same instance -> exact EntityEntry found
value-equal different instance -> not treated as same entry
untracked instance -> not found
added/deleted/modified/unchanged tracked entries -> all findable by reference
```

If a duplicate reference somehow appears impossible by EF design, no special duplicate policy is necessary beyond deterministic map construction.

---

# 4. Remove the remaining generated-FK fixup full ChangeTracker scan

## 4.1 Current forbidden code pattern

Find and remove the equivalent of:

```csharp
tracker.Entries().SingleOrDefault(entry =>
    ReferenceEquals(entry.Entity, principal))
```

from `CaptureGeneratedFixupEvidence`.

After this pass there must be **no full tracked-entry enumeration per modified property** for principal lookup.

## 4.2 Thread the snapshot through policy-aware capture

`CapturePolicyAwareSnapshot` currently performs navigation capture and scalar-property capture separately.

Refactor so one `TrackedGraphSnapshot` instance can be reused for the complete capture operation.

Preferred conceptual flow:

```text
CapturePolicyAwareSnapshot
    -> create TrackedGraphSnapshot once
    -> capture navigation mutations using snapshot
    -> capture scalar/generated-fixup evidence using same snapshot
```

Do not create one snapshot for navigation capture and another for every property.

Do not regress the ordinary `CaptureUnitOfWork` path.

## 4.3 Update `CaptureGeneratedFixupEvidence`

Change the helper to receive the tracked snapshot rather than the raw `ChangeTracker` for entity-reference lookup.

Conceptual:

```csharp
CaptureGeneratedFixupEvidence(
    TrackedGraphSnapshot snapshot,
    EntityEntry dependent,
    IProperty property)
```

Then:

```csharp
var principalEntry = snapshot.FindEntry(principal);
```

Keep all existing generated-value/fixup correctness conditions unchanged.

Do not weaken checks around:

- temporary keys;
- `ValueGenerated` metadata;
- intended principal navigation identity;
- FK component correspondence;
- final generated values.

## 4.4 Add a work counter

Extend internal diagnostics with a narrowly scoped count such as:

```text
GeneratedFixupPrincipalLookups
GeneratedFixupTrackedEntryScans
```

The new implementation should show:

```text
GeneratedFixupTrackedEntryScans = 0
```

Do not add public diagnostics API.

---

# 5. Add a generated-FK fixup scalability regression test

The previous benchmark focused on navigation resolution. Add a targeted test/benchmark shape for the remaining fixup path.

## 5.1 Construct the graph

Create a model with many dependents where a relevant FK property is modified and `CapturePolicyAwareSnapshot` evaluates generated-FK fixup evidence.

Use at least:

```text
100
1,000
10,000
```

dependents if runtime is reasonable.

The test must exercise the actual `CapturePolicyAwareSnapshot` path, not only `TrackedGraphSnapshot.FindEntry` directly.

## 5.2 Required work-count assertion

Do not assert fragile millisecond limits in unit tests.

Assert algorithmic work instead.

Example acceptable evidence:

```text
principal lookup count grows ~linearly with modified FK property count
full tracked-entry principal scans = 0
```

If diagnostics expose exact counts, assert exact/simple bounds.

Example:

```csharp
Assert.Equal(modifiedCount, diagnostics.GeneratedFixupPrincipalLookups);
Assert.Equal(0, diagnostics.GeneratedFixupTrackedEntryScans);
```

Do not write an assertion that merely says execution is “fast enough”.

## 5.3 Optional benchmark output

If easy, extend `EfMutationCaptureBenchmark` with a separate generated-fixup case:

```text
N | generated-FK policy capture ms | allocated bytes | principal lookups
```

Keep it clearly separate from ordinary navigation capture.

---

# 6. Verify EF key comparison semantics

The current `TrackedValueKey` uses ordinary `object.Equals` / `HashCode` semantics through `SequenceEqual` and `HashCode.Add`.

Do not assume this is always equivalent to EF key equality.

## 6.1 Investigate before changing code

Check EF metadata for each indexed property:

```text
property.GetKeyValueComparer()
property.GetValueComparer()
```

Determine which comparer EF itself expects for key/FK matching in the supported model.

Write down the result in code comments or DOGFOOD findings before making a change.

Do not blindly replace equality logic.

## 6.2 Add tests for non-trivial key values

At minimum cover:

### Case A — byte-array key component

Use a key/FK involving `byte[]` if EF model/provider supports it in the test setup.

Two separate arrays with equal bytes should follow EF key equality semantics, not reference equality, if EF treats them as equal key values.

### Case B — value-converted/custom comparable key

Use a small custom value object or converted key type with an explicit `ValueComparer`.

Prove that tracked principal/dependent resolution follows the EF-configured comparer semantics.

### Case C — ordinary scalar key regression

`int`, `string`, nullable scalar and composite scalar behavior must remain unchanged.

## 6.3 Only then change `TrackedValueKey` if needed

If tests prove current equality is wrong, make the index key metadata-aware.

Possible direction:

```text
Index descriptor already knows properties
    -> key object stores values + corresponding EF comparers
    -> equality/hash use matching property comparer per component
```

Do not use one global comparer for heterogeneous composite keys.

Do not allocate new comparer arrays on every lookup if avoidable.

Do not make `TrackedValueKey` public.

If investigation proves ordinary equality is correct for all supported EF key scenarios, keep implementation unchanged and document why.

---

# 7. Preserve ambiguous-resolution fail-closed behavior

Do not weaken this behavior while changing index/null semantics.

Required cases must still fail closed:

```text
two tracked principals matching one dependent FK
multiple original dependents matching a principal-side one-to-one reference
conflicting tracked key state that EF exposes ambiguously
```

The error should remain deterministic and explain that the navigation is not tracked unambiguously.

Add/retain tests for both:

```text
dependent -> principal ambiguity
principal -> dependent ambiguity
```

Do not silently select the first bucket entry.

---

# 8. Verify collection semantics after partial-null changes

Partial-null composite FKs must not create false collection resets.

Add tests for:

```text
child composite FK [1, "A"] -> [1, null]
    old parent's collection reset exactly once
    no phantom new parent reset

child composite FK [1, null] -> [2, "B"]
    new parent's collection reset exactly once

child composite FK [1, null] -> [2, null]
    remains null relationship
    no principal collection reset caused by fake partial-key matching
```

Also retain:

```text
move old parent -> new parent
    old reset once
    new reset once
```

Deduplication by `(owner reference, member)` must remain intact.

---

# 9. Clarify authority validation semantics in documentation

Do not change runtime behavior in this phase.

Update:

- `experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md`
- `docs/ef-core-consistency.md`

The docs must distinguish:

```text
tracked-state staleness detection
```

from:

```text
authoritative database snapshot isolation
```

Required wording/concepts:

```text
External consumer discovery is retained during rejected-preview validation because the tracked graph alone cannot reveal newly created external consumers.

Repeating authority discovery before preview reads strengthens stale-state detection, but it does not create an atomic database snapshot. Another transaction can change the authoritative database after a validation completes.

Final SaveChanges planning/enforcement remains the durability boundary that must revalidate authoritative consistency requirements.
```

Do not claim:

```text
preview is transactionally authoritative
preview sees a stable database snapshot
per-read discovery eliminates external races
```

Preview remains internal and experimental.

---

# 10. Re-run the sixth-pass benchmarks unchanged

After correctness fixes, run the existing benchmark workload without silently changing graph shape.

Required outputs:

## Navigation capture benchmark

```text
100
1,000
10,000
```

Report:

```text
capture ms
allocated bytes
reference navigations
reference index lookups
reference candidate scans/checks
collection index lookups
collection candidate checks
```

The sixth-pass algorithmic improvement must remain.

At 10k, there must be no return to hundreds of millions of reference candidate scans.

## Rejected EF preview benchmark

Run the same:

```text
rejected save
CreateRejectedPreview
five repair-decision reads
```

Report:

```text
rejection + preview ms
repair query ms
allocated bytes
reads
validations
```

Do not reduce validation count in this pass.

## Generated-FK policy-aware capture benchmark/test

If implemented as benchmark, report:

```text
N
capture ms
allocated bytes
principal reference lookups
full tracker scans
```

The key acceptance criterion is algorithmic:

```text
full tracker scans per modified FK property = 0
```

---

# 11. Do not redesign per-read preview validation in this pass

The sixth pass concluded that current per-read validation is acceptable for the **internal experiment** after the main capture fix.

This pass must not introduce:

- validation lease;
- public query batch;
- session revision API;
- weaker stale detection;
- skipped external discovery;
- cached authority results across arbitrary caller mutations.

If benchmarks unexpectedly regress badly, document the evidence and stop. Do not invent a new preview architecture inside this pass.

---

# 12. Do not change repair architecture

Preserve all current dogfood truths:

```text
single-step rejected-save repair passes
multi-step repair uses fresh plan/preview after each mutation
second-order repair produces new repair request for the newly violated source
committed runtime version does not advance on rejected plans
runtime advances once after final successful save
full temporary runtime reseed count remains zero on preview path
application owns ranking/convergence
```

Do not move replacement ranking into Raffinert core.

Do not make preview mutable.

Do not add staged repair workflow DSL.

---

# 13. Search for remaining `ChangeTracker.Entries()` hot-loop patterns

Before declaring completion, search the EF project for:

```text
tracker.Entries()
changeTracker.Entries()
context.ChangeTracker.Entries()
```

Classify every occurrence as one of:

```text
A. one enumeration per capture/save operation -> acceptable
B. inside a loop over tracked entities/properties/navigations -> suspicious
C. intentionally tiny lookup outside hot capture path -> document
```

Do not mechanically rewrite all occurrences.

But there must be no known pattern equivalent to:

```text
for each tracked item
    scan every tracked item
```

in the mutation/fingerprint capture path without an explicit justification.

Include the audit summary in the final agent report.

---

# 14. Public API / compatibility rules

The library is still release-candidate stage, so backward compatibility is not a goal.

However this pass should require **no new public API**.

Expected changes should remain internal/tests/docs/experiment level.

Do not add:

```text
V2 APIs
compatibility aliases
obsolete wrappers
public snapshot/index types
public diagnostics counters
```

If a public API change appears necessary, stop and explain why in the implementation report instead of quietly expanding surface area.

---

# 15. Required verification commands

Run all of these after implementation:

```powershell
dotnet build Raffinert.Consistency.sln -c Release

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release

dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build

dotnet format --verify-no-changes
git diff --check
git status --short
```

Also run the repository's release/package verification scripts used in the previous pass, including isolated-cache package consumer checks.

Do not use a stale global NuGet cache as proof of package correctness.

---

# 16. Definition of Done

Do not report completion unless every item below is true.

## Composite FK correctness

- [ ] `[null, null]` optional composite FK is null relationship.
- [ ] `[1, null]` optional composite FK is null relationship.
- [ ] `[null, "A"]` optional composite FK is null relationship.
- [ ] `[1, "A"]` resolves normally.
- [ ] transitions to/from partial-null emit correct navigation old/new values.
- [ ] partial-null values do not create false collection resets.

## Generated-FK capture scalability

- [ ] `CaptureGeneratedFixupEvidence` no longer scans all tracked entries per property.
- [ ] policy-aware capture reuses one tracked-graph snapshot where practical.
- [ ] principal entry lookup uses reference identity.
- [ ] algorithmic regression test proves no per-property full tracker scan.
- [ ] existing generated-value/fixup semantics remain unchanged.

## Key equality

- [ ] non-trivial key comparer behavior investigated.
- [ ] byte-array or equivalent structural-key test exists where supported.
- [ ] custom/value-converted key comparer test exists where supported.
- [ ] ordinary scalar/composite key tests remain green.
- [ ] implementation uses EF comparer semantics if tests prove ordinary equality insufficient.

## Ambiguity / lifecycle

- [ ] dependent-side ambiguity still fails closed.
- [ ] principal-side ambiguity still fails closed.
- [ ] added/deleted/modified/unchanged tracked entries preserve behavior.
- [ ] collection reset deduplication remains exact.

## Preview / authority docs

- [ ] docs say authority validation is stale-state detection, not snapshot isolation.
- [ ] external discovery remains enabled for current experimental rejected-preview validation.
- [ ] final SaveChanges authority enforcement remains unchanged.
- [ ] preview remains internal/experimental.

## Performance

- [ ] sixth-pass navigation benchmark does not regress algorithmically.
- [ ] 10k navigation capture remains indexed, not quadratic.
- [ ] rejected-preview benchmark still practical relative to sixth-pass numbers.
- [ ] generated-FK policy-aware capture has zero full-tracker principal scans per modified property.

## Repository quality

- [ ] Release solution build passes with 0 warnings.
- [ ] Core net8 tests pass.
- [ ] Core net10 tests pass.
- [ ] EF tests pass.
- [ ] Allocation dogfood build passes.
- [ ] Allocation dogfood executable passes all scenarios.
- [ ] package/release verification passes with isolated package cache.
- [ ] formatting passes.
- [ ] `git diff --check` passes.
- [ ] working tree is clean after commit.

---

# 17. Required final report format for the weak agent

The final report must use exactly these sections:

```text
## 1. Starting state
- starting commit
- baseline test counts
- dogfood scenario count

## 2. Composite FK null-semantics fix
- failing cases before
- implementation
- tests added

## 3. Generated-FK fixup scalability
- old hot path
- new lookup mechanism
- work-count evidence

## 4. Key comparer result
- EF comparer semantics investigated
- tests
- whether implementation changed

## 5. Navigation/fingerprint regression benchmark
- 100 / 1,000 / 10,000
- time / allocations / work counts

## 6. Rejected-preview benchmark
- same workload as sixth pass
- time / allocations / validations

## 7. Authority semantics
- what preview validation detects
- what it does NOT guarantee

## 8. Repair regression
- single-step
- multi-step
- second-order
- runtime version semantics
- full reseed count

## 9. Remaining limitations
- authoritative scope
- explicit core mutation reporting
- unreported POCO drift
- authority races / no DB snapshot guarantee
- relation scan behavior

## 10. Verification
- exact commands and counts

## 11. Hot-loop audit
- every suspicious ChangeTracker.Entries occurrence classified

## 12. Commit
- commit SHA
- branch
- remote synchronized
- working tree clean
```

Do not say “all good” without the concrete counts and benchmark/work-count evidence.

---

# 18. Explicit MUST NOT list

The agent must NOT:

- remove the sixth-pass indexed navigation snapshot;
- restore full ChangeTracker scans for relationship resolution;
- treat partial-null composite FK as a real relationship key;
- silently choose the first ambiguous relationship candidate;
- weaken generated-value/fixup safety checks;
- weaken rejected-preview stale-state validation;
- remove external consumer discovery;
- claim per-read authority checks provide transaction/database snapshot isolation;
- promote Preview to public API;
- redesign repair ranking or convergence;
- introduce compatibility/V2 APIs;
- change business rules in allocation dogfood;
- reduce benchmark graph sizes to hide regressions;
- use only timing assertions instead of algorithmic work-count assertions;
- claim completion while a known per-item full tracker scan remains in the relevant capture path.
