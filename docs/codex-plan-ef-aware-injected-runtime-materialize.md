# Codex plan — EF-aware injected runtime and one-call `Materialize`

Status: **IMPLEMENTATION PLAN — DO NOT REDESIGN THE UX**

Audience: a weak coding agent. Follow this document literally and in order.

This plan is intentionally narrow. Its purpose is to make the EF Core integration feel like normal application code instead of exposing Raffinert infrastructure inside services.

The target application experience is fixed:

```csharp
public sealed class LinkService(
    AppDbContext db,
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

That example, or a mechanically equivalent version of it using the neutral domain defined below, **must compile and pass as a real integration test**.

The application service must not need:

```text
ObjectSet<T>
ConsistencyEfCoreMappings
RuntimeSeedBuilder
Change.Property(...)
runtime.Add(...)
runtime.Apply(...)
old property values
SaveChangesConsistentlyAsync(...)
manual runtime registration
manual mutation capture
manual consistency unit-of-work handling
```

The only Raffinert dependency in the service is:

```csharp
ConsistencyRuntime consistency
```

The only explicit pre-save Raffinert call required when the service needs materialized properties immediately is:

```csharp
consistency.Materialize(link);
```

Normal persistence remains:

```csharp
await db.SaveChangesAsync(cancellationToken);
```

---

# 1. Neutral acceptance domain

Do not use purchase orders, invoices, goods receipts, accounting, or any Semine-specific concept in this implementation or its acceptance tests.

Use this deliberately neutral model:

```csharp
public sealed class Item
{
    public long Id { get; set; }
    public decimal Value { get; set; }
}

public sealed class Link
{
    public long Id { get; set; }

    public long LeftId { get; set; }
    public Item Left { get; set; } = null!;

    public long RightId { get; set; }
    public Item Right { get; set; } = null!;

    public decimal? Ratio { get; set; }
    public decimal? NormalizedRatio { get; set; }
}
```

Use a calculation such as:

```csharp
static decimal? CalculateRatio(Link link)
{
    if (link.Right.Value == 0)
        return null;

    return link.Left.Value / link.Right.Value;
}

static decimal? CalculateNormalizedRatio(Link link, decimal? ratio)
{
    if (ratio is null)
        return null;

    if (ratio == 0)
        return 0;

    return ratio >= 1
        ? ratio
        : 1 / ratio;
}
```

The exact arithmetic is not the feature. The dependency chain is:

```text
Left.Value ─┐
            ├──> Ratio ──> NormalizedRatio
Right.Value ┘
```

Both derived values are materialized onto `Link`:

```text
logical Ratio            -> link.Ratio
logical NormalizedRatio  -> link.NormalizedRatio
```

---

# 2. Required model declaration

The acceptance model must use the current API only.

Conceptually:

```csharp
var items = model
    .Objects<Item>()
    .Key(x => x.Id)
    .Named("items");

var links = model
    .Objects<Link>()
    .Key(x => x.Id)
    .Named("links");

var ratio = model
    .Derived(links)
    .DependsOn(
        x => x.Left.Value,
        x => x.Right.Value)
    .Select(CalculateRatio)
    .MaterializeTo(x => x.Ratio)
    .Named("ratio");

var normalizedRatio = model
    .Derived(links)
    .From(ratio)
    .Select((link, value) => CalculateNormalizedRatio(link, value))
    .MaterializeTo(x => x.NormalizedRatio)
    .Named("normalized-ratio");
```

If exact builder spelling differs slightly because of the current production API, use the current production spelling, but preserve the semantics above.

Do not add application-service calls to compensate for integration weaknesses.

---

# 3. Core architectural rule

`Raffinert.Consistency` must remain independent of Entity Framework Core.

Do **not** add a reference from the Core project to:

```text
Microsoft.EntityFrameworkCore
Raffinert.Consistency.EntityFrameworkCore
DbContext
ChangeTracker
EntityEntry
```

EF-specific synchronization belongs in `Raffinert.Consistency.EntityFrameworkCore`.

If Core needs an integration seam so that `ConsistencyRuntime.Materialize(source)` can delegate to an attached EF session, define a small EF-agnostic internal hook/participant interface in Core.

The hook must not expose EF types.

Conceptual shape only:

```csharp
internal interface IRuntimeMaterializationIntegration
{
    bool TryMaterializeObject(
        ConsistencyRuntime runtime,
        object source);
}
```

The exact name may follow repository conventions.

Important semantics:

```text
no integration attached
    -> existing pure-Core Materialize behavior

