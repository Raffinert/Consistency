---
name: raffinert-consistency-consumer
description: Use Raffinert.Consistency correctly in .NET applications. Apply when designing or implementing semantic dependency graphs, object sets, relations, derived values, invariants, repair, materialization, EF Core mappings, authoritative scope, consumer discovery, runtime evaluation, or consistent save boundaries.
---

# Raffinert.Consistency coding skill

## Purpose

Use this skill when working with:

```text
Raffinert.Consistency
Raffinert.Consistency.EntityFrameworkCore
```

**Raffinert.Consistency is an incremental semantic dependency engine for .NET object graphs.**

Declare what depends on what. When the object graph changes, Raffinert determines what became stale, what must be recalculated, what is no longer valid, and what must happen before the change can safely be persisted.

The goal is to model business consistency as an explicit dependency graph around ordinary .NET objects instead of relying on every mutation path to remember manual recalculation, invalidation, reverse-consumer lookup, validation, or repair orchestration.

All examples are domain-neutral. Rename types and members to match the application's ubiquitous language.

---

# 1. Mental model

Think in terms of **semantic consequences of change**, not event handlers or setter side effects.

```text
ordinary POCO mutation
    ↓
mutation evidence / EF ChangeTracker
    ↓
semantic dependency graph
    ↓
relations / derived values / aggregates
    ↓
Dirty / Invalid propagation
    ↓
logical evaluation as needed
    ↓
invariant / repair requirements
    ↓
optional physical materialization
    ↓
SQL durability
    ↓
runtime-plan installation and dispatch
```

Keep these concepts distinct:

```text
From(...)          = logical value flow from another graph node
DependsOn(...)     = tracked member read hidden inside ordinary calculator code
Select(...)        = define a scalar logical derived value
Sum/Count/LongCount/Any
                   = semantic relation aggregates with optimized execution plans
MaterializeTo(...) = declare an optional physical mirror/sink
Evaluate(...)      = obtain the current logical value without synchronizing the mirror
Materialize(...)   = synchronize physical representation
Invariant          = required truth
Repair             = consequence handling after violation is proven
```

The key boundaries are:

```text
logical derived value != materialized property
committed runtime state != proposed tracked state
materialization != repair
EF ChangeTracker tells what changed; Raffinert models what that change means
```

A materialized property is an output representation. Downstream graph nodes should consume the logical derived handle.

Do not describe Raffinert primarily as a validation library, rules engine, reactive UI framework, or ORM extension. Validation, repair, and EF integration are consumers/boundaries around the semantic dependency graph.

---

# 2. Inspect the application before declaring the graph

Identify:

1. domain/entity types;
2. `DbContext` and EF mappings;
3. stable keys for every Raffinert `ObjectSet`;
4. the business formula or consistency rule;
5. every modeled input that can change the result;
6. which dependencies are source members, relations, logical derived values, or projected derived values;
7. which derived values need physical mirrors;
8. which violations block persistence and which schedule repair;
9. whether reverse consumers or relation members can be unloaded;
10. raw SQL, bulk operations, triggers, cascades, jobs, or other writers that may bypass tracked mutation evidence;
11. transaction/outbox/generated-value requirements;
12. runtime lifetime and serialization boundary;
13. whether application-owned repair needs to reason about the rejected proposed state.

Write the dependency chain first.

Example:

```text
SourceItem.Value ──┐
                  ├──> CombinedValue ──> NormalizedValue ──> invariant
TargetItem.Value ──┘
```

Prefer a graph that expresses domain semantics once over duplicated mutation-path orchestration.

---

# 3. Object sets

Create an `ObjectSet<T>` for objects whose identity/lifecycle or consistency state is modeled.

```csharp
var records = model.Objects<Record>()
    .Key(x => x.Id);
```

Use a stable application key. A mutable semantic field is not a suitable identity.

Object-set identity is stronger than CLR type identity. Two sets containing the same CLR type are different graph owners.

---

# 4. Derived values

## 4.1 Source-local expression

```csharp
var total = model.Derived(records)
    .Select(record => record.Quantity * record.UnitValue)
    .Named("total");
```

Prefer analyzable expressions when practical.

## 4.2 Opaque calculator with explicit tracked reads

