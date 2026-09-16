# Codex implementation plan — EF scope preparation with host-owned authoritative loading

Status: **ACTIVE IMPLEMENTATION PLAN**

Baseline commit: `6a9394730bff1dfed19f89bfc031f34518ebde21`

Tasks: **182–189**

## Why this roadmap exists

Raffinert correctly refuses to pretend that an incomplete in-memory runtime is authoritative. `ConsistencyScope.Complete(set)` is a host assertion: the host promises that the runtime contains every object belonging to that object set inside the authoritative boundary for the operation. Raffinert deliberately does not know how to query the application's database, tenant boundary, soft-delete policy, repository abstraction, security filter, or required EF `Include(...)` graph.

That boundary is correct, but the current ergonomics force every host to hand-write the same bootstrap ceremony:

```text
inspect which object sets are required for the active Enforce/Materialize policy
query those sets with application-owned EF queries
make sure the returned instances are tracked by the same DbContext
seed the runtime with those authoritative objects
construct ConsistencyScope.Complete(...) for exactly those sets
then perform domain mutations and SaveChangesConsistently(...)
```

The dependency-maintenance dogfood sample makes this especially visible. Raffinert can say that complete `Association` coverage is required because a nested navigation dependency needs reverse-owner discovery, but only the application knows that its authoritative query is, for example:

```csharp
db.Set<Association>()
    .Include(x => x.SourceItem)
    .Include(x => x.TargetItem)
```

This roadmap adds one **optional EF-side preparation helper**. It does not add database knowledge to Core and it does not make normal save paths perform hidden queries.

The contract is:

```text
Raffinert says WHAT object-set coverage is required.
The host registers HOW to load that authoritative coverage.
The EF helper runs only the required host queries, bootstraps a pristine runtime once,
and returns the ConsistencyScope that can honestly be passed to consistent save.
```

This is a bootstrap convenience feature, not live-runtime reconciliation.

---

# Frozen v1 public API

Add a new EF-side loader registry:

```csharp
public sealed class ConsistencyEfCoreLoaders
{
    public ConsistencyEfCoreLoaders Complete<TEntity>(
        ObjectSet<TEntity> set,
        Func<DbContext, IQueryable<TEntity>> query)
        where TEntity : class;
}
```

Add one async DbContext extension:

```csharp
public static Task<ConsistencyScope> PrepareConsistencyScopeAsync(
    this DbContext context,
    ConsistencyRuntime runtime,
    ConsistencyEfCoreMappings mappings,
    ConsistencyEfCoreLoaders loaders,
    ConsistencySaveBehavior saveBehavior = ConsistencySaveBehavior.RecalculateAndValidate,
    CancellationToken cancellationToken = default);
```

Required use:

```csharp
var runtime = compiled.CreateRuntime();

var loaders = new ConsistencyEfCoreLoaders()
    .Complete(
        associations,
        db => db.Set<Association>()
            .Include(x => x.SourceItem)
            .Include(x => x.TargetItem));

var scope = await db.PrepareConsistencyScopeAsync(
    runtime,
    mappings,
    loaders,
    cancellationToken: cancellationToken);

// Only after scope preparation succeeds:
association.SourceItem.UnitValue = 20m;

await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope },
    cancellationToken);
```

Do not introduce alternative public names in this wave:

```text
HydrateConsistencyAsync
RehydrateRuntimeAsync
LoadMissingDependenciesAsync
SynchronizeRuntimeAsync
ConsistencyRepository
ConsistencyDbLoader
AutoLoad
```

`PrepareConsistencyScopeAsync` is intentionally explicit: the operation prepares the authoritative runtime/scope baseline before domain mutation. It does not promise arbitrary persistence synchronization.

---

# Frozen semantics

## 1. Host owns every query

Raffinert must never invent:

- a `DbSet` query;
- a tenant filter;
- a soft-delete filter;
- an `Include` graph;
- a security boundary;
- a repository call;
- a partition key;
- a database transaction boundary.

`Complete(set, query)` means the host asserts:

> this query returns the complete authoritative membership of this exact `ObjectSet` for the runtime boundary I am establishing, and it loads whatever navigation graph my consistency computations require.

Raffinert executes the query; the application defines it.

