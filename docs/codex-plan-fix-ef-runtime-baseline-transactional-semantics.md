# Codex plan — fix EF-aware runtime baseline and transactional semantics

Status: **CORRECTNESS FOLLOW-UP — IMPLEMENT BEFORE TREATING INJECTED EF RUNTIME AS DONE**

Audience: a very weak coding agent. Follow this document literally and in order. Do not redesign the public UX. Do not add unrelated features.

Baseline implementation commit:

```text
586b2f70544ca57a2069154036c6e67b96be2595
```

The implementation at that commit established the desired application-facing API, but it contains a correctness risk: baseline refresh can mutate committed navigation/projection indexes from current CLR state before SQL succeeds.

This plan fixes that risk while preserving the already-selected UX.

---

# 0. Public UX is frozen

Do **not** redesign this:

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

Application code must still require only:

```text
DbContext
ConsistencyRuntime
Materialize(entity) when mirrors are needed before save
ordinary SaveChanges / SaveChangesAsync
```

Do not reintroduce:

```text
runtime.Add(...)
runtime.Apply(...)
ObjectSet<T> in application services
old values in application services
ConsistencyEfCoreMappings in application services
SaveChangesConsistentlyAsync(...) in application services
manual consistency unit-of-work handling
```

---

# 1. The bug to fix

Current EF session code can execute this sequence:

```text
DetectChanges
    ↓
RefreshTrackedBaselines
    ↓
runtime.RefreshBaseline(...)
    ↓
_navigation.RefreshRoot(...)
_projections.RefreshRoot(...)
```

`RefreshRoot` rebuilds committed runtime indexes from **current CLR state**.

That is unsafe after an application mutation.

Example:

```csharp
link.Left = replacement;
consistency.Materialize(link);
```

The committed runtime must continue to represent the pre-save relationship until SQL succeeds.

It is forbidden for `Materialize` to mutate committed runtime-owned navigation/projection indexes merely because the CLR graph changed.

The required ordering remains:

```text
EF tracked mutation
    ↓
prepare exact pending plan
    ↓
evaluate/materialize from pending plan
    ↓
committed runtime remains unchanged
    ↓
SQL succeeds
    ↓
commit exact pending plan
    ↓
dispatch once
```

If SQL fails:

```text
committed runtime == pre-save state
```

for scalar dependency state, relation indexes, navigation indexes, projection indexes, derived caches, invariant state, and repair/policy state.

---

# 2. Do not “fix” this by removing all baseline stabilization blindly

The original implementation likely introduced baseline refresh because EF relationship fixup can occur while a tracking query is materializing objects.

For example, an entity can be admitted before all reference navigations are fully fixed up.

Therefore this is **not** the correct fix:

```diff
- RefreshTrackedBaselines();
```

with no replacement analysis.

We need two distinct operations:

```text
A. baseline stabilization
   allowed only when current CLR state is proven authoritative baseline state

B. semantic mutation planning
   required when CLR state differs from committed baseline
```

Never use A to implement B.

---

# 3. Fixed safety rule

Adopt this rule literally:

> Baseline admission/stabilization may update committed runtime indexes only for tracked objects whose relevant current CLR state is still authoritative baseline state. Once EF reports a semantic mutation for an object/root, that mutation must flow through Prepare/Plan/Commit and must not be silently rebased into the committed runtime.

For this task, choose a conservative implementation rather than clever inference.

When preparing `Materialize` or `SaveChanges`:

1. `DetectChanges()`.
2. Capture EF semantic mutations **before any baseline refresh**.
3. Build a set of directly touched source objects from those mutations.
4. Baseline-stabilize only already-registered mapped sources that are not directly touched by a property, navigation, collection, add, remove, or other structural semantic mutation.
5. Then run the normal existing coordinator Prepare/Plan path.

A conservative false negative is acceptable:

```text
skip a safe refresh and do slightly less stabilization
```

An unsafe false positive is not acceptable:

```text
refresh a semantically changed root and silently rebase committed runtime state
```

---

# 4. Introduce one internal “touched baseline roots” concept

Do not scatter ad-hoc conditions through the session.

Add one small internal representation/helper in the EF package that answers:

```text
Which tracked object instances are directly touched by the currently captured EF semantic mutation set?
```