When ordinary application code hides member reads, declare them with `DependsOn`:

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .Named("combined-value");
```

`DependsOn` tracks changes. It does not add calculator arguments.

Rules:

- declare every hidden modeled source-state input;
- keep the calculator deterministic from modeled state;
- external time, randomness, service calls, and database lookups require an explicit application strategy;
- if the logical input is another `Derived` handle, model value flow with `From`;
- properties registered as physical mirrors remain sinks, not semantic graph inputs.

## 4.3 Derived-from-derived

```csharp
var remainingCapacity = model.Derived(containers)
    .From(usedCapacity)
    .Select((container, used) => container.Capacity - used)
    .Named("remaining-capacity");
```

The downstream calculator receives the current logical value of `usedCapacity`.

## 4.4 Multiple logical inputs

```csharp
var result = model.Derived(records)
    .From(first)
    .From(second)
    .Select((record, firstValue, secondValue) =>
        Combine(firstValue, secondValue));
```

The order of `From` calls defines argument order.

## 4.5 Projected upstream dependency

```csharp
var assignmentValid = model.Derived(assignments)
    .From(x => x.Container, remainingCapacity)
    .Select((assignment, remaining) =>
        assignment.Amount <= remaining)
    .Named("assignment-valid");
```

Meaning:

```text
Assignment
  -> Assignment.Container
  -> logical RemainingCapacity owned by that Container
```

Mixed projected and local logical inputs can be chained:

```csharp
var actualQuantity = model.Derived(links)
    .From(x => x.PurchaseOrderLine, remainingQuantity)
    .From(unitRate)
    .Select((link, remaining, rate) =>
        CalculateActualQuantity(link, remaining, rate));
```

Projected dependencies require authoritative coverage of their consumers.

---

# 5. Relations and semantic aggregates

Declare matching semantics once:

```csharp
var contributionsForContainer = model.Relation(containers, contributions)
    .Where((container, contribution) =>
        container.Id == contribution.ContainerId)
    .Named("contributions-for-container");
```

Consume the relation through `From`.

```csharp
var usedCapacity = model.Derived(containers)
    .From(contributionsForContainer)
    .Sum(x => x.Amount)
    .Named("used-capacity");
```

Available recognized aggregates:

```text
Sum
Count
LongCount
Any
```

For custom relation calculations:

```csharp
var summary = model.Derived(containers)
    .From(contributionsForContainer)
    .Select((container, related) =>
        CustomSummary(container, related));
```

---

# 6. Dirty, Invalid, and Impact

Use `Impact` when stale-state safety differs by change kind.

```csharp
var usedCapacity = model.Derived(containers)
    .From(contributionsForContainer)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Sum(x => x.Amount);
```

Interpretation:

```text
Dirty   = stale but allowed to remain until evaluation according to the configured workflow
Invalid = stale state must not be relied upon before successful evaluation/repair
```

Severity is semantic information about the consequence of a change, not merely a cache/performance hint. Choose it from correctness semantics.

---

# 7. Invariants and repair

```csharp
var capacityValid = model.Invariant(containers)
    .From(remainingCapacity)
    .Must((container, remaining) => remaining >= 0)
    .Named("capacity-valid");
```

Multiple logical inputs can be added with another `From` before `Must`.

An invariant blocks persistence only when EF policy explicitly enforces it:

```csharp
mappings.Enforce(capacityValid);
```

Keep repair policy separate from pure derived computation and from physical materialization.

```text
Materialize != Repair
```

Repair is application-owned consequence handling. Raffinert should provide structured evidence about what is violated and why; domain-specific replacement selection, ordering, and convergence policy remain application concerns.

A repair mutation creates a new proposed state. Do not assume one repair step always converges:

```text
proposed state
    ↓
violation
    ↓
repair decision
    ↓
mutation
    ↓
new proposed state
    ↓
re-evaluate until valid or application policy stops
```

---

# 8. Materialization

Declare the physical mirror on the derived definition:

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(
        x => x.Source.Value,
        x => x.Target.Value)
    .Select(ValueCalculator.Calculate)
    .MaterializeTo(x => x.CombinedValue)
    .Named("combined-value");
```

The mirror is sink-only. If another calculation needs the value, consume the logical handle:

