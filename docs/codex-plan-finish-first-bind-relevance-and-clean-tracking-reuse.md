# Codex plan — finish first-bind relevance filtering and clean-tracking plan reuse

Status: **FINAL NARROW CORRECTNESS CLEANUP — DO NOT REDESIGN THE EF INTEGRATION**

Audience: a weak coding agent. Follow this document literally and in order. Do not invent a new runtime/session architecture. Do not change the application-facing UX. Do not broaden the task.

Baseline implementation commit:

```text
114b7fe61f5938b9f55c04102ab3784c5865f10f
```

That commit correctly introduced `BaselineRevision`, stale-plan invalidation, rollback before rebuild, bounded rebuild during discovery, and unrelated-unmapped first-bind filtering.

Two small gaps remain:

1. first runtime binding still rejects **any changed member on a mapped entity**, even when that member is not used anywhere by the consistency graph;
2. there is no dedicated regression test proving that **clean tracking/stabilization before the first pending plan does not cause a pointless second plan/recalculation**.

This plan fixes only those gaps.

---

# 0. Public UX is frozen

Do not change this application shape:

```csharp
public sealed class LinkService(
    LinkContext db,
    ConsistencyRuntime consistency)
{
    public async Task ChangeValueAsync(
        long linkId,
        decimal newValue,
        CancellationToken cancellationToken)
    {
        var link = await db.Links
            .Include(x => x.Left)
            .Include(x => x.Right)
            .SingleAsync(x => x.Id == linkId, cancellationToken);

        link.Left.Value = newValue;

        consistency.Materialize(link);

        Use(link.Ratio);
        Use(link.NormalizedRatio);

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

Application code must still require only:

```text
DbContext
ConsistencyRuntime
Materialize(entity) only when mirrors are needed before save
ordinary SaveChanges / SaveChangesAsync
```

Do not reintroduce:

```text
runtime.Add(...)
runtime.Apply(...)
manual old values
ObjectSet<T> in application services
ConsistencyEfCoreMappings in application services
manual consistency unit-of-work handling
custom save methods
```

---

# 1. Exact remaining first-binding bug

Current first-binding relevance logic is approximately:

```csharp
PropertyChange change =>
    change.Set is not null ||
    runtime.GetTrackedMemberUsage(null, change.Member) != ModelMemberUsageKind.None
```

and similarly for `CollectionChange`.

That means this is rejected merely because `Link` is mapped into a consistency object set:

```csharp
public sealed class Link
{
    public long Id { get; set; }

    // used by consistency graph
    public Item Left { get; set; } = null!;
    public Item Right { get; set; } = null!;

    // ordinary EF/application field, NOT used by consistency graph
    public string Comment { get; set; } = "";
}
```

Scenario:

```csharp
var db = scope.ServiceProvider.GetRequiredService<LinkContext>();
var link = await db.Links.SingleAsync();

link.Comment = "changed";

// runtime not previously resolved
var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
```

This should succeed if `Comment` is not:

```text
- object-set key
- relation dependency
- derived dependency
- invariant dependency
- projected selector
- nested semantic dependency
```

The mapped entity itself is not the semantic unit. The **changed member** is.

---

# 2. Fixed relevance rule

Use this exact conceptual rule for property and collection changes.

## 2.1 Mapped root change

If `PropertyChange.Set` / `CollectionChange.Set` is not null:

```csharp
runtime.GetTrackedMemberUsage(change.Set, change.Member)
    != ModelMemberUsageKind.None
```

means the change is Raffinert-relevant.

Do **not** use:

```csharp
change.Set is not null
```

as an automatic reason to reject first binding.

## 2.2 Unmapped/nested change

If the mutation has no mapped root set, preserve support for nested dependency members:

```csharp
runtime.GetTrackedMemberUsage(null, change.Member)
    != ModelMemberUsageKind.None