EF integration attached and handles the source
    -> EF session synchronizes tracked changes and materializes from the pending plan
```

Do not make Core call `DbContext` directly.

---

# 4. Why a simple `ChangeTracker -> runtime.Apply(...)` inside `Materialize` is forbidden

Do not implement this shortcut:

```text
Materialize(link)
    -> DetectChanges
    -> runtime.Apply(...)
    -> write mirrors
    -> later SaveChanges
```

That would advance committed runtime state **before** the database operation succeeds.

If SQL later fails, the database is unchanged while the runtime has already committed the new semantic state.

This plan requires:

```text
EF tracked mutation
      ↓
prepare exact pending consistency plan
      ↓
materialize from that pending plan
      ↓
NO committed runtime advance yet
      ↓
SaveChanges
      ↓
SQL succeeds
      ↓
commit exact pending plan into runtime
      ↓
dispatch once
```

The existing prepared/binding plan infrastructure must be reused.

Do not create a second consistency engine.

---

# 5. Reuse existing production machinery

Before editing code, inspect and map the implementation to these existing components:

```text
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.Materialization.cs
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
src/Raffinert.Consistency/Runtime/RuntimeSeed.cs
src/Raffinert.Consistency.EntityFrameworkCore/ChangeTrackerAdapter.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyPersistencePolicy.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySaveChangesInterceptor.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreMappings.cs
```

The implementation must reuse:

```text
ChangeTracker capture
ConsistencyUnitOfWork
Prepare(...)
PlanDetailed(...)
PreparedImpactPlan
planned derived evaluations
materialization descriptors
materialization rollback
post-database Commit(...)
post-commit Dispatch(...)
existing consistency scope validation
existing generated-value guards
existing external-consumer discovery
```

Do not bypass these features in the new Materialize path.

---

# 6. Required scoped EF session

Introduce one scoped EF integration session per `TDbContext` + runtime pair.

Suggested conceptual name:

```text
ConsistencyEfCoreSession<TDbContext>
```

It may be internal.

It must own only integration/session state, not duplicate consistency graph state.

It must know:

```text
DbContext
ConsistencyRuntime
ConsistencyEfCoreMappings
ConsistencySaveOptions
pending captured semantic changes, if any
pending prepared/binding plan, if any
physical materialization writes made before SaveChanges, if any
```

It must support:

```text
bind runtime to this DbContext
observe tracked entities entering the context
prepare/reuse a pending plan
materialize one requested source from the pending plan
prepare normal SaveChanges
complete after successful SaveChanges
clear/rollback after failed SaveChanges
```

The session itself must be scoped.

Do not make it singleton.

---

# 7. DI lifetime contract

Required lifetime model:

```text
CompiledConsistencyModel     Singleton
ConsistencyEfCoreMappings   Singleton
ConsistencySaveOptions      Singleton or immutable registration configuration
AppDbContext                 Scoped
ConsistencyEfCoreSession    Scoped
ConsistencyRuntime           Scoped
SaveChanges interceptor      Scoped
```

The runtime injected into an application service and the runtime used by the EF integration for the same scope must be the **same object instance**.

Add an integration registration API in the EF package.

A reasonable target is conceptually:

```csharp
services.AddRaffinertConsistency<AppDbContext>(
    compiledModel,
    mappings,
    new ConsistencySaveOptions());
```

The exact registration overload may follow project conventions.

If a public DI extension uses `IServiceCollection`, add an explicit package dependency on the DI abstractions package if needed. Do not rely accidentally on an unrelated transitive package for a public API type.

The application service must not inject compiled model, mappings, options, session, or interceptor.

---

# 8. Avoid a DbContext/runtime construction cycle

Be careful here.

A naive graph can create:

```text
DbContext
  -> interceptor
      -> ConsistencyRuntime
          -> DbContext
```

Do not ship that cycle.

Use this safe shape:

```text
DbContext
  -> interceptor
      -> lightweight scoped EF session

ConsistencyRuntime scoped factory
  -> resolves the already-scoped DbContext
  -> creates runtime from compiled model
  -> binds runtime + DbContext into EF session
