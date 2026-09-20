# Allocation dogfood sixth pass: EF fingerprint scalability and tracked-navigation capture

## Purpose

This is a **literal step-by-step anti-dumb implementation plan for a weak agent**. Follow it in order. Do not jump to a new Preview API. Do not cache away validation before proving the underlying capture is efficient. Do not weaken stale-state correctness to improve a benchmark. Do not rewrite unrelated EF integration code.

The library is still release-candidate quality. **Backward compatibility is not a goal.** Breaking internal/public API changes are allowed when they are needed for correctness or a materially cleaner design, but this pass is intentionally focused on EF capture scalability.

The fifth-pass experiment proved:

```text
core preview construction is cheap
EF rejected-preview validation is catastrophically expensive at larger tracked graphs
```

Representative fifth-pass result:

```text
allocations     EF rejection+preview     repair query      allocated bytes
100             ~7 ms                    ~17 ms            ~16 MB
1,000           ~74 ms                   ~184 ms           ~965 MB
10,000          ~9.6 s                   ~23.7 s           ~90 GB
```

The important new hypothesis is **not merely** "fingerprinting is expensive".

The current capture path repeatedly scans `ChangeTracker.Entries()` while resolving reference and collection navigation state. That can turn one fingerprint capture into O(N²)-like work for common tracked graphs. Repeating that capture once per preview read multiplies the problem, but the underlying capture must be fixed before any attempt to reduce the number of validations.

This pass therefore has five goals:

1. Measure where one `CaptureFingerprint()` actually spends time and allocations.
2. Prove or disprove that navigation mutation capture is super-linear.
3. Replace repeated tracked-entry scans with a single indexed tracked-graph snapshot while preserving exact mutation semantics.
4. Separate **tracked-state identity/staleness** concerns from **persistence authority / external-consumer discovery** where safely possible.
5. Re-run the real EF rejected-preview benchmark before deciding whether per-read validation still needs a separate design.

Expected branch:

```text
plan/allocation-dogfood
```

Expected starting implementation:

```text
32becb9308e59973e3f27ace065236dc04d5c5ef
```

or later if only this plan was added afterwards.

---

# 0. Stop conditions and non-goals

Before touching code, understand these constraints.

## Do not do these things in this pass

Do **not**:

- make `ConsistencyPreview` public;
- add a mutable preview;
- add a generic workflow engine;
- add a domain-event system;
- solve arbitrary POCO observability;
- trust the caller to manually invalidate preview state;
- cache one fingerprint forever;
- disable stale-state checks;
- remove `DetectChanges()` just to make numbers green;
- skip navigation mutations because the benchmark graph is simple;
- make full-runtime reseed disappear from dogfood baseline measurements;
- change application-owned repair ranking/convergence semantics;
- redesign `ConsistencyScope`;
- optimize external consumer discovery queries unless this plan explicitly reaches that phase;
- introduce an EF-internal dependency on undocumented EF implementation details unless unavoidable and documented.

## Preserve these already-correct semantics

Do not regress:

```text
rejected SaveChanges does not advance runtime.Version
successful final SaveChanges advances runtime exactly once
repair requests come from actual violated invariants
RepairRequestInfo.Reason uses current propagated severity
Invalid dominates Dirty
multi-step repair requires fresh re-planning after each mutation
superseded rejected plans invalidate older previews
relevant EF tracked changes invalidate rejected previews
core unreported POCO drift remains explicitly not guaranteed
```

Preview must remain **internal/experimental** after this pass unless a later explicit plan says otherwise.

---

# 1. Read and map the existing call chain before editing

Read these files completely enough to identify all relevant paths:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreSession.cs
src/Raffinert.Consistency.EntityFrameworkCore/ExternalConsumerDiscovery.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyPersistencePolicy.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreMappings.cs or equivalent mapping files

experiments/Raffinert.Consistency.AllocationDogfood/RejectedEfPreviewBenchmark.cs
experiments/Raffinert.Consistency.AllocationDogfood/ProposedStateBenchmark.cs
experiments/Raffinert.Consistency.AllocationDogfood/EfScenarios.cs
experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md