```

This is important for dependency paths such as:

```text
Link.Left.Value
```

where EF may report the mutation on the `Item.Value` entity/member rather than as a root `Link` property mutation.

Do not weaken nested semantic detection.

## 2.3 Lifecycle changes

For mapped lifecycle changes:

```text
ObjectAdded
ObjectRemoved
CoverageAdmission
```

keep the current conservative behavior.

If `ChangeTrackerAdapter` only creates these mutations for mapped object sets, treat them as relevant.

Do not try to make `ObjectAdded` partially relevant by inspecting individual members in this task.

Do not silently baseline-admit Added entities.

---

# 3. Centralize first-bind mutation relevance

Do not leave a complicated switch expression with duplicated member logic.

Refactor to a small internal helper with one obvious responsibility, for example:

```csharp
private static bool IsFirstBindingRelevant(
    ConsistencyRuntime runtime,
    RuntimeMutation mutation)
```

or equivalent repository-style name.

Inside it, use another small helper if useful:

```csharp
private static bool IsRelevantMemberChange(
    ConsistencyRuntime runtime,
    IObjectSetDefinition? set,
    MemberInfo member)
```

Conceptual implementation:

```csharp
private static bool IsRelevantMemberChange(
    ConsistencyRuntime runtime,
    IObjectSetDefinition? set,
    MemberInfo member) =>
    runtime.GetTrackedMemberUsage(set, member) !=
        ConsistencyRuntime.ModelMemberUsageKind.None;
```

Then:

```csharp
PropertyChange change =>
    IsRelevantMemberChange(runtime, change.Set, change.Member),

CollectionChange change =>
    IsRelevantMemberChange(runtime, change.Set, change.Member),

ObjectAdded => true,
ObjectRemoved => true,
CoverageAdmission => true,
```

Keep the fail-fast default for unknown mutation types.

Do not expose any of these helpers publicly.

---

# 4. Do not change normal save-time semantic filtering accidentally

`IncludeSemanticProperty(...)` already uses model member usage to avoid treating unrelated EF properties as consistency-semantic fingerprint inputs.

Do not rewrite or broaden that code unless a test proves it is necessary.

This task is primarily about **first runtime binding**.

Preserve these existing semantics:

```text
- an application may save ordinary unrelated EF fields;
- unrelated fields must not invalidate consistency pending plans;
- materialized mirror writes remain excluded only under the existing owned-write rules;
- relevant consistency inputs still participate in mutation fingerprinting and planning.
```

Do not make a blanket rule such as:

```text
all properties on mapped entities are semantic
```

anywhere in the EF adapter.

---

# 5. Required test — mapped entity, irrelevant scalar change before first runtime resolution

Extend the neutral `Link` fixture with one ordinary mapped scalar that the consistency model never references.

Recommended:

```csharp
private sealed class Link
{
    // existing properties...

    public string Comment { get; set; } = "";
}
```

Do **not** add `Comment` to any:

```text
Key(...)
DependsOn(...)
From(...)
relation predicate
invariant
projection selector
MaterializeTo(...)
```

Seed a value such as:

```text
Comment = "original"
```

Test timeline:

```csharp
await using var fixture = await Fixture.CreateAsync();
await using var scope = fixture.Provider.CreateAsyncScope();

var db = scope.ServiceProvider.GetRequiredService<LinkContext>();

var link = await db.Links
    .Include(x => x.Left)
    .Include(x => x.Right)
    .SingleAsync();

link.Comment = "changed";

// IMPORTANT: runtime is resolved only AFTER the irrelevant mapped change.
var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();