```

The interceptor constructor must not require the runtime eagerly if that creates the cycle.

If the interceptor needs the runtime during `SavingChanges`, resolve/get it lazily through the scoped session only after the `DbContext` instance already exists.

Add a DI test that resolves both:

```csharp
AppDbContext
ConsistencyRuntime
```

from one scope in both orders and proves the same session/runtime binding works:

```text
resolve DbContext first, then runtime
resolve runtime first, then DbContext
```

No circular dependency exception is allowed.

---

# 9. Automatic baseline registration of EF-tracked entities

This is release-critical.

Today `ConsistencyRuntime.Materialize(source)` requires the source to already be registered in the runtime.

The application service must not call `runtime.Add(...)`.

Therefore the EF session must automatically make mapped tracked entities available as runtime baseline objects.

Do not wait until after the entity is mutated and then blindly bootstrap from its current CLR property values. That can build a false baseline.

The preferred strategy is:

```text
1. When the scoped runtime is bound to the DbContext, inspect already tracked entries.
2. Subscribe to ChangeTracker.Tracked for entities tracked later by queries/Attach.
3. Baseline-admit mapped entries into the runtime before application mutation occurs.
4. Keep exact object-set identity from ConsistencyEfCoreMappings.
```

You may need a small **internal Core primitive** for baseline admission of one mapped instance without treating initial EF tracking as a business mutation.

Do not expose that primitive as application API unless absolutely necessary.

Do not simulate baseline admission by calling public `runtime.Apply(...)` and dispatching policies.

Baseline tracking is not a domain change.

Required tests:

```text
- runtime created before query; Include query later tracks Item + Link entities; Materialize works
- entities already tracked before runtime resolution; binding admits them; Materialize works
- same CLR type mapped to two object sets preserves exact set identity
- duplicate runtime key is still rejected correctly
- tracked baseline admission does not emit repair/policy callbacks
```

---

# 10. EF-aware `Materialize(source)` behavior

When an EF-bound runtime receives:

```csharp
consistency.Materialize(link);
```

perform these logical steps:

```text
1. Validate runtime/session/DbContext ownership.
2. DbContext.ChangeTracker.DetectChanges().
3. Capture current EF semantic mutations using existing mappings.
4. Run existing scope validation / generated-value validation / store-side safety rules that are applicable before planning.
5. Discover required external consumers using existing machinery.
6. Build one ConsistencyUnitOfWork.
7. Prepare it against the committed runtime.
8. Create a binding PreparedImpactPlan with affected derived evaluation enabled.
9. Leave committed runtime state unchanged.
10. Find materialization descriptors physically owned by the requested source object.
11. Read the planned Fresh derived values for those descriptors from the plan.
12. Write only those physical mirrors on the requested object.
13. Mark those EF properties modified when values actually changed.
14. Record enough information for retry/rebuild/failure handling.
15. Store the pending unit/plan in the scoped EF session for SaveChanges reuse.
```

Do not call the ordinary Core materializer afterwards against stale committed runtime state.

The EF integration hook must either:

```text
handle the object materialization fully
```

or decline and allow normal Core behavior.

Do not execute both.

---

# 11. Physical scope remains the requested object

Explicit:

```csharp
consistency.Materialize(link);
```

must physically synchronize materialization targets **on `link` only**.

It may evaluate/traverse dependencies on `Left`, `Right`, other logical upstream nodes, projections, or relations as needed.

It must not eagerly write materialized properties on unrelated tracked objects merely because they are also affected by the same pending plan.

This preserves the established object-level Materialize contract:

> make configured materialized representations on this object current

not:

> flush every materialization in the entire DbContext

Normal `SaveChanges` may later materialize all remaining affected persisted representations required for persistence correctness.

---

# 12. Pending plan reuse

After:

```csharp
link.Left.Value = 55;
consistency.Materialize(link);
```

there must be a pending binding plan in the EF session.

Then:

```csharp
await db.SaveChangesAsync();
```

must reuse that exact pending plan **if the semantic EF mutation set has not changed**.

Do not rerun calculators, predicates, classifiers, or dependency propagation merely because SaveChanges is called.

This preserves binding-plan semantics and avoids double work.

Required test:

```text
- instrument a derived calculator or impact classifier with a counter
- Materialize(link) prepares once
- SaveChangesAsync with no intervening semantic changes reuses the plan
- counter does not indicate a second semantic execution
```

---

# 13. Detect changes after explicit Materialize

Application code may do this:

```csharp
link.Left.Value = 55;
consistency.Materialize(link);