It should understand at least:

```text
PropertyChange.Instance
CollectionChange.Owner
ObjectAdded.Instance
ObjectRemoved.Instance
CoverageAdmission.Instance if present in the evidence being inspected
```

Reference identity must be used for object instances.

Suggested conceptual helper:

```csharp
internal sealed class EfTouchedSources
{
    bool Contains(object source);
}
```

The exact name may follow repository conventions.

Do not expose this publicly.

Do not use CLR equality for entities.

---

# 5. Capture mutation evidence before baseline stabilization

Refactor `ConsistencyEfCoreSession<TDbContext>` so that `TryMaterializeObject`, `TryMaterializeDerived`, `PrepareForSave`, and async save preparation do not call an unconditional refresh before semantic mutations are known.

The flow should conceptually become:

```text
DetectChanges
    ↓
capture current mapped EF semantic mutation evidence
    ↓
compute touched source identities
    ↓
stabilize only untouched registered baseline roots
    ↓
run normal policy/scope/discovery Prepare/Plan using current EF mutation set
```

Reuse `ChangeTrackerAdapter` rather than inventing a second EF diff engine.

If necessary, extract an internal coordinator/helper method for capturing the current `ConsistencyUnitOfWork` or raw mutation list before full policy planning.

Do not duplicate navigation-change detection logic.

Do not duplicate generated-value handling.

Do not change public `ChangeTrackerAdapter` API unless unavoidable.

---

# 6. Replace unconditional `RefreshTrackedBaselines`

Current behavior approximately does:

```csharp
foreach (var entry in _context.ChangeTracker.Entries())
{
    var mapping = _mappings.UnitOfWorkMappings.Resolve(entry);
    if (mapping is not null)
        _runtime.RefreshBaseline(mapping.SetDefinition, entry.Entity);
}
```

This is forbidden after semantic changes.

Replace it with a method whose semantics are explicitly conservative, conceptually:

```csharp
StabilizeUntouchedTrackedBaselines(touchedSources);
```

Rules:

```text
entry must be mapped
entry must already be registered in the exact mapped object set
entry must not be Added
entry must not be Deleted
entry.Entity must not be in touchedSources
only then may runtime.RefreshBaseline(set, entity) be called
```

If the same CLR type belongs to multiple object sets, keep the exact mapping-selected set identity.

Do not scan same-CLR object sets and guess membership.

Do not refresh an object merely because its EF EntityState is `Unchanged` if captured navigation/collection evidence says it is touched.

The mutation evidence wins.

---

# 7. Navigation mutation must remain pending, not rebased

Add a release-blocking integration test using the neutral domain.

Extend the fixture with a third `Item`, for example:

```text
left1: Id=1, Value=60
right: Id=2, Value=10
left2: Id=3, Value=50
link.Left = left1
link.Right = right
Ratio = 6
NormalizedRatio = 6
```

Test timeline:

```csharp
var link = await db.Links
    .Include(x => x.Left)
    .Include(x => x.Right)
    .SingleAsync();

var replacement = await db.Items.SingleAsync(x => x.Id == 3);

// prove committed runtime baseline before mutation
// use existing internal navigation diagnostic available to the test assembly

link.Left = replacement;
link.LeftId = replacement.Id;

var version = runtime.Version;

consistency.Materialize(link);

Assert.Equal(5m, link.Ratio);
Assert.Equal(5m, link.NormalizedRatio);
Assert.Equal(version, runtime.Version);
```

Then prove the **committed navigation index still points to the old Left before SQL**.

Use existing internal test-visible runtime diagnostics/helpers where possible. The tests project already has InternalsVisibleTo.

Do not add a public diagnostic API solely for this test.

If a tiny internal read helper is required, keep it internal and narrowly scoped.

After successful SaveChanges, prove the committed navigation index now points to the replacement exactly once.

---

# 8. Navigation mutation + SQL failure is mandatory

This is the most important regression test.

Use the same navigation mutation, then force SQL failure after `Materialize`.

The failure mechanism may use the existing SQLite check-constraint technique or another deterministic database error.

Required assertions:

```text
before Materialize:
    runtime committed navigation = old Left

immediately after Materialize, before SaveChanges:
    link.Ratio reflects NEW Left through pending plan
    runtime.Version unchanged
    runtime committed navigation STILL = old Left

SaveChanges fails:
    runtime.Version unchanged
    no repair/policy dispatch
    runtime committed navigation STILL = old Left
    pending plan discarded
```

Then perform a valid retry and prove:

```text
new plan is built
save succeeds
runtime commits new navigation only after SQL success
```

This test must fail against the unsafe unconditional baseline-refresh implementation.

If it does not fail against the old code, the test is not proving the bug.

---

# 9. Projection selector mutation needs its own regression test

`RefreshBaseline` also updates projection indexes. Do not fix navigation only.

Add a minimal projected derived path in the neutral fixture or in a separate focused fixture.

Recommended neutral shape:

```csharp
var doubledItemValue = model
    .Derived(items)
    .Select(x => x.Value * 2)
    .Named("doubled-item-value");

var projectedLeftValue = model
    .Derived(links)
    .From(x => x.Left, doubledItemValue)
    .Select((_, value) => value)
    .MaterializeTo(x => x.ProjectedLeftValue)
    .Named("projected-left-value");
```

Add `ProjectedLeftValue` to the test-only neutral `Link` type.

Scenario:

```text
link initially projects left1
change link.Left -> left2
Materialize(link)
```

Required behavior:

```text
materialized ProjectedLeftValue uses left2 through the pending plan
committed projection index still represents left1 before SQL
failed SQL leaves committed projection index at left1
successful retry commits projection index to left2
```

Prefer behavioral assertions through existing derived invalidation/planning rather than adding public introspection.

If internal projection-index inspection is unavoidable, add only an internal test-visible helper.

---

# 10. Late runtime binding against dirty tracked state must not silently rebase

The normal DI application path resolves `ConsistencyRuntime` with the service before the service query/mutation, so this is an edge case. Handle it conservatively.

Scenario:

```csharp
var db = scope.ServiceProvider.GetRequiredService<LinkContext>();
var link = await db.Links.Include(...).SingleAsync();

link.Left.Value = 55m;

// runtime has NOT been resolved yet
var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
```

Current-state values must not be admitted as if `55` were the authoritative committed baseline.

For this task, implement this fixed policy:

> First binding of a scoped ConsistencyRuntime to a DbContext is allowed only when mapped tracked state has no pending semantic mutations. If mapped semantic changes already exist, reject the binding with a clear exception/message instructing the caller to resolve/inject the runtime before mutating tracked entities.

Do **not** attempt to reconstruct a historical object graph from EF `OriginalValues` in this task.

Do **not** temporarily rewrite CLR navigation/property values to bootstrap the runtime.

Do **not** silently accept dirty current state.

A clear failure is safer than a false baseline.

Use an existing suitable exception if one clearly matches. Otherwise introduce the smallest EF-package exception or `InvalidOperationException` with a stable, explicit message. Do not add a broad exception hierarchy.

Required tests:

```text
tracked unchanged entities before runtime resolution -> supported
tracked mapped scalar mutation before runtime resolution -> rejected
tracked mapped navigation mutation before runtime resolution -> rejected
tracked mapped collection mutation before runtime resolution -> rejected
```

If there are only Added mapped entities before runtime resolution, use the same conservative rejection in this task. Do not expand this task into pre-save materialization for newly Added roots.

Document that this restriction is about **first runtime binding**, not ordinary changes after a correctly bound runtime exists.

---

# 11. Clean late binding must still work

Preserve the existing supported scenario:

```text
resolve DbContext
query mapped entities
make NO application mutations
resolve ConsistencyRuntime
Materialize later after normal mutation
```

During binding:

```text
DetectChanges
prove mapped tracker is clean
admit existing mapped unchanged objects
perform one safe baseline stabilization after all admissions
```

This is the correct place for a full baseline stabilization because the tracker has been proven clean.

After the runtime is bound, ordinary application changes must use pending plans, not baseline refresh.

---

# 12. Tracking-query baseline stabilization

Preserve the scenario where runtime exists before query:

```text
resolve runtime
DbContext tracks zero domain entities
execute Include query
EF tracks Item + Link objects and performs relationship fixup
later mutate Left.Value
Materialize(link)
```

The runtime baseline must contain the correct fixed-up navigation graph before the application mutation is committed.

Do not solve this by unconditional refresh inside `Materialize`.