// Binding must succeed.
```

Required assertions:

```text
runtime resolution succeeds
mapped baseline objects are registered
runtime.Version == 0 immediately after binding
no repair/policy callback fired during binding
```

Then perform a genuine consistency mutation after binding:

```csharp
link.Left.Value = 55m;
runtime.Materialize(link);
await db.SaveChangesAsync();
```

Verify with a fresh `DbContext`:

```text
Comment == "changed"
Left.Value == 55
Ratio == 5.5
NormalizedRatio == 5.5
```

This proves the ordinary mapped EF property survives and does not interfere with consistency binding.

---

# 6. Keep the existing dirty relevant scalar first-bind rejection test

Do not weaken this existing scenario:

```csharp
var db = scope.ServiceProvider.GetRequiredService<LinkContext>();
var link = await db.Links
    .Include(x => x.Left)
    .Include(x => x.Right)
    .SingleAsync();

link.Left.Value = 55m;

var error = Assert.Throws<InvalidOperationException>(() =>
    scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>());
```

This must still reject.

Why:

```text
Link.Left.Value
```

is a real derived dependency.

Add/retain an assertion that the exception text clearly indicates the runtime must be bound/resolved before relevant tracked mutation.

The new irrelevant-property test and this relevant-property test must sit near each other so future maintainers can see the contrast.

---

# 7. Add a direct member-usage regression test if cheap

If `GetTrackedMemberUsage(...)` is test-visible through `InternalsVisibleTo`, add a focused Core test proving:

```text
Link.Comment                -> None
Link.Left                   -> DerivedDependency and/or relevant navigation usage
Item.Value used via Left    -> DerivedDependency through nested usage
Link.Id                     -> ObjectSetKey
```

Do not expose the API publicly just for this test.

If existing Core member-usage tests already cover the same distinction, strengthen those instead of duplicating them.

This test is useful because first-bind behavior now depends directly on this metadata.

---

# 8. Required test — clean tracking/stabilization before the first pending plan does not cause a pointless rebuild

This is the missing plan-reuse proof.

Use the existing neutral fixture and counters.

Required timeline:

```text
1. Resolve ConsistencyRuntime FIRST.
2. Resolve DbContext.
3. Query Link with Left and Right using Include.
4. Let EF tracking/fixup baseline admission happen.
5. Do NOT call Materialize yet.
6. Reset evaluation and physical-write counters AFTER query materialization.
7. Record runtime.BaselineRevision and runtime.Version.
8. Mutate a real consistency input: link.Left.Value = 55.
9. Call runtime.Materialize(link).
10. Record evaluation counts and revision after materialization.
11. Call ordinary SaveChangesAsync with no further tracking/query activity.
12. Prove SaveChanges reused the same pending plan instead of recalculating.
```

Required assertions:

```text
before business mutation:
    runtime.Version == 0
    baseline revision may be > 0 because query tracking admitted/stabilized rows

Materialize:
    Ratio calculator count == 1
    NormalizedRatio calculator count == 1
    Ratio physical write count == 1
    NormalizedRatio physical write count == 1
    runtime.Version unchanged

between Materialize and SaveChanges:
    no new query/tracking occurs
    BaselineRevision remains unchanged

SaveChanges:
    Ratio calculator count STILL == 1
    NormalizedRatio calculator count STILL == 1
    Ratio physical write count STILL == 1
    NormalizedRatio physical write count STILL == 1
    runtime.Version advances exactly once after successful SQL
```

Also verify persisted values with a fresh context.

This test must fail if the implementation performs an unnecessary `RefreshBaseline` during save and invalidates/rebuilds the pending plan.

---

# 9. Do not fake the clean-tracking test

The test must not accidentally query all entities before runtime resolution.

The exact order matters:

```csharp
var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
var db = scope.ServiceProvider.GetRequiredService<LinkContext>();

var link = await db.Links
    .Include(x => x.Left)
    .Include(x => x.Right)
    .SingleAsync(...);
```

This specifically exercises:

```text
runtime already bound
    ↓
Tracked events
    ↓
baseline admission
    ↓
relationship fixup stabilization
    ↓