link.Left.Value = 50;

await db.SaveChangesAsync();
```

The stored pending plan is now stale.

The interceptor must not blindly commit the old plan.

Before reusing a pending plan at SaveChanges:

```text
DetectChanges
recapture semantic EF mutations
compare them with the semantic mutation evidence used by the pending plan
```

If they differ:

```text
- invalidate/discard the pending plan
- prepare a new exact plan from the committed runtime
- materialize all required persisted mirrors using the new plan
- save using the new plan
```

The final persisted values must correspond to `50`, not `55`.

Do not try to patch a binding plan in place.

Rebuild it.

---

# 14. Library-owned materialization writes are not new semantic inputs

After `Materialize(link)`, EF sees `link.Ratio` and `link.NormalizedRatio` as modified properties.

Those writes were produced by Raffinert itself from `MaterializeTo` descriptors.

When the EF session recaptures semantic mutations before SaveChanges, do not accidentally treat those library-owned sink writes as independent domain input changes.

The integration must distinguish:

```text
application semantic input mutation
    Left.Value = 55

library-owned representation write
    Link.Ratio = 5.5
    Link.NormalizedRatio = 5.5
```

Use the materialization descriptors and/or session-owned write journal to exclude sink writes from semantic-change fingerprinting/planning.

Do not introduce a dependency edge from a materialized mirror back into its own logical graph.

Required test:

```text
Materialize(link)
SaveChangesAsync()
```

must not produce an extra second semantic mutation solely because the mirror properties became Modified.

---

# 15. Ordinary SaveChanges without explicit Materialize

This must continue to work:

```csharp
link.Left.Value = 55;

await db.SaveChangesAsync(cancellationToken);
```

The SaveChanges interceptor must:

```text
capture tracked semantic changes
prepare/plan
validate
materialize all required affected persisted representations
execute SQL
commit exact plan into runtime
then dispatch
```

Explicit `Materialize(link)` is only necessary when application code needs the materialized CLR properties **before** SaveChanges.

Do not require the service to call a Raffinert-specific SaveChanges method.

---

# 16. SaveChanges success semantics

On successful database persistence:

```text
1. SQL completes successfully.
2. Commit the exact pending binding plan into the scoped runtime.
3. Advance runtime state exactly once.
4. Dispatch policy/repair work exactly once.
5. Clear pending session state.
6. Keep the materialized CLR properties at their current persisted values.
```

Required assertions:

```text
runtime Version before Materialize == runtime Version after Materialize
runtime Version advances only after successful SaveChanges
policy callback count before SaveChanges == 0
policy callback count after successful SaveChanges == expected once
```

Baseline admission may have its own internal bookkeeping, but the domain mutation represented by `Left.Value` must not be committed before SQL succeeds.

---

# 17. SaveChanges failure semantics

Reuse the existing library policy where possible.

At minimum, after database failure:

```text
committed runtime must still represent pre-save state
pending binding plan must not be committed
policy/repair work must not dispatch
pending session state must be cleared or faulted safely
subsequent retry must not reuse a poisoned plan
```

Preserve the current physical materialization rollback contract unless changing it is necessary and separately justified by tests.

Do not silently invent new transaction semantics in this task.

Required test:

```text
- mutate Left.Value
- Materialize(link)
- force SaveChanges failure
- runtime semantic version/state for this mutation remains uncommitted
- no repair callback dispatches
- next valid attempt rebuilds a fresh plan and can succeed
```

---

# 18. Repeated Materialize behavior

This sequence must be safe:

```csharp
link.Left.Value = 55;

consistency.Materialize(link);
consistency.Materialize(link);
consistency.Materialize(link);
```

If tracked semantic inputs did not change:

```text
- reuse the same pending plan where practical
- do not dispatch
- do not commit runtime
- do not rewrite equal mirror values unnecessarily
- do not execute expensive logical computation repeatedly
```

Add calculator-count and setter-count assertions.

---

# 19. Targeted `Materialize(definition, source)`

Do not break existing targeted materialization.

If straightforward, make targeted materialization EF-aware through the same session infrastructure.

Semantics:

```text
EF-bound runtime + targeted Materialize
    -> synchronize ChangeTracker into pending plan
    -> physically write only that one descriptor on the requested source
    -> no runtime commit before SQL