```csharp
var normalizedValue = model.Derived(associations)
    .From(combinedValue)
    .Select((association, value) => Normalize(value));
```

## 8.1 Logical evaluation

```csharp
var current = runtime.Evaluate(combinedValue, association);
```

`Evaluate` returns the current logical value and does not synchronize the mirror property.

## 8.2 Targeted materialization

```csharp
var current = runtime.Materialize(combinedValue, association);
```

This synchronizes the representation configured for that definition.

## 8.3 Object materialization

```csharp
runtime.Materialize(association);
```

This synchronizes every configured materialized representation physically located on that source object. It does not expand physical writes to dependency objects or downstream objects, and it does not execute repair.

A direct property read can be stale until the application reaches a boundary that guarantees materialization.

---

# 9. Keep declarations together

Prefer one composition component that owns sets, definitions, the compiled model, and EF policy.

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

The runtime is mutable and not thread-safe. Its lifetime must match an explicit consistency/serialization boundary.

---

# 10. EF Core policy

For dependency-injected applications, prefer the scoped integration registration:

```csharp
services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
services.AddRaffinertConsistency<AppDbContext>(compiledModel, mappings);
```

Register `AddRaffinertConsistency<TDbContext>` exactly once per `IServiceCollection`. Resolve or inject the
scoped runtime before application code mutates consistency-relevant tracked state. Clean tracked entities may
be admitted during first binding. Pending scalar, navigation, or collection changes block first binding only
when the changed member is used by a consistency key, dependency, invariant, relation, or projected selector;
mapped additions and removals remain conservative. Ordinary mapped properties unused by the consistency
model, and dirty EF state outside its mappings and dependency graph, do not block binding.

The application service should inject only its `DbContext` and `IConsistencyRuntime` when it needs logical
evaluation or materialization. The interface resolves to the same scoped concrete runtime that the EF
integration uses for baseline admission, explicit materialization, and SaveChanges commit.
Mapped tracked entities are admitted automatically; application code must not call `runtime.Add(...)` or
`runtime.Apply(...)` for ordinary EF changes.

Use `IConsistencyRuntime` for ordinary EF application services that need `Evaluate`/`Materialize`. Use the
concrete `ConsistencyRuntime` for advanced engine, mutation, planning, or diagnostic code.

Map sets whose tracked lifecycle/property changes participate in persistence orchestration:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(containers)
    .Map(contributions);
```

`Map(set)` translates tracked changes for that exact object set. It is not proof that all rows are loaded.

Materialization metadata comes from Core `MaterializeTo(...)` declarations. The EF adapter consumes those descriptors for mapped source sets.

---

# 11. Authoritative coverage

Cross-object consistency requires proof that reverse consumers or relation members are not missing.

Two common strategies:

```text
closed world
    ConsistencyScope.Complete(set)

targeted direct-reference discovery
    DiscoverConsumers(...)
```

`Complete(set)` is a host assertion. Apply it only when every relevant object in that consistency boundary is represented.

Relation-backed aggregates generally require authoritative coverage of the participating relation sets.

Projected upstream dependencies require authoritative projected-consumer coverage.

---

# 12. DiscoverConsumers

For eligible direct-reference reverse consumers:

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
3. load all references needed by active formulas;
4. preserve tenant/security/soft-delete semantics that define the consistency partition;
5. batch requested targets;
6. a safe superset is acceptable;
7. under-fetch is never acceptable;
8. account for current tracked retargeting rather than assuming database relationship state is final.

Targeted discovery is not whole-set completeness.

---

# 13. Consistent save boundary

With the injected EF integration, ordinary persistence remains the application boundary:

```text
Need a materialized property before SaveChanges?  runtime.Materialize(entity)
Only saving?                                         db.SaveChanges / SaveChangesAsync
```

`runtime.Materialize(entity)` is the only explicit pre-save Raffinert call needed when the application
must read a materialized property immediately. It prepares and stores a pending plan without committing
runtime state. `SaveChanges` reuses that plan when semantic EF inputs are unchanged, rebuilds it after an
intervening semantic change, and commits/dispatches only after SQL succeeds. Pending relationship changes
must not rebase committed navigation or projection indexes before SQL; failure leaves the durable baseline
unchanged and retry prepares a fresh plan. Pending plans are also bound to tracked baseline/coverage state:
new mapped tracking invalidates an older plan, restores its owned mirror writes, and forces preparation
against the final baseline before persistence. Clean tracking admission and relationship-fixup stabilization
finish before the first pending plan; once stable, `Materialize` followed by `SaveChanges` reuses that plan
when no further semantic mutation or mapped tracking occurs.

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

`ConsistencySaveBehavior.Validate` evaluates affected enforced invariants without synchronizing configured mirrors.

The default recalculation-and-validation flow evaluates required logical state, synchronizes affected mirrors, persists SQL, installs the runtime plan, and dispatches according to policy.

---

# 14. Rejected saves and proposed-state repair

Allocation dogfooding established an important distinction:

```text
committed runtime / durable database state
                !=
