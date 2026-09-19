# Codex plan — bind EF pending plans to runtime baseline revision

Status: **CORRECTNESS FOLLOW-UP — IMPLEMENT BEFORE CLOSING INJECTED EF RUNTIME WORK**

Audience: a very weak coding agent. Follow this document literally and in order. Do not redesign the public API. Do not add unrelated features. Do not simplify away tests.

Baseline implementation commit:

```text
0250613686fa3330a82f459a9d0418a4a1808506
```

That commit fixed the earlier unsafe unconditional baseline refresh. The remaining correctness hole is narrower:

> a pending binding plan can survive later committed baseline admission/stabilization because those baseline changes do not advance `ConsistencyRuntime.Version` and are not part of the EF mutation fingerprint.

This plan closes that hole.

---

# 0. Public UX is frozen

Do not change this application shape:

```csharp
public sealed class LinkService(
    LinkContext db,
    ConsistencyRuntime consistency)
{
    public async Task ChangeLeftValueAsync(
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

Application code must continue to require only:

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
ObjectSet<T> in application services
manual old values
ConsistencyEfCoreMappings in application services
manual consistency unit-of-work handling
custom save methods
```

---

# 1. Exact bug to fix

Current pending-plan reuse is approximately:

```text
Materialize(link)
    ↓
Prepare exact pending plan P
    ↓
store EF mutation fingerprint F
    ↓
NO runtime commit yet
```

Then before SaveChanges the runtime baseline may change without changing either:

```text
runtime.Version
EF mutation fingerprint F
```

Examples of committed baseline changes:

```text
- a newly tracked mapped entity is baseline-admitted
- a tracking query causes relationship fixup and a clean baseline root is stabilized
- another mapped consumer becomes known to the runtime
- projection/navigation baseline indexes are refreshed for clean tracked roots
```

A stale plan can therefore be reused against a different runtime dependency universe.

Forbidden sequence:

```text
runtime baseline = A
    ↓
plan P is prepared against A
    ↓
new mapped object is tracked
    ↓
AdmitBaseline / safe baseline stabilization changes committed baseline to B
    ↓
runtime.Version still unchanged
EF semantic mutation fingerprint still unchanged
    ↓
SaveChanges reuses P
```

Required invariant:

> A binding `PreparedImpactPlan` may be reused only while both domain runtime state and committed baseline/coverage state are exactly the same as when the plan was prepared.

---

# 2. Do not misuse `runtime.Version`

Do **not** solve this by incrementing the existing public/runtime domain mutation `Version` for EF query tracking or baseline admission.

`runtime.Version` currently represents committed semantic/domain mutation progression. Baseline loading must continue not to look like a domain mutation.

Required separation:

```text
Version
    committed semantic mutation version

BaselineRevision
    committed runtime baseline / coverage / baseline-index revision
```

These are distinct concepts.

Baseline admission/stabilization must not:

```text
- dispatch repair/policy callbacks
- increment domain mutation Version
- pretend that querying rows is a business mutation
```

---

# 3. Add one internal monotonic baseline revision

Add one internal monotonic counter to `ConsistencyRuntime`.

Conceptual shape:

```csharp
internal long BaselineRevision { get; private set; }
```

Exact integral type may follow repository conventions. Prefer `long`.

Do not expose this as public API.

Tests may read it through `InternalsVisibleTo`.

The revision means:

> identity/version of the committed baseline state used by navigation indexes, projection indexes, object-set coverage, and any other runtime-owned baseline structures that can affect planning.

Initial value:

```text
0
```

It must be monotonic within one runtime instance.

Do not reset it during the lifetime of a runtime.

---

# 4. Define exactly what bumps `BaselineRevision`

For this task, use a conservative rule.

Increment `BaselineRevision` after a successful committed baseline operation that can change the runtime baseline.

At minimum:

```text
AdmitBaseline(...) when it actually admits a previously-unregistered instance
RefreshBaseline(...) when the session intentionally performs baseline stabilization
```

Do not increment for:

```text
- duplicate AdmitBaseline that returns because the instance is already registered
- validation-only calls
- pending Prepare/Plan execution
- Materialize physical mirror writes
- failed/rolled-back domain plan execution
- ordinary committed domain mutations (those already advance Version)
```

Important: `RefreshBaseline` may be conservative. If it is called as an intentional stabilization operation, bumping `BaselineRevision` is acceptable even when the resulting indexes happen to be equal.

However, do **not** call `RefreshBaseline` on every `Materialize`/Save merely to make the revision change. Section 5 prevents that.

Revision must change **after** the baseline operation succeeds. If the baseline operation throws and runtime state is restored/unchanged, do not publish a new revision.

---

# 5. Do not destroy pending-plan reuse with repeated harmless stabilization

A dumb implementation can make every `Materialize` do:

```text
Refresh every clean tracked root
BaselineRevision++
old pending plan becomes stale
rebuild
```

That would be correct but would destroy the plan-reuse feature and make the existing repeated-Materialize test meaningless.

Do not do that.

Introduce a small session-level flag or candidate mechanism that represents:

> baseline stabilization may be needed because EF tracked something / relationship fixup may have changed clean baseline shape since the last stabilization.

A simple conservative shape is acceptable:

```csharp
private bool _baselineMayNeedStabilization;
```

Set it when tracking activity occurs while the runtime is already bound.

Before planning:

```text
1. DetectChanges / capture semantic mutations.
2. If no tracking activity occurred since last stabilization:
      do not baseline-refresh anything.
3. If tracking activity occurred:
      stabilize only already-registered mapped roots that are NOT in touchedSources.
      clear the flag only after successful stabilization.
```

This preserves:

```text
Materialize
Materialize
Materialize
```

with no intervening tracking/mutation as one prepared plan and one set of logical calculations.

If repository structure makes a candidate set cleaner than one boolean, use a candidate set. Do not over-engineer it.

---

# 6. Tracking event rules

Current `Tracked` behavior baseline-admits mapped entities after the runtime is bound.

Preserve automatic admission, but make its semantics explicit.

On EF `ChangeTracker.Tracked` while runtime is bound:

```text
if entry is an eligible clean mapped baseline entity:
    AdmitBaseline(exact mapped set, entity)
    mark baseline stabilization as potentially needed

if entry is Added:
    do not baseline-admit it as durable baseline
    but mark stabilization as potentially needed if relationship fixup may have affected existing roots

if entry is unrelated/unmapped:
    it must not become a Raffinert object-set admission
    only mark stabilization if there is a concrete reason; prefer no-op
```

Do not dispatch policy callbacks from tracking.

Do not increment domain `Version` from tracking.

Do not silently admit Added entities as committed baseline.

---

# 7. Pending plan must carry the baseline revision it was prepared against

Extend pending EF session state so the binding evidence includes baseline revision.

Preferred shape:

```csharp
internal sealed record PendingConsistencySave(
    ConsistencyUnitOfWork Unit,
    PreparedImpactPlan? Plan,
    MaterializationRollback? MaterializationRollback,
    EfMutationFingerprint Fingerprint,
    long BaselineRevision);
```

The exact record ordering may follow repository style.

Capture `BaselineRevision` only after all baseline admissions/stabilizations that are allowed for that prepare operation have completed and immediately around the actual binding plan preparation.

Do not capture a revision, then perform more baseline mutation, then still store the old revision.

Required invariant after `Prepare(...)` returns:

```text
pending.BaselineRevision == runtime.BaselineRevision
```

unless preparation itself intentionally detected an unsupported re-entrant baseline change and failed.

---

# 8. Reuse predicate becomes three-part

A pending plan is reusable only if all of these are true:

```text
1. runtime domain Version still matches the binding plan assumptions
2. runtime.BaselineRevision == pending.BaselineRevision
3. current EF semantic mutation fingerprint == pending.Fingerprint
```

The `PreparedImpactPlan` already carries normal runtime binding/version semantics. Do not duplicate that logic incorrectly.

At the EF session reuse decision, explicitly check baseline revision before returning `_pending`.

Conceptually:

```csharp
if (_pending is not null)
{
    if (_pending.BaselineRevision != runtime.BaselineRevision)
    {
        DiscardPending();
    }
    else
    {
        var fingerprint = CaptureFingerprint(...);
        if (_pending.Fingerprint.Equals(fingerprint))
        {
            // reuse
        }
        else
        {
            DiscardPending();
        }
    }
}
```

Ordering may differ if fingerprint capture itself can track/discover entities. See section 10.

Do not reuse a plan solely because the EF fingerprint matches.

---

# 9. Any committed baseline change after pending-plan creation invalidates the pending plan

Use this simple safety rule:

> If `BaselineRevision` differs, discard and rebuild. Do not attempt to prove that a particular baseline change is irrelevant to the pending plan.

A conservative rebuild is acceptable.

Do not patch `PreparedImpactPlan` in place.

Do not merge newly admitted objects into an existing plan.

Do not mutate the old plan's forward patch.

Do not reuse planned derived evaluations from the stale plan.

Required flow:

```text
pending plan P against baseline revision 10
    ↓
new tracked mapped consumer admitted
baseline revision becomes 11
    ↓
SaveChanges / Materialize
    ↓
P is stale
    ↓
rollback any physical mirrors owned by P if necessary
    ↓
discard P
    ↓
prepare fresh plan against revision 11
```

---

# 10. Handle baseline changes that happen DURING preparation/discovery

This is release-critical.

The existing coordinator may perform external-consumer discovery during `Prepare` / `PrepareAsync`. Discovery can execute EF queries. EF queries can track new mapped objects. Tracking can trigger baseline admission and therefore change `BaselineRevision` while the prepare operation itself is in progress.

Do not assume baseline revision is stable merely because it was stable before calling coordinator preparation.

Implement one of these safe patterns:

## Preferred pattern — stabilize/prepare until revision is stable

```text
revisionBefore = runtime.BaselineRevision
Prepare coordinator plan
revisionAfter = runtime.BaselineRevision

if revisionAfter != revisionBefore because consistency-owned discovery tracked/admitted baseline objects:
    discard the just-built binding result
    rebuild once against the new committed baseline
```

If the second preparation causes another revision change, do **not** loop forever silently. Fail with a clear internal exception indicating unstable baseline discovery / unsupported re-entrant tracking.

A bounded retry of one rebuild is enough for this task.

## Alternative pattern

If the existing external-consumer discovery architecture already has a clean way to prevent discovered objects from being baseline-admitted and instead represent them only as pending `CoverageAdmission` mutations, reuse that mechanism.

Do not invent a second discovery engine.

Whichever implementation is chosen, add a test. See section 16.

---

# 11. Pending physical mirrors must be rolled back before stale-plan rebuild

If an explicit call already did:

```csharp
runtime.Materialize(link);
```

then physical mirrors may have been written from pending plan P.

If a later baseline revision change makes P stale, rebuilding must not leave physical representations from P mixed with plan Q.

Use the existing `DiscardPending()` / `MaterializationRollback` machinery.

Required order:

```text
baseline revision mismatch detected
    ↓
restore materializations written by old pending plan
    ↓
clear owned-write journal for old plan
    ↓
clear old pending
    ↓
prepare new plan
    ↓
apply selected/all materializations from new plan as appropriate
```

Do not clear `_pending` before giving rollback a chance to restore physical mirrors.

Do not lose `IsModified` rollback semantics.

---

# 12. Test 1 — track a new mapped consumer after Materialize, before SaveChanges

Add a release-blocking integration test using the neutral fixture.

The test must create a situation where a newly tracked mapped object could affect dependency coverage.

Use a second `Link` or another mapped neutral consumer. Prefer reusing the existing `Item` / `Link` model.

Required timeline:

```text
1. Resolve DbContext and ConsistencyRuntime.
2. Query only the initial Link1 and the Items required for Link1.
3. Mutate an input affecting Link1.
4. Call runtime.Materialize(Link1).
5. Record:
       pending logical calculation count
       runtime.Version
       runtime.BaselineRevision
6. BEFORE SaveChanges, execute another EF query that tracks Link2, which is mapped and relevant to the same dependency universe.
7. Prove BaselineRevision changed while Version did not.
8. Call ordinary SaveChangesAsync().
9. Prove the old pending plan was NOT reused.
10. Prove a fresh plan was built against the new baseline revision.
11. Prove final persisted mirrors are correct.
12. Prove runtime commits once after SQL success.
```

Instrument calculator / planner-visible behavior with counters so this test cannot pass accidentally.

The test must fail if baseline revision is ignored and only fingerprint equality is used.

---

# 13. Test 2 — repeated Materialize without tracking still reuses the plan

Keep the existing repeated-materialize test and strengthen it to assert baseline revision stability.

Timeline:

```csharp
link.Left.Value = 55m;

var revision = runtime.BaselineRevision;

runtime.Materialize(link);
runtime.Materialize(link);
runtime.Materialize(link);

await db.SaveChangesAsync();
```

Required assertions:

```text
BaselineRevision did not change between repeated calls
Ratio calculator count == 1
NormalizedRatio calculator count == 1
Ratio physical write count == 1
NormalizedRatio physical write count == 1
runtime.Version advances only after SQL
```

This test prevents the dumb implementation that refreshes every root and bumps revision on every call.

---

# 14. Test 3 — tracking after Materialize invalidates physical pending mirrors safely

Create a case where:

```text
plan P materializes value X
then a new mapped baseline object is tracked
fresh plan Q should produce Y (or at minimum must be recomputed)
```

Required assertions:

```text
old plan's physical writes are rollback-capable
stale plan is discarded
new plan evaluates again
final physical value comes from Q
no duplicate policy dispatch
runtime.Version still unchanged before SQL
```

If constructing X != Y is cumbersome, use counters plus rollback/write assertions to prove P was discarded and Q rebuilt.

Do not settle for only checking `BaselineRevision` numerically.

---

# 15. Test 4 — clean tracking before first pending plan must not cause pointless rebuild

Scenario:

```text
resolve runtime
execute complete Include query / track baseline rows
allow one baseline stabilization
then mutate
then Materialize
then SaveChanges
```

Required behavior:

```text
baseline revision may advance during initial admission/stabilization
pending plan captures the final stabilized revision
SaveChanges with no later tracking reuses that plan
calculator does not execute twice
```

This proves revision capture occurs at the correct time.

---

# 16. Test 5 — consistency-owned external discovery must not leave a stale binding plan

Add one focused injected-EF test using the repository's existing `DiscoverConsumers` / external-consumer discovery mechanism.

The exact model may reuse an existing test fixture if easier; keep domain neutral if adding a new fixture.

Required scenario:

```text
tracked mutation exists
Materialize or Save preparation begins
external consumer discovery issues a query
query tracks a new mapped consumer during preparation
```

Required assertions:

```text
no stale plan prepared against the pre-discovery baseline is committed
final plan sees the discovered consumer
runtime domain Version commits once after SQL
repair/policy dispatch occurs once
BaselineRevision reflects committed baseline admissions
```

If current discovery deliberately represents discovered objects as pending coverage admissions rather than committed baseline admissions, assert that behavior instead and prove BaselineRevision stays stable.

Do not skip this test merely because ordinary application queries are already covered. Discovery is library-owned tracking and is exactly where re-entrant baseline changes can happen.

---

# 17. Dirty-first-bind filtering must be Raffinert-relevant, not whole-DbContext dirty state

Current first-binding guard uses a captured unit of work and may reject because an unrelated tracked entity is Modified even when it has no mapping into Raffinert consistency sets.

Fix the policy so the documented contract is true:

> First runtime binding is rejected for pending Raffinert-relevant mapped semantic state, not for arbitrary unrelated EF changes in the same DbContext.

Do not broadly weaken the guard.

At first binding:

```text
mapped scalar/navigation/collection/add/remove semantic change -> reject
unmapped unrelated entity change -> ignore for baseline-binding guard
```

Prefer adding an internal capture/filter specifically for mapped consistency mutations rather than changing public adapter behavior used elsewhere.

Do not remove navigation/collection detection.

Do not reconstruct historical state.

---

# 18. Test 6 — unrelated dirty entity does not block first runtime binding

Extend the test DbContext with a small unrelated EF entity that is NOT mapped through `ConsistencyEfCoreMappings`.

Example:

```csharp
private sealed class Note
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
}
```

Scenario:

```text
resolve DbContext
query clean mapped Link/Items
query Note
modify Note.Text
runtime has NOT been resolved yet
resolve ConsistencyRuntime
```

Expected:

```text
runtime binding succeeds
clean mapped baseline is admitted
unrelated Note mutation remains an ordinary EF change
```

Then keep existing tests proving dirty mapped scalar/navigation/collection state still rejects first binding.

---

# 19. Baseline revision must not be confused with object-set membership/public semantics

Do not expose APIs such as:

```text
IncrementBaselineRevision()
SetBaselineRevision(...)
```

publicly.

Do not let callers manipulate revision.

Do not persist revision to database.

Do not serialize it into model metadata.

Do not use it as entity concurrency token.

It is runtime-internal binding evidence only.

---

# 20. Core implementation checklist

Before editing, inspect these exact areas:

```text
src/Raffinert.Consistency/Runtime/RuntimeSeed.cs
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
src/Raffinert.Consistency/Dependencies/NavigationAndImpact.cs
src/Raffinert.Consistency/Dependencies/ProjectionIndexRegistry.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreSession.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs
src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs
src/Raffinert.Consistency.EntityFrameworkCore/EfTouchedSources.cs
```

Map the current paths for:

```text
AdmitBaseline
RefreshBaseline
Tracked event
StabilizeCurrentTrackedBaselines
StabilizeUntouchedTrackedBaselines
PreparePending
PrepareForSaveCore
PrepareForSaveCoreAsync
PendingConsistencySave
DiscardPending
ExternalConsumerDiscovery
```

Do not start coding until you can point to each one.

---

# 21. Suggested implementation sequence

Follow this order.

## Phase A — add internal revision only

1. Add internal `BaselineRevision` to `ConsistencyRuntime`.
2. Add one private/internal helper to increment it after successful baseline mutation.
3. Make successful new `AdmitBaseline` bump it.
4. Make intentional `RefreshBaseline` stabilization bump it.
5. Add narrow Core tests for revision behavior.
6. Do not change EF pending-plan reuse yet.

Gate A:

```text
Build Core.
Run Core tests.
No public API file changes should be needed.
```

## Phase B — prevent repeated stabilization churn

1. Add session tracking flag/candidates for baseline stabilization need.
2. Set it from relevant `Tracked` activity.
3. Run stabilization only when needed.
4. Keep touched-source exclusion.
5. Clear stabilization-needed state only after successful stabilization.
6. Strengthen repeated-Materialize test.

Gate B:

```text
Repeated Materialize still computes/writes once.
BaselineRevision stable across repeated calls without tracking.
```

## Phase C — bind pending state to revision

1. Add `BaselineRevision` to `PendingConsistencySave` or equivalent pending session evidence.
2. Capture it at the correct point after permitted baseline stabilization.
3. Compare it before reuse.
4. On mismatch call existing rollback/discard machinery.
5. Rebuild fresh plan.
6. Do not patch old plan.

Gate C:

```text
New mapped tracking after Materialize forces rebuild.
No tracking after Materialize preserves reuse.
```

## Phase D — preparation/discovery re-entrancy

1. Instrument revision before/after preparation.
2. Handle discovery-time baseline revision change safely.
3. Prefer one bounded rebuild.
4. Fail clearly if revision keeps changing during rebuild.
5. Add external discovery regression test.

Gate D:

```text
No plan can be returned as reusable if baseline changed underneath its preparation.
```

## Phase E — first-bind relevance filter

1. Identify only mapped/Raffinert-relevant pending mutations.
2. Keep mapped dirty rejection.
3. Permit unrelated unmapped EF changes.
4. Add test entity and tests.

Gate E:

```text
Dirty mapped -> reject.
Dirty unrelated -> bind succeeds.
```

## Phase F — full regression

Run the full matrix in section 24.

---

# 22. Anti-dumb rules

Do not do any of the following:

1. Do not increment `runtime.Version` for baseline loading.
2. Do not use EF mutation fingerprint as the only pending-plan validity evidence.
3. Do not refresh every clean tracked root on every `Materialize`.
4. Do not make repeated `Materialize` recompute just to simplify revision logic.
5. Do not patch a stale `PreparedImpactPlan`.
6. Do not merge new admissions into an existing plan manually.
7. Do not commit runtime state before SQL.
8. Do not dispatch policy/repair on baseline admission.
9. Do not baseline-admit Added entities as durable rows.
10. Do not ignore tracking that occurs during external discovery.
11. Do not create an unbounded retry loop around preparation.
12. Do not expose `BaselineRevision` publicly.
13. Do not add `DbContext` references to Core.
14. Do not make application services inject session/mappings/model.
15. Do not remove existing navigation/projection transactional tests.
16. Do not weaken dirty mapped first-bind rejection.
17. Do not reject unrelated unmapped dirty EF entities after this task.
18. Do not delete legacy lower-level EF APIs merely because the injected path exists.
19. Do not add concurrency/thread-safety scope to this task.
20. Do not change push/pull evaluation semantics in this task.
21. Do not change materialization O2/F2 semantics.
22. Do not change invariant/repair ordering.
23. Do not add source generators/proxies/INotifyPropertyChanged.
24. Do not redesign `AddRaffinertConsistency<TDbContext>`.
25. Do not claim done without running the full tests and reporting exact commands/results.

---

# 23. Failure semantics

## Baseline stabilization failure

```text
baseline operation throws
    -> do not publish new BaselineRevision
    -> pending plan must not be treated as valid merely because revision stayed unchanged if runtime state may be partial
```

Use existing atomic/rollback behavior. If current `RefreshBaseline` is not atomic and can partially mutate indexes before throwing, stop and fix atomicity or fail the task explicitly. Do not hide partial mutation behind revision logic.

## Stale pending detected

```text
revision mismatch
    -> rollback old pending physical mirrors
    -> clear owned writes
    -> discard pending
    -> rebuild
```

## SQL failure

Existing contract remains:

```text
runtime.Version unchanged
pending plan not committed
policy/repair not dispatched
physical pending mirrors restored per existing rollback contract
BaselineRevision reflects only legitimate committed baseline tracking/stabilization that happened before the save; SQL failure must not invent new baseline state
```

---

# 24. Required regression matrix

All of these must pass before reporting done.

```text
R01  injected LinkService happy path still works
R02  ordinary SaveChanges without explicit Materialize still works
R03  runtime Version unchanged before SQL
R04  SQL failure leaves domain Version unchanged
R05  navigation committed index remains old before SQL
R06  navigation failed SQL keeps old committed index
R07  navigation successful SQL commits new index
R08  projection committed index remains old before SQL
R09  projection failed SQL keeps old committed index
R10  projection successful retry commits new index
R11  dirty mapped scalar before first runtime bind rejects
R12  dirty mapped navigation before first runtime bind rejects
R13  dirty mapped collection before first runtime bind rejects
R14  clean late binding succeeds
R15  runtime-before-query baseline admission succeeds
R16  baseline admission does not dispatch repair/policy
R17  repeated Materialize evaluates once
R18  repeated Materialize writes mirrors once
R19  repeated Materialize does not churn BaselineRevision
R20  targeted Materialize remains EF-aware
R21  targeted Materialize writes only selected target before save
R22  invariant enforcement still blocks before SQL
R23  incomplete scope still blocks
R24  generated-key guard still blocks
R25  setter failure rolls back physical mirrors and does not commit runtime
R26  duplicate Raffinert EF registration still rejects
R27  new mapped entity tracked after Materialize bumps BaselineRevision
R28  pending plan from old BaselineRevision is discarded
R29  stale-plan physical writes are rolled back before rebuild
R30  fresh plan after baseline change persists correct mirrors
R31  baseline tracking after initial stabilization but before SaveChanges cannot reuse stale plan
R32  external-consumer discovery tracking cannot commit a pre-discovery stale plan
R33  dirty unrelated unmapped EF entity does not block first runtime bind
R34  dirty unrelated entity plus clean mapped entities binds and later mapped workflow works
R35  same CLR type / exact object-set identity behavior remains correct
R36  no application-service Add/Apply/manual registration appears
```