```

However, object-level `Materialize(source)` is the release-critical requirement for this plan.

If targeted EF-aware integration requires a major separate redesign, stop and report it instead of weakening object-level correctness.

---

# 20. Exact acceptance service test

Create a real test class containing an application-style service that looks like this:

```csharp
public sealed class LinkService(
    AppDbContext db,
    ConsistencyRuntime consistency)
{
    public decimal? ObservedRatio { get; private set; }
    public decimal? ObservedNormalizedRatio { get; private set; }

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

    private void Use(decimal? value)
    {
        if (ObservedRatio is null)
            ObservedRatio = value;
        else
            ObservedNormalizedRatio = value;
    }
}
```

You may implement `Use` more cleanly, for example with two explicit assignments, but **do not add any other Raffinert calls to this service**.

The integration test must resolve the service through Microsoft DI, not construct it manually with hidden helpers.

Test scenario:

```text
Database initial state:
    Left.Value  = 60
    Right.Value = 10
    Link.Ratio = 6
    Link.NormalizedRatio = 6

Resolve LinkService from DI.

Call:
    await service.ChangeLeftValueAsync(linkId, 55, ct)

Inside service after consistency.Materialize(link), before SaveChanges:
    link.Ratio == 5.5
    link.NormalizedRatio == 5.5
    service observed both current values

After SaveChanges:
    database Left.Value == 55
    database Link.Ratio == 5.5
    database Link.NormalizedRatio == 5.5

Runtime:
    mutation was committed only after database success
    no manual Add/Apply was required
```

This test is the primary definition of done.

Do not replace it with a lower-level coordinator test.

Keep lower-level tests too, but this exact application shape must execute end to end.

---

# 21. DI acceptance test

Add a test that proves the application-facing constructor is exactly simple:

```csharp
public LinkService(
    AppDbContext db,
    ConsistencyRuntime consistency)
```

No fixture-only constructor overload may inject:

```text
compiled model
object sets
mappings
session
interceptor
unit of work
```

Resolve it from a real `ServiceCollection` / `ServiceProvider` scope.

Assert:

```text
service DbContext is the scoped AppDbContext
service runtime is the same scoped runtime used by the EF session/interceptor
new scope gets a different runtime
new scope gets a different DbContext
singleton compiled model/mappings are reused
```

---

# 22. Baseline admission acceptance test

The runtime is injected when `LinkService` is created, before the query runs.

Therefore explicitly prove this timeline:

```text
scope created
LinkService resolved
ConsistencyRuntime already exists
DbContext currently tracks zero domain entities

service query executes
EF tracks Left, Right, Link
EF session baseline-admits them automatically

service mutates Left.Value
service calls consistency.Materialize(link)
Materialize succeeds
```

If this test requires application code to register entities, the implementation is wrong.

---

# 23. Intervening-change acceptance test

Use the same neutral domain:

```csharp
link.Left.Value = 55;
consistency.Materialize(link);

Assert.Equal(5.5m, link.Ratio);

link.Left.Value = 50;

await db.SaveChangesAsync();
```

Expected persisted result:

```text
Left.Value = 50
Ratio = 5
NormalizedRatio = 5
```

The old pending 5.5 plan must not be committed.

---

# 24. Save-without-explicit-materialize acceptance test

Also prove:

```csharp
link.Left.Value = 55;
await db.SaveChangesAsync();
```

Expected database result:

```text
Left.Value = 55
Ratio = 5.5
NormalizedRatio = 5.5
```

This ensures ordinary EF persistence remains ergonomic.

---

# 25. Runtime commit timing test

Capture runtime state immediately before mutation.

Then:

```csharp
link.Left.Value = 55;
consistency.Materialize(link);
```

Assert:

```text
materialized properties are current
runtime has NOT committed the EF mutation yet
repair/policy callbacks have NOT dispatched
```

Then call:

```csharp
await db.SaveChangesAsync();
```

Assert:

```text
runtime commits exact pending plan
runtime state/version reflects persisted mutation
callbacks dispatch once
```

Do not use only mirror values as proof. Inspect runtime version/state/diagnostics or a known downstream derived state.

---

# 26. Transaction/failure tests

Keep existing transaction safety rules.

Add tests for:

```text
SaveChanges throws before SQL success
SaveChanges database constraint failure
existing unsupported external/ambient transaction behavior
```

In all failed-save cases:

```text
pending EF plan is not committed
policy callbacks are not dispatched
session does not retain a reusable poisoned plan
```

---

# 27. Same-runtime proof

It is not enough that application runtime and interceptor runtimes are equivalent.

They must be reference-equal in one scope.

Test:

```csharp
var runtimeFromService = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
var session = /* internal test access */;

