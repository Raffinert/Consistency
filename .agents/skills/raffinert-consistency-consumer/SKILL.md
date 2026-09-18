---
name: raffinert-consistency-consumer
description: Use Raffinert.Consistency 0.2 API correctly in downstream .NET applications. Apply when an AI must design or implement relations, derived values, invariants, materialization, EF Core mappings, authoritative scope, consumer discovery, or consistent save boundaries in application code that consumes Raffinert.Consistency. This skill is NOT for changing Raffinert.Consistency library internals.
---

# Raffinert.Consistency consumer coding skill

## Purpose

Use this skill when modifying a **consumer application** that references:

```text
Raffinert.Consistency
Raffinert.Consistency.EntityFrameworkCore
```

This skill targets the **0.2 API line**.

Do not generate removed v1 vocabulary:

```text
Using(...)
Compute(...)
Incrementally()
ConsistencyRuntime.Get(...)
ConsistencyEfCoreMappings.Materialize(...)
DerivedUsingBuilder
InvariantUsingBuilder
```

Use the v2 vocabulary instead:

```text
From(...)
DependsOn(...)
Select(...)
Sum / Count / LongCount / Any
MaterializeTo(...)
Evaluate(...)
Materialize(...)
```

The goal is to turn existing business consistency rules into a declarative dependency graph without moving domain behavior into the library or relying on every mutation path to remember manual recalculation.

This skill is consumer-only. If the current repository is `Raffinert/Consistency` itself and the task changes compiler/runtime/adapter internals, use the contributor/architecture documentation instead.

All examples below are domain-neutral. Rename types and members to the consumer application's real ubiquitous language.

---

# 1. Mental model

Treat Raffinert.Consistency as a **deferred transactional reactive consistency graph** around ordinary .NET objects.

Keep these concepts separate:

```text
From(...)          = logical value flow from another graph node
DependsOn(...)     = tracked member read hidden inside ordinary calculator code
Select(...)        = define a scalar logical derived value
Sum/Count/Any      = declare semantic aggregate; runtime chooses optimized plan
MaterializeTo(...) = declare an optional physical mirror/sink
Evaluate(...)      = make/read logical value current, without writing the mirror
Materialize(...)   = synchronize physical representation
Invariant          = required truth
Repair             = consequence handling
```

The most important boundary is:

```text
logical derived value != materialized property
```

A materialized property is an output representation. It is not the source of truth for downstream Raffinert calculations.

Typical flow:

```text
ordinary POCO mutation
        ↓
mutation evidence / EF ChangeTracker
        ↓
declared dependency graph
        ↓
Dirty / Invalid propagation
        ↓
logical evaluation as needed
        ↓
invariant / repair policy
        ↓
optional mirror materialization
        ↓
SQL durability
        ↓
exact runtime-plan installation
```

Do not design application services around calling recalculation methods after every setter.

---

# 2. Before writing code

Inspect the consumer repository first and identify:

1. domain/entity types involved;
2. `DbContext` and EF mappings if EF is used;
3. stable application keys for every Raffinert `ObjectSet`;
4. the existing formula/rule currently maintained manually;
5. every property that can change the result;
6. whether each dependency is a direct source member, a logical derived value, a relation, or a projected derived value;
7. whether the result needs a persisted mirror;
8. whether violation must block SQL or schedule later repair;
9. whether reverse consumers/contributors can be unloaded;
10. whether raw SQL, bulk operations, triggers, cascades, jobs, or other processes mutate relevant state outside EF tracking;
11. transaction/outbox/generated-key requirements;
12. runtime ownership and serialization boundary.

Write the dependency chain down before coding.

Example:

```text
SourceItem.Value ──┐
                  ├──> CombinedValue ──> NormalizedValue ──> invariant
TargetItem.Value ──┘
```

If you cannot state the graph clearly, do not guess the Raffinert configuration.

---

# 3. Object sets

Create an `ObjectSet<T>` for objects whose identity/lifecycle or own derived/invariant state Raffinert models.

```csharp
var records = model.Objects<Record>()
    .Key(x => x.Id);
```

Use a stable application key.

Do not use a mutable semantic property as the object-set key.

