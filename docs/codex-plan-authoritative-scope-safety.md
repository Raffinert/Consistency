# Codex implementation plan — authoritative consistency scope safety

Status: **COMPLETED**

Completed on 2026-09-15. Implementation head `009da18f01ab2e0eb4495d705628ea4708ab60fd`
passed CI run [35002380559](https://github.com/Raffinert/Consistency/actions/runs/35002380559)
and release-candidate run [35002982418](https://github.com/Raffinert/Consistency/actions/runs/35002982418).

Baseline commit: `170eb615e3df6021257f3ffb81ce90a06682c30e`

Tasks: **130–136**

This plan closes the most important remaining semantic gap before Raffinert.Consistency can make strong persistence-consistency claims: an invariant or materialized derived value can currently be evaluated correctly over the objects known to the runtime while the runtime contains only a subset of the authoritative database state.

The implementation must make that boundary explicit and enforceable. A consistent save must fail before SQL when the runtime cannot prove that the object sets required by an enforced invariant or persisted derived mirror are complete for the configured consistency scope.

This is intentionally a conservative first implementation. Do not add EF auto-loading, database querying, partition inference, distributed synchronization, or a generic query planner.

---

## 1. Problem statement

Today the EF adapter correctly states that it does not load missing database graph state, but the API still allows this shape:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(orderLines)
    .Map(allocations)
    .Enforce(capacityInvariant);

await db.SaveChangesConsistentlyAsync(runtime, mappings);
```

If the runtime contains only one allocation while the database contains several allocations for the same order line, an aggregate invariant can evaluate to `Valid` even though the database-global state would violate it.

Example:

```text
Database:
  OrderLine capacity = 100
  Allocation A = 60
  Allocation B = 60

Runtime:
  OrderLine capacity = 100
  Allocation A = 60

Runtime evaluation:
  60 <= 100  -> Valid

Authoritative database state:
  120 <= 100 -> Violated
```

The runtime did not compute incorrectly. It computed over an incomplete universe.

The same problem exists for automatic materialization:

```text
Database has fulfillment quantities 4 + 6
Runtime only knows fulfillment 4
Derived FulfilledQuantity = 4
EF materializes 4 into a persisted mirror
Correct value is 10
```

Therefore scope safety must gate both:

1. `.Enforce(invariant)`; and
2. `.Materialize(derived, property)` when `RecalculateAndValidate` is used.

---

## 2. Architectural boundary

Keep the existing architectural split:

```text
Core
  knows dependency topology
  knows which object-set coverage a derived/invariant needs
  knows whether a supplied scope proof satisfies those requirements

EF Core adapter
  decides that missing proof blocks SaveChanges
  throws persistence-facing exception before SQL
  never queries the database to fill the missing scope
```

Do **not** put EF concepts into Core.

Do **not** add methods such as:

```csharp
invariant.RequireLoadedDbSet();
derived.LoadMissingRows();
runtime.AttachDbContext(...);
```

Do **not** teach Core about `DbContext`, `DbSet`, `IQueryable`, transactions, tracking, or SQL.

---

## 3. Hard stops / non-goals

The weaker agent must obey these constraints.

### 3.1 No auto-loading

Do not issue EF queries while validating scope.

No:

```csharp
await context.Set<TEntity>().LoadAsync();
```

No generated `Where(...)` queries.

No per-relation database lookup.

### 3.2 No partition/key-level completeness in this wave

Do not attempt:

```text
"complete for OrderId = 123"
"complete for TenantId = X"
"complete for this hash bucket"
```

V1 completeness is whole-object-set completeness within the application-declared consistency boundary.

Partitioned completeness can be designed later.

### 3.3 No distributed runtime synchronization

Do not add Redis, Service Bus, event sourcing, change-data-capture, database notifications, or cross-pod runtime synchronization.

This plan does not make a runtime distributed.

### 3.4 No thread-safety redesign

`ConsistencyRuntime` remains externally synchronized and not thread-safe.

Do not add locks to every runtime method.

### 3.5 No broad runtime rewrite

Do not replace the dependency graph, relation runtime, planning journal, or binding-plan machinery.

Scope safety is metadata + validation around the existing model.

### 3.6 Do not confuse dependency-analysis completeness with data-scope completeness

These are different concepts:

```text
Dependency-analysis completeness
  = did expression analysis discover all members/external state that affect semantics?

Data-scope completeness
  = does this runtime contain all object instances required to make a cross-object consistency claim?
```

Existing `DependencyCompletenessIssue` / `AllowIncompleteDependencies()` behavior must remain unchanged.

Do not reuse those names for the new feature.

---

# Task 130 — Compile data-scope requirements in Core

## Goal

For every derived definition and invariant definition, Core must be able to answer:

```text
Which ObjectSet definitions must be complete before this definition can be treated as authoritative?
```

Do this from the already-compiled model topology. Do not inspect EF metadata.

## Required semantics

Add an internal representation such as:

```csharp
internal enum ScopeRequirementReason
{
    RelationSourceCoverage,
    RelationTargetCoverage,
    ProjectedConsumerCoverage
}

internal sealed record ScopeRequirement(
    IObjectSetDefinition Set,
    ScopeRequirementReason Reason);
```

Exact internal naming may vary, but keep the concepts explicit. Do not encode reasons only in strings.

Add a compiler/helper, preferably under `src/Raffinert.Consistency/Model/`, that computes transitive requirements.

Suggested file:

```text
src/Raffinert.Consistency/Model/ConsistencyScopeRequirements.cs
```

## Derivation algorithm

For a **source-only derived value**:

```csharp
model.Derived(orderLines)
    .Compute(line => line.OrderedQuantity * line.UnitPrice)
```

required complete sets = none.

Reason: evaluating a known source reads only that source.

For a **relation-backed derived value**:

```csharp
var matchingFulfillments = model.Relation(orderLines, fulfillments)...;

var fulfilled = model.Derived(orderLines)
    .Using(matchingFulfillments)
    .Compute(...);
```

required complete sets must include **both**:

```text
orderLines   -> RelationSourceCoverage
fulfillments -> RelationTargetCoverage
```

Why both are required:

- target completeness is needed to compute the value for a known order line;
- source completeness is needed when a target-side mutation occurs, because reverse propagation must be able to discover every affected source.

Do not implement the unsafe shortcut of requiring only the relation right side.

For a **same-source composed derived value**:

```csharp
remaining = Derived(orderLines).Using(fulfilled)...
```

required complete sets = transitive union of the upstream requirements.

Do not add `orderLines` merely because it is the source set of a same-source composition.

For a **projected upstream derived value**:

```csharp
allocationValidity = model.Derived(allocations)
    .Using(x => x.OrderLine, remainingQuantity)
    ...;
```

required complete sets =

1. all transitive requirements of `remainingQuantity`; plus
2. `allocations` with reason `ProjectedConsumerCoverage`.

Why: when the selected `OrderLine` changes, the reverse projection index can only discover all affected allocation consumers if every allocation in the authoritative consistency boundary is registered.

Do **not** require the upstream source set only because it is the projected target. Its own transitive requirements already describe what its computation needs.

For a **two-upstream composed value**, union requirements from both upstreams and de-duplicate by object-set definition/reference.

For an **invariant**, union the requirements of all upstream derived values.

An invariant that depends only on source-local derived values has zero data-scope requirements.

## Multiple reasons for one set

One object set may be required for several reasons.

Internally preserve all reasons or merge them deterministically. Do not produce duplicate public gaps for the same `(set, reason)` pair.

Ordering must be deterministic:

1. object-set model ID ascending;
2. requirement reason enum ascending.

## Required internal APIs

Expose enough internal API from the compiled model/runtime for adapters without duplicating graph analysis.

Recommended shape on `ConsistencyRuntime`:

```csharp
internal IReadOnlyList<ScopeRequirement> GetScopeRequirements(IDerivedDefinition definition);
internal IReadOnlyList<ScopeRequirement> GetScopeRequirements(IInvariantDefinition definition);
```

These methods must reject definitions from another compiled model.

Do not make the EF adapter recursively inspect `DerivedInput` itself.

Core owns requirement derivation.

## Tests for Task 130

Add a dedicated test file, for example:

```text
tests/Raffinert.Consistency.Tests/ConsistencyScopeRequirementTests.cs
```

Minimum cases:

1. source-only derived -> no required sets;
2. direct source-only invariant -> no required sets;
3. relation-backed derived -> left + right sets;
4. invariant over relation-backed derived -> same left + right sets;
5. composed derived -> transitive relation requirements preserved;
6. two-upstream composition -> union without duplicates;
7. projected composition -> projected consumer set added;
8. projected composition also retains upstream relation requirements;
9. one object set required for two reasons -> deterministic distinct reasons;
10. foreign derived/invariant definition rejected;
11. requirement result independent of relation access plan (hash vs scan);
12. requirement result independent of exact vs conservative propagation plan.

Do not use string matching as the only proof. Assert object-set IDs/references and reason enums internally.

### Task 130 completion gate

Do not start EF scope enforcement until all Task 130 tests pass on net8.0 and net10.0.

---

# Task 131 — Add a public `ConsistencyScope` proof object

## Goal

Applications need an explicit way to state which runtime object sets are complete for the consistency boundary they are claiming.

Add a Core public type:

```csharp
public sealed class ConsistencyScope
{
    public ConsistencyScope Complete<T>(ObjectSet<T> set) where T : class;
}
```

The exact backing representation stays internal.

Suggested file:

```text
src/Raffinert.Consistency/ConsistencyScope.cs
```

## Meaning of `Complete(set)`

Document it precisely:

> The application asserts that the runtime contains every object belonging to this object set within the authoritative consistency boundary used by the current operation.

The library does not verify that assertion against a database.

The scope object is a **proof supplied by the host**, not an auto-discovery mechanism.

## Required behavior

- `Complete(null)` must throw `ArgumentNullException`.
- repeated `Complete(theSameSet)` is idempotent;
- multiple object sets using the same CLR type remain distinct;
- object-set identity must be based on the actual model definition, not CLR `Type`;
- preserve fluent usage.

Example:

```csharp
var scope = new ConsistencyScope()
    .Complete(orderLines)
    .Complete(fulfillments)
    .Complete(allocations);
```

## Ownership validation

A scope can technically be constructed with sets from more than one model before it is used.

When a runtime validates a scope, every set present in the scope must belong to that runtime's compiled model.

If not, throw `ArgumentException` with a clear message such as:

```text
The consistency scope contains an object set from another compiled model.
```

Do not silently ignore foreign sets.

## Public diagnostic gap type

Add a small public immutable data type so failures are machine-readable.

Recommended shape:

```csharp
public enum ConsistencyScopeRequirementKind
{
    RelationSourceCoverage,
    RelationTargetCoverage,
    ProjectedConsumerCoverage
}

public sealed record ConsistencyScopeGap(
    int ObjectSetId,
    string? ObjectSetDefinitionKey,
    Type ObjectType,
    ConsistencyScopeRequirementKind RequirementKind);
```

Names may be adjusted for style, but keep all four data points.

Do not expose internal definition objects publicly.

Object-set ID is model-scoped diagnostic identity, not a durable cross-version wire identity. Document that.

## Core validation helper

Add internal runtime helpers that convert internal requirements + a supplied scope to public gaps:

```csharp
internal IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
    IDerivedDefinition definition,
    ConsistencyScope? scope);

internal IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
    IInvariantDefinition definition,
    ConsistencyScope? scope);