Preferred conservative strategy:

```text
Tracked event:
    admit newly tracked mapped unchanged entity

before planning:
    capture semantic mutations first
    refresh only already-registered roots that are not touched by those mutations
```

This permits a `Link` whose navigation was fixed up during query materialization to be stabilized while still refusing to rebase a `Link` whose navigation was explicitly changed by the application.

Keep the existing “runtime created before query” test and strengthen it with an assertion that the navigation-dependent calculation is correct.

---

# 13. Baseline admission must not dispatch policy/repair work

The original plan required this and the first implementation did not prove it strongly enough.

Add a test with a counter-backed repair/policy callback.

Timeline:

```text
resolve runtime
query/track clean baseline objects
baseline admission/stabilization occurs
```

Assert:

```text
repair callback count == 0
policy dispatch count == 0
runtime domain-mutation version was not advanced merely because EF loaded baseline rows
```

Then perform a real mutation and SaveChanges and prove expected dispatch occurs only after successful SQL commit.

Do not treat baseline admission as a domain change.

---

# 14. Repeated Materialize needs setter-count coverage

Existing test checks calculator reuse. Keep it.

Add physical write counting.

Use test-only field-backed properties or a setter counter, for example:

```csharp
public decimal? Ratio
{
    get => _ratio;
    set
    {
        SetterCounter.RatioWrites++;
        _ratio = value;
    }
}
```

Reset counters after EF query materialization and before the explicit operation being measured.

Test:

```csharp
link.Left.Value = 55m;

runtime.Materialize(link);
runtime.Materialize(link);
runtime.Materialize(link);
await db.SaveChangesAsync();
```

Required assertions after counter reset:

```text
Ratio logical calculator executed once
NormalizedRatio logical calculator executed once if separately instrumented
Ratio physical mirror written once
NormalizedRatio physical mirror written once
SaveChanges did not rewrite already-equal mirrors
runtime committed once after SQL
```

If EF itself invokes setters during materialization, reset the counter only after loading the entity.

---

# 15. Targeted Materialize is already implemented — prove it

The current implementation added EF-aware targeted materialization. Do not leave it untested.

Expose the test fixture's `ratio` derived handle internally to the test.

Scenario:

```csharp
link.Left.Value = 55m;

var value = runtime.Materialize(ratio, link);
```

Before SaveChanges assert:

```text
value == 5.5
link.Ratio == 5.5
link.NormalizedRatio still has old physical value
runtime.Version unchanged
```

Then call ordinary `SaveChangesAsync`.

After SaveChanges assert:

```text
Ratio persisted current
NormalizedRatio persisted current through save-boundary materialization
runtime committed once
no duplicate Ratio recomputation
```

Preserve the established targeted T1 physical-scope semantics.

---

# 16. Verify interceptor/session DI boundary with a real path

Do not redesign DI merely because it looks unusual.

The interceptor currently obtains the session through the DbContext infrastructure path. Prove the behavior by executing it.

Add/keep a test that:

```text
creates a real ServiceCollection
uses AddDbContext<TContext>
uses AddRaffinertConsistency<TContext>
BuildServiceProvider with ValidateScopes=true and ValidateOnBuild=true
resolves ONLY the DbContext first
never explicitly resolves ConsistencyRuntime or the EF session
queries and mutates an entity
calls ordinary SaveChangesAsync
```

Expected:

```text
interceptor runs
session/runtime are obtained correctly
mirrors persist
runtime is committed
no missing-service exception
```

Then resolve `ConsistencyRuntime` after save and prove it is the same scoped instance used by the session.

If this test fails because `DbContext.GetService<ConsistencyEfCoreSession<TContext>>()` cannot resolve the application-scoped session, stop and fix the DI bridge. Do not suppress the test and do not replace ordinary SaveChanges with a custom save API.

If it passes, do not redesign the bridge in this task.

---

# 17. Preserve all existing persistence policies

The fix must continue to route through existing production machinery:

```text
ConsistencyStoreSideEffectGuard
ConsistencyGeneratedValueGuard
ConsistencyPersistencePolicyEngine
ConsistencyScope validation
ExternalConsumerDiscovery
ConsistencyUnitOfWork.Prepare
PlanDetailed
PreparedImpactPlan
MaterializationRollback
Commit after database success
Dispatch after commit
```