## 2. Requirement-driven, not loader-driven

Preparation must derive required object sets only from the EF persistence policy that will actually be used:

```text
Enforced invariant requirements:
    always required for Validate and RecalculateAndValidate

Materialized derived requirements:
    required only for RecalculateAndValidate
```

This must match the existing `ConsistencyEfCoreMappings.GetScopeGaps(...)` policy semantics.

Registered loaders for irrelevant sets must not execute.

If the same set is required for multiple reasons, its loader executes exactly once and the returned scope contains that set once.

## 3. Bootstrap-only in v1

`PrepareConsistencyScopeAsync` is valid only when:

```text
ConsistencyRuntime has no registered objects in any ObjectSet
runtime.Version == 0
DbContext has no Added / Modified / Deleted entries after DetectChanges()
```

If either precondition fails, throw before executing any loader query.

This restriction is intentional.

Do **not** attempt in this roadmap to reconcile an already-live runtime. Live reconciliation would need a separate design for:

- stale runtime-only objects;
- same-key/different-reference identity replacement;
- already-cached derived values;
- relation/navigation/projection index replacement;
- whether reconciliation should emit impact policies, callbacks, repairs, or durable work;
- concurrent/domain mutations while loading.

Do not fake this by calling normal `runtime.Add/Remove` on an active runtime.

## 4. Preparation is side-effect-free from the consistency-policy perspective

Preparation establishes a baseline. It must not behave as business mutation.

Use the existing runtime bootstrap path (`RuntimeSeedEntry` + `ConsistencyRuntime.Bootstrap(...)`) after all loader results have been staged and validated.

Do not use:

```text
runtime.Add(...)
runtime.Remove(...)
runtime.Apply(...)
PreparedImpactPlan
policy dispatch
repair callbacks
```

for scope preparation.

Successful preparation must leave:

```text
runtime.Version == 0
no policy requests dispatched
no repair callbacks invoked
diagnostics reset as normal bootstrap currently does
```

## 5. Runtime bootstrap is atomic with respect to loader results

Do not bootstrap one set at a time.

Required sequence:

```text
validate arguments
DetectChanges
validate clean DbContext
validate pristine runtime
calculate all required sets
validate every required set has a registered loader
validate loader ownership/model compatibility
execute every required loader sequentially into memory
validate every returned root instance is tracked by this exact DbContext
build all RuntimeSeedEntry values
call runtime.Bootstrap(entries) exactly once
construct and return ConsistencyScope.Complete(...) for every successfully prepared required set
```

If a loader is missing, do not execute any query.

If a query throws/cancels or returned data fails validation, do not call `Bootstrap` and do not return a scope.

It is acceptable that successfully executed EF queries remain tracked in the DbContext if a later query fails. The runtime must remain untouched.

## 6. Tracking is required

The loader query must return entities tracked by the exact `DbContext` passed to `PrepareConsistencyScopeAsync`.

Reject `AsNoTracking()` results.

For every returned root entity, verify by reference that the context has a non-detached entry for that entity.

Do not clone entities and do not attach untracked results automatically.

Reason: the later EF mutation capture, materialization, relationship fixup, and runtime dependency routing must operate on the same CLR object identities.

## 7. Query completeness remains a host assertion

Raffinert can prove that:

- a required loader exists;
- its root results are tracked;
- its root set can be bootstrapped without duplicate runtime keys;
- scope requirements were mapped to loaders deterministically.

Raffinert cannot prove that a host query omitted no database rows.

Do not add a second database verification query.

Do not inspect table counts.

Do not claim that `Include(...)` completeness can be generally proven from EF metadata.

## 8. No automatic save-path loading

Do not call `PrepareConsistencyScopeAsync` from:

```text
SaveChangesConsistently
SaveChangesConsistentlyAsync
ConsistencySaveChangesInterceptor
CaptureConsistencyUnitOfWork
SaveChangesAndApply
SaveChangesAndApplyAsync
```

Normal persistence remains fail-closed with `IncompleteConsistencyScopeException` when the caller has not supplied scope coverage.

The preparation helper is an explicit earlier host operation.

---

# Non-negotiable out-of-scope items

Do not implement any of these in Tasks 182–189:

- live-runtime reconciliation;
- incremental "load only associations affected by changed endpoint X" discovery;
- partitioned / tenant-scoped `ConsistencyScope` semantics;
- automatic query generation from dependency paths;
- automatic `Include` generation;
- lazy loading integration;
- repository abstractions;
- loaders for non-EF stores;
- loader execution from interceptors;
- background refresh;
- CDC/change-feed synchronization;
- cross-process runtime invalidation;
- raw SQL/trigger reconciliation;
- synchronous `PrepareConsistencyScope(...)` overload;
- parallel loader execution against one DbContext;
- changes to ordinary `ConsistencyScope.Complete(...)` semantics.

A later roadmap may address partitions or live reconciliation, but this roadmap must not pre-design them into a generic framework.

---

# Task 182 — Add failing EF tests first

Create:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/ConsistencyScopePreparationTests.cs
```

Do not modify product implementation until the tests below exist.

Use small neutral entities and SQLite in-memory. Reuse existing test infrastructure when it reduces code, but keep these tests readable and independent of the dogfood executable.

## 182.1 Required nested-navigation source is loaded and bootstrapped

Model shape:

```text
Association (only Raffinert ObjectSet)
    -> SourceEndpoint.Value
    -> TargetEndpoint.Value
```

Derived value uses explicit or analyzable nested dependencies and is materialized.

Start with:

```text
rows persisted in SQLite
fresh operation DbContext tracking nothing
empty ConsistencyRuntime
```

Register:

```csharp
loaders.Complete(
    associations,
    db => db.Set<Association>()
        .Include(x => x.Source)
        .Include(x => x.Target));
```

Call `PrepareConsistencyScopeAsync`.

Assert:

```text
returned scope satisfies mappings under RecalculateAndValidate
runtime can Get(derived, loadedAssociation)
loaded association/source/target are tracked by the same DbContext
runtime.Version == 0
subsequent direct source mutation + SaveChangesConsistentlyAsync succeeds
tracked/runtime/persisted mirror values agree
```

This is the primary acceptance scenario.

## 182.2 Missing required loader fails before any query

Have two required sets and register only one loader.

Use a counter/side effect in the registered query factory.

Assert:

```text
PrepareConsistencyScopeAsync throws before registered query factory is invoked
runtime remains empty/version 0
```

The exception/message must identify every missing required set deterministically.

## 182.3 Irrelevant registered loader is not executed

Register loaders for a required set and an unrelated set.

Assert unrelated query factory invocation count remains zero.

## 182.4 Same required set for multiple reasons loads once

Construct mappings where the same object set appears in more than one scope requirement/reason.

Assert its loader executes once and scope contains it once.

## 182.5 Save behavior controls materialization requirements

With a materialized derived value but no enforced invariant:

```text
Prepare(..., Validate) -> materialization-only loader is not required/executed
Prepare(..., RecalculateAndValidate) -> loader executes
```

Preserve existing persistence-policy semantics exactly.

---

# Task 183 — Implement the loader registry only

Add a focused file, recommended:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreLoaders.cs
```

Implement exactly the public registry shape:

```csharp
public sealed class ConsistencyEfCoreLoaders
{
    public ConsistencyEfCoreLoaders Complete<TEntity>(
        ObjectSet<TEntity> set,
        Func<DbContext, IQueryable<TEntity>> query)
        where TEntity : class;
}
```

Required behavior:

```text
null set      -> ArgumentNullException
null query    -> ArgumentNullException
same ObjectSet registered twice -> InvalidOperationException
same CLR type in two different ObjectSets -> allowed; registry keys by exact ObjectSet definition identity
```

Internally store entries keyed by `IObjectSetDefinition` reference identity.

Do not key by `Type`.

Do not key only by model-scoped integer ID.

Do not expose `IObjectSetDefinition` publicly.

Recommended internal abstraction:

```csharp
internal interface IConsistencyEfCoreLoader
{
    IObjectSetDefinition Set { get; }
    Task<IReadOnlyList<object>> LoadAsync(DbContext context, CancellationToken cancellationToken);
}
```

A generic implementation may call:

```csharp
await query(context).ToListAsync(cancellationToken)
```

Do not force `AsTracking()` automatically. The host query decides its EF shape; preparation validates the result is tracked and rejects otherwise.