```

Rules:

- zero requirements + `scope == null` -> zero gaps;
- requirements exist + `scope == null` -> all requirements become gaps;
- supplied complete set satisfies every requirement for that set;
- deterministic ordering as Task 130;
- foreign scope set -> ownership exception before gap calculation.

## Public API baselines

Nothing has been published yet.

Add the new Core public API directly to:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
```

Keep `PublicAPI.Unshipped.txt` empty except its normal marker/header.

Do not introduce obsolete aliases.

---

# Task 132 — Enforce scope in EF consistent-save paths

## Goal

Before planning/materialization/SQL, EF must verify that every cross-object definition being used as a persistence guarantee has sufficient scope proof.

## Extend `ConsistencySaveOptions`

Add:

```csharp
public ConsistencyScope? Scope { get; init; }
```

Do not invent multiple scope modes in this wave.

No enum such as `Ignore`, `Warn`, `AutoLoad`, `BestEffort`.

The behavior is simple and safe:

```text
no cross-object requirements -> scope not required
cross-object requirements    -> missing proof throws before SQL
```

## Add persistence-facing exception

Add in the EF package:

```csharp
public sealed class IncompleteConsistencyScopeException : Exception
{
    public IReadOnlyList<ConsistencyScopeGap> Gaps { get; }
}
```