Assert.Same(runtimeFromService, session.Runtime);
```

The SaveChanges interceptor must commit into that same runtime instance.

Do not create a hidden second runtime for persistence.

---

# 28. No global model scan per Materialize

Do not degrade object materialization lookup into reflection or full-model scans.

Use existing compiled materialization indexes and planned derived evaluations.

The explicit Materialize path should roughly be:

```text
capture/plan affected graph
lookup descriptors owned by exact source membership
lookup planned value by derived id + source
write changed targets
```

Do not search all exported types, all properties, or all derived definitions by CLR reflection on every call.

---

# 29. Core non-EF regression

Pure Core behavior must remain valid:

```csharp
var runtime = compiled.CreateRuntime(...);
runtime.Materialize(source);
```

with no EF package/session attached.

Add/regress tests proving:

```text
existing object Materialize behavior still works
existing targeted Materialize behavior still works
O2 evaluate-before-write behavior remains
physical rollback behavior remains
runtime-only definitions remain ignored by object Materialize
repair remains separate
```

The new EF hook must be optional.

---

# 30. Public API discipline

Do not make the application-facing API larger than needed.

Expected additions are approximately:

```text
one DI registration surface in EF package
possibly one options/configuration type if truly needed
```

Prefer internal session/bridge types.

Do not expose:

```text
PendingEfPlan
EfRuntimeSession internals
plan fingerprints
materialization journals
baseline-admission internals
```

unless there is a concrete external-use requirement.

Update `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` deliberately.

---

# 31. Update EF sample after tests pass

After implementation tests are green, update:

```text
samples/Raffinert.Consistency.EntityFrameworkCore.Sample
```

The sample's application code should demonstrate the same shape:

```csharp
entity.Input = newValue;
runtime.Materialize(entity);
Use(entity.MaterializedValue);
await db.SaveChangesAsync();
```

No manual Add/Apply plumbing in the application service example.

---

# 32. Update docs and consumer skill after code is proven

After all tests pass, update current-state documentation:

```text
README.md
docs/ef-core-consistency.md
.agents/skills/raffinert-consistency-consumer/SKILL.md
.agents/skills/raffinert-consistency-consumer/references/recipes.md
.agents/skills/raffinert-consistency-consumer/references/verification.md
```

Document the simple EF rule:

```text
Need a materialized property before SaveChanges?
    runtime.Materialize(entity)

Only saving?
    db.SaveChanges / SaveChangesAsync