Do not execute any loader in this task yet outside tests that directly validate registration behavior.

---

# Task 184 — Expose the exact internal requirement set from mappings

Primary file:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreMappings.cs
```

The current `GetScopeGaps(...)` correctly exposes public diagnostic gaps, but preparation needs the actual internal `IObjectSetDefinition` instances to match exact loader registrations.

Add an **internal only** helper, suggested shape:

```csharp
internal IReadOnlyList<ScopeRequirement> GetScopeRequirements(
    ConsistencyRuntime runtime,
    ConsistencySaveBehavior saveBehavior)
```

or an internal EF-owned projection carrying the set plus public requirement kind.

Required semantics must be mechanically identical to current `GetScopeGaps(...)`:

```text
start with requirements of every enforced invariant
if saveBehavior == RecalculateAndValidate:
    union requirements of every materialized derived definition
if saveBehavior == Validate:
    do not include materialization-only requirements
```

Deduplicate exact duplicate `(set, reason)` requirements and return deterministic order:

```text
Set.Id ascending
RequirementKind/reason ascending
```

Do not change public scope APIs.

Do not duplicate `ConsistencyScopeRequirementCompiler` logic in EF. Call the existing runtime internal `GetScopeRequirements(...)` methods.

Refactor `GetScopeGaps(...)` to share this aggregation path if doing so makes divergence impossible.

Required regression: all existing scope tests remain green.

---

# Task 185 — Add a safe pristine-runtime bootstrap gate

Core must remain persistence-agnostic. Do not add EF references or public loading APIs to Core.

The EF adapter needs only one extra internal fact: whether the runtime can still safely accept authoritative bootstrap data without treating it as domain mutation.

Add an internal Core helper/property, suggested shape:

```csharp
internal bool CanBootstrapAuthoritativeScope =>
    Version == 0 && _sets.Values.All(set => set.Count == 0);
```

The exact name may differ, but keep it internal.

Do not expose runtime contents publicly merely for this feature.

Do not change `RuntimeSeedBuilder` public API.

Do not change `ConsistencyRuntime.Bootstrap(...)` semantics.

Preparation must continue using existing `Bootstrap(...)`, which already validates duplicate references/keys, commits baseline set membership directly, validates projections, and resets diagnostics without going through normal mutation dispatch.

Add focused Core tests only if needed to freeze the pristine predicate:

```text
new empty runtime -> true
seeded runtime -> false
runtime that had Add/Remove history but is empty again -> false because Version > 0
```

Do not introduce a way to reset runtime history just to satisfy preparation.

---

# Task 186 — Implement `PrepareConsistencyScopeAsync`

Recommended location:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyScopePreparation.cs
```

Add the frozen extension method to `ConsistencyDbContextExtensions` or another public static extension class in the same namespace. Prefer keeping all `DbContext` consistency extensions discoverable together unless file size becomes unreasonable.

## 186.1 Argument/precondition order

Use this exact high-level order:

```text
1. ThrowIfNull(context/runtime/mappings/loaders)
2. validate saveBehavior enum if necessary
3. context.ChangeTracker.DetectChanges()
4. reject if any tracked entry state is Added, Modified, or Deleted
5. reject if runtime is not pristine/empty/version 0
6. calculate required scope requirements
7. validate every registered loader belongs to this runtime model (fail closed)
8. calculate distinct required sets
9. verify every required set has a registered loader
10. only now execute queries
```

No query may execute before all missing-loader/model-ownership/precondition checks succeed.

For clean-context rejection, an `InvalidOperationException` with a stable message is acceptable; do not create a public exception hierarchy unless genuinely needed by tests/API usability.

Suggested phrase:

```text
"Consistency scope preparation must run before domain mutations on a clean DbContext."
```

For non-pristine runtime rejection, `InvalidOperationException` is also acceptable.

Suggested phrase:

```text
"Consistency scope preparation requires a pristine empty runtime; live-runtime reconciliation is not supported."
```

## 186.2 Missing-loader error

Add one public actionable exception:

```csharp
public sealed class MissingConsistencyScopeLoaderException : Exception
{
    public IReadOnlyList<ConsistencyScopeGap> Gaps { get; }
}
```

Use one entry per missing `(set, requirement kind)` in deterministic order.