Constructor remains internal.

Message must state:

1. persistence was not attempted;
2. which object-set coverage is missing;
3. that the application must seed/maintain authoritative runtime coverage and declare it through `ConsistencyScope`;
4. that Raffinert will not auto-load it.

Do not call it a validation failure or invariant violation. It is a missing precondition for making the consistency claim.

## Exact coordinator placement

Current flow is approximately:

```text
DetectChanges
-> reject store-generated relation keys
-> mappings.Validate(...)
-> CaptureUnitOfWork
-> Prepare
-> PlanDetailed
-> reject enforced violations
-> apply materializations
-> SaveChanges
```

Insert scope validation **after mapping/model ownership validation but before CaptureUnitOfWork / semantic planning**.

Required flow:

```text
DetectChanges
-> reject unsupported transaction / generated-key convenience case
-> validate EF mappings and resolve definition ownership
-> validate data-scope requirements
-> CaptureUnitOfWork
-> Prepare
-> PlanDetailed
-> reject enforced invariant violations
-> apply mirrors
-> DetectChanges
-> SaveChanges
-> install exact plan
-> dispatch
```

The scope failure must occur before:

- domain semantic predicates execute for the binding plan;
- adapter-owned materialization writes;
- SQL.

## Which definitions require scope validation

### Enforced invariants