---

# 25. Required focused test names

Use descriptive names close to these so future regressions are obvious:

```text
Tracking_mapped_consumer_after_materialize_invalidates_pending_plan
Baseline_revision_changes_without_advancing_domain_version
Repeated_materialize_without_tracking_keeps_baseline_revision_and_reuses_plan
Stale_pending_materialization_is_rolled_back_before_revision_rebuild
External_discovery_tracking_rebuilds_plan_against_final_baseline_revision
Dirty_unmapped_entity_does_not_block_first_runtime_binding
Dirty_mapped_entity_still_blocks_first_runtime_binding
```

Do not replace these integration tests with only unit tests for a counter.

---

# 26. Documentation updates

After implementation is proven, update only current-state documentation:

```text
README.md
docs/ef-core-consistency.md
.agents/skills/raffinert-consistency-consumer/SKILL.md
.agents/skills/raffinert-consistency-consumer/references/recipes.md
.agents/skills/raffinert-consistency-consumer/references/verification.md
CHANGELOG.md
```

Document the concept without exposing internal implementation unnecessarily:

```text
Pending EF consistency plans are bound to both the current semantic runtime state and the current tracked baseline/coverage state. Tracking additional mapped baseline objects invalidates an older pending plan and causes a fresh plan to be prepared before persistence.
```

Do not mention historical API versions.

Do not add migration notes about removed APIs.

Do not tell application users to manage BaselineRevision.

---

# 27. Verification commands

Run from a clean checkout at the implementation head.

At minimum:

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Also run the focused EF project explicitly if the solution-level command can skip target-specific tests:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release
```

Run formatting/public API/package validation commands already used by repository CI.

If package-consumer tests exist, run them too.

Report exact commands and exact pass/fail counts.

Do not say "all tests pass" without actually running them.

---

# 28. Git/CI evidence

Before reporting done:

```text
1. git status must be clean
2. report final commit SHA
3. report files changed
4. report exact test commands/results
5. if GitHub Actions ran for the exact commit, report the exact run/result
6. if exact-head CI did not run, say so explicitly
```

Do not use an older green CI run as proof for the new head.

---

# 29. Stop conditions

Stop and report instead of inventing semantics if any of these are discovered:

```text
S1  RefreshBaseline is not atomic and can leave partial committed indexes on failure.
S2  ExternalConsumerDiscovery tracks mapped entities in a way that fundamentally conflicts with automatic baseline admission.
S3  A binding PreparedImpactPlan cannot safely be discarded/rebuilt after physical materialization rollback.
S4  EF tracking re-enters planning recursively and causes unbounded preparation.
S5  The only implementation path would require advancing domain Version for baseline loading.
S6  Fixing unrelated-dirty filtering would require changing public ChangeTrackerAdapter semantics used by downstream consumers.
S7  Tests reveal more than one ConsistencyRuntime per scoped DbContext.
S8  The exact neutral LinkService UX would need additional application calls.
```

For a stop condition, report:

```text
- exact failing scenario
- exact current code path
- why the selected semantics cannot be preserved
- smallest next design decision required
```

Do not silently work around it.

---

# 30. Definition of done

This task is done only when the following statement is true:

> An EF-bound `ConsistencyRuntime` can prepare and materialize a pending consistency plan, then safely survive additional mapped tracking/baseline stabilization before `SaveChanges`: any baseline change invalidates the old binding plan, physical pending writes are rolled back as required, a fresh plan is prepared against the final baseline, and runtime semantic state still commits exactly once only after successful SQL.

And this remains true:

```csharp
public sealed class LinkService(
    LinkContext db,
    ConsistencyRuntime consistency)
{
    public async Task ChangeLeftValueAsync(
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

No extra Raffinert plumbing is allowed in that service.