If a set is missing a loader for two distinct reasons, keep both gaps so the user understands why the set is required.

The message should name definition key/type + requirement kind and say to register a host query with `ConsistencyEfCoreLoaders.Complete(...)`.

Do not reuse `IncompleteConsistencyScopeException`: that exception means the caller attempted persistence without asserted scope; this new exception means explicit preparation lacks a host loader.

## 186.3 Execute loaders sequentially

DbContext is not thread-safe.

For each distinct required set in Set.Id order:

```text
invoke its query factory
await ToListAsync(cancellationToken)
store result in memory
```

Do not use `Task.WhenAll`.

Cancellation must naturally stop preparation and must occur before runtime bootstrap.

## 186.4 Validate tracking by reference

After each query materializes, verify every returned root entity is tracked by `context` and not detached.

Do not merely compare keys.

Efficient implementation is allowed, but correctness is primary. A reference-equality set of `context.ChangeTracker.Entries().Select(e => e.Entity)` is acceptable after all queries are loaded.

If any returned root is untracked, throw `InvalidOperationException` with a message containing:

```text
"scope loader must return entities tracked by the same DbContext"
```

Do not attach it automatically.

## 186.5 Stage then bootstrap once

Only after every loader has succeeded and every result has passed validation:

```csharp
var entries = ... RuntimeSeedEntry[];
runtime.Bootstrap(entries);
```

Call Bootstrap exactly once.

Then construct:

```csharp
var scope = new ConsistencyScope();
foreach (required set once, Set.Id order)
    scope.Complete(... typed registration internally ...);
```

Because `ConsistencyScope.Complete<T>` is typed, the EF adapter may need an **internal Core helper** such as:

```csharp
internal ConsistencyScope Complete(IObjectSetDefinition set)
```

if reflection would otherwise be required.

If adding that helper, keep it internal and validate nothing beyond adding the exact model definition. Do not add a new public untyped `Complete` API.

Return the scope.

## 186.6 No requirements is a valid no-op preparation

If the active mappings/save behavior have zero scope requirements:

```text
execute zero loaders
bootstrap no data / avoid unnecessary Bootstrap call
return an empty ConsistencyScope
leave runtime pristine/version 0
```

Do not require registrations merely because loaders were provided.

---

# Task 187 — Complete the misuse and integrity test matrix

Extend `ConsistencyScopePreparationTests.cs` with all cases below.

## 187.1 Pending EF mutations are rejected before loader execution

Cover at least Modified and Added. Deleted may be a theory case.

Assert query factory counter remains zero.

## 187.2 Non-pristine runtime is rejected before loader execution

Cover:

```text
seeded runtime
runtime where an object was Added then Removed and Version > 0
```

If the second case is awkward because removing the only object changes other state, still prove history/version prevents reuse.

## 187.3 `AsNoTracking()` loader is rejected

Register:

```csharp
db => db.Set<Association>().AsNoTracking()
```

Assert:

```text
throws before Bootstrap
runtime remains empty/version 0
```

## 187.4 Query failure leaves runtime untouched

First required loader may succeed, second throws.

Assert runtime remains pristine because bootstrap happens only after all queries are staged.

## 187.5 Cancellation leaves runtime untouched

Use an already-cancelled token or deterministic cancellation path.

Assert no bootstrap/version change.

## 187.6 Duplicate runtime seed keys fail before partial bootstrap mutation

Return/query data that would result in duplicate object-set keys if test infrastructure can construct it without EF identity-map interference. If EF prevents such a query naturally, cover duplicate-key atomicity at the Core bootstrap level instead and document why.

Do not weaken existing `Bootstrap` duplicate-key validation.

## 187.7 Same CLR type in two ObjectSets uses exact set identity

Create two ObjectSets of the same CLR type with different definition keys and requirements.

Register distinct loaders.

Prove the correct loader runs for each set and registration is not keyed by CLR type.

If EF cannot naturally distinguish the same CLR entity type for the two logical sets, use loader query counters plus model requirements to prove registry identity without forcing artificial EF mapping.

## 187.8 Preparation emits no consistency-policy side effects

Use a model with a repair/callback/policy path if existing test helpers make this easy.

At minimum prove:

```text
runtime.Version remains 0
runtime diagnostics reflect bootstrap reset behavior
no post-commit callback/repair scheduler was called solely because scope was prepared
```

Do not invent new diagnostics API.

## 187.9 Existing fail-closed save remains unchanged

Without calling preparation and without supplying a complete scope, `SaveChangesConsistently` must still throw `IncompleteConsistencyScopeException` before SQL for the same incomplete scenario.

The new helper is opt-in only.

---

# Task 188 — Dogfood the helper and document the boundary

## 188.1 Update the dependency-maintenance executable sample

Refactor:

```text
samples/Raffinert.Consistency.DependencyMaintenanceSample/Program.cs
```

so it demonstrates actual preparation instead of manually creating the runtime from already-held association instances.

Preferred flow:

```text
1. open SQLite in-memory connection
2. use a short-lived seed DbContext to EnsureCreated + insert Source/Target/Association rows
3. dispose seed context
4. create a fresh operation DbContext on the same open connection
5. build Raffinert model/mappings
6. create EMPTY runtime with compiled.CreateRuntime()
7. register host loader:
       associations -> Set<Association>().Include(SourceItem).Include(TargetItem)
8. call PrepareConsistencyScopeAsync
9. obtain the tracked Association/Source/Target instances from the operation context
10. perform the existing dogfood mutations
11. pass returned scope into every SaveChangesConsistently call
```

Do not seed the runtime manually in this sample after this change.

Do not add ObjectSets for SourceItem or TargetItem.

Do not move query ownership into Raffinert.

Keep all existing calculator, fan-out result, retargeting, null, zero, tracked/runtime/persisted mirror assertions and the recently corrected proof-boundary wording.

Add one visible comment near loader registration:

```text
Raffinert knows Association coverage is required; the host owns the authoritative EF query and Include graph.
```

## 188.2 README

Add a concise example near EF authoritative-scope documentation:

```csharp
var loaders = new ConsistencyEfCoreLoaders()
    .Complete(associations, db => db.Set<Association>()
        .Include(x => x.SourceItem)
        .Include(x => x.TargetItem));

var scope = await db.PrepareConsistencyScopeAsync(runtime, mappings, loaders);
```

State explicitly:

```text
- optional bootstrap helper;
- only for a pristine runtime / clean DbContext;
- host query is authoritative and application-owned;
- no hidden loading occurs during SaveChangesConsistently;
- live runtime reconciliation is not supported by this helper.
```

## 188.3 `docs/ef-core-consistency.md` and architecture docs

Update whichever existing EF architecture document is canonical; if `docs/ef-core-consistency.md` exists, use it. Also update `docs/architecture.md` only where the boundary belongs.

Required sentence in substance:

> Scope preparation can execute host-registered EF queries to bootstrap required runtime coverage, but scope completeness remains a host assertion. Raffinert does not infer query boundaries, verify omitted database rows, or reconcile an already-live runtime.

Preserve the existing core rule:

> Scope completeness proves runtime data coverage; it does not prove mutation coverage for SQL-side effects.

Do not blur the two concepts.

## 188.4 Improve `IncompleteConsistencyScopeException` guidance

Current message says to seed/maintain authoritative coverage and that Raffinert will not auto-load objects.

Keep that truth, but mention the optional explicit helper, e.g. in substance:

```text
Seed and maintain authoritative runtime coverage yourself, or explicitly call PrepareConsistencyScopeAsync with host-owned loaders before domain mutation. Raffinert does not auto-load during persistence.
```

Do not make the exception imply preparation can repair a live runtime.

---

# Task 189 — Public API, local release proof, and roadmap closeout

## 189.1 Expected public API delta

Core public API:

```text
NONE
```

All Core changes for pristine-check/untyped internal scope completion must remain internal.

EF public API should add only:

```text
ConsistencyEfCoreLoaders
ConsistencyEfCoreLoaders.Complete<TEntity>(...)
ConsistencyDbContextExtensions.PrepareConsistencyScopeAsync(...)
MissingConsistencyScopeLoaderException
MissingConsistencyScopeLoaderException.Gaps
```

Do not add public loader interfaces or public result wrapper types in v1.

Update:

```text
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
```

according to the repository's current prepublication API-baseline convention.