Every definition configured via:

```csharp
.Enforce(invariant)
```

must have its requirements checked on every `Validate` and `RecalculateAndValidate` save.

### Materialized derived values

Every definition configured via:

```csharp
.Materialize(derived, property)
```

must have its requirements checked only when:

```csharp
SaveBehavior == ConsistencySaveBehavior.RecalculateAndValidate
```

because `Validate` does not calculate/write mirrors.

This point is mandatory.

Do not implement invariant-only scope safety while leaving derived materialization unsafe.

### Unenforced / unmaterialized definitions

Do not require scope merely because the compiled model contains relation-backed derived values or invariants.

Only persistence policies actually invoked by the current EF save are gated.

Example:

```text
compiled model has 20 invariants
mappings.Enforce() selects 2
only those 2 invariants contribute scope requirements
```

## Merge gaps across definitions

If multiple enforced/materialized definitions require the same missing set/reason, de-duplicate the final exception gaps.

Ordering must be stable.

Do not throw one exception per definition.

## `.Map(...)` is not completeness proof

Critical rule:

```csharp
mappings.Map(allocations)
```

means only:

> translate tracked Allocation EF changes into this Raffinert ObjectSet.

It does **not** mean:

> all Allocation rows are loaded into the runtime.

Do not infer completeness from `.Map(...)`, `DbSet`, or current `ChangeTracker` entries.

Only `options.Scope.Complete(set)` supplies the proof.

## EF public API baseline

Update:

```text
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
```

for:

- `ConsistencySaveOptions.Scope`;
- `IncompleteConsistencyScopeException`;
- `IncompleteConsistencyScopeException.Gaps`.

Do not touch `PublicAPI.Unshipped.txt` except to keep it empty.

---