first pending plan created later
```

Do not replace it with:

```text
query first
resolve runtime later
```

because that is the clean-late-binding scenario and is already a different path.

---

# 10. Do not change BaselineRevision semantics

Keep the implementation introduced in `114b7fe6`.

Do not:

```text
- make BaselineRevision public;
- increment Version for baseline admission;
- stop bumping BaselineRevision on successful AdmitBaseline;
- stop bumping BaselineRevision on intentional successful RefreshBaseline;
- bump BaselineRevision on every Materialize;
- bump BaselineRevision on validation-only operations;
- bump BaselineRevision on failed baseline operations;
- remove bounded reprepare during external discovery.
```

The cleanup in this plan should not require redesigning baseline revision.

---

# 11. Do not weaken dirty navigation/collection first-binding protection

Keep all existing tests for:

```text
dirty mapped scalar dependency before first binding -> reject
dirty mapped navigation before first binding -> reject
dirty mapped collection before first binding -> reject
Added mapped entity before first binding -> conservative reject
Removed mapped entity before first binding -> conservative reject
```

If changing the relevance helper causes one of those tests to start passing, stop and fix the helper.

Especially do not assume:

```text
navigation/collection mutation is irrelevant because no scalar property is used
```

Navigation members can participate in:

```text
projected selectors
relation dependencies
nested dependency roots
consumer discovery
```

Use `GetTrackedMemberUsage(...)`; do not invent ad-hoc property-name checks.

---

# 12. Optional but recommended — mapped irrelevant navigation/collection test

Only do this if the current neutral fixture makes it simple. Do not expand the task substantially.

If `Link` or another mapped entity already has a mapped navigation/collection that is not referenced by the consistency graph, add a first-bind test proving its unrelated change does not reject binding.

If adding such a relationship requires a large fixture redesign, skip it. The required scalar test plus direct member-usage test is sufficient for this task.

Do not create artificial complexity just to satisfy this optional test.

---

# 13. Review `HasRelevantChanges` name and semantics

After the change, the method should mean exactly:

> Does the captured dirty EF state contain a mutation that matters to the currently compiled consistency model and therefore makes current CLR state unsafe to admit as first baseline?

If the existing method name still communicates that accurately, keep it.

Do not rename public APIs.

Do not split this into a new service/class hierarchy.

A private helper in `ConsistencyEfCoreSession<TDbContext>` is enough.

---

# 14. Required code inspection before editing

Before changing code, inspect these exact locations at current HEAD:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreSession.cs
    HasRelevantChanges
    IncludeSemanticProperty
    Bind
    Tracked
    StabilizeCurrentTrackedBaselines

src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
    ModelMemberUsageKind
    GetMemberUsage
    GetTrackedMemberUsage
    HasNestedSemanticUsageForClrType

src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs
    CaptureUnitOfWork
    ReadModifiedProperties
    CaptureNavigationChanges

tests/Raffinert.Consistency.EntityFrameworkCore.Tests/InjectedRuntimeMaterializationTests.cs
    dirty-first-bind tests
    unrelated dirty entity test
    repeated-materialize test
    runtime-before-query test
```

Do not edit until you understand which mutation type `ChangeTrackerAdapter` emits for each scenario.

---

# 15. Implementation sequence — follow literally

## Step 1 — add the failing mapped-irrelevant-property test first

Add `Link.Comment` to the EF test model but not the consistency model.

Write the first-binding test described in section 5.

Run only that test.

Expected before fix:

```text
FAIL: runtime resolution throws because change.Set != null
```

If it already passes, inspect why before changing production code.

Do not make speculative changes.

## Step 2 — add/confirm relevant dirty scalar rejection test

Run the existing dirty `Left.Value` first-binding test.

Confirm it fails binding as expected before the production change.

This is your anti-regression control.

## Step 3 — refactor relevance helper

Change only the first-bind mutation relevance logic.

For member changes, use exact set-aware `GetTrackedMemberUsage(set, member)`.

Do not change lifecycle behavior.