Keep `PublicAPI.Unshipped.txt` empty/header-only as required by existing release verification.

## 189.2 Required local verification

Run exactly:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes

dotnet pack Raffinert.Consistency.sln -c Release --no-build -o artifacts/packages

pwsh ./eng/VerifyReleaseCandidate.ps1 -PackageDirectory artifacts/packages -RequireEmptyUnshipped
```

Run the existing packed-package consumers if `VerifyReleaseCandidate.ps1` does not already cover them in the local workflow used by prior roadmaps.

Do not publish a package.

Do not create a tag.

Do not create a GitHub release.

Remote CI will naturally run after push; this roadmap requires local proof before closeout and does not require the agent to wait for remote pipelines unless explicitly asked.

## 189.3 Closeout documentation

After local verification passes:

1. mark this roadmap `COMPLETED LOCALLY — REMOTE PIPELINES NOT CHECKED`;
2. record exact implementation-head SHA;
3. record core net8/net10 and EF test counts;
4. record all three executable samples as passed;
5. add a short entry to `docs/release-candidate-verification.md`;
6. replace the active roadmap entry in `docs/roadmaps/README.md` with a completed entry.

---

# Required implementation sequence

Use small commits in this order unless a compiler-only adjustment requires combining adjacent steps:

```text
1. test(ef): expose scope preparation bootstrap requirements
2. feat(ef): add host-owned consistency scope loaders
3. refactor(ef): expose exact persistence scope requirements
4. fix(core): expose pristine runtime bootstrap eligibility internally
5. feat(ef): prepare consistency scope from host loaders
6. test(ef): prove scope preparation misuse and atomicity boundaries
7. docs(sample): dogfood explicit scope preparation
8. chore: record scope preparation release proof
```

Do not commit generated build artifacts.

---

# Anti-shortcut checklist

The implementation is wrong if any of the following is true:

- `SaveChangesConsistently` starts issuing queries automatically.
- The interceptor starts issuing loader queries.
- Core references EF Core.
- Raffinert generates `DbSet`/`Include`/tenant queries itself.
- Loader registry is keyed by CLR type instead of exact `ObjectSet` identity.
- All registered loaders execute even when their sets are not required.
- Materialization-only requirements are loaded under `ConsistencySaveBehavior.Validate`.
- A missing loader is discovered only after another loader already ran.
- Queries run in parallel against the same DbContext.
- `AsNoTracking()` results are silently attached.
- Preparation works on a runtime that already contains data.
- Preparation works after Added/Modified/Deleted EF changes exist.
- Preparation calls `runtime.Add`, `runtime.Remove`, or `runtime.Apply`.
- Runtime bootstrap happens one set at a time.
- A loader failure can leave the runtime partially bootstrapped.
- Preparation increments runtime version or dispatches repairs/callbacks.
- The helper claims it can prove the host query returned every database row.
- The helper claims it can infer required `Include` graphs.
- The implementation introduces partition scope in this wave.
- The implementation attempts live-runtime reconciliation.
- SourceItem/TargetItem become fake Raffinert object sets in the dogfood sample.
- Existing incomplete-scope save rejection is weakened.
- Existing store-side SQL mutation boundaries are weakened.

---

# Final acceptance statement

Tasks 182–189 are complete only when this exact workflow is real and tested:

```csharp
var runtime = compiled.CreateRuntime();

var loaders = new ConsistencyEfCoreLoaders()
    .Complete(
        associations,
        db => db.Set<Association>()
            .Include(x => x.SourceItem)
            .Include(x => x.TargetItem));

var scope = await db.PrepareConsistencyScopeAsync(
    runtime,
    mappings,
    loaders,
    cancellationToken: cancellationToken);

// domain mutation starts only after preparation
source.UnitValue = 20m;

await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope },
    cancellationToken);
```

and all of these statements are true:

```text
Raffinert determined that Association coverage was required.
The application supplied the authoritative EF query and Include graph.
Only required loaders executed.
The query results were tracked by the same DbContext.
The pristine runtime was bootstrapped exactly once with all staged required sets.
The returned ConsistencyScope represented exactly the successfully prepared required sets.
No hidden query occurred during consistent save.
No live-runtime reconciliation was attempted.
```