Do not create an object set merely because a nested reference is read. A referenced object needs its own set when its lifecycle/identity/derived state participates in the graph, not simply because a source-local expression traverses a navigation.

Object-set identity is stronger than CLR type identity. Two sets of the same CLR type are different graph owners.

---

# 4. Derived values

## 4.1 Source-local expression

When the formula is analyzable directly from the source:

```csharp
var total = model.Derived(records)
    .Select(record => record.Quantity * record.UnitValue)
    .Named("total");
```

Prefer analyzable expressions when practical.

## 4.2 Opaque calculator: `DependsOn`

When ordinary application code hides the reads Raffinert cannot infer, declare all hidden source-member inputs explicitly:

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .Named("combined-value");
```

Semantics:

```text
DependsOn = track changes to these member paths
```

It does **not** pass those values as additional calculator arguments.

Rules:

- declare every hidden modeled input;
- keep calculator deterministic from modeled state;
- do not use `DependsOn` to pretend external services/time/random/database lookups are tracked;
- do not use `DependsOn` instead of `From` when the logical input is another `Derived` handle;
- do not depend on a property registered with `MaterializeTo`; depend on the logical handle instead.

## 4.3 Derived-from-derived: `From`

When one derived value logically consumes another:

```csharp
var remainingCapacity = model.Derived(containers)
    .From(usedCapacity)
    .Select((container, used) => container.Capacity - used)
    .Named("remaining-capacity");
```

`From(usedCapacity)` means the current **logical** `usedCapacity` value flows into this node.

Do not read a persisted mirror of `usedCapacity` inside the downstream calculation.

## 4.4 Multiple logical inputs

Chain `From` calls:

```csharp
var result = model.Derived(records)
    .From(first)
    .From(second)
    .Select((record, firstValue, secondValue) =>
        Combine(firstValue, secondValue));
```

The order of `From` calls defines calculator argument order.

## 4.5 Projected upstream dependency

When a source consumes a logical derived value owned by a referenced object:

```csharp
var assignmentValid = model.Derived(assignments)
    .From(x => x.Container, remainingCapacity)
    .Select((assignment, remaining) => assignment.Amount <= remaining)
    .Named("assignment-valid");
```

Meaning:

```text
Assignment
  -> Assignment.Container
  -> logical RemainingCapacity owned by that Container
```

For mixed projected + local logical inputs, chain them:

```csharp
var actualQuantity = model.Derived(links)
    .From(x => x.PurchaseOrderLine, remainingQuantity)
    .From(unitRate)
    .Select((link, remaining, rate) => ...);
```

Treat projected dependencies as requiring authoritative projected-consumer coverage. `DiscoverConsumers` does not substitute projected-consumer completeness in the current supported model.

---

# 5. Relations and recognized aggregates

When a source depends on matching objects, declare the relation once:

```csharp
var contributionsForContainer = model.Relation(containers, contributions)
    .Where((container, contribution) =>
        container.Id == contribution.ContainerId)
    .Named("contributions-for-container");
```

Then consume it through `From`.

Preferred aggregate syntax:

```csharp
var usedCapacity = model.Derived(containers)
    .From(contributionsForContainer)
    .Sum(x => x.Amount)
    .Named("used-capacity");
```

Recognized operators are:

```text
Sum
Count
LongCount
Any
```

Do **not** generate `.Incrementally()`.

These semantic operators select the existing incremental execution plans automatically.

When a relation computation is not a recognized aggregate, use `Select`:

```csharp
var summary = model.Derived(containers)
    .From(contributionsForContainer)
    .Select((container, related) => CustomSummary(container, related));
```

The relation predicate remains semantic authority. Do not duplicate the matching rule in application services.

---

# 6. Dirty vs Invalid and `Impact`

Use `Impact` when stale-state safety differs by kind/direction of change.

Example:

```csharp
var usedCapacity = model.Derived(containers)
    .From(contributionsForContainer)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Sum(x => x.Amount);