Add focused injected-runtime tests for at least:

```text
invariant violation blocks SQL and does not commit runtime
incomplete consistency scope still throws
ordinary save generated-value guard still behaves as before
```

Do not rewrite those engines.

The purpose is to prove the new injected session did not bypass them.

---

# 18. Materialization failure and SQL failure remain distinct

Keep these semantics distinct:

```text
materialization setter failure before SQL
    -> physical writes roll back according to O2/F2 policy
    -> committed runtime unchanged
    -> no SQL attempted

SQL failure after successful pre-save materialization
    -> pending plan not committed
    -> configured materialization rollback restored
    -> no policy dispatch
    -> next attempt builds a fresh plan
```

Add at least one setter-failure regression through the injected EF path if no existing test already covers this exact path.

Do not weaken the existing Core materialization rollback semantics.

---

# 19. Library-owned sink writes must remain excluded

Preserve the existing `_ownedWrites` / descriptor-based exclusion behavior.

After:

```csharp
runtime.Materialize(link);
```

these EF Modified properties:

```text
Link.Ratio
Link.NormalizedRatio
```

must not appear as new independent semantic input mutations merely because Raffinert wrote them.

Strengthen the test so it proves both:

```text
semantic calculator count does not increase at SaveChanges
pending fingerprint is considered unchanged
```

Do not exclude an application write merely because it targets the same property after Raffinert wrote it.

If application code changes a mirror property to a different value after `Materialize`, the fingerprint/rebuild logic must not silently hide it. Existing mirror-dependency rules still apply; treat this as an external physical overwrite, not as evidence that the logical graph changed unless current library policy says otherwise.

---

# 20. Multi-DbContext registration: fail safely for now

Current public extension is generic:

```csharp
AddRaffinertConsistency<TDbContext>(...)
```

but it registers unqualified singleton model/mappings/options and unqualified scoped `ConsistencyRuntime`.

Do not pretend that multiple different Raffinert-bound DbContexts in one `IServiceCollection` are safely supported if they are not.

For this task choose the conservative policy:

> Exactly one Raffinert EF integration registration is supported per service collection.

Add an internal registration marker and reject a second call to `AddRaffinertConsistency<...>` with a clear `InvalidOperationException`.

Required tests:

```text
first registration succeeds
same TDbContext registered twice -> rejected clearly
two different TDbContext registrations -> rejected clearly
```

Do not implement multi-context keyed runtimes in this task.

Do not add static/global process state; the marker belongs to the `IServiceCollection` registration set.

Document this current limitation briefly in EF docs/README only after tests pass.

---

# 21. Do not change Core public API

This task should not add new public Core methods.

Keep:

```csharp
runtime.Evaluate(...)
runtime.Materialize(definition, source)
runtime.Materialize(source)
```

The internal integration seam may evolve as needed.

Any new baseline/touched-source helpers should be `internal`.

Do not expose:

```text
RefreshBaseline
AdmitBaseline
EF session
pending plan
fingerprint
baseline stabilization
```

as application APIs.

---

# 22. Do not change push/pull semantics in this task

This task is only about the existing pull evaluation/materialization architecture and EF binding correctness.

Do not add:

```text
.Push()
eager derived recomputation
INotifyPropertyChanged
proxies
source generators
setter interception
automatic immediate materialization after arbitrary POCO mutation
```

Those are separate design topics.

---

# 23. Required implementation order

Follow this exact order.

## Phase A — write failing regression tests first

Before modifying runtime/session code, add tests that expose:

```text
A1 navigation mutation does not alter committed runtime before SQL
A2 navigation mutation + SQL failure leaves committed runtime unchanged
A3 projection selector mutation does not alter committed runtime before SQL
A4 projection selector mutation + SQL failure leaves committed runtime unchanged
A5 dirty-before-first-runtime-resolution is rejected
```

At least A1/A2 must demonstrably fail against commit `586b2f7` for the expected reason.

If they do not fail, inspect the test; do not proceed with a fake green regression.

## Phase B — extract touched-source evidence

Reuse ChangeTrackerAdapter capture logic.

Add one internal helper to identify directly touched object instances.

No runtime behavior changes yet.

## Phase C — make baseline stabilization conservative