tests/Raffinert.Consistency.EntityFrameworkCore.Tests/*
```

Write down the actual current path in comments/notes before implementation:

```text
preview read
  -> ConsistencyPreview.Validate()
  -> ConsistencyEfCoreSession.ValidateRejected()
  -> ConsistencyCoordinator.CaptureFingerprint()
  -> ChangeTracker.DetectChanges()
  -> policy validation
  -> CaptureUnitOfWork()
      -> ChangeTrackerAdapter.CaptureNavigationChanges()
      -> ChangeTrackerAdapter.ReadModifiedProperties()
  -> ExternalConsumerDiscovery.Discover()
  -> EfMutationFingerprint.Create(...)
```

Then identify repeated full scans inside navigation capture:

```text
CaptureNavigationChangesCore
  foreach tracked owner
    foreach owner reference
      ResolveReference
        ResolveMatches
          tracker.Entries().Where(...)

    foreach owner collection
      CollectionRelationshipChanged
        tracker.Entries().Where(...).Any(...)
```

Do not proceed until you can explain why this can become super-linear.

---

# 2. Record the starting verification state

Before code changes, run and record exact counts:

```powershell
dotnet build Raffinert.Consistency.sln -c Release

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net8.0 -c Release
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -f net10.0 -c Release
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release

dotnet build experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj -c Release --no-build
```

Expected approximate starting counts from the previous pass:

```text
Core net8  : 395
Core net10 : 395
EF         : 216
Dogfood    : 32
```

Do not hard-code these expectations into tests. Record the actual counts from the branch.

If the branch is red before sixth-pass changes, stop and fix the existing breakage first.

---

# 3. Instrument ONE fingerprint capture before optimizing anything

The fifth-pass benchmark measures end-to-end cost, but we need phase-level evidence.

Add **internal-only diagnostics** so one `CaptureFingerprint()` can report where time/work goes.

Do not add public API.

## 3.1 Required phase counters/timings

At minimum distinguish:

```text
DetectChanges
policy/scope validation
navigation mutation capture
scalar/property mutation capture
external consumer discovery
fingerprint construction
```

Also record structural work counts if easy:

```text
tracked entry count
reference navigation count visited
collection navigation count visited
ResolveMatches candidate checks
CollectionRelationshipChanged candidate checks
captured property mutation count
captured navigation mutation count
external discovery resolver invocations
external discovery rows admitted/resolved
```

A possible internal diagnostic record is fine:

```csharp
internal sealed record EfFingerprintDiagnostics(
    int TrackedEntries,
    long ReferenceCandidateChecks,
    long CollectionCandidateChecks,
    ...);
```

Do **not** make production behavior depend on diagnostics.

## 3.2 Instrumentation rules

- counters must be zero-overhead-ish when disabled;
- do not allocate one diagnostic object per tracked entry;
- do not use logging as the measurement mechanism;
- do not alter mutation ordering;
- do not alter navigation resolution semantics;
- do not make benchmark correctness depend on `Stopwatch` inside hot inner loops if a coarse phase timer is sufficient.

## 3.3 Add one diagnostic test

Construct a small graph and assert the diagnostics are structurally plausible:

```text
TrackedEntries > 0
ReferenceNavigationsVisited > 0
ReferenceCandidateChecks > ReferenceNavigationsVisited
```

The exact numeric values do not need to be public contract.

---

# 4. Add a focused navigation-capture scalability benchmark

Create a benchmark separate from the full dogfood repair benchmark.

Suggested file:

```text
experiments/Raffinert.Consistency.AllocationDogfood/EfMutationCaptureBenchmark.cs
```

or an equivalent experiment project/file.

The purpose is to measure **one capture**, not six preview validations.

## 4.1 Benchmark graph

Use a graph with many allocation-like dependents that each have reference navigations:

```text
Demand
Supply
Allocation -> Demand
Allocation -> Supply
```

At minimum sizes:

```text
100
1,000
10,000
```

If the optimized path becomes fast, optionally add:

```text
50,000
```

Do not start with 50k before correctness.

## 4.2 Required measurements

For one fingerprint/capture:

```text
total capture ms
allocated bytes
tracked entries
reference navigations visited
collection navigations visited
reference candidate checks
collection candidate checks
captured mutation count
```

If external consumer discovery is not active in this benchmark, say so explicitly.

## 4.3 Prove scaling shape

Before optimization, record whether candidate checks look approximately like:

```text
N        candidate checks
100      ~10^4 scale
1,000    ~10^6 scale
10,000   ~10^8 scale
```

Exact numbers may differ because of graph shape.

Do not claim O(N²) solely from wall-clock time. Candidate-check instrumentation should support or refute the hypothesis.

---

# 5. Add correctness tests BEFORE replacing navigation resolution

The navigation capture code is correctness-sensitive. Add a regression matrix first.

The new implementation must preserve exactly the same mutation semantics as the old implementation.

Create or extend EF tests for all required cases below.

## 5.1 Reference navigation matrix

### Reference A — unchanged reference

```text
original principal == current principal
```

Expected:

```text
no PropertyChange for navigation
```

### Reference B — dependent retarget

```text
Allocation.Supply: S1 -> S2
SupplyId: 1 -> 2
```

Expected navigation mutation:

```text
old = S1
new = S2
```

### Reference C — null -> principal

Expected exact old/new references.

### Reference D — principal -> null

Expected exact old/new references.

### Reference E — composite foreign key retarget

At least two FK components.

Expected old/new principal resolution is correct for both original and current values.

### Reference F — nullable composite key

Null components must preserve existing semantics.

### Reference G — one-to-one / principal-side navigation

Exercise the branch where current principal values are used to find dependent matches.

### Reference H — ambiguous tracked match

If current implementation throws for ambiguous resolution, optimized implementation must throw the same class of error rather than silently picking one.

### Reference I — Added dependent

Verify navigation capture interacts correctly with Added state.

### Reference J — Deleted dependent

Verify original relationship is resolved correctly.

### Reference K — principal key mutation when supported by current EF semantics

Preserve existing behavior or existing rejection.

### Reference L — self-referencing relationship

Example:

```text
Node.Parent
```

Do not assume left/right CLR types differ.

---

# 6. Add collection navigation correctness tests

The existing `CollectionRelationshipChanged(...)` does global scans. The replacement must preserve behavior.

Required tests:

### Collection A — child added to principal

### Collection B — child removed from principal

### Collection C — child moved principal A -> B

Both A and B collection owners must be recognized appropriately.

### Collection D — dependent FK scalar changed without directly mutating collection

EF relationship fixup may change collection state. Capture must still identify the semantic collection reset.

### Collection E — deleted dependent

### Collection F — added dependent

### Collection G — composite FK collection relationship

### Collection H — no relationship change

Must not emit a false collection reset.

### Collection I — multiple dependents move in one unit of work

Deduplicate resets per `(owner, member)` exactly as today.

### Collection J — self-referencing collection

Example:

```text
Node.Children
```

---

# 7. Design a single indexed tracked-graph snapshot

Only after tests and measurements exist, design the replacement.

The central rule is:

> Enumerate the tracked graph once, build lookup indexes once, and answer reference/collection relationship questions from those indexes.

Do not build a new index separately for every navigation.

A conceptual internal abstraction is acceptable:

```csharp
internal sealed class TrackedGraphSnapshot
{
    ...
}
```

Name is not public API.

## 7.1 Snapshot inputs

Construct it from:

```csharp
ChangeTracker
```

after the required `DetectChanges()` boundary.

Capture tracked entries into an array once:

```csharp
var entries = changeTracker.Entries().ToArray();
```

All later lookup construction should reuse this array.

Do not repeatedly call `changeTracker.Entries()` inside nested loops.

## 7.2 Required conceptual indexes

You may need variants of:

```text
current key -> tracked entity/entities
original key -> tracked entity/entities

current FK -> dependent entities
original FK -> dependent entities

entity instance -> EntityEntry
entity type -> entries
```

Do not blindly implement all of these if metadata tells you fewer are needed.

The key point is that relationship resolution must be indexed by **EF metadata identity + key values**, not only by CLR type.

For example, avoid ambiguous global dictionaries like:

```text
(Type, object[]) -> entity
```

when two entity types/keys/relationships can overlap.

Prefer metadata-aware keys conceptually like:

```text
(IKey metadata, composite values)
(IForeignKey metadata, composite values)
```

Use EF metadata reference identity where appropriate.

## 7.3 Composite key representation

Do not allocate excessive `object[]` keys in every lookup if avoidable.

However, **correctness first**. It is acceptable to begin with a simple immutable composite key representation and optimize allocations only after tests pass.

Key equality must follow EF property/value equality semantics already relied on by current code. Do not accidentally switch to reference equality for scalar key values.

## 7.4 Current vs original values

This is critical.

For relationship mutation detection, both worlds matter:

```text
original relationship graph
current relationship graph
```

The snapshot must be able to answer:

```text
resolve principal from original FK values
resolve principal from current FK values
find dependents belonging to original principal key
find dependents belonging to current principal key
```

Do not overwrite original information when building current indexes.

---

# 8. Replace reference resolution with indexed lookup

Current slow conceptual path:

```text
ResolveReference
  -> ResolveMatches
     -> scan every tracker entry
```

Replace this with snapshot lookup.

Conceptually:

```csharp
snapshot.ResolveReference(ownerEntry, navigation, original: true)
snapshot.ResolveReference(ownerEntry, navigation, original: false)
```

## 8.1 Dependent-to-principal navigation

Use the navigation foreign key metadata.

For dependent navigation:

```text
FK values on dependent
  -> matching principal key index
  -> zero / one / ambiguous result according to existing semantics
```

Preserve behavior when all FK components are null.

## 8.2 Principal-to-dependent reference navigation

Use principal key values and the corresponding FK index.

Do not scan all tracked target entries.

If more than one dependent matches a relationship that must be unique, preserve existing ambiguity failure.

## 8.3 No hidden lazy loading / DB queries

This snapshot is only about already tracked state.

Do not query the database while resolving tracked navigation mutations.

---

# 9. Replace collection relationship detection with indexed lookup

Current slow conceptual path:

```text
for each collection owner
    scan all tracked candidate dependents
```

Replace it with metadata-aware buckets.

For a principal collection, derive:

```text
original principal key
current principal key
```

Then look up only dependent buckets whose original/current FK matches those keys.

Determine whether any dependent is:

```text
Added into this principal
Deleted from this principal
retargeted into this principal
retargeted out of this principal
```

Do not enumerate unrelated dependents.

Expected complexity should be closer to:

```text
O(tracked entries + relationship edges inspected)
```

rather than:

```text
O(owners × all tracked entries)
```

---

# 10. Preserve navigation mutation ordering and deduplication

The optimized implementation must not subtly change deterministic output ordering if fingerprint equality depends on mutation ordering.

Inspect how `EfMutationFingerprint.Create(...)` normalizes/compares mutations.

If fingerprint creation already canonicalizes order, document that.

If it does not, explicitly preserve or canonicalize ordering.

Required stable categories should remain conceptually:

```text
additions
properties
navigation changes
removals
```

Collection resets must remain deduplicated by owner instance + member.

Do not allow dictionary enumeration order to become an externally observable fingerprint instability.

Add a test that repeated capture of unchanged tracked state produces equal fingerprints.

---

# 11. Ensure scalar/property capture does not accidentally re-enumerate the graph excessively

After fixing navigation scans, inspect:

```text
ReadModifiedProperties
mappings.Resolve(entry)
IncludeSemanticProperty
RecordOwnedWrites interactions
```

Do not launch a broad optimization campaign, but check whether any obvious nested full scans remain.

Example red flags:

```csharp
foreach (entry in allEntries)
    mappings.Resolve(entry) // okay if mapping count small

foreach (property in entry.Properties)
    changeTracker.Entries().SingleOrDefault(...) // suspicious
```

Only fix additional super-linear behavior if directly observed in diagnostics.

---

# 12. Separate tracked-state fingerprinting from persistence authority work

This phase is a **design and test phase first**, not an automatic refactor.

Current `CaptureFingerprint()` performs more than “is tracked proposed state unchanged?” It also re-runs:

```text
policy/scope validation
ExternalConsumerDiscovery
```

External consumer discovery may execute database queries.

That means a preview read can conceptually become:

```text
preview.Evaluate
  -> stale check
  -> DB query
```

We need to decide whether this is actually required for preview staleness.

## 12.1 Write down two different responsibilities

Document explicitly:

```text
A. Tracked-state identity / staleness
   "Is the proposed tracked unit of work the same one that was rejected?"

B. Persistence authority / evaluation closure
   "Is the authoritative graph complete enough to enforce this save?"
```

These are not obviously the same operation.

## 12.2 Required safety rule

Do not weaken final save enforcement.

Every actual retry of `SaveChanges()` must still perform whatever:

```text
scope validation
consumer discovery
authoritative closure checks
```

are required by the EF adapter.

## 12.3 Experiment: tracked-state-only fingerprint

After navigation capture is correct and efficient, prototype an internal fingerprint calculation that uses only the already tracked unit of work relevant to consistency semantics.

Conceptual shape:

```csharp
CaptureTrackedStateFingerprint(...)
```

It may perform:

```text
DetectChanges
capture mapped/unmapped relevant tracked mutations
semantic-member filtering
fingerprint creation
```

but must **not** run external DB consumer discovery.

Do not replace the production preview validation yet.

## 12.4 Prove what external discovery can change

Add targeted tests answering:

1. If tracked state has not changed, can external consumer discovery produce a different admission result solely because the database changed externally?
2. If yes, does preview need to detect that between reads, or is it sufficient that the next actual save re-validates authority?
3. Does preview query correctness depend on those newly discoverable consumers being included in the retained rejected plan?

Do not answer these questions by assumption.

If preview could return materially wrong proposed-state query results because an external consumer appeared after rejection, then tracked-only validation may be insufficient.

If that external change is outside the retained tracked graph and final save will rediscover/reject, document the trade-off explicitly.

## 12.5 Decision outcome

Choose exactly one after tests:

```text
KEEP AUTHORITY WORK IN PREVIEW VALIDATION
```

or

```text
PREVIEW STALENESS MAY USE TRACKED-STATE FINGERPRINT; FINAL SAVE REVALIDATES AUTHORITY
```

Do not silently change semantics.

---

# 13. Re-run one-capture scalability benchmark after indexed navigation snapshot

After implementation, run the Phase 4 benchmark again.

Report before/after for each size:

```text
100
1,000
10,000
```

Required table:

```text
N | old capture ms | new capture ms | old bytes | new bytes |
  | old ref candidate checks | new ref lookup checks |
  | old collection candidate checks | new collection lookup checks
```

The expected success signal is not a specific nanosecond target.

The important shape is:

```text
candidate work grows approximately linearly with tracked graph / affected relationships
```

and no longer explodes by roughly N².

If candidate-check counts are still super-linear, stop and investigate before moving on.

---

# 14. Re-run the real EF rejected-preview benchmark

Now rerun the same fifth-pass benchmark unchanged in logical workload:

```text
rejected SaveChanges
CreateRejectedPreview
5 repair-decision reads
```

Do not reduce read count.

Do not change graph semantics.

Report:

```text
N
rejection + preview ms
repair query ms
allocated bytes
preview reads
fingerprint validations
```

Compare directly to fifth-pass numbers.

## Important interpretation

After indexed capture, there may still be six validations per decision.

That is okay for this phase if each validation is now cheap enough.

Only after seeing new measurements decide whether repeated validations remain unacceptable.

---

# 15. Decide whether per-read validation still needs a seventh-pass design

Choose exactly one outcome:

## Outcome A — CURRENT VALIDATION COUNT IS NOW ACCEPTABLE

Use this only if:

- capture is near-linear;
- allocations are reasonable;
- 10k graph repair decision is practical;
- repeated validation no longer dominates.

Then keep per-read validation because it is the strongest stale-state guarantee.

## Outcome B — CAPTURE IS FIXED BUT 6× VALIDATION IS STILL TOO EXPENSIVE

Then **do not implement a validation lease in this pass**.

Document a future design requirement for something like:

```text
explicit validated preview read batch
tracked-state revision contract
or another safe mutation observation boundary
```

A separate plan should design it.

## Outcome C — CAPTURE REMAINS TOO EXPENSIVE

Do not hide the result behind preview batching.

Investigate the remaining capture hotspot first.

---

# 16. Add regression tests preventing return of quadratic scans

We do not want a future refactor to accidentally reintroduce `tracker.Entries()` inside relationship loops.

Do not write brittle source-text tests.

Instead add behavioral diagnostics-based tests.

For example:

```text
100 tracked allocations -> X relationship candidate lookups
1,000 tracked allocations -> less than, say, 20× X
```

Choose a threshold with enough margin for legitimate metadata work but low enough to catch N² growth.

Better if diagnostics can assert more directly:

```text
FullTrackedEntryRescans == 0 inside relationship resolution
```

or:

```text
ReferenceIndexLookupCount proportional to references visited
```

Do not assert wall-clock milliseconds in unit tests.

---

# 17. Verify all lifecycle/staleness behavior again

Run/extend tests for:

```text
relevant scalar mutation after rejected preview -> stale
navigation retarget after rejected preview -> stale
tracked add after rejected preview -> stale
tracked remove after rejected preview -> stale
runtime baseline advancement -> stale
superseding rejection -> old preview stale
preview disposal -> ObjectDisposedException
irrelevant tracked property behavior -> according to semantic filtering contract
unreported core POCO mutation -> still explicitly not guaranteed
```

The indexed snapshot must not accidentally make staleness less precise.

---

# 18. Re-run multi-step and second-order repair scenarios unchanged

Do not simplify them.

They must still prove:

```text
Multi-step:
Save #1 reject
repair #1
Save #2 reject
repair #2
Save #3 success

Second-order:
S1 violation
repair moves load to S2
S2 becomes newly violated
new plan emits S2 repair request
fresh preview
repair moves load to S3
final save succeeds
```

Assertions must continue to include:

```text
runtime.Version unchanged during rejected attempts
runtime.Version advances exactly once on success
zero full runtime reseeds
exact rejection count
exact preview count
exact repair mutation count
correct persisted allocation targets
correct final materialized RemainingCapacity values
```

---

# 19. Update documentation honestly

Update:

```text
experiments/Raffinert.Consistency.AllocationDogfood/DOGFOOD.md
docs/ef-core-consistency.md
```

Document:

1. fifth-pass benchmark exposed repeated validation cost;
2. sixth pass identified whether tracked-navigation capture was super-linear;
3. exact before/after measurements;
4. whether external consumer discovery remains part of preview validation;
5. whether per-read validation remains acceptable after capture optimization;
6. preview remains internal/experimental;
7. final save still re-validates authoritative persistence requirements;
8. arbitrary unreported core POCO mutation remains outside guarantees.

Do not leave stale 90 GB numbers presented as current if the implementation has been optimized. Keep them in a clearly labeled **before** table if useful.

---

# 20. Forbidden shortcuts

The agent must not:

- replace navigation capture with `ReferenceEntry.IsModified` only unless proven semantically equivalent for every tested case;
- ignore original relationship values;
- ignore collection mutations;
- rely solely on FK scalar `IsModified` flags if current logic detects cases beyond them;
- use `Include`/database queries to resolve tracked relationships;
- create one dictionary per owner/navigation inside loops;
- rebuild indexes separately on every preview read after a single capture begins;
- use reflection string names where EF metadata identity is available;
- turn off `AutoDetectChangesEnabled` globally and forget to restore it;
- remove external consumer discovery from final save;
- claim O(N) without candidate-count evidence;
- claim the benchmark is formal BenchmarkDotNet output;
- promote internal diagnostics to public API;
- promote Preview to public API;
- delete baseline full-runtime reseed benchmark;
- reduce test graph sizes because performance is bad;
- skip 10,000 because it takes too long before optimization; if it is pathological, record that exact fact.

---

# 21. Suggested implementation structure

This is guidance, not mandatory naming.

A clean internal design may look like:

```text
ChangeTrackerAdapter
  -> DetectChanges once at caller boundary
  -> TrackedGraphSnapshot.Create(changeTracker)
      -> entries[]
      -> current key indexes
      -> original key indexes
      -> current FK indexes
      -> original FK indexes
  -> CaptureNavigationChanges(snapshot, mappings)
  -> ReadModifiedProperties(snapshot/entries, mappings)
```

Possible types:

```csharp
internal sealed class TrackedGraphSnapshot
internal readonly record struct TrackedKey(...)
internal readonly record struct RelationshipLookupKey(...)
```

Do not force these names if a smaller design fits the existing code better.

The important invariant is:

```text
NO nested global ChangeTracker.Entries() scans during relationship resolution.
```

---

# 22. Performance acceptance criteria

Do not use one arbitrary time threshold across machines.

Use shape-based criteria.

Minimum acceptable outcome:

```text
Reference candidate checks:
  no longer scale as owner count × tracked entry count

Collection candidate checks:
  no longer scale as owner count × tracked entry count

10k capture allocations:
  orders of magnitude below fifth-pass pathological result

10k one-capture wall time:
  practical enough to run repeatedly in development
```

For the full rejected-preview decision, report improvement factor but do not require a fixed target.

If 10k remains multi-second after navigation indexing, use phase diagnostics to identify the next hotspot rather than hiding it.

---

# 23. Mandatory verification commands

Run all of these from a clean working tree candidate before reporting completion:

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

Also run the repository's existing package/release consumer validation exactly as configured today.

If NuGet same-version cache causes stale package consumption, use the repository's isolated-cache approach and report that explicitly rather than claiming a product failure.

---

# 24. Required final report format

The agent must report exactly these sections.

## 1. Starting state

```text
starting commit
build/test counts before changes
```

## 2. Root-cause evidence

Report one-capture diagnostics before optimization:

```text
tracked entries
reference navigations
collection navigations
reference candidate checks
collection candidate checks
capture time
allocated bytes
```

State whether the O(N²)/super-linear hypothesis was confirmed.

## 3. Navigation capture implementation

Describe:

```text
what indexed snapshot was added
what indexes exist
how current/original relationships are represented
how ambiguity/null/composite keys are handled
```

## 4. Correctness matrix

Report pass/fail for:

```text
reference retarget
null transitions
composite FK
one-to-one/principal side
Added/Deleted
collection add/remove/move
self-reference
ambiguity
fingerprint stability
```

## 5. One-capture benchmark before/after

Provide a table for:

```text
100 / 1,000 / 10,000
```

including work counters and allocated bytes.

## 6. Persistence-authority decision

Choose exactly one:

```text
KEEP AUTHORITY WORK IN PREVIEW VALIDATION
```

or

```text
TRACKED-STATE FINGERPRINT IS SUFFICIENT FOR PREVIEW STALENESS; FINAL SAVE REVALIDATES AUTHORITY
```

Explain why, backed by tests.

## 7. Real EF rejected-preview benchmark after optimization

Report the same fifth-pass table:

```text
allocations
rejection+preview ms
repair-query ms
allocated bytes
reads
validations
```

## 8. Validation-count decision

Choose exactly one:

```text
CURRENT PER-READ VALIDATION ACCEPTABLE
NEEDS SEPARATE VALIDATED-BATCH DESIGN
CAPTURE STILL TOO EXPENSIVE
```

## 9. Repair regression

Report:

```text
single-step repair
multi-step repair
second-order repair
full runtime reseeds used = 0
```

## 10. Remaining limitations

Must still include at least:

```text
authoritative scope is host-asserted
core POCO mutations require explicit reporting
unreported core POCO drift cannot be guaranteed
repair ranking/convergence remain application-owned
Preview remains internal/experimental
```

## 11. Verification

Report each command separately, not one generic “all passed”.

## 12. Commit

Report:

```text
branch
commit SHA
working tree clean yes/no
```

---

# 25. Definition of Done

This sixth pass is complete only when all of the following are true:

- [ ] One fingerprint capture has phase/work diagnostics.
- [ ] Pre-fix measurements prove or refute the navigation-scan hypothesis.
- [ ] Reference navigation capture no longer performs nested global tracked-entry scans.
- [ ] Collection relationship detection no longer performs nested global tracked-entry scans.
- [ ] Current/original relationship semantics are preserved.
- [ ] Composite FK, nullable FK, add/delete, retarget, principal-side, collection and self-reference cases are tested.
- [ ] Ambiguous resolution behavior is preserved.
- [ ] Fingerprint ordering/equality remains deterministic.
- [ ] 100/1k/10k one-capture benchmark is recorded before and after.
- [ ] Real EF rejected-preview benchmark is rerun after optimization.
- [ ] External consumer discovery / persistence authority responsibility is explicitly decided, not silently changed.
- [ ] Final SaveChanges still performs authoritative enforcement.
- [ ] Multi-step repair still requires exactly two rejected plans before final success in the existing scenario.
- [ ] Second-order repair still produces the new S2 repair request from a fresh plan.
- [ ] No full-runtime reseed is used by preview repair scenarios.
- [ ] Preview remains internal/experimental.
- [ ] Full solution build passes.
- [ ] Core net8 tests pass.
- [ ] Core net10 tests pass.
- [ ] EF tests pass.
- [ ] Dogfood executable passes.
- [ ] Package consumer validation passes.
- [ ] Formatting verification passes.
- [ ] `git diff --check` passes.
- [ ] Working tree is clean after commit.

---

# Final design principle

Do not solve this pass by hiding expensive work behind fewer calls.

First make the fundamental operation sane:

```text
tracked EF graph
    -> one indexed snapshot
    -> deterministic mutation capture
    -> cheap fingerprint
```

Only then decide whether Preview needs a lower validation count.

The target is not “make the benchmark green”. The target is:

> **Raffinert should be able to prove that a rejected EF proposed state is still the same tracked state without repeatedly performing quadratic graph reconstruction or database-backed authority discovery unless that authority work is genuinely required for correctness.**