```

Typical interpretation:

```text
Dirty   = cached value is stale but may remain usable until reevaluated
Invalid = cached value must not be relied upon before successful reevaluation/repair
```

For source members with asymmetric semantics, use member-specific impact classification when the domain requires it.

Do not mark everything Invalid defensively. Do not mark destructive changes Dirty merely to postpone required repair.

---

# 7. Invariants and repair

When correctness consumes a logical derived value:

```csharp
var capacityValid = model.Invariant(containers)
    .From(remainingCapacity)
    .Must((container, remaining) => remaining >= 0)
    .Named("capacity-valid");
```

Multiple logical inputs can be composed with another `From` before `Must`.

An invariant does not automatically block SQL. EF blocks only invariants explicitly configured with `.Enforce(...)`.

If temporary inconsistency is allowed and later repair is required, use the library's repair/reaction API rather than hiding repair inside the derived calculator.

Important boundary:

```text
Materialize != Repair
```

Synchronizing a mirror must not be treated as executing repair.

---

# 8. Materialization

## 8.1 Declare the mirror in Core

Materialization metadata belongs to the derived definition:

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .MaterializeTo(x => x.CombinedValue)
    .Named("combined-value");
```

Do **not** use the removed EF API:

```csharp
mappings.Materialize(...); // removed in 0.2
```

A materialized property is a sink-only mirror.

Never feed it back into:

```text
ObjectSet key
Relation predicate
DependsOn
Derived calculation
Invariant dependency
Projected selector
```

If another calculation needs the value, use:

```csharp
.From(combinedValue)
```

## 8.2 Runtime logical read

Use:

```csharp
var current = runtime.Evaluate(combinedValue, association);
```

`Evaluate`:

```text
Fresh -> returns cached logical value
Dirty/Invalid -> evaluates as required and makes logical state Fresh
never writes the MaterializeTo property
```

Do not use removed `runtime.Get(...)`.

## 8.3 Targeted materialization

When one specific mirror must be synchronized:

```csharp
var current = runtime.Materialize(combinedValue, association);
```

This synchronizes only that derived representation and returns the logical value.

## 8.4 Object materialization

When all configured materialized derived properties on one source object should be synchronized:

```csharp
runtime.Materialize(association);

Use(association.CombinedValue, association.OtherDerivedMirror);
```

Object materialization:

- evaluates only what is needed;
- writes only mirrors physically located on the requested object;
- does not materialize dependency objects/downstream objects;
- does not dispatch repair;
- skips equal values when possible;
- uses evaluate-first/write-second behavior and restores previous physical values if a later mirror setter fails.

A direct property read can still be stale **before** materialization. Do not assume `MaterializeTo` turns POCO getters into live computed properties.

---

# 9. Build once; keep declarations together

Prefer one composition component that owns sets, definitions, compiled model, and EF policy.

Example:

```csharp
public sealed class ConsistencyDefinition
{
    public ObjectSet<Association> Associations { get; }
    public Derived<Association, decimal?> CombinedValue { get; }
    public CompiledConsistencyModel Compiled { get; }
    public ConsistencyEfCoreMappings EfMappings { get; }

    public ConsistencyDefinition()
    {
        var model = new ConsistencyModelBuilder();

        Associations = model.Objects<Association>()
            .Key(x => x.Id);

        CombinedValue = model.Derived(Associations)
            .DependsOn(
                x => x.Source.Value,
                x => x.Target.Value)
            .Select(ValueCalculator.Calculate)
            .MaterializeTo(x => x.CombinedValue)
            .Named("combined-value");

        Compiled = model.Build();

        EfMappings = new ConsistencyEfCoreMappings()
            .Map(Associations);
    }
}
```

Do not introduce a new DI architecture solely for Raffinert.

The runtime is mutable and not thread-safe. Do not register one globally shared runtime across concurrent unrelated operations unless the application has an explicit serialization/partition boundary that makes that correct.

---

# 10. EF Core persistence policy

`ConsistencyEfCoreMappings` owns EF-specific persistence/coverage policy, not logical computation definitions.

## 10.1 Map sets

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(containers)
    .Map(contributions);
