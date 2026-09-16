# Codex implementation plan — store-side referential actions and persistence authority

Status: **COMPLETED**

Baseline commit: `87e9bc1b9b0c8aa177cd709140dc406ce22e0326`

Implementation head: `d3458575c58f6f5c22594b2906526fb855aba3c7`

Release proof (2026-09-16):

- CI run `35122089225`: success.
- Release-candidate run `35122105220`: success.
- Artifact `10457238209` (`release-candidate-packages`).
- Core tests: 300 passed on .NET 8 and 300 passed on .NET 10.
- EF Core/SQLite tests: 129 passed on .NET 10.
- Both samples, formatting, package/API validation, and fresh package consumers passed.
- No package was published and no tag or release was created.

Tasks: **159–166**

## Goal

Close the next EF persistence-correctness hole after Tasks 152–158:

> an EF save can execute database-side referential actions such as `ON DELETE CASCADE` or `ON DELETE SET NULL` against rows that are not tracked by the saving `DbContext`. The database then changes or deletes consistency-relevant rows, but Raffinert has no corresponding tracked mutation to install into `ConsistencyRuntime`.

`ConsistencyScope.Complete(set)` does **not** solve this problem. Scope says the runtime contains the authoritative object set. It does not say the current EF `ChangeTracker` contains every row the database will mutate as a side effect of the SQL command.

Example unsafe state:

```text
Runtime (complete):
Parent P
Child  C -> P

Saving DbContext:
Parent P only

EF model:
Parent -> Child : DeleteBehavior.Cascade

operation:
context.Remove(P)
SaveChangesConsistently(...)

Database after SQL:
P deleted
C deleted by ON DELETE CASCADE

Runtime after current adapter commit:
P removed
C STILL REGISTERED   <-- stale authoritative runtime
```

The same class of bug exists for `DeleteBehavior.SetNull`: the database may update an untracked dependent FK/navigation while the runtime still contains the old relationship state.

This roadmap makes authoritative EF persistence **fail closed** when an EF-declared store-side referential action can reach consistency-relevant state that the adapter cannot prove it will observe through tracked mutations.

This is a correctness wave. Do not add domain features.

---

## Non-negotiable rules

- Do **not** auto-load dependents from the database.
- Do **not** issue hidden SQL queries to prove whether cascade rows exist.
- Do **not** treat `ConsistencyScope.Complete(...)` as proof that EF tracks every affected row.
- Do **not** globally ban every cascade in the `DbContext`; unrelated store-side cascades must remain usable.
- Do **not** weaken `ChangeValidationMode.StrictNewValue`.
- Do **not** weaken the generated INSERT/UPDATE value rules from Tasks 145–158.
- Do **not** weaken authoritative scope checks.
- Do **not** move EF-specific referential-action policy into Core.
- Do **not** implement trigger parsing, database CDC, `ExecuteUpdate`, `ExecuteDelete`, raw-SQL interception, or distributed runtime synchronization in this roadmap.
- Do **not** pretend database triggers that mutate other rows are automatically safe. Document that boundary explicitly.
- Preserve low-level Core/runtime semantics.
- Preserve client-side EF cascade workflows when all affected consistency rows are explicitly tracked.
- No package publication, tags, or GitHub release in this roadmap.

The desired v1 rule is:

> Raffinert authoritative EF saves may rely on tracked EF mutations plus explicitly proven same-entry store-generated values. They may not rely on opaque store-side referential mutations of consistency-relevant rows.

---

# Task 159 — Add failing SQLite proofs before implementation

Create focused tests **before** adding the guard.

Recommended file:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/StoreSideReferentialActionTests.cs
```

All failing tests must demonstrate a real database/runtime divergence against baseline `87e9bc1...` or demonstrate missing fail-closed behavior.

## 159.1 Untracked database cascade delete must be rejected before SQL

Use real SQLite foreign keys and `DeleteBehavior.Cascade`.

Entities:

```csharp
sealed class Parent
{
    public int Id { get; set; }
}

sealed class Child
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public Parent Parent { get; set; } = null!;
    public int Quantity { get; set; }
}
```

Raffinert model:

```csharp
var parents = model.Objects<Parent>().Key(x => x.Id);
var children = model.Objects<Child>().Key(x => x.Id);
var relation = model.Relation(parents, children)
    .Where((parent, child) => parent.Id == child.ParentId);
```

Seed:

- DB contains `Parent P` and `Child C`;
- runtime contains both P and C;
- deleting `DbContext` tracks **only P**, not C.

Scope may truthfully declare both sets complete because the runtime is complete:

```csharp
var scope = new ConsistencyScope()
    .Complete(parents)
    .Complete(children);
```

Then:

```csharp
context.Remove(parent);
context.SaveChangesConsistently(runtime, mappings,
    new ConsistencySaveOptions { Scope = scope });
```

Required final behavior:

- throw the new dedicated referential-action exception **before SQL**;
- DB still contains P and C;
- runtime version unchanged;
- runtime still contains P and C;
- do not satisfy the test by loading C implicitly.

The baseline should fail because SQLite deletes C while the current adapter only captures P removal.

## 159.2 Untracked `SET NULL` must be rejected before SQL

Use nullable FK and:

```csharp
.OnDelete(DeleteBehavior.SetNull)
```

DB contains P and C, runtime contains both, deleting context tracks only P.

Required behavior:

- reject before SQL;
- DB `Child.ParentId` remains unchanged;
- runtime relationship remains unchanged;
- runtime version unchanged.

This is a distinct proof. Do not assume cascade-delete coverage proves set-null coverage.

## 159.3 Detect transitive store cascade paths

Prove the guard cannot inspect only immediate dependents.

Example database graph:

```text
Root --CASCADE--> Intermediate --CASCADE--> ConsistencyLeaf
```

Only `ConsistencyLeaf` belongs to a Raffinert object set. `Intermediate` is not mapped to Raffinert.

Delete a tracked `Root` while neither downstream row is tracked.

Required behavior:

- reject before SQL;
- exception identifies the original deleted type and the downstream consistency-relevant type;
- no rows are deleted;
- runtime unchanged.

This test prevents a shallow one-hop implementation.

## 159.4 Unrelated database cascade must remain allowed

Create a cascade from a saved entity into an audit/detail type that is not represented by any Raffinert object set and does not participate in the consistency model.

Required behavior:

- the consistent save is not rejected solely because the EF model contains a cascade;
- database cascade may execute normally;
- runtime installs only the relevant tracked mutation.

The solution must not become `if any Cascade then throw`.

## 159.5 Owned aggregate deletion must not be accidentally banned

Add an EF-owned value object owned by a mapped root. It may use EF's normal ownership cascade semantics.

If the owned type is not an independent Raffinert object set, deleting the root must remain supported.

This test exists to prevent a naive type-graph guard from making ordinary owned entities unusable.

## 159.6 Path parity

The unsafe server-side referential effect must fail closed consistently for:

- `SaveChangesConsistently`;
- `SaveChangesConsistentlyAsync`;
- `ConsistencySaveChangesInterceptor`;
- `SaveChangesAndApply`;
- `SaveChangesAndApplyAsync`;
- `CaptureConsistencyUnitOfWork` before the application's first SQL command.

`ChangeTrackerAdapter.CaptureUnitOfWork` remains a low-level policy-agnostic primitive; do not silently change its contract in this task.

---

# Task 160 — Add exact runtime relevance helpers for EF preflight

The EF adapter needs to know whether a store-side referential path can reach state represented by the compiled consistency model.

Keep this information query **internal**. Do not add public Core API merely for the adapter.

Add an internal runtime helper with semantics equivalent to:

```csharp
bool HasObjectSetForClrType(Type clrType);
```

It must conservatively treat inheritance overlap as relevant:

```text
ObjectSet<Base> + EF Derived type  -> relevant
ObjectSet<Derived> + EF Base metadata that can contain Derived rows -> relevant
```

Use assignability overlap, not only exact `Type == Type`.

Also add an internal helper for nested semantic usage when required by Task 161. Prefer reusing the existing compiled dependency metadata and `GetTrackedMemberUsage(...)`; do not re-parse expression trees in the EF package.

Do not regress the exact-set member-usage behavior added by Tasks 145–151.

Required Core tests:

- exact object-set CLR type;
- base/derived overlap in both directions;
- unrelated CLR type returns false;
- same CLR type in multiple object sets remains relevant without losing set-scoped member semantics.

No public API baseline change should be needed for Task 160.

---

# Task 161 — Implement a store-side referential-action preflight analyzer

Add one internal EF adapter component, for example:

```text
ConsistencyStoreSideEffectGuard
```

Do not scatter independent cascade checks across save methods.

## 161.1 Start from actual tracked deletions

After `ChangeTracker.DetectChanges()`, inspect every entry with:

```csharp
entry.State == EntityState.Deleted
```

For each deleted EF entity type, inspect referencing foreign keys through EF metadata.

Do not inspect every model relationship indiscriminately; the guard is about store effects reachable from the current delete operation.

## 161.2 Classify only store-mutating delete behaviors

For this roadmap, database-side referential mutation means:

```text
DeleteBehavior.Cascade
DeleteBehavior.SetNull
```

Do not treat these client-only/non-mutating database behaviors as the same thing:

```text
ClientCascade
ClientSetNull
Restrict
NoAction
ClientNoAction
```

The exact EF relational mapping remains authoritative. Do not guess from names.

## 161.3 Traverse cascade transitively

For `Cascade`:

1. the database may delete rows of the dependent entity type;
2. if that dependent state is consistency-relevant, the path is unsafe;
3. even when the immediate dependent type is irrelevant, recurse into its own store-side `Cascade`/`SetNull` relationships because a downstream type may be relevant.

Maintain a visited EF entity-type/foreign-key set so malformed or cyclic metadata cannot recurse forever.

For `SetNull`:

- the dependent row survives, so do not recursively treat it as deleted;
- determine whether nulling the FK/navigation can affect consistency semantics on that dependent.

## 161.4 Relevance rules

### Cascade delete relevance

Treat a cascaded dependent as relevant when either:

1. its CLR type overlaps a Raffinert `ObjectSet` type; or
2. it is a non-owned nested dependency target whose disappearance can affect a surviving Raffinert dependency path.

Do not globally reject an unrelated audit/detail type.

### Set-null relevance

Treat `SetNull` as relevant when any of these is true:

1. dependent CLR type overlaps a Raffinert object set;
2. one of the FK CLR members participates in tracked Raffinert semantics;
3. the dependent-to-principal navigation CLR member participates in tracked Raffinert semantics.

Use compiled runtime metadata. Do not infer relevance from property names.

### Ownership exemption

For `IForeignKey.IsOwnership == true`:

- if the owned dependent is not independently represented by a Raffinert object set, deleting it as part of deleting its owner is not by itself a reason to reject;
- continue traversal through nested ownership/cascade metadata so a downstream independent consistency object is still detected;
- if an owned CLR type is explicitly represented as an independent object set, fail closed.

This exemption must be proved by Task 159.5.

## 161.5 Public exception

Add one explicit public adapter exception. Recommended shape:

```csharp
public sealed class ConsistencyStoreSideReferentialActionNotSupportedException : Exception
{
    public Type DeletedEntityType { get; }
    public Type AffectedEntityType { get; }
    public DeleteBehavior DeleteBehavior { get; }
    public IReadOnlyList<Type> EntityPath { get; }
}
```

The message must explain:

- which delete can invoke the store-side action;
- which consistency-relevant type can be changed/deleted without tracked evidence;
- Raffinert will not auto-load the missing rows;
- safe options are to configure client-side referential behavior and track/load dependents, or execute outside authoritative-save APIs and rebuild/reconcile the runtime.

Do not create a generic `InvalidOperationException` for this condition.

Add the public API baseline entry normally; do not suppress PublicApi analyzers.

---

# Task 162 — Wire one preflight into every DB-owning convenience path

The same guard must protect every public helper that itself proceeds to `SaveChanges` after capturing runtime work.

## High-level policy-aware paths

Wire the shared preflight into:

```text
SaveChangesConsistently
SaveChangesConsistentlyAsync
ConsistencySaveChangesInterceptor
CaptureConsistencyUnitOfWork
```

For `CaptureConsistencyUnitOfWork`, rejection must occur during capture, before the caller's first SQL command.

The interceptor must reuse the same coordinator/preflight; do not create interceptor-only logic.

## Low-level save-and-apply helpers

Also protect:

```text
SaveChangesAndApply
SaveChangesAndApplyAsync
```

These methods own the database save ordering and therefore must not knowingly permit an unobservable store-side mutation.

Do not change the lower-level contract of:

```text
ChangeTrackerAdapter.CaptureUnitOfWork
ConsistencyUnitOfWork.Prepare/PlanDetailed/Commit
```

Those remain runtime-binding primitives. Their documentation must state that callers who own SQL also own the external/store-side-effect boundary.

## Ordering

Call `DetectChanges()` once before the referential-action preflight observes deletion state.

Avoid duplicate `DetectChanges()` waves when practical, but correctness is more important than micro-optimization.

All paths must throw before:

- mirror setters;
- invariant planning with incomplete store effects;
- SQL;
- runtime version changes.

---

# Task 163 — Prove the supported client-side alternatives

The purpose is not only to reject unsafe SQL. Prove a practical safe workflow.

## 163.1 `ClientCascade` + fully tracked dependents succeeds

Configure:

```csharp
.OnDelete(DeleteBehavior.ClientCascade)
```

Load/track P and all children that must be deleted. Delete P.

Required:

- EF marks dependent rows deleted in the tracker;
- Raffinert capture contains explicit `ObjectRemoved` mutations for mapped child objects;
- database transaction succeeds;
- runtime removes parent and children;
- runtime and DB agree after commit.

## 163.2 `ClientSetNull` + tracked dependent succeeds

Configure nullable FK with:

```csharp
.OnDelete(DeleteBehavior.ClientSetNull)
```

Track parent and child before deleting parent.

Required:

- EF emits the FK/reference mutation in tracked state;
- Raffinert observes relation/dependency impact;
- database and runtime agree after commit.

## 163.3 Client-side behavior with missing dependent must fail safely at the database boundary

Use `ClientCascade`/`ClientSetNull` with a dependent row not loaded when the relational FK prevents deleting the principal.

Required:

- database operation fails;
- runtime is not committed;
- no `ConsistencyRuntimeSynchronizationException` is emitted because DB durability did not succeed;
- caller can reload/discard context normally.

This proves why client-only referential actions are safer for an authoritative in-memory runtime: missing rows cause a DB failure rather than an invisible successful side effect.

---

# Task 164 — Define the database-side effect boundary explicitly in current docs

Update at least:

```text
README.md
docs/architecture.md
docs/ef-core-consistency.md
```

Document these categories separately.

## Supported automatic EF evidence

```text
tracked lifecycle changes
tracked scalar/reference/collection changes
proven temporary/generated FK fixup
proven same-entry store-generated INSERT values
proven same-entry store-generated UPDATE values
```

## Rejected before SQL when consistency-relevant

```text
EF-declared database ON DELETE CASCADE into consistency state
EF-declared database ON DELETE SET NULL into consistency state
transitive store cascade paths that reach consistency state
```

## Outside automatic synchronization contract

```text
database triggers that mutate other rows
raw SQL that changes consistency-relevant rows
ExecuteUpdate / ExecuteDelete
bulk libraries that bypass ChangeTracker
other application processes/pods writing the same authoritative data
manual DBA/data-fix writes
```

For these external mutations, the application must either:

- emit exact domain/runtime mutations through an application-controlled integration that has authoritative old/new evidence; or
- rebuild/reseed/reconcile the runtime from authoritative persisted state before relying on it again.

Do not claim trigger safety merely because EF metadata declares a trigger. Trigger metadata does not describe arbitrary row side effects.

Add one concise statement near `ConsistencyScope`:

> Scope completeness proves data coverage in the runtime; it does not prove mutation coverage for SQL-side effects.

Also document the recommended EF relationship configuration for consistency-managed aggregates:

```text
prefer ClientCascade / ClientSetNull + explicitly tracked dependents
or Restrict / NoAction where domain rules require explicit deletion
```

Do not tell users that `Cascade` is universally bad; the restriction applies when the reachable store-side effect can touch Raffinert-managed consistency state.

---

# Task 165 — Regression and misuse matrix

Keep all existing Tasks 130–158 tests green.

Add focused regressions for:

- generated INSERT value workflows;
- generated UPDATE value workflows;
- temporary FK finalization;
- unmapped nested dependency capture;
- same-CLR-type multiple object sets;
- authoritative scope errors;
- manual transaction state machine;
- runtime-install failure after DB commit;
- resumable dispatch;
- normal tracked delete without database cascade;
- client-side cascade success;
- irrelevant server-side cascade allowed.

Add explicit tests that prove the guard itself has no runtime side effects:

```text
runtime.Version unchanged
no materialized mirror write
no callback dispatch
DB command not executed
```

If practical, use a SQLite command interceptor/counter to prove zero SQL for rejected operations rather than inferring only from unchanged rows.

## Public API

Update:

```text
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
```

for the new exception and its public properties.

`PublicAPI.Unshipped.txt` should remain empty after the baseline is intentionally updated, following the repository's current pre-publication API-baseline policy.

Do not add compatibility wrappers; nothing has been published yet.

---

# Task 166 — Full release proof and roadmap closeout

After implementation, run the complete gate from the repository root.

At minimum:

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Run the repository's normal pack/API/package-consumer verification too.

Then require:

1. normal GitHub CI success on the exact implementation head;
2. manual `Release candidate verification` workflow success on that exact implementation head;
3. record both run IDs and artifact ID in `docs/release-candidate-verification.md`;
4. mark this plan `COMPLETED` only after both remote gates pass;
5. update `docs/roadmaps/README.md` from active to completed with the verified implementation SHA.

Do not publish packages, create tags, or create a GitHub Release as part of Task 166.

---

# Required commit sequence

Prefer this sequence so review stays mechanical:

```text
1. test(ef): expose untracked store referential action gaps
2. fix(core): expose consistency entity type relevance
3. fix(ef): reject unsafe store-side referential actions
4. test(ef): prove client-side referential action workflows
5. docs: document EF store-side effect boundary
6. test: prove store-side referential action release integrity
7. chore: record store-side referential action release proof
```

If formatting requires an extra style-only commit, keep it separate.

---

# Definition of done

This roadmap is complete only when all statements below are true:

```text
A server-side Cascade cannot silently delete a Raffinert-managed object that was absent from ChangeTracker.

A server-side SetNull cannot silently mutate a consistency-relevant relationship/member that was absent from ChangeTracker.

Cascade analysis follows transitive database cascade paths.

Unrelated database cascades are not rejected globally.

Ordinary EF ownership deletion remains usable when the owned type is not independent consistency state.

ClientCascade/ClientSetNull with explicitly tracked dependents produces ordinary tracked Raffinert mutations and succeeds.

Client-side behavior with missing required dependents fails at the DB boundary without advancing runtime state.

All DB-owning convenience APIs and the policy-aware manual capture fail closed consistently.

Low-level runtime binding primitives remain policy-agnostic and are documented as such.

ConsistencyScope documentation explicitly distinguishes runtime data completeness from SQL mutation coverage.

Triggers/raw SQL/ExecuteUpdate/ExecuteDelete/external writers are explicitly outside automatic synchronization unless the application supplies authoritative reconciliation.

Tasks 130–158 regressions stay green.

Full CI and manual RC verification are green on the exact implementation head.
```