## Step 4 — run the two contrast tests

Required:

```text
mapped irrelevant Comment change -> binding succeeds
mapped relevant Left.Value change -> binding rejects
```

Do not continue until both pass.

## Step 5 — add the clean-tracking/no-rebuild test

Implement section 8 exactly.

Reset counters only after query/materialization/fixup baseline work is complete.

## Step 6 — run existing repeated-materialize and tracking-invalidates-plan tests

Required existing tests must still pass:

```text
Repeated_materialize_without_tracking_keeps_baseline_revision_and_reuses_plan
Tracking_mapped_consumer_after_materialize_invalidates_pending_plan
Stale_pending_materialization_is_rolled_back_before_revision_rebuild
External_discovery_tracking_rebuilds_plan_against_final_baseline_revision
External_discovery_during_async_save_rebuilds_against_final_baseline_revision
```

This proves the cleanup did not break revision semantics.

## Step 7 — run all dirty-first-bind tests

Run all tests containing concepts equivalent to:

```text
Dirty_scalar_before_first_runtime_resolution
Dirty_navigation_before_first_runtime_resolution
Dirty_collection_before_first_runtime_resolution
Dirty_unmapped_entity_does_not_block_first_runtime_binding
mapped irrelevant property first binding
```

## Step 8 — run full EF Core test project

Run:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj
```

No filtering.

## Step 9 — run Core tests

Run:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj
```

## Step 10 — build the solution

Run the repository's normal solution build with warnings as errors.

Do not suppress new warnings.

---

# 16. Required regression matrix

Do not claim done until every row is either covered by an existing test or a new test from this task.

| ID | Scenario | Expected |
|---|---|---|
| R01 | mapped irrelevant scalar changed before first runtime resolution | binding succeeds |
| R02 | same irrelevant scalar persists through ordinary SaveChanges | persisted normally |
| R03 | mapped relevant scalar dependency changed before first binding | reject |
| R04 | mapped relevant navigation changed before first binding | reject |
| R05 | mapped relevant collection changed before first binding | reject |
| R06 | unrelated unmapped entity dirty before first binding | binding succeeds |
| R07 | mapped Added root before first binding | conservative reject |
| R08 | mapped Removed root before first binding | conservative reject |
| R09 | runtime resolved before clean Include query | baseline admission succeeds |
| R10 | clean query tracking may advance BaselineRevision | allowed |
| R11 | clean query tracking does not advance Version | required |
| R12 | mutation after clean tracking + first Materialize | one logical evaluation |
| R13 | SaveChanges after that Materialize with no new tracking | reuses same plan |
| R14 | calculator counts stay unchanged during SaveChanges reuse | required |
| R15 | mirror setter counts stay unchanged during SaveChanges reuse | required |
| R16 | Version advances once only after successful SQL | required |
| R17 | repeated Materialize without tracking | reuse preserved |
| R18 | new mapped tracking after Materialize | stale plan invalidated |
| R19 | stale plan invalidation restores owned mirrors | preserved |
| R20 | external discovery tracks mapped consumer during prepare | bounded rebuild preserved |
| R21 | failed baseline refresh | indexes/revision rollback preserved |
| R22 | failed SQL after Materialize | runtime still uncommitted |
| R23 | invariant enforcement | unchanged |
| R24 | incomplete scope guard | unchanged |
| R25 | generated-key guard | unchanged |
| R26 | setter failure O2/F2 rollback | unchanged |
| R27 | exact object-set identity with same CLR type | unchanged |
| R28 | duplicate EF integration registration | still rejected |

---

# 17. Anti-dumb prohibitions

Do not “fix” this task by doing any of the following:

```text
BAD: treat every property on a mapped entity as semantic
BAD: ignore all mapped entity changes before first binding
BAD: disable dirty-first-bind rejection
BAD: resolve runtime automatically by reaching into application services from entity setters
BAD: reconstruct old CLR state from EF OriginalValues
BAD: call RefreshBaseline after a dirty semantic mutation
BAD: increment Version during tracking
BAD: bump BaselineRevision on every Materialize/Save
BAD: remove pending-plan caching
BAD: remove external consumer discovery retry/rebuild
BAD: make BaselineRevision public
BAD: add runtime.Add/runtime.Apply back into application services
BAD: change the public Materialize API
BAD: add another SaveChanges wrapper
BAD: weaken navigation/collection tests because they are inconvenient
BAD: use member/property names as strings to determine relevance
```

Use compiled-model metadata already available through `GetTrackedMemberUsage(...)`.

---

# 18. Documentation updates

After code/tests are green, update current-state docs only where needed.

The docs should say, in current terminology:

> First runtime binding rejects pending EF changes only when those changes are relevant to the configured consistency model. Ordinary mapped properties that are not used by consistency keys, dependencies, invariants, relations, or projected selectors do not block binding.

Also make plan reuse semantics explicit:

> Baseline admission/stabilization caused by clean tracking happens before a pending plan is created. Once the baseline is stable, `Materialize` followed by ordinary `SaveChanges` reuses that plan when no further semantic mutation or mapped tracking occurs.

Likely files:

```text
README.md
docs/ef-core-consistency.md
.agents/skills/raffinert-consistency-consumer/SKILL.md
.agents/skills/raffinert-consistency-consumer/references/verification.md
```

Do not add historical notes.

Do not mention previous API generations.

Do not explain old broken behavior.

Describe only current behavior.

---

# 19. Final verification commands

Run, at minimum:

```bash
dotnet build
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj
```

If the repository has a normal full-solution test command, run it as well.

Do not report success based only on targeted tests.

---

# 20. Required agent completion report

When done, report exactly:

```text
1. Files changed.
2. Exact production-code change to first-binding relevance filtering.
3. Exact behavior of mapped irrelevant scalar before first runtime binding.
4. Exact behavior of mapped relevant scalar/navigation/collection before first binding.
5. New clean-tracking/no-rebuild test name.
6. Calculator/write counts proving SaveChanges reused the pending plan.
7. Confirmation that BaselineRevision semantics were not redesigned.
8. Confirmation that external-discovery stale-plan rebuild tests still pass.
9. Full test/build commands executed.
10. Test/build results.
11. Final commit SHA.
```

Do not write “done” without those details.

---

# 21. Definition of Done

This task is complete only when all of the following are true:

```text
[ ] A changed ordinary scalar property on a mapped entity does not block first runtime binding when the member is unused by the consistency model.
[ ] A changed consistency-relevant scalar still blocks first runtime binding.
[ ] Relevant navigation and collection dirty-state guards still block first runtime binding.
[ ] Unmapped unrelated dirty EF state still does not block binding.
[ ] Member relevance is determined from compiled consistency-model metadata, not mapping presence alone.
[ ] Runtime-before-query clean tracking is covered by a dedicated test.
[ ] Baseline admission/stabilization completes before the first pending plan in that test.
[ ] Materialize computes each logical value once.
[ ] SaveChanges with no intervening tracking reuses the same pending plan.
[ ] SaveChanges does not rewrite equal mirrors from a rebuilt plan.
[ ] BaselineRevision stays unchanged between Materialize and SaveChanges when no tracking occurs.
[ ] Version advances only after successful SQL.
[ ] Existing stale-plan invalidation after mapped tracking still works.
[ ] Existing external-discovery bounded rebuild still works.
[ ] Existing failed SQL and baseline rollback semantics still work.
[ ] Public API surface is unchanged.
[ ] Application service UX remains DbContext + ConsistencyRuntime + optional Materialize + ordinary SaveChanges.
[ ] Documentation and skill describe current behavior only.
[ ] Full build passes.
[ ] Core tests pass.
[ ] EF Core tests pass.
```
