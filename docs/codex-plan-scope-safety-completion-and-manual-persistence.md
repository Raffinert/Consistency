# Codex implementation plan — scope safety completion and policy-aware manual persistence

Status: **COMPLETED**

Completed on 2026-09-15. Implementation head `fdf8c010917c07c0ea57737a7dc2b0bbdbd96dee`
passed CI run [35021962335](https://github.com/Raffinert/Consistency/actions/runs/35021962335)
and release-candidate run [35024349773](https://github.com/Raffinert/Consistency/actions/runs/35024349773).

Baseline commit: `c9b6a340b7eb3a21946725a41ec87b109417abb8`

Tasks: **137–144**

This is a narrow correctness continuation after Tasks 130–136. Do not turn it into a new feature wave.

The previous scope-safety work correctly protects `SaveChangesConsistently` and the save interceptor for relation-backed enforced invariants and materialized derived values. Two important gaps remain:

1. scope requirement compilation currently walks derived-input topology but misses ordinary **nested navigation dependencies** that rely on `NavigationIndexRegistry` reverse-owner coverage; and
2. the documented **manual transaction / store-generated-key workflow bypasses the automatic persistence-policy scope gate**, leaving the most advanced persistence path dependent on the developer remembering to establish the same safety manually.

Both gaps must be closed before making a strong claim that authoritative persistence decisions are fail-closed across every supported EF workflow.

---

# 1. Confirmed baseline facts

Do not rediscover or redesign these facts. They are already true at the baseline commit.

## 1.1 Existing scope API

Core already exposes:

```csharp
public sealed class ConsistencyScope
{
    public ConsistencyScope Complete<T>(ObjectSet<T> set) where T : class;
}

public enum ConsistencyScopeRequirementKind
{
    RelationSourceCoverage = 0,
    RelationTargetCoverage = 1,
    ProjectedConsumerCoverage = 2
}

public sealed record ConsistencyScopeGap(
    int ObjectSetId,
    string? ObjectSetDefinitionKey,
    Type ObjectType,
    ConsistencyScopeRequirementKind RequirementKind);
```

`ConsistencyScope` is a host assertion. Raffinert does not inspect the database to prove the assertion.

## 1.2 Existing EF gate

`ConsistencySaveOptions` already has:

```csharp
public ConsistencyScope? Scope { get; init; }
```

The convenience-save coordinator validates selected `.Enforce(...)` definitions and selected `.Materialize(...)` definitions before semantic planning or SQL and throws:

```csharp
IncompleteConsistencyScopeException
```

when required complete-set assertions are absent.

The save interceptor goes through the same coordinator.

## 1.3 Existing requirement compiler

`ConsistencyScopeRequirementCompiler` currently derives requirements by recursively following `DerivedInput`:

- `RelationDerivedInput` -> relation left + relation right coverage;
- `UpstreamDerivedInput` -> transitive upstream requirements;
- projected upstream -> consumer coverage.

That is necessary but not sufficient.

## 1.4 Existing navigation routing

`NavigationIndexRegistry` also builds reverse indexes from ordinary expression dependency paths in:

- derived source dependencies;
- relation-item dependencies;
- invariant source dependencies;
- relation predicate dependency paths.

`DependencyGraphRuntime.ResolveNavigationRoots(...)` uses those reverse indexes to discover affected source roots when a nested target object/member changes.

Therefore this expression:

```csharp
var currentPrice = model.Derived(orderLines)
    .Compute(line => line.Product.Price);
```

is not truly "source-local" for authoritative propagation. A `Product.Price` mutation reaches `OrderLine` sources through the reverse navigation index. If the runtime contains only some order lines that point to the product, missing order lines cannot be discovered.

The current scope compiler incorrectly reports zero completeness requirements for that definition.

## 1.5 Existing manual workflow gap

The current EF documentation explicitly says the automatic scope gate belongs to the convenience/interceptor coordinator and does not wrap the low-level manual `ConsistencyUnitOfWork` workflow.

That is an acknowledged footgun. It must not remain the recommended generated-key/outbox workflow.

---

# 2. Architectural decisions — frozen for this wave

The weaker agent has **no design discretion** on these points.

## 2.1 Core derives requirements; EF enforces persistence policy

Keep the boundary:

```text
Core
  expression dependency topology
  scope requirement derivation
  scope-gap calculation

EF adapter
  selected persistence policies
  persistence-facing scope failure
  materialization
  transaction workflow hosting
```

Do not put `DbContext`, `EntityEntry`, SQL, transactions, or EF metadata into Core.

## 2.2 Whole-object-set scope remains the only completeness unit

Do not implement tenant, key, partition, relation-bucket, query, or predicate-specific completeness.

Still use:

```csharp
scope.Complete(orderLines)
```

not:

```csharp
scope.Complete(orderLines, orderId: 123); // NOT IN THIS WAVE
```

## 2.3 A navigation-dependent consumer needs complete root coverage

When reverse navigation routing is required to discover consumers, the **root/source object set** must be complete.

Example:

```csharp
line => line.Product.Price
```

requires complete `orderLines`, not complete `products` merely because `Product.Price` is the terminal member.

Why:

```text
Product.Price changed
    -> reverse NavigationIndex(OrderLine.Product)
    -> every OrderLine owner must be discoverable
```

The terminal target object itself is already the changed object. The missing correctness risk is undiscovered consumer roots.

## 2.4 Manual persistence must become policy-aware by construction

Do not solve the manual workflow with documentation such as:

```text
remember to validate scope first
```

Do not add a helper that the developer may simply forget to call.

Add a high-level manual persistence unit-of-work API that captures:

- context;
- runtime;
- high-level `ConsistencyEfCoreMappings`;
- `ConsistencySaveOptions` / scope proof;
- the captured low-level mutations;
- the selected enforced invariant IDs;
- selected materializations.

The object must drive planning through the same persistence-policy engine used by convenience saves.

## 2.5 The high-level manual object does not own the database transaction

Do not make it begin, commit, or rollback the database transaction.

The application owns transaction durability because it may need to persist domain rows, an outbox, additional tables, or other work atomically.

The high-level object owns only the **consistency side** of the protocol.

## 2.6 Keep low-level APIs

Do not remove or obsolete:

```text
ConsistencyUnitOfWork
ChangeTrackerAdapter.CaptureUnitOfWork
ConsistencyRuntime.PlanDetailed
```

They remain valid runtime primitives.

But documentation/XML docs must state that they do **not by themselves** apply EF persistence policy (`Enforce`, `Materialize`, `ConsistencyScope`).

Use the new high-level persistence unit of work for authoritative EF persistence workflows.

---

# 3. Hard stops / non-goals

Do not implement any of the following in Tasks 137–144:

- EF auto-loading or generated database queries;
- partition/key/tenant-scoped completeness;
- distributed runtime synchronization;
- Redis / Service Bus / CDC integration;
- runtime thread-safety redesign;
- broad relation/dependency engine rewrite;
- automatic database transaction ownership;
- automatic retry after database/runtime divergence;
- new invariant DSL;
- generic workflow engine;
- silently treating `DbContext.ChangeTracker` as complete data;
- silently treating `.Map(set)` as completeness proof;
- `IgnoreScopeSafety`, `BestEffort`, or warning-only persistence modes;
- package publication, version bump, release tag, or GitHub release.

Do not change semantic relation concepts merely because the product is named `Consistency`.

---

# Task 137 — Complete scope topology for reverse navigation consumers

## Goal

Make Core scope requirement derivation match every reverse-navigation dependency path that the runtime actually uses for affected-source routing.

## 137.1 Add a public requirement reason

Append, do not renumber existing values:

```csharp
public enum ConsistencyScopeRequirementKind
{
    RelationSourceCoverage = 0,
    RelationTargetCoverage = 1,
    ProjectedConsumerCoverage = 2,
    NavigationConsumerCoverage = 3
}
```

Add corresponding internal reason:

```csharp
internal enum ScopeRequirementReason
{
    RelationSourceCoverage = 0,
    RelationTargetCoverage = 1,
    ProjectedConsumerCoverage = 2,
    NavigationConsumerCoverage = 3
}
```

Keep numeric parity between the internal and public enums because current conversion is a cast.

Do not reorder the old values.

## 137.2 Centralize reverse-navigation path detection

Today navigation detection logic lives in `NavigationIndexRegistry`.

Do not duplicate a subtly different test inside the scope compiler.

Extract or add one internal helper in the Core expression/dependency layer, for example:

```csharp
internal static class DependencyPathNavigation
{
    public static bool RequiresReverseOwnerCoverage(DependencyPath path);
    public static bool IsNavigation(DependencyPathSegment segment);
}
```

Exact internal naming may vary.

The important rule is:

```text
Only segments BEFORE the terminal dependency member participate in reverse owner traversal.
```

Examples:

```text
line.Quantity
  segments: Quantity
  reverse navigation: NO

line.Product
  segments: Product
  reverse navigation: NO
  reason: mutation of line.Product is already rooted at line

line.Product.Price
  segments: Product -> Price
  reverse navigation: YES

line.Product.Category.Code
  segments: Product -> Category -> Code
  reverse navigation: YES

line.Address.PostCode
  where Address is a value type
  reverse navigation: NO

order.Lines[].Quantity
  collection segment before Quantity
  reverse navigation: YES
```

Use the same definition of navigation as `NavigationIndexRegistry`:

- collection navigation; or
- non-value-type, non-string reference segment.

Refactor `NavigationIndexRegistry` to call the shared helper. Do not maintain two implementations.

## 137.3 Extend derived requirement compilation

In `ConsistencyScopeRequirementCompiler.AddDerived(...)`, after processing upstream inputs, inspect:

```csharp
definition.Analysis.Dependencies
```

For each dependency with:

```csharp
Role == ExpressionParameterRole.DerivedSource
```

and whose `DependencyPath` requires reverse owner coverage, add:

```text
definition.SourceSet -> NavigationConsumerCoverage
```

This applies to:

- source-only derived definitions;
- relation-backed derived source expressions;
- same-source composed definitions;
- two-upstream composed definitions;
- projected composed definitions when their computation expression itself contains nested source navigation.

A direct projected selector remains covered by `ProjectedConsumerCoverage`; do not treat a one-segment selector as reverse-navigation traversal.

## 137.4 Extend invariant requirement compilation

`ConsistencyScopeRequirementCompiler.Compile(IInvariantDefinition)` must also inspect:

```csharp
invariant.Analysis.Dependencies
```

For each dependency with:

```csharp
Role == ExpressionParameterRole.InvariantSource
```

and reverse-navigation traversal, add:

```text
invariant.SourceSet -> NavigationConsumerCoverage
```

Do this in addition to transitive upstream-derived requirements.

Example:

```csharp
var invariant = model.Invariant(orderLines)
    .Using(total)
    .Must((line, value) => value <= line.Product.AllocationLimit);
```

Even if `total` is source-local, `Product.AllocationLimit` requires complete `orderLines` for authoritative propagation from product changes.

## 137.5 Relation dependencies

Do not invent extra target-set requirements for nested navigation terminal objects.

For a relation-backed derived definition, existing:

```text
RelationSourceCoverage
RelationTargetCoverage
```

already requires complete relation root sets and therefore covers their reverse navigation registrations.

It is acceptable if the compiler also reports `NavigationConsumerCoverage` for the same root set when the derived/invariant source expression independently has such a path. A single `scope.Complete(set)` satisfies every reason for that set.

Do not suppress a requirement merely to make diagnostics prettier unless a test explicitly proves the normalization rule.

## 137.6 Task 137 Core tests

Extend `ConsistencyScopeRequirementTests` with at least:

1. direct scalar source-derived -> zero requirements;
2. one-segment reference source-derived -> zero navigation requirement;
3. `source.Navigation.Value` -> source set `NavigationConsumerCoverage`;
4. two-hop navigation -> one source-set navigation requirement;
5. value-object nesting -> no navigation requirement;
6. collection-navigation item dependency -> source set navigation requirement if the expression analyzer supports the shape;
7. composed derived preserves navigation requirement;
8. two-upstream composition unions navigation + relation requirements;
9. invariant direct source navigation adds navigation coverage;
10. projected direct selector remains `ProjectedConsumerCoverage` and is not incorrectly classified merely because the selector is a reference;
11. same set may have multiple reasons with deterministic ordering;
12. hash/scan and exact/conservative propagation choices do not change navigation scope requirements.

Run on both net8.0 and net10.0 before Task 138.

---

# Task 138 — Prove navigation-scope safety end to end

## Goal

Reproduce the false-authority bug against SQLite and prove the new requirement prevents it.

Use real EF tracking and real SQLite. Do not use InMemory provider for the decisive proof.

## 138.1 False-valid nested-navigation invariant

Create a compact model such as:

```text
Product
  Id
  CurrentLimit

OrderLine
  Id
  ProductId
  Product
  ReservedQuantity
```

Derived/invariant shape should include an ordinary nested dependency, for example:

```csharp
var currentLimit = model.Derived(orderLines)
    .Compute(line => line.Product.CurrentLimit);

var valid = model.Invariant(orderLines)
    .Using(currentLimit)
    .Must((line, limit) => line.ReservedQuantity <= limit);
```

Database:

```text
Product limit = 100
Line A reserved = 60
Line B reserved = 120
```

Runtime deliberately contains:

```text
Product
Line A only
```

Change:

```text
Product limit 100 -> 80
```

Without complete `orderLines`, `SaveChangesConsistently` must throw:

```text
IncompleteConsistencyScopeException
NavigationConsumerCoverage
```

before SQL and without runtime version advancement.

Then build an authoritative runtime containing both lines and supply:

```csharp
new ConsistencyScope().Complete(orderLines)
```

The invariant must discover/evaluate every affected runtime line through reverse navigation and reject the violating final state.

Do not require `.Complete(products)` merely for this nested dependency unless another selected policy independently needs it.

## 138.2 False materialized mirror through nested navigation

Add a separate SQLite proof where a materialized source-derived value follows a navigation:

```csharp
var currentPrice = model.Derived(orderLines)
    .Compute(line => line.Product.Price);

mappings.Materialize(currentPrice, line => line.CurrentPriceMirror);
```

With an incomplete order-line runtime, a product price change must fail on missing navigation consumer scope before mirror setters or SQL.

With authoritative runtime + complete `orderLines`, all affected tracked sources must receive the correct mirror.

Remember the existing materialization contract: every affected materialized source must be tracked by the saving `DbContext` instance. The positive test must track all affected order lines.

## 138.3 Interceptor parity

Repeat at least one navigation-scope failure through `ConsistencySaveChangesInterceptor` and prove the `Gaps` payload is identical to the extension API.

---

# Task 139 — Extract one immutable EF persistence-policy engine

## Goal

Before adding the high-level manual workflow, eliminate semantic duplication between convenience save, interceptor, and manual persistence planning.

Create one internal engine/snapshot used by all three hosts.

Suggested internal concepts:

```csharp
internal sealed record ConsistencyPersistencePolicySnapshot(
    IReadOnlySet<int> EnforcedInvariantIds,
    IReadOnlyList<...> Materializations,
    ConsistencySaveBehavior SaveBehavior,
    RuntimeImpactDetailLevel DetailLevel);
```

and an internal helper such as:

```csharp
internal static class ConsistencyPersistencePolicyEngine
{
    public static ConsistencyPersistencePolicySnapshot CaptureAndValidate(...);
    public static PreparedImpactPlan? PrepareAndPlan(...);
    public static void ApplyMaterializations(...);
}
```

Exact internal names may vary. Semantics may not.

## 139.1 Snapshot policy at workflow capture

A high-level workflow must not observe later mutation of `ConsistencyEfCoreMappings` or `ConsistencyScope` as if policy changed halfway through a transaction.

At capture/prepare time, snapshot the selected persistence policy into immutable internal data:

- enforced invariant IDs/definitions;
- materialization mappings;
- save behavior;
- detail level;
- successful scope precondition result.

Do not retain a live mutable list as the authoritative policy for a running unit of work.

The host-supplied scope is only an assertion used to pass/fail the precondition. After successful validation, the running unit does not need to reinterpret future `scope.Complete(...)` calls.

## 139.2 Scope validation remains before semantic planning

The shared engine must preserve:

```text
mapping/model ownership validation
-> scope validation
-> Prepare / PlanDetailed
-> enforced violation filtering
-> materialization
```

A scope failure must not execute invariant predicates or derived computations for the binding plan.

## 139.3 One violation filter

Keep exactly one implementation of:

```text
only .Enforce(...) invariant IDs block
unenforced violations do not block
```

Convenience save, interceptor, and manual policy-aware workflow must use the same filter.

## 139.4 One materialization implementation

Move/refactor the existing adapter-owned materialization writer so all hosts reuse:

- same-source-instance requirement;
- EF value comparer;
- skip equal writes;
- sink-only mapping checks;
- rollback journal when a setter fails;
- second `DetectChanges()` after successful writes.

Do not copy `ApplyMaterializations` into a second class.

## 139.5 Existing convenience transaction restrictions remain host policy

Do not move this rule into the shared engine:

```text
convenience save / interceptor reject ambient or externally controlled transactions
```

The new manual workflow explicitly exists to operate inside an application-owned transaction.

---

# Task 140 — Add policy-aware manual `ConsistencyPersistenceUnitOfWork`

## Goal

Replace the documentation-only manual safety requirement with a high-level object that cannot accidentally omit `ConsistencyScope`, `.Enforce(...)`, or `.Materialize(...)` policy processing.

## 140.1 Public API — use these names unless a compile-time conflict proves impossible

Add in the EF package:

```csharp
public sealed class ConsistencyPersistenceUnitOfWork
{
    public bool HasChanges { get; }

    public PreparedImpactPlan? PrepareAndPlan();

    public ChangeImpact? CommitAfterDatabaseCommit();

    public void Dispatch();
}
```

Add extension factory:

```csharp
public static ConsistencyPersistenceUnitOfWork CaptureConsistencyUnitOfWork(
    this DbContext context,
    ConsistencyRuntime runtime,
    ConsistencyEfCoreMappings mappings,
    ConsistencySaveOptions? options = null);
```

Place the extension in `ConsistencyDbContextExtensions` unless separation materially improves the source layout.

Do not require runtime/mappings/options again on later calls. The captured object owns those references/snapshots.

## 140.2 Capture semantics

`CaptureConsistencyUnitOfWork(...)` must:

1. null-check arguments;
2. validate EF mapping/model ownership;
3. validate authoritative scope for the selected persistence policies;
4. capture relationship evidence and low-level mutations immediately through the existing `ChangeTrackerAdapter` machinery;
5. create an immutable persistence-policy snapshot;
6. **allow** an existing application-owned transaction;
7. **allow** added entities whose consistency keys are still store-generated/temporary at capture time.

Why generated keys are allowed at capture:

```text
capture must happen before first SaveChanges so old relationship/mutation evidence is not lost
planning must happen after generated keys become final
```

Scope failure should normally occur at capture, before the first generated-key SQL command.

## 140.3 Meaning of `Complete(set)` for pending additions

Document this explicitly:

> For a captured persistence unit of work, `Complete(set)` asserts authoritative coverage of the planned final boundary: current runtime coverage plus lifecycle changes captured by this unit of work.

Do not reject a complete-set assertion merely because a newly added object has not yet been committed into runtime state.

## 140.4 `PrepareAndPlan()` semantics

`PrepareAndPlan()` must:

1. be callable exactly once;
2. verify that store-generated consistency keys required by captured additions are now final;
3. call the existing low-level `ConsistencyUnitOfWork.Prepare(runtime)`;
4. request planned invariant evaluation iff enforced invariants exist;
5. request planned derived evaluation iff `RecalculateAndValidate` + materializations exist;
6. create the binding plan exactly once;
7. apply the shared enforced-violation filter;
8. throw `ConsistencyInvariantViolationException` when selected enforced evaluations violate;
9. apply configured materializations through the shared writer;
10. call `context.ChangeTracker.DetectChanges()` after materialization;
11. return the exact retained `PreparedImpactPlan?` so callers can persist durable policy work/outbox rows.

Do not call `SaveChanges` inside this method.

If planning/invariant/materialization fails, runtime must remain unchanged and the persistence UoW becomes unusable for commit. The caller should rollback/discard/reconcile the EF context as documented.

## 140.5 Store-generated key not-ready exception

The existing convenience exception says to use the manual workflow. It is not appropriate when the caller is already using the manual workflow.

Add:

```csharp
public sealed class ConsistencyStoreGeneratedKeyNotReadyException : Exception
```

with internal constructor and a message equivalent to:

```text
Added entity 'X' uses store-generated consistency key 'Id' that is not final yet.
Save inside the current database transaction to obtain final generated values before calling PrepareAndPlan().
```

The convenience/interceptor path must continue throwing:

```csharp
ConsistencyStoreGeneratedKeyRequiresManualWorkflowException
```

before SQL.

The high-level manual object throws `...NotReadyException` only when `PrepareAndPlan()` is invoked before the first generated-key save has finalized the key.

Refactor the generated-key detection logic into one internal helper; do not duplicate its EF metadata rules.

## 140.6 State machine

Implement explicit internal state. Do not infer lifecycle from nullable fields alone.

Conceptual states:

```text
Captured
Planned
Committed
Dispatched
Faulted
```

Rules:

- `PrepareAndPlan()` in any state except `Captured` -> `InvalidOperationException`;
- successful `PrepareAndPlan()` -> `Planned`;
- any exception during prepare/plan/violation/materialization -> `Faulted`;
- `CommitAfterDatabaseCommit()` requires `Planned`;
- successful commit -> `Committed`;
- commit twice -> `InvalidOperationException`;
- `Dispatch()` requires `Committed`;
- successful dispatch -> `Dispatched`;
- dispatch twice after success -> `InvalidOperationException` unless existing resumable dispatch semantics require a narrowly documented alternative;
- if dispatch callback fails, do not mark dispatch complete before the callback layer reports success; preserve the existing retry/resume semantics.

## 140.7 `CommitAfterDatabaseCommit()` semantics

This method does **not** commit the database.

It means:

> The caller asserts that the external database transaction has already become durable; install the retained runtime plan now.

Call the existing low-level unit commit.

Wrap runtime install failure in the existing:

```csharp
ConsistencyRuntimeSynchronizationException
```

with the pre-commit runtime version, exactly as convenience save does.

Do not retry the DB operation.

## 140.8 `Dispatch()` semantics

Dispatch only after runtime commit.

Use the existing low-level dispatch/policy action machinery. Do not invent another callback queue.

---

# Task 141 — Refactor convenience save and interceptor onto the shared policy engine

## Goal

After Task 140 there must not be three independent implementations of consistency persistence semantics.

## 141.1 Convenience save

`SaveChangesConsistently` / `SaveChangesConsistentlyAsync` must continue to provide their current UX:

```text
reject unsupported external/ambient transaction
reject unresolved store-generated consistency key with RequiresManualWorkflow exception
validate mappings/scope
capture mutations
prepare + plan
reject enforced violations
apply mirrors
SaveChanges
runtime commit
Dispatch
```

Use the Task 139 policy engine for mapping/scope/plan/violation/materialization work.

Do not route convenience save through the public manual object's transaction state if doing so complicates semantics. Shared internal engine is sufficient.

## 141.2 Interceptor

Keep the interceptor thin and context-scoped.

It must use the same Task 139 engine and still:

- reject external/ambient transactions;
- reject unresolved generated consistency keys;
- store one pending prepared policy-aware operation per `DbContext`;
- commit exact runtime plan only from `SavedChanges`/`SavedChangesAsync`;
- discard pending state on failure/cancellation;
- preserve reentrant/overlap protection.

## 141.3 No semantic drift

Add parity tests ensuring extension, interceptor, and manual policy-aware planning produce the same:

- `IncompleteConsistencyScopeException.Gaps`;
- enforced invariant blocking decision;
- planned invariant evaluation IDs/states;
- planned derived evaluation IDs/values where applicable.

The manual workflow differs only in who controls SQL/transaction timing.

---

# Task 142 — Prove the manual generated-key and outbox workflows

## Goal

Make the previously documentation-only advanced path executable and fail-closed.

Use SQLite and real transactions.

## 142.1 Missing scope fails before first SQL

Scenario:

- relation-backed enforced invariant;
- Added entity uses store-generated consistency key;
- runtime scope is incomplete / no `ConsistencyScope` supplied.

Call:

```csharp
context.CaptureConsistencyUnitOfWork(runtime, mappings, options)
```

Expected:

```text
IncompleteConsistencyScopeException
before BeginTransaction/SaveChanges is necessary
DB unchanged
runtime unchanged
```

This proves generated-key workflows no longer bypass scope safety.

## 142.2 Generated key planning too early

With complete scope:

1. capture high-level unit;
2. begin transaction;
3. call `PrepareAndPlan()` before first `SaveChanges`.

Expected:

```text
ConsistencyStoreGeneratedKeyNotReadyException
runtime unchanged
no materialization
```

Then discard that persistence UoW. Do not attempt to reuse a faulted unit.

## 142.3 Generated-key invariant violation rollback

Flow:

```text
CaptureConsistencyUnitOfWork
-> BeginTransaction
-> first SaveChanges obtains key
-> PrepareAndPlan
-> enforced invariant violates
-> exception
-> rollback transaction
-> discard/reload DbContext
-> runtime remains unchanged
```

Assert database durable state does not contain the provisional added row after rollback.

## 142.4 Valid generated-key + materialization + durable work

Flow:

```text
CaptureConsistencyUnitOfWork
-> BeginTransaction
-> first SaveChanges obtains generated key
-> PrepareAndPlan
     -> scope already proven
     -> invariant evaluations bound
     -> derived evaluations bound
     -> mirrors written to tracked objects
-> persist plan.Result.GetDurablePolicyWork() / test outbox row if non-empty
-> second SaveChanges
-> Commit database transaction
-> CommitAfterDatabaseCommit
-> Dispatch
```

Assert:

- DB row durable;
- mirror durable;
- outbox/durable work corresponds to exact plan result;
- runtime version advances once;
- `CommitAfterDatabaseCommit()` performs no semantic recomputation;
- dispatch occurs after DB + runtime commit.

## 142.5 Runtime install failure after DB durability

Inject deliberate runtime commit failure after the database transaction commits.

`CommitAfterDatabaseCommit()` must throw:

```csharp
ConsistencyRuntimeSynchronizationException
```

Assert:

- DB transaction is durable;
- runtime remains at pre-commit version;
- exception contains original runtime failure;
- documentation says reconcile/rebuild runtime, never retry SQL blindly.

## 142.6 Ordering misuse matrix

Add focused tests:

```text
Commit before PrepareAndPlan -> throws
Dispatch before Commit -> throws
PrepareAndPlan twice -> throws
Commit twice -> throws
Dispatch twice after success -> throws
faulted planning unit cannot commit
mapping mutation after capture does not alter captured policy
scope mutation after successful capture does not alter captured policy
```

For callback failure, prove the existing resumable dispatch behavior is not accidentally destroyed.

---

# Task 143 — Documentation, naming cleanup, and EF boundary audit

## 143.1 Rewrite manual workflow docs

Update:

```text
docs/ef-core-consistency.md
docs/architecture.md
README.md
```

The recommended manual generated-key/outbox flow must use the new high-level API.

Show this shape:

```csharp
var scope = new ConsistencyScope()
    .Complete(orderLines)
    .Complete(allocations);

var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var tx = await db.Database.BeginTransactionAsync();

// Only required when generated values must become final before planning.
await db.SaveChangesAsync();

var plan = work.PrepareAndPlan();

if (plan is not null)
    PersistDurablePolicyWork(db, plan.Result.GetDurablePolicyWork());

await db.SaveChangesAsync();
await tx.CommitAsync();

work.CommitAfterDatabaseCommit();
work.Dispatch();
```

Clarify that a stable application-key workflow can call `PrepareAndPlan()` before the first SQL command and may need only one DB save.

## 143.2 Low-level API warning

Update XML docs for `ConsistencyUnitOfWork.PlanDetailed` and relevant low-level capture APIs:

> This is a runtime binding primitive. It does not apply `ConsistencyEfCoreMappings.Enforce`, `Materialize`, or `ConsistencyScope` persistence policy. Use `CaptureConsistencyUnitOfWork` for authoritative EF persistence workflows.

Do not make the low-level API throw merely because no scope exists; Core remains persistence-host agnostic.

## 143.3 Clean remaining product-level `Relations` wording

At baseline there are still messages such as:

```text
"store-generated Relations key"
"cannot feed the Relations graph"
"enforced relation invariants"
```

These are product-level references, not actual `Relation<TLeft,TRight>` concept names.

Change to neutral wording, for example:

```text
"store-generated consistency key"
"cannot feed the consistency graph"
"enforced consistency invariants"
```

Do **not** globally replace the word `Relation` where it denotes a real relation concept.

Run targeted active-source/doc greps and review each remaining `Relations` occurrence manually.

## 143.4 Update samples

The main stable-key EF sample should remain simple.

Add either:

- a compact second generated-key/manual transaction scenario to the existing EF sample; or
- a clearly runnable method in the same sample project.

It must compile and demonstrate the high-level manual workflow without pretending the library owns the transaction.

Do not create a new sample project unless necessary.

---

# Task 144 — Public API, regression matrix, release proof, and closeout

## 144.1 Public API baselines

Nothing has been published yet.

Add new public API directly to the appropriate `PublicAPI.Shipped.txt` baselines.

Core baseline change:

```text
ConsistencyScopeRequirementKind.NavigationConsumerCoverage = 3
```

EF baseline additions should include:

```text
ConsistencyPersistenceUnitOfWork
ConsistencyPersistenceUnitOfWork.HasChanges
ConsistencyPersistenceUnitOfWork.PrepareAndPlan
ConsistencyPersistenceUnitOfWork.CommitAfterDatabaseCommit
ConsistencyPersistenceUnitOfWork.Dispatch
ConsistencyStoreGeneratedKeyNotReadyException
DbContext CaptureConsistencyUnitOfWork extension
```

Keep both `PublicAPI.Unshipped.txt` files empty except their normal marker/header.

Do not add obsolete compatibility aliases.

## 144.2 Full regression matrix

Must preserve all existing proof areas:

- relation access semantics;
- exact/conservative propagation;
- incremental aggregates;
- projected dependency lifecycle integrity;
- binding plan rollback and no-rerun commit;
- affected planned invariant evaluation;
- affected planned derived evaluation;
- durable policy work;
- materialization sink-only validation;
- stable-key consistent saves;
- generated-key manual transaction semantics;
- external/ambient transaction rejection for convenience/interceptor;
- runtime synchronization failure boundary;
- dispatch failure boundary;
- authoritative scope relation coverage;
- authoritative scope projected consumer coverage;
- **new navigation consumer coverage**;
- **new policy-aware manual workflow**.

## 144.3 Mandatory local commands

Run from repository root:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build src/Raffinert.Consistency/Raffinert.Consistency.csproj -c Release -f net8.0 --no-restore
dotnet build src/Raffinert.Consistency/Raffinert.Consistency.csproj -c Release -f net10.0 --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-restore
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-restore

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-restore

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample -c Release --no-restore
dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample -c Release --no-restore

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes

dotnet pack src/Raffinert.Consistency/Raffinert.Consistency.csproj -c Release --no-restore
dotnet pack src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj -c Release --no-restore
```

Also run the repository's package/API verification scripts and fresh package consumer builds used by the existing release-candidate workflow.

## 144.4 Remote proof

After implementation commits are on `main`:

1. require normal push CI success on the exact implementation head;
2. run `Release candidate verification` manually on that exact head;
3. record run IDs, commit SHA, test counts, and artifact ID in `docs/release-candidate-verification.md`;
4. update this plan status to `COMPLETED` only after both remote gates pass;
5. update `docs/roadmaps/README.md` from Active to Completed.

Do not publish NuGet packages.
Do not create a tag.
Do not create a GitHub release.
Do not bump the version in this roadmap.

---

# 4. Required test names / proof intent

Exact xUnit method names can vary, but the weaker agent must implement tests equivalent to all of these intents.

## Core

```text
Source_nested_navigation_requires_navigation_consumer_coverage
Direct_source_member_requires_no_navigation_scope
Direct_reference_terminal_member_requires_no_navigation_scope
Value_object_path_requires_no_navigation_scope
Invariant_nested_navigation_requires_navigation_consumer_coverage
Navigation_requirement_composes_transitively
Projected_selector_keeps_projected_coverage_without_false_navigation_reason
Navigation_requirements_are_deterministic
```

## EF / SQLite

```text
Nested_navigation_incomplete_runtime_cannot_make_false_valid_claim
Nested_navigation_incomplete_runtime_cannot_persist_false_mirror
Interceptor_and_extension_report_same_navigation_scope_gap
Manual_capture_rejects_missing_scope_before_generated_key_sql
Manual_plan_before_generated_key_is_final_is_rejected
Manual_generated_key_violation_rolls_back_database_and_runtime
Manual_generated_key_valid_plan_persists_mirror_and_installs_exact_plan
Manual_runtime_install_failure_after_db_commit_throws_sync_exception
Manual_persistence_unit_of_work_rejects_out_of_order_calls
Captured_manual_policy_is_immutable_after_mapping_changes
```

Do not replace behavior assertions with message-only assertions.

---

# 5. Failure matrix

The implementation is incomplete until this matrix is represented in tests/docs.

| Failure point | DB durable? | Runtime committed? | Mirror writes | Required result |
|---|---:|---:|---|---|
| Missing scope at high-level capture | No | No | None | `IncompleteConsistencyScopeException` |
| Generated key not final at manual plan | No new consistency SQL required | No | None | `ConsistencyStoreGeneratedKeyNotReadyException` |
| Planning expression throws | No final durability | No | None/rolled back | original exception |
| Enforced invariant violates | No final durability | No | None | `ConsistencyInvariantViolationException` |
| Mirror setter throws | No final durability | No | adapter writes rolled back | setter exception |
| EF command fails after plan | No / transaction rolled back | No | POCO may still contain deterministic mirror | DB exception; discard/reload as appropriate |
| DB transaction commits, runtime install fails | Yes | No | durable | `ConsistencyRuntimeSynchronizationException`; rebuild/reconcile runtime |
| Dispatch callback fails | Yes | Yes | durable | retry resumable dispatch only |

For generated-key workflows, the first `SaveChanges` may execute SQL inside an uncommitted transaction. "No final durability" means the transaction must still be rollbackable and must not be committed after a consistency failure.

---

# 6. Commit discipline for the weak agent

Use small commits. Recommended sequence:

```text
1. fix(core): require scope for navigation consumers
2. test(core): prove navigation scope topology
3. test(ef): prove nested navigation scope safety
4. refactor(ef): centralize persistence policy planning
5. feat(ef): add policy-aware manual consistency unit of work
6. test(ef): prove generated key manual consistency workflow
7. docs: document policy-aware manual persistence
8. refactor: clean stale Relations product wording
9. test: complete consistency persistence failure matrix
10. chore: record scope completion release proof
```

After every code commit, run the narrow affected tests.

Do not stack a large rewrite and then repair it at the end.

---

# 7. Acceptance checklist

Tasks 137–144 are complete only when every statement below is true.

```text
[ ] Source/invariant nested navigation dependencies produce NavigationConsumerCoverage.
[ ] Navigation detection uses the same shared rule as NavigationIndexRegistry.
[ ] Direct scalar/direct terminal-reference/value-object paths are not falsely classified.
[ ] Relation and projected scope behavior from Tasks 130–136 remains green.
[ ] SQLite proves an incomplete navigation consumer set cannot silently validate.
[ ] SQLite proves an incomplete navigation consumer set cannot persist a wrong mirror.
[ ] Manual generated-key/outbox workflow no longer relies on remembering a separate scope check.
[ ] High-level manual capture uses ConsistencyEfCoreMappings + ConsistencySaveOptions.
[ ] Missing scope fails at manual capture before the first generated-key SQL whenever capture occurs first as documented.
[ ] PrepareAndPlan refuses unresolved generated consistency keys with a manual-specific exception.
[ ] Manual PrepareAndPlan uses the same enforced-violation filter as convenience/interceptor.
[ ] Manual PrepareAndPlan uses the same materialization writer as convenience/interceptor.
[ ] Manual plan returns the exact PreparedImpactPlan for outbox/durable work.
[ ] CommitAfterDatabaseCommit installs the retained plan without semantic rerun.
[ ] Runtime install failure after DB durability produces ConsistencyRuntimeSynchronizationException.
[ ] Dispatch ordering/retry semantics remain correct.
[ ] Low-level ConsistencyUnitOfWork remains available and is documented as policy-agnostic.
[ ] Convenience save and interceptor retain their transaction/generated-key restrictions.
[ ] No EF auto-loading was introduced.
[ ] No partition/tenant completeness inference was introduced.
[ ] Public API baselines are clean.
[ ] Core tests pass net8 + net10.
[ ] EF/SQLite tests pass net10.
[ ] Both samples run.
[ ] Format verification passes.
[ ] Pack/package consumer verification passes.
[ ] Push CI passes on exact implementation head.
[ ] Manual release-candidate verification passes on exact implementation head.
[ ] No NuGet package/tag/release was published by this wave.
```

---

# 8. Why this continuation matters

After Tasks 130–136, this was safe:

```text
relation aggregate -> enforced/materialized through convenience save
```

but these two paths still had holes:

```text
ordinary navigation dependency
Product.Price
   -> reverse owner index
   -> missing OrderLine consumer
   -> affected source silently undiscovered
```

and:

```text
manual generated-key/outbox workflow
   -> low-level PlanDetailed
   -> developer must remember scope/policy rules
   -> authoritative decision can bypass the automatic gate
```

After Tasks 137–144, the intended persistence story becomes coherent:

```text
Expression topology
    -> complete scope requirements, including reverse-navigation consumers

ConsistencyScope
    -> host assertion of authoritative final boundary

EF persistence policy
    -> one shared policy engine

Convenience save / interceptor / manual transaction workflow
    -> all enforce the same scope, invariant, and materialization semantics

Database durability
    -> exact retained runtime plan install
    -> dispatch last
```

That is the boundary required before describing the EF adapter as fail-closed for authoritative consistency decisions across its supported persistence workflows.