```

Do not document manual mutation plumbing as the normal EF application path.

---

# 33. Suggested implementation sequence

Do the work in this order.

## Commit 1 — Core integration seam

```text
- add optional EF-agnostic materialization integration hook
- preserve existing pure-Core Materialize path
- add Core regression tests
```

Suggested message:

```text
core: add materialization integration seam
```

## Commit 2 — scoped EF session + DI registration

```text
- add scoped EF session
- add AddRaffinertConsistency<TDbContext> registration
- bind one scoped runtime to one scoped DbContext
- refactor interceptor construction to avoid DI cycle
- add DI lifetime/order tests
```

Suggested message:

```text
efcore: add scoped runtime integration session
```

## Commit 3 — automatic tracked baseline admission

```text
- bind current tracked entries
- subscribe to tracked entities
- baseline-admit exact mapped set membership
- add query-after-runtime-created tests
```

Suggested message:

```text
efcore: admit tracked entities into scoped runtime baseline
```

## Commit 4 — EF-aware object Materialize

```text
- capture current tracker mutations
- prepare exact pending binding plan
- materialize requested source only
- store pending plan
- do not commit runtime
- add pre-save materialization tests
```

Suggested message:

```text
efcore: make runtime Materialize synchronize tracked state
```

## Commit 5 — SaveChanges pending-plan reuse

```text
- interceptor reuses unchanged pending plan
- detects intervening semantic changes
- rebuilds stale plan
- commits only after SQL success
- dispatches once
```

Suggested message:

```text
efcore: reuse pending materialization plan on SaveChanges
```

## Commit 6 — failure/retry hardening

```text
- database failure behavior
- materialization rollback compatibility
- clear poisoned pending state
- retry tests
```

Suggested message:

```text
efcore: harden pending consistency plan failure handling
```

## Commit 7 — exact application acceptance service

```text
- neutral Item/Link domain
- DI-resolved LinkService
- only DbContext + ConsistencyRuntime injected
- one Materialize(link) call
- ordinary SaveChangesAsync
```

Suggested message:

```text
tests: prove one-call EF materialization service flow
```

## Commit 8 — sample/docs/skill

Suggested message:

```text
docs: show injected EF runtime materialization workflow
```

Every commit should build and tests should pass before continuing.

---

# 34. Mandatory stop conditions

Stop and report evidence instead of inventing hacks if any of these is true:

```text
S1. Existing binding plan cannot provide Fresh planned derived values without committing runtime.
S2. Existing ChangeTracker capture cannot distinguish materialization sink writes from semantic inputs.
S3. Automatic baseline admission cannot be made safe with EF tracking order/fixup.
S4. One scoped runtime cannot safely represent the scoped DbContext tracked baseline.
S5. SaveChanges interceptor cannot reuse a pending plan without violating generated-value handling.
S6. External consumer discovery changes the semantic mutation set after Materialize in a way that cannot be reproduced at Save.
S7. Generated keys/values make the exact acceptance service impossible for ordinary non-generated semantic inputs.
S8. DI registration necessarily creates a DbContext/runtime circular dependency.
S9. Reusing the pending plan would rerun semantic code and violate binding-plan guarantees.
S10. EF-aware Materialize would require Core to reference EF Core.
S11. The only way to make the test pass is to call runtime.Add/runtime.Apply from LinkService.
S12. The implementation would require a hidden second ConsistencyRuntime instance.
```

Forbidden workarounds:

```text
dynamic
reflection scanning of the whole model per call
AsyncLocal global DbContext
static current DbContext
service-locator calls from application code
a second shadow consistency engine
committing runtime before SQL
silently ignoring failed DI/session ownership
```

---

# 35. Definition of done

The task is complete only when every item below is true:

```text
[ ] LinkService injects only AppDbContext + ConsistencyRuntime
[ ] LinkService performs no runtime Add
[ ] LinkService performs no runtime Apply
[ ] LinkService does not know ObjectSet<T>
[ ] LinkService does not know ConsistencyEfCoreMappings
[ ] LinkService does not know old values
[ ] LinkService calls consistency.Materialize(link) once
[ ] Ratio is current before SaveChanges
[ ] NormalizedRatio is current before SaveChanges
[ ] ordinary db.SaveChangesAsync persists input + mirrors
[ ] runtime domain mutation is not committed before SQL success
[ ] runtime commits exact pending plan after SQL success
[ ] dispatch happens exactly once after successful save
[ ] failed save does not commit pending runtime state
[ ] failed save does not dispatch
[ ] retry after failure rebuilds valid pending state
[ ] repeated Materialize without new changes does not repeatedly recompute
[ ] intervening semantic change invalidates/rebuilds pending plan
[ ] library-owned mirror writes are excluded as new semantic inputs
[ ] runtime created before query automatically learns tracked baseline entities
[ ] entities already tracked before runtime resolution are admitted
[ ] exact object-set identity is preserved
[ ] DI resolution has no DbContext/runtime cycle
[ ] runtime used by service and interceptor is the same instance
[ ] new DI scope gets a new runtime and DbContext
[ ] pure-Core Materialize behavior is unchanged
[ ] no Core -> EF package dependency introduced
[ ] public API baselines are updated intentionally
[ ] EF sample demonstrates the simple workflow
[ ] README/docs/skill describe the simple workflow
```

---

# 36. Final UX that must remain visible throughout implementation

If the implementation becomes complicated internally, that complexity stays inside the integration package.

The application developer should still see this:

```csharp
public sealed class LinkService(
    AppDbContext db,
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

If the final implementation requires extra Raffinert plumbing inside this service, the task is **not done**.