```

`Map(set)` means tracked changes/lifecycle for this exact Raffinert set are translated into the consistency protocol.

It does **not** prove all database rows of that type are loaded.

## 10.2 Enforce invariants

```csharp
mappings.Enforce(capacityValid);
```

Only explicitly enforced invariant violations block persistence.

## 10.3 Materialized mirrors

Do not configure them on `ConsistencyEfCoreMappings`.

The EF adapter consumes Core `MaterializeTo(...)` descriptors automatically for mapped source sets.

---

# 11. Authoritative coverage: never fake completeness

Cross-object consistency requires proof that reverse consumers/contributors are not missing.

Supported proof strategies:

```text
A. closed world
   ConsistencyScope.Complete(set)

B. targeted open world for eligible direct reference navigation
   DiscoverConsumers(...)
```

Never use `Complete(set)` merely to silence `IncompleteConsistencyScopeException`.

`Complete(set)` is a host assertion. Raffinert trusts it.

Use it only when every relevant object in that consistency boundary is represented in the planned/runtime state.

---

# 12. `DiscoverConsumers`

Use targeted discovery when:

- a derived/invariant reads a direct reference path such as `Association.Source.Value`;
- the referenced target can change while some root consumers are unloaded;
- the host can query every authoritative consumer for the requested targets;
- the path is a supported direct non-collection EF reference navigation.

Example:

```csharp
mappings.DiscoverConsumers(
    associations,
    x => x.Source,
    (db, sources) =>
    {
        var ids = sources.Select(x => x.Id).ToArray();

        return db.Set<Association>()
            .Where(x => ids.Contains(x.SourceId))
            .Include(x => x.Source)
            .Include(x => x.Target);
    });
```

Resolver requirements:

1. return every persisted consumer for requested targets within the authoritative boundary;
2. use the same tracked `DbContext`;
3. do not return `AsNoTracking()` roots;
4. load all references needed by active formulas, not just the discovery navigation;
5. preserve tenant/security/soft-delete semantics defining the authoritative partition;
6. batch targets;
7. a safe superset is acceptable;
8. current tracked retargeting must remain authoritative over stale database relationship state.

Do not invent support for:

```text
multi-hop external discovery
collection-navigation discovery
relation source/target completeness through DiscoverConsumers
projected-consumer completeness through DiscoverConsumers
automatic Include generation
automatic lazy-loading correctness
automatic proof that the host query did not under-fetch
```

If the required shape is unsupported, use genuine `Complete(set)` coverage or redesign the boundary. Fail closed rather than weakening consistency.

---

# 13. Coverage decision table

| Situation | Correct strategy |
|---|---|
| source-local calculation | usually no whole-set declaration |
| direct nested reference dependency; every root genuinely loaded | `Complete(rootSet)` may be valid |
| direct nested reference dependency; roots may be unloaded | `DiscoverConsumers(rootSet, directNavigation, query)` |
| relation-backed aggregate | relation sets must be authoritatively covered |
| relation source/target coverage | `DiscoverConsumers` does not substitute it |
| projected upstream dependency | projected consumer set must be complete |
| collection-navigation dependency | current targeted discovery unsupported; complete set or redesign |
| multi-hop navigation dependency | current targeted discovery unsupported; complete set or redesign |

---

# 14. Consistent save boundary

For ordinary stable-key EF workflows:

```csharp
await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    new ConsistencySaveOptions
    {
        Scope = scope
    },
    cancellationToken);
```

Use `ConsistencySaveBehavior.Validate` when the operation should evaluate affected enforced invariants without writing configured materialized mirrors.

Use default `RecalculateAndValidate` when affected materialized mirrors must also be synchronized and persisted.

Do not call ordinary `SaveChangesAsync()` first and then try to make Raffinert catch up unless deliberately using the documented manual Unit of Work flow.

---

# 15. Manual Unit of Work

Use manual UoW when the application owns the transaction, generated semantic values must become final before planning, or durable repair/outbox work must be written atomically.

```csharp
var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

// Only if generated semantic values must become final first.
await db.SaveChangesAsync(cancellationToken);

var plan = work.PrepareAndPlan();

if (plan is not null)
{
    var durableWork = plan.Result.GetDurablePolicyWork();
    PersistDurablePolicyWork(db, durableWork);
}

await db.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);