# Task 133 — Add correctness proofs for incomplete and complete scope

This task is mandatory. Do not treat documentation as sufficient proof.

## Core behavioral matrix

Core tests from Tasks 130–131 must prove requirement derivation and ownership.

## EF in-memory/unit tests

Add tests covering:

1. source-local enforced invariant succeeds with `Scope == null`;
2. relation-backed enforced invariant + `Scope == null` throws `IncompleteConsistencyScopeException`;
3. only relation left declared complete -> right gap remains;
4. only relation right declared complete -> left gap remains;
5. both required relation sets declared complete -> save proceeds;
6. projected invariant requires projected consumer set as well as upstream requirements;
7. foreign-model set inside scope -> clear ownership exception;
8. duplicate `Complete(set)` does not duplicate gaps or fail;
9. multiple enforced invariants merge duplicate gaps;
10. unenforced relation-backed invariant does not require scope;
11. `Validate` mode ignores materialization-only scope requirements;
12. `RecalculateAndValidate` requires materialized-derived scope requirements;
13. source-only materialized derived does not require scope;
14. relation-backed materialized derived with incomplete scope fails before setter invocation;
15. scope failure leaves runtime version unchanged;
16. scope failure leaves EF entity states/current values unchanged except the caller's pre-existing mutations;
17. scope failure executes no repair callback;
18. scope failure does not call SQL provider save.

## Mandatory SQLite false-valid proof

Add a real SQLite test reproducing the original bug class.

Suggested scenario:

```text
OrderLine Id=1 Capacity=100
Allocation A OrderLineId=1 Quantity=60
Allocation B OrderLineId=1 Quantity=60
```

Persist all three rows in SQLite.

Create a runtime seeded with:

```text
OrderLine
Allocation A
```

but intentionally omit Allocation B.

Create a relation-backed aggregate/invariant that would see only 60 in the incomplete runtime.

Attempt a consistency save with `.Enforce(...)` and no complete scope proof.

Expected:

```text
IncompleteConsistencyScopeException
SQL not executed for the pending mutation
runtime version unchanged
```

Then construct a new authoritative runtime containing both allocations, declare the required sets complete, and show that the same logical state evaluates the real total and correctly blocks a violating save (or accepts a corrected state).

This test must demonstrate why the scope feature exists. Do not reduce it to a synthetic exception-only test.

## Mandatory materialization false-value proof

SQLite test:

```text
Fulfillment A Quantity=4
Fulfillment B Quantity=6
runtime initially knows only A
materialized FulfilledQuantity would otherwise become 4 instead of 10
```

Without scope proof, `SaveChangesConsistently` must throw before writing the wrong mirror.

With full authoritative runtime + complete scope proof, the mirror must become 10.

---

# Task 134 — Diagnostics and documentation

## README

Update the EF consistency section so the main example that uses a cross-object invariant/materialization shows scope explicitly.

Example:

```csharp
var scope = new ConsistencyScope()
    .Complete(orderLines)
    .Complete(fulfillments);

await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    new ConsistencySaveOptions
    {
        Scope = scope
    },
    cancellationToken);
```

Do not imply that `.Map(...)` loads or proves a complete set.

## `docs/ef-core-consistency.md`

Replace the current caveat-only scope wording with a contractual section.

Explain:

```text
ConsistencyScope is an application assertion.
Raffinert does not query the database to verify it.
Cross-object Enforce/Materialize operations fail closed when the required proof is absent.
Source-local invariants/derived mirrors need no complete-set declaration.
```

Include examples for:

- source-local invariant with no scope;
- relation aggregate requiring both relation sets;
- projected consumer requiring downstream consumer-set coverage;
- why `.Map(...) != .Complete(...)`.

## `docs/architecture.md`

Add a distinct section named approximately:

```text
Dependency completeness vs data-scope completeness
```

Explain both meanings and do not conflate them.

Document the requirement derivation rules from Task 130.

Also document the limitation:

> Whole-set completeness is intentionally coarse in this version. Partition/key-scoped completeness is not implemented.