current tracked proposed state after a rejected save
```

An enforced invariant rejection is produced from a reversible plan: SQL is not executed and committed runtime state must remain unchanged. Application-owned repair may nevertheless need to answer questions against the proposed world, for example which replacement candidate is valid if the current tracked changes were applied.

The repository currently contains an **internal/experimental proposed-state preview** for this purpose. It can evaluate logical values, relations, and invariants against retained rejected-plan overlays without installing that rejected plan into committed runtime state.

Consumer guidance:

- do not rebuild a second complete runtime merely to query the rejected proposed state when the retained-plan preview is available internally;
- do not treat the experimental preview as stable public consumer API;
- do not document or generate production code against internal preview types unless the task explicitly concerns that experiment;
- a preview is tied to the rejected plan and tracked mutation fingerprint and must fail closed when stale;
- after application code performs a repair mutation, obtain/rebuild a fresh plan/preview rather than continuing to trust the old view;
- multi-step repair convergence is an application workflow, not an implicit one-shot framework guarantee.

Conceptually:

```text
rejected plan
    ↓
read-only proposed-state query
    ↓
application chooses repair
    ↓
tracked mutation
    ↓
fresh SaveChanges planning
    ↓
repeat if another violation remains
```

---

# 15. Manual transaction / generated values / outbox

When the application owns the transaction:

```csharp
var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

// Persist first only when semantic store-generated values must become final.
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

Never install runtime state before database durability.

---

# 16. Invisible database mutations

Search for paths that can change modeled state outside tracked EF mutations:

```text
ExecuteUpdate / ExecuteDelete
raw SQL mutation
bulk libraries
stored procedures
triggers
database cascades
other services/processes writing the same rows
```

Define an explicit strategy: exact mutation publication, reconciliation, runtime rebuild/reseed, serialized ownership, or exclusion from the consistency boundary.

---

# 17. Required tests

At minimum cover the scenarios relevant to the graph:

```text
logical dependency propagation
From(handle) with a stale physical mirror
Dirty/Invalid transitions
relation aggregate add/remove/item-change behavior
Evaluate without mirror write
targeted Materialize
object Materialize
invariant enforcement
structured repair requirements
unloaded direct-reference consumer discovery
retargeting
Validate vs recalculation-and-validation
SQL/planning failure semantics
committed runtime remains unchanged after rejected save
repair convergence when more than one mutation/save attempt is required
fresh-DbContext verification of persisted mirrors
```

If testing the internal proposed-state experiment, additionally prove that stale previews fail closed after tracked mutation/baseline changes and that querying a preview does not install rejected state into the committed runtime.

Prefer verifying persisted results from a separate `DbContext`.

---

# 18. Review output

Summarize an integration in this form:

```text
Consistency graph
    <inputs -> DependsOn/From -> derived -> invariant>

ObjectSets
    <sets and keys>

Materialized mirrors
    <Derived -> MaterializeTo property>

EF policy
    mapped sets:
    enforced invariants:
    discovery resolvers:

Coverage strategy
    Complete(...): <why valid>
    DiscoverConsumers(...): <navigation + authoritative query>

Save boundary
    <convenience or manual unit of work>

Repair/convergence
    <blocking invariant, structured repair, proposed-state needs>

Runtime ownership
    <lifetime + synchronization>

Invisible mutation boundary
    <other writers and mitigation>

Tests proving correctness
    <focused list>
```

For concrete patterns, read `references/recipes.md`. For review checks, read `references/verification.md`.