Remove unconditional refresh-after-DetectChanges.

Stabilize only registered mapped roots absent from touched-source evidence.

Re-run A1–A4.

## Phase D — make first binding safe

Before baseline-admitting already tracked objects:

```text
DetectChanges
capture mapped semantic mutations
reject if any exist
```

Then admit clean mapped objects and perform one safe stabilization pass.

Add scalar/navigation/collection dirty-binding tests.

## Phase E — complete missing acceptance coverage

Add:

```text
setter-count test
targeted Materialize test
baseline no-dispatch test
interceptor DI-path test
invariant/scope/generated-value integration tests
```

## Phase F — add single-registration guard

Reject multiple AddRaffinertConsistency registrations.

Add tests.

## Phase G — docs only after code is green

Update:

```text
README.md
docs/ef-core-consistency.md
.agents/skills/raffinert-consistency-consumer/SKILL.md
references/recipes.md
references/verification.md
CHANGELOG.md if appropriate
```

Do not add historical discussion. Describe only current behavior.

---

# 24. Required regression matrix

All of these must pass.

```text
R01 exact LinkService acceptance flow
R02 runtime resolved before query
R03 clean entities tracked before runtime resolution
R04 dirty scalar before runtime resolution rejected
R05 dirty navigation before runtime resolution rejected
R06 dirty collection before runtime resolution rejected
R07 same CLR type / exact ObjectSet identity
R08 duplicate runtime key behavior preserved
R09 baseline admission dispatches no repair/policy work
R10 scalar mutation Materialize leaves runtime version unchanged
R11 navigation mutation Materialize leaves committed nav index unchanged
R12 navigation mutation successful save commits nav index once
R13 navigation mutation failed save leaves committed nav index unchanged
R14 projection selector Materialize leaves committed projection state unchanged
R15 projection failed save leaves committed projection state unchanged
R16 intervening semantic change rebuilds pending plan
R17 repeated Materialize reuses logical plan
R18 repeated Materialize skips equal physical writes
R19 targeted Materialize writes only selected target before save
R20 ordinary SaveChanges without explicit Materialize persists mirrors
R21 library-owned mirror writes do not cause second semantic execution
R22 setter failure rolls back physical mirrors and leaves runtime uncommitted
R23 SQL failure does not commit or dispatch
R24 valid retry after failure succeeds with fresh plan
R25 invariant violation still blocks SQL
R26 scope gap still blocks planning/save
R27 generated-value guard still applies
R28 DbContext-first ordinary SaveChanges resolves interceptor/session/runtime correctly
R29 runtime/context resolve in both orders without cycle
R30 new DI scope gets new runtime/context
R31 second Raffinert EF registration is rejected clearly
```

Do not delete existing tests to make this matrix pass.

---

# 25. Anti-dumb rules

1. **Do not call `runtime.Apply` from `Materialize`.**
2. **Do not mutate committed runtime indexes to mirror current CLR state after a semantic mutation.**
3. **Do not use `RefreshBaseline` as mutation propagation.**
4. **Do not remove all baseline stabilization without preserving EF query/fixup correctness.**
5. **Capture semantic mutation evidence before deciding which baselines are safe to stabilize.**
6. **Use reference identity for tracked object instances.**
7. **Preserve exact ObjectSet identity; same CLR type is not enough.**
8. **Do not silently baseline-admit dirty tracked entities.**
9. **Do not reconstruct historical state by temporarily mutating CLR objects.**
10. **Do not invent a second change tracker. Reuse ChangeTrackerAdapter.**
11. **Do not bypass scope validation.**
12. **Do not bypass generated-value guards.**
13. **Do not bypass external-consumer discovery.**
14. **Do not commit pending runtime state before SQL success.**
15. **Do not dispatch repair/policy work before SQL success.**
16. **Do not make Materialize flush unrelated objects physically.**
17. **Do not downgrade targeted T1 materialization semantics.**
18. **Do not treat Raffinert-owned sink writes as fresh domain mutations.**
19. **Do not hide an application mutation merely because the same member was previously written by Raffinert.**
20. **Do not add public Core APIs for test convenience.**
21. **Do not add push/eager semantics in this task.**
22. **Do not add multi-DbContext support; reject it safely for now.**
23. **Do not modify PublicAPI baselines before source/API changes are final.**
24. **Do not rewrite unrelated persistence architecture.**
25. **Do not declare success without running the full EF integration suite.**