work.CommitAfterDatabaseCommit();
work.Dispatch();
```

Rules:

- capture before the first relevant save;
- plan only after required generated semantic values are final;
- install runtime state only after database durability;
- do not dispatch repair before commit;
- never reuse a stale plan after additional tracked mutations.

---

# 16. Runtime ownership and concurrency

The runtime is mutable and not thread-safe.

Before registering it in DI, identify:

```text
who owns the runtime
which object partition it represents
how concurrent access is serialized
how/when it is seeded or rebuilt
what happens when external writers bypass the process
```

Do not casually register one application-wide singleton runtime for concurrent requests.

---

# 17. Invisible database mutations

Search consumer code for:

```text
ExecuteUpdate
ExecuteDelete
raw SQL writes
bulk update/delete libraries
stored procedures
triggers
Cascade / SetNull effects
other services/processes writing the same rows
```

If they can mutate modeled state without tracked evidence, document an explicit strategy:

```text
publish exact mutation evidence
reconcile
rebuild/reseed runtime
serialize writes through one owner
or exclude the state from this authoritative boundary
```

Do not assume EF ChangeTracker sees database-side effects.

---

# 18. Required tests

At minimum choose tests matching the real integration:

## Formula/value flow

```text
[ ] each declared input changes the result correctly
[ ] unrelated changes do not
[ ] downstream From(handle) uses the logical value, not a stale materialized property
```

## Materialization

```text
[ ] Evaluate changes logical freshness but does not write the POCO mirror
[ ] targeted Materialize writes only one mirror
[ ] object Materialize writes all applicable mirrors on that source
[ ] corrupt/stale mirror is restored from a Fresh logical cache without unnecessary recomputation
[ ] materialization does not dispatch repair
```

## Aggregate

```text
[ ] Sum/Count/LongCount/Any produce expected values
[ ] additive/removal/item-change impact follows configured Dirty/Invalid policy
```

## Discovery

```text
[ ] runtime starts with only a subset of roots
[ ] unloaded consumers are discovered
[ ] all affected consumers receive correct logical/materialized values
[ ] persisted values verified in a fresh DbContext
```

## Persistence policy

```text
[ ] Validate enforces invariants but does not write mirrors
[ ] RecalculateAndValidate writes affected mirrors
[ ] enforced violation blocks SQL
[ ] SQL failure does not install the runtime plan
```

## Coverage failure

```text
[ ] missing authoritative coverage fails before unsafe persistence
```

---

# 19. Do not generate removed API

Reject or rewrite examples containing:

```csharp
.Derived(set).Compute(...)
.Derived(set).Using(...)
.Incrementally()
.Invariant(set).Using(...)
runtime.Get(...)
mappings.Materialize(...)
```

Use:

```csharp
.Derived(set).Select(...)
.Derived(set).From(...)
.Sum(...) // etc. for recognized aggregates
.Invariant(set).From(...)
runtime.Evaluate(...)
derived.MaterializeTo(...)
```

Do not name variables/types after the removed public builder concepts (`DerivedUsingBuilder`, `InvariantUsingBuilder`).

---

# 20. Reference files

For concrete patterns, read:

```text
references/recipes.md
```

For review/checklist work, read:

```text
references/verification.md
```

Those references are part of this skill and must stay consistent with this file.

---

# 21. Final implementation summary expected from an AI

When finishing a consumer integration, summarize:

```text
Consistency graph
    <inputs -> From/DependsOn -> derived -> invariant>

Runtime-owned ObjectSets
    <sets and stable keys>

Materialized mirrors
    <Derived handle -> MaterializeTo property>

EF policy
    mapped sets:
    enforced invariants:
    DiscoverConsumers resolvers:

Coverage strategy
    Complete(...): <why authoritative>
    DiscoverConsumers(...): <navigation + query>

Save boundary
    <convenience save or manual UoW>

Runtime ownership
    <lifetime + synchronization boundary>

Invisible mutation boundary
    <raw SQL/triggers/other writers and mitigation>

Tests proving correctness
    <focused list>

Unsupported/out-of-scope behavior
    <anything intentionally not modeled>
```

If one of these cannot be explained from the code, do not claim the integration is complete.