## Debug/diagnostics

Do not add a large new diagnostics subsystem.

However, if a simple addition is straightforward, include required complete object-set IDs/keys in compiled diagnostics for derived/invariant definitions.

This is optional for this task only if it would significantly expand public API. Exception gaps are mandatory.

Do not block Task 134 on a diagnostics redesign.

---

# Task 135 — Preserve transaction, binding-plan, and failure semantics

Scope safety must not weaken the existing correctness work.

## Required regression proofs

Existing tests must continue proving:

- `PlanDetailed` remains binding;
- `Commit(plan)` does not rerun semantic predicates;
- runtime rollback on planning exception remains atomic;
- materialization rollback on setter failure remains correct;
- generated-key manual workflow remains unchanged;
- ambient/external transaction convenience-save rejection remains unchanged;
- database-success/runtime-failure still throws `ConsistencyRuntimeSynchronizationException`;
- dispatch occurs only after runtime commit.

## Manual generated-key workflow

Do not force `ConsistencyScope` into core `PlanDetailed` APIs in this wave.

The manual generated-key workflow remains application-controlled. If an application wants persistence enforcement around that workflow, it must validate scope through the same Core requirement/gap helper exposed to the EF adapter or through a small EF helper if necessary.

Preferred solution: add an EF public helper only if the manual workflow otherwise has no safe way to reuse scope validation.

Possible API if needed:

```csharp
mappings.ValidateScope(runtime, scope, saveBehavior);
```

But do not add this public method speculatively. First try to keep it internal to the consistent-save coordinator and document that the current automatic scope gate applies to convenience saves/interceptor paths.

If manual generated-key enforcement is documented as equivalent to automatic `.Enforce`, then it must gain an explicit reusable validation API and tests. Do not leave contradictory docs.

## Interceptor parity

The `ConsistencySaveChangesInterceptor` must use the exact same coordinator scope validation as `SaveChangesConsistently`.

Do not duplicate validation logic in the interceptor.

Add parity tests:

```text
extension method incomplete scope -> same exception/gaps
interceptor incomplete scope      -> same exception/gaps
```

---

# Task 136 — Final proof, API baseline, release gate, and closeout

## Required searches

Before closeout:

```bash
git grep -n "IncompleteConsistencyScope"
git grep -n "ConsistencyScope"
```

Review every hit.

Also search for documentation statements that still imply tracking alone proves global correctness:

```bash
git grep -ni "tracked-only"
git grep -ni "authoritative scope"
git grep -ni "missing graph"
```

Reconcile wording with the new fail-closed contract.

## Required local commands

Run at minimum:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes

dotnet pack src/Raffinert.Consistency/Raffinert.Consistency.csproj -c Release --no-build

dotnet pack src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj -c Release --no-build
```

Then run the repository's release-candidate verification script/workflow exactly as currently documented.

## Remote proof

After all implementation commits:

1. push `main`;
2. wait for normal CI on the implementation head to pass;
3. run `Release candidate verification` through `workflow_dispatch` on that exact implementation head;
4. require success;
5. only then close this roadmap.

Do not use an older green RC run as proof for this change.

## Roadmap closeout

Update `docs/roadmaps/README.md` only after all gates are green.

Mark Tasks 130–136 completed and record the implementation-head SHA used for remote proof.

---

# Exact API target for the weaker agent

The intended public surface after this wave is approximately:

```csharp
namespace Raffinert.Consistency;

public sealed class ConsistencyScope
{
    public ConsistencyScope Complete<T>(ObjectSet<T> set) where T : class;
}

public enum ConsistencyScopeRequirementKind
{
    RelationSourceCoverage,
    RelationTargetCoverage,
    ProjectedConsumerCoverage
}

public sealed record ConsistencyScopeGap(
    int ObjectSetId,
    string? ObjectSetDefinitionKey,
    Type ObjectType,
    ConsistencyScopeRequirementKind RequirementKind);
```

and:

```csharp
namespace Raffinert.Consistency.EntityFrameworkCore;

public sealed class ConsistencySaveOptions
{
    public ConsistencySaveBehavior SaveBehavior { get; init; };
    public RuntimeImpactDetailLevel DetailLevel { get; init; }
    public ConsistencyScope? Scope { get; init; }
}

public sealed class IncompleteConsistencyScopeException : Exception
{
    public IReadOnlyList<ConsistencyScopeGap> Gaps { get; }
}
```

Small naming adjustments are acceptable only if they improve consistency with existing API conventions. Do not change semantics.

---

# Examples the implementation must support

## Source-local invariant — no scope declaration needed

```csharp
var lines = model.Objects<OrderLine>().Key(x => x.Id);
var positive = model.Derived(lines).Compute(x => x.OrderedQuantity >= 0);
var invariant = model.Invariant(lines).Using(positive).Must((_, value) => value);

var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Enforce(invariant);

await db.SaveChangesConsistentlyAsync(runtime, mappings);
```

This must continue working without `ConsistencyScope` because no cross-object coverage is required.

## Relation-backed invariant — scope required

```csharp
var lines = model.Objects<OrderLine>().Named("order-lines").Key(x => x.Id);
var allocations = model.Objects<Allocation>().Named("allocations").Key(x => x.Id);

var byLine = model.Relation(lines, allocations)
    .Where((line, allocation) => line.Id == allocation.OrderLineId);

var allocated = model.Derived(lines)
    .Using(byLine)
    .Compute((_, rows) => rows.Sum(x => x.Quantity));

var capacity = model.Invariant(lines)
    .Using(allocated)
    .Must((line, quantity) => quantity <= line.Capacity);
```

This requires:

```csharp
var scope = new ConsistencyScope()
    .Complete(lines)
    .Complete(allocations);
```

Both are mandatory in v1.

## Relation-backed materialization — same protection

```csharp
mappings.Materialize(allocated, x => x.AllocatedQuantity);
```

With `RecalculateAndValidate`, the same `lines + allocations` completeness proof is required before the mirror can be written.

---

# Acceptance criteria

The roadmap is complete only when all statements below are true.

```text
Core can deterministically derive complete-object-set requirements for derived values and invariants.

Relation-backed requirements include both source and target sets.

Projected dependencies add the projected consumer/source set for reverse-routing completeness.

ConsistencyScope explicitly records host assertions of complete object sets.

Multiple ObjectSet values with the same CLR type remain distinguishable.

Foreign-model object sets in a scope fail explicitly.

EF Enforce fails closed before SQL when required scope proof is missing.

EF RecalculateAndValidate fails closed before mirror writes/SQL when a materialized derived value lacks required scope.

Validate mode does not demand scope solely for unused materializations.

Source-local invariants and source-local materializations do not require artificial full-set scope declarations.

.Map(...) is never treated as proof of completeness.

No EF auto-loading or SQL querying is introduced by scope validation.

A SQLite test proves the false-valid aggregate case is blocked.

A SQLite test proves a wrong partial aggregate mirror cannot be persisted.

Extension-method and interceptor paths have identical scope behavior.

Existing binding-plan, transaction, generated-key, rollback, synchronization-failure, and dispatch semantics remain green.

Core and EF PublicAPI.Shipped baselines contain the new public surface; Unshipped remains empty.

Normal CI and a fresh workflow_dispatch RC verification are green on the exact implementation head.
```

---

# Commit guidance for a weaker agent

Prefer small commits in this order:

```text
feat(core): compile consistency scope requirements
feat(core): add explicit consistency scope proof
feat(ef): reject incomplete persistence consistency scope
test: prove incomplete scope cannot validate or materialize
docs: document authoritative consistency scope contract
chore: finalize scope safety release proof
```

Do not combine unrelated refactors, formatting sweeps, naming changes, or performance optimizations into these commits.

If implementation reveals that the requirement derivation rules above cannot be satisfied without changing fundamental runtime semantics, **STOP implementation and document the exact contradiction in the roadmap**. Do not invent a weaker guarantee silently.