---

# 26. Stop conditions — report instead of guessing

Stop and report if any of these happens:

```text
S1 fixing baseline safety appears to require committing runtime state before SQL
S2 navigation correctness seems to require unconditional refresh of touched roots
S3 projection correctness cannot be preserved without a new public API
S4 dirty late binding cannot be detected using existing EF evidence
S5 ChangeTrackerAdapter cannot provide enough mutation evidence without major redesign
S6 the injected ordinary SaveChanges path cannot resolve the scoped session/runtime through EF DI
S7 generated-value handling would need new transaction semantics
S8 scope/external consumer discovery conflicts with pending-plan reuse
S9 targeted Materialize cannot remain T1 without changing Core semantics
S10 fixing the issue requires temporary mutation of user CLR objects to original values
S11 tests require weakening an existing invariant/repair/materialization guarantee
S12 a second DbContext registration cannot be rejected without breaking normal single-context registration
S13 a regression test intended to expose 586b2f7 remains green for reasons you cannot explain
S14 full tests reveal unrelated failures requiring broad cleanup
```

Do not work around a stop condition with a hidden special case.

---

# 27. Full verification commands

Run the repository's normal restore/build/test/format/package validation exactly as CI expects.

At minimum execute the equivalent of:

```text
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Also run focused EF tests repeatedly during implementation.

If the repository has formatting, package validation, PublicAPI validation, .NET 8 package-consumer tests, or sample build checks, run them too.

Do not report “all tests pass” unless you actually ran them on the final exact commit.

Record the exact final commit SHA and commands/results in the agent completion report.

---

# 28. Definition of Done

This task is complete only when all statements below are true.

```text
[ ] LinkService still injects only DbContext + ConsistencyRuntime.
[ ] LinkService still uses exactly one explicit Raffinert pre-save call: Materialize(link).
[ ] Ordinary SaveChangesAsync remains the persistence API.
[ ] Materialize builds/evaluates from a pending binding plan.
[ ] Materialize does not advance runtime Version for the domain mutation.
[ ] Materialize does not silently rebase changed navigation indexes.
[ ] Materialize does not silently rebase changed projection indexes.
[ ] Failed SQL leaves committed navigation/projection runtime state unchanged.
[ ] Successful SQL commits the exact planned navigation/projection changes once.
[ ] Clean late runtime binding remains supported.
[ ] Dirty late runtime binding is rejected explicitly.
[ ] Query/fixup baseline stabilization still works.
[ ] Baseline admission emits no repair/policy dispatch.
[ ] Repeated Materialize reuses logical work and skips equal physical writes.
[ ] Targeted Materialize remains EF-aware and T1-scoped.
[ ] Library-owned mirror writes are excluded from semantic recapture.
[ ] Intervening application mutations rebuild stale pending plans.
[ ] Invariant, scope, generated-value, and external-discovery policies remain active.
[ ] SQL failure clears/discards pending state safely.
[ ] Retry builds a fresh plan and succeeds.
[ ] One Raffinert EF registration per IServiceCollection is enforced clearly.
[ ] No new public Core API was added for this fix.
[ ] No push/eager feature was mixed into this work.
[ ] Existing tests were preserved.
[ ] New R01-R31 regression matrix is covered.
[ ] Full final test suite passes on the exact final commit.
```

---

# 29. Final mental model

Before SQL success there are two different worlds and they must stay different:

```text
CURRENT CLR / EF TRACKED STATE
    may contain proposed changes
    may contain newly materialized mirrors

            │ Prepare + Plan
            ▼

PENDING CONSISTENCY PLAN
    understands proposed changes
    can evaluate current logical values
    can drive pre-save Materialize

            X  no commit yet

COMMITTED CONSISTENCY RUNTIME
    still represents durable/pre-save state
```

Only after successful SQL:

```text
PENDING PLAN
    ↓ Commit
COMMITTED RUNTIME
    ↓ Dispatch
policy / repair effects
```

`RefreshBaseline` is allowed only to stabilize proven baseline state. It is never a shortcut for applying a domain mutation.

That distinction is the entire purpose of this follow-up.