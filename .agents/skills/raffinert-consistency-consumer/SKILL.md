---
name: raffinert-consistency-consumer
description: Use Raffinert.Consistency correctly in downstream .NET applications. Apply when an AI must design or implement relations, derived values, invariants, EF Core mappings, materialization, authoritative scope, targeted consumer discovery, or consistent save boundaries in application code that consumes Raffinert.Consistency. This skill is NOT for changing Raffinert.Consistency library internals.
---

# Raffinert.Consistency consumer coding skill

## Purpose

Use this skill when modifying a **consumer application** that references:

```text
Raffinert.Consistency
Raffinert.Consistency.EntityFrameworkCore
```

The goal is to turn existing business consistency rules into a declarative Raffinert model without moving domain behavior into the library or relying on every mutation path to remember manual recalculation.

This skill is intentionally consumer-only.

If the current repository is `Raffinert/Consistency` itself and the task asks to change runtime/compiler/adapter internals, do not use this skill as an implementation guide. Use the library's contributor/architecture documentation instead.

All examples in this skill are deliberately domain-neutral. Rename the neutral types to match the consumer application's actual ubiquitous language.

---

# 1. Mental model

Treat Raffinert.Consistency as a **deferred transactional reactive consistency graph** around ordinary .NET objects.

```text
ordinary POCO mutation
        ↓
mutation evidence / EF ChangeTracker
        ↓
declared Raffinert dependency graph
        ↓
affected derived values
        ↓
affected invariants / repair policy
        ↓
optional persisted mirrors
        ↓
SQL durability
        ↓
exact runtime-plan installation
```

Do not design the application around manually invoking recomputation methods after every setter.

Raffinert's job is to declare once:

```text
what depends on what
```

and derive the consistency consequences from that graph.

---

# 2. Before writing code: inspect the consumer application

Do not start by creating a `ConsistencyModelBuilder` blindly.

First inspect the downstream repository and identify:

1. the domain/entity types involved;
2. the `DbContext` and EF mappings if EF is used;
3. stable application keys for every Raffinert `ObjectSet`;
4. the current formula or rule being maintained manually;
5. every input property that can change the result;
6. whether the result is source-local, relation-backed, or projected from another object;
7. whether the result must be persisted as a mirror;
8. whether a violation must block SQL or only schedule later repair;
9. whether all reverse consumers are guaranteed to be loaded;
10. whether mutations can occur through arbitrary handlers/imports/jobs/services;
11. whether raw SQL, `ExecuteUpdate`, triggers, bulk libraries, other processes, or database cascades can mutate relevant state outside EF tracking;
12. the application's transaction/outbox/generated-key requirements.

Write down the dependency chain before coding.

Neutral example:

```text
SourceItem.Value
TargetItem.Value
        ↓
Association.CombinedValue
        ↓
NormalizedValue
        ↓
validity invariant
```

If you cannot state the dependency chain clearly, do not guess the Raffinert configuration yet.

---

# 3. Choose the smallest correct Raffinert construct

Use this decision order.

## 3.1 Object set

Create an `ObjectSet<T>` for objects whose identity/lifecycle or derived/invariant state Raffinert owns.

```csharp
var records = model.Objects<Record>()
    .Key(x => x.Id);
```

Use a stable application key.

Do not use a mutable semantic field as the Raffinert identity.

Do not create unnecessary object sets merely because a nested navigation target is read. A tracked nested target can supply scalar dependency changes without being its own mapped Raffinert set unless its lifecycle or own consistency state needs to be modeled.

## 3.2 Source-local derived value

If the value depends only on properties reachable from the source and no set/relation aggregation is required:

```csharp
var total = model.Derived(records)
    .Compute(record => record.Quantity * record.UnitValue);
```

Prefer an analyzable expression when practical.

## 3.3 Relation-backed derived value

When a source depends on a set of matching objects, define the relation explicitly.

```csharp
var containers = model.Objects<Container>()
    .Key(x => x.Id);

var contributions = model.Objects<Contribution>()
    .Key(x => x.Id);

var contributionsForContainer = model.Relation(containers, contributions)
    .Where((container, contribution) => container.Id == contribution.ContainerId);

var usedCapacity = model.Derived(containers)
    .Using(contributionsForContainer)
    .Incrementally()
    .Compute((container, related) => related.Sum(x => x.Amount));
```

The relation predicate is semantic authority. Do not duplicate the matching rule in application services after introducing the relation.

## 3.4 Derived-from-derived

When one derived value depends on another:

```csharp
var remainingCapacity = model.Derived(containers)
    .Using(usedCapacity)
    .Compute((container, used) => container.Capacity - used);
```

Do not manually recompute `remainingCapacity` after `usedCapacity` changes.

## 3.5 Projected upstream dependency

When a source consumes a derived value belonging to a referenced object:

```csharp
var assignments = model.Objects<Assignment>()
    .Key(x => x.Id);

var assignmentValid = model.Derived(assignments)
    .Using(x => x.Container, remainingCapacity)
    .Compute((assignment, remaining) => assignment.Amount <= remaining);
```

Treat projected dependencies as requiring authoritative consumer coverage. `DiscoverConsumers` does not substitute projected-consumer completeness in the current supported version.

## 3.6 Opaque calculator with explicit source dependencies

If the computation must call opaque/application code that Raffinert cannot analyze, declare every hidden source-member dependency explicitly:

```csharp
var combinedValue = model.Derived(associations)
    .DependsOn(x => x.Source.Value)
    .DependsOn(x => x.Target.Value)
    .Compute(x => ValueCalculator.Calculate(
        x.Source.Value,
        x.Target.Value));
```

`DependsOn(...)` is a correctness assertion by the application author.

Rules:

- declare **all** hidden source-state inputs;
- use direct non-collection member paths;
- do not use `DependsOn` as a substitute for a relation, projected dependency, or external-state dependency;
- keep the calculator deterministic from the declared inputs.

## 3.7 Invariant

Use an invariant when a correctness rule consumes direct or derived state.

```csharp
var capacityValid = model.Invariant(containers)
    .Using(remainingCapacity)
    .Must((container, remaining) => remaining >= 0);
```

An invariant does not automatically block SQL. EF persistence blocks only invariants explicitly configured with `.Enforce(...)`.

## 3.8 Dirty vs Invalid

If subtractive or destructive changes make stale state unsafe, configure impact severity rather than treating all staleness equally.

Example shape:

```csharp
var usedCapacity = model.Derived(containers)
    .Using(contributionsForContainer)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Compute((container, related) => related.Sum(x => x.Amount));
```

Use `Invalid` only when the stale value must not be relied upon before repair/recomputation.

---

# 4. Build once; keep declarations together

Prefer a dedicated application composition component that owns the declarations and exposes the resulting handles.

Conceptual shape:

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
            .DependsOn(x => x.Source.Value)
            .DependsOn(x => x.Target.Value)
            .Compute(x => ValueCalculator.Calculate(
                x.Source.Value,
                x.Target.Value));

        Compiled = model.Build();

        EfMappings = new ConsistencyEfCoreMappings()
            .Map(Associations)
            .Materialize(CombinedValue, x => x.CombinedValue);
    }
}
```

Adapt the shape to the application's existing composition style. Do not introduce a new DI architecture solely for Raffinert.

The runtime is mutable and not thread-safe. Do not register one globally shared runtime across concurrent unrelated operations unless the application already provides an explicit serialization/partition boundary that makes that correct.

Choose runtime lifetime according to the application's authoritative consistency boundary. If that boundary is unclear, stop and make it explicit rather than guessing.

---

# 5. EF Core mappings

Use `ConsistencyEfCoreMappings` for persistence policy.

## 5.1 Map object sets

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(containers)
    .Map(contributions);
```

`Map(set)` means:

> translate tracked lifecycle/property changes for this exact Raffinert object set.

It does **not** mean:

> all rows of this type are loaded.

Never treat `Map(...)` as completeness proof.

## 5.2 Enforce invariant

```csharp
mappings.Enforce(capacityValid);
```

Only explicitly enforced invariant violations block persistence.

## 5.3 Materialize derived value

```csharp
mappings.Materialize(combinedValue, x => x.CombinedValue);
```

A materialized property is a persisted **sink-only mirror**.

Do not use a materialized mirror as an input to another Raffinert relation/derived/invariant dependency.

Keep the semantic source of truth in the declared computation, not in the persisted mirror.

---

# 6. Authoritative coverage: never fake completeness

Cross-object consistency requires proof that reverse consumers/contributors are not missing.

There are two valid consumer strategies for supported cases:

```text
A. closed world
   ConsistencyScope.Complete(set)

B. open world for eligible direct reference navigation
   DiscoverConsumers(...)
```

Never use `Complete(set)` merely to silence `IncompleteConsistencyScopeException`.

`Complete(set)` is a host assertion. Raffinert trusts it and does not verify the database.

Use it only when the application genuinely knows that every relevant object in the consistency boundary is represented in the runtime/planned final state.

---

# 7. `DiscoverConsumers` for unloaded direct-reference consumers

Use targeted external consumer discovery when:

- a derived/invariant reads a direct reference path such as `Association.Source.Value`;
- `Source.Value` can change while some `Association` consumers are not loaded;
- the application can query every authoritative `Association` whose `Source` is one of the changed targets;
- the path is a supported direct non-collection EF reference navigation.

Neutral domain:

```csharp
public sealed class Association
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }
    public required SourceItem Source { get; set; }

    public Guid TargetId { get; set; }
    public required TargetItem Target { get; set; }

    public decimal? CombinedValue { get; set; }
}
```

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

The resolver is keyed by the **exact object set + exact direct navigation**.

## Resolver requirements

The query must:

1. return every persisted consumer for the requested targets inside the application's authoritative boundary;
2. be tracked by the same `DbContext`;
3. return no `AsNoTracking()` roots;
4. load every direct reference needed by active evaluation, not only the navigation used to discover the root;
5. respect tenant/security/soft-delete/business filters that define the application's authoritative boundary;
6. be safe when targets are batched;
7. allow a safe superset if convenient — Raffinert filters by current tracked navigation state;
8. not rely on stale database relationship state when ChangeTracker has retargeted current state.

Example with two independent dependencies:

```csharp
mappings
    .DiscoverConsumers(
        associations,
        x => x.Source,
        (db, sources) =>
        {
            var ids = sources.Select(x => x.Id).ToArray();
            return db.Set<Association>()
                .Where(x => ids.Contains(x.SourceId))
                .Include(x => x.Source)
                .Include(x => x.Target);
        })
    .DiscoverConsumers(
        associations,
        x => x.Target,
        (db, targets) =>
        {
            var ids = targets.Select(x => x.Id).ToArray();
            return db.Set<Association>()
                .Where(x => ids.Contains(x.TargetId))
                .Include(x => x.Source)
                .Include(x => x.Target);
        });
```

## What `DiscoverConsumers` does not cover

Do not invent support for:

- multi-hop external discovery;
- collection-navigation discovery;
- relation-source/target completeness;
- projected-consumer completeness;
- automatic SQL generation;
- automatic `Include` generation;
- automatic `Reference.Load()`;
- lazy-loading correctness;
- proving that the host query did not under-fetch.

If the required path is unsupported, keep the operation fail-closed and use genuine `Complete(set)` coverage or redesign the application boundary.

---

# 8. Choosing between `Complete` and `DiscoverConsumers`

Use this table.

| Situation | Correct strategy |
|---|---|
| source-local computation | usually no complete-set declaration |
| direct nested reference dependency and every root is genuinely loaded | `Complete(rootSet)` is valid |
| direct nested reference dependency and roots may be unloaded | `DiscoverConsumers(rootSet, directNavigation, query)` |
| relation-backed aggregate | complete relation sets required |
| relation source/target coverage | `DiscoverConsumers` does not substitute it |
| projected upstream dependency | projected consumer set must be complete |
| collection-navigation dependency | current targeted discovery unsupported; complete relevant set or redesign |
| multi-hop navigation dependency | current targeted discovery unsupported; complete relevant set or redesign |

When in doubt, fail closed rather than weakening coverage.

---

# 9. Consistent save boundary

For ordinary stable-key EF workflows, use the convenience save boundary:

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

Use `ConsistencySaveBehavior.Validate` when the operation should validate affected enforced invariants without recalculating/writing configured materialized mirrors.

Use the default `RecalculateAndValidate` when affected materializations must also be recomputed and persisted.

Do not call ordinary `SaveChangesAsync()` first and then try to make Raffinert catch up unless you are deliberately using the documented manual Unit of Work flow.

---

# 10. Manual Unit of Work for transactions/generated values/outbox

Use the policy-aware manual workflow when the application owns the database transaction, needs store-generated semantic values to become final before planning, or must persist durable policy/outbox work atomically.

Canonical shape:

```csharp
var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope });

await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

// Only when required to make generated semantic values final.
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

- capture before the first save;
- plan only after required generated semantic values are final;
- call `CommitAfterDatabaseCommit()` only after database durability;
- never reuse a stale plan after the tracked domain state changes;
- after failed generated-key/database operations, prefer discarding/reloading the context rather than guessing how to restore provider state.

---

# 11. DB-side mutations outside Raffinert evidence

Do not assume Raffinert sees mutations performed through:

```text
raw SQL
ExecuteUpdate / ExecuteDelete
bulk libraries that bypass ChangeTracker
database triggers modifying other rows
other application instances/pods
manual DBA/data-fix writes
untracked database cascade/set-null effects
```

For these paths, the application must provide exact mutation evidence or rebuild/reseed/reconcile runtime state before relying on it again.

Do not silently combine an authoritative Raffinert runtime with invisible database mutation channels.

---

# 12. Consumer implementation workflow for AI agents

When asked to implement a feature in a downstream repository, follow this sequence.

## Step 1 — locate existing behavior

Find the current service/handler/calculator/query that maintains the rule.

Extract:

```text
inputs
consumer roots
formula
matching/relation semantics
persisted mirror
blocking invariant
repair behavior
all mutation paths
```

## Step 2 — design the Raffinert graph on paper

Produce a compact neutral chain such as:

```text
SourceItem.Value
TargetItem.Value
        ↓
Association.CombinedValue
        ↓
NormalizedValue
        ↓
Invariant
```

Classify each edge as:

```text
source-local
relation
upstream derived
projected upstream
direct nested reference
opaque explicit DependsOn
```

## Step 3 — determine coverage before coding

For every cross-object edge ask:

```text
Can all consumers/contributors be guaranteed present?
```

If yes, identify the exact `Complete(set)` assertions and why they are true.

If no, check whether the missing-consumer edge is an eligible direct reference and configure `DiscoverConsumers`.

If neither is possible, do not generate unsafe code. Report the unsupported boundary explicitly.

## Step 4 — implement declarations

Add or extend the application's composition code with:

```text
ObjectSet declarations
relations
derived values
DependsOn where required
invariants
impact policy if required
model.Build()
EF mappings
```

Keep declarations centralized enough that future dependency changes are visible in one place.

## Step 5 — wire persistence boundary

Use one documented save path consistently.

Do not leave some relevant mutation handlers using plain `SaveChangesAsync()` while others use `SaveChangesConsistentlyAsync()` unless the plain path is proven outside the consistency boundary.

## Step 6 — remove duplicated orchestration only after proof

After Raffinert covers the rule and tests prove it, delete old code that manually:

```text
collects changed IDs
queries affected consumers
unions tracked consumers
loads missing references
recalculates mirrors
invalidates dependent state
```

Do not remove old logic first and hope the new graph is equivalent.

## Step 7 — run focused and full tests

Test the downstream behavior, not Raffinert internals.

---

# 13. Mandatory consumer tests

For any non-trivial EF integration, add tests matching the application scenario.

At minimum cover the relevant subset of:

```text
known consumer updates correctly
unloaded consumer is discovered and updates correctly
multiple consumers of one changed target all update
retargeted-away consumer is excluded
retargeted-in consumer is included
materialized mirror persists correctly
separate verification DbContext sees persisted value
Validate does not write materializations
RecalculateAndValidate does write materializations
invariant violation blocks SQL when Enforced
resolver omission of a required reference fails before SQL
loaded optional null is distinguished from unloaded null
SQL failure does not install runtime plan
same-context retry behaves as documented where supported
raw/untracked mutation path is explicitly outside the automatic guarantee
```

For production-shaped discovery tests, verify independently through a separate `AsNoTracking()` verification context after the successful save.

Do not use `AsNoTracking()` inside the discovery resolver itself.

---

# 14. Anti-patterns — do not generate these

Never solve an integration problem by doing any of the following without a proven application reason:

```text
scope.Complete(set) just to silence an exception
runtime.Add(...) for rows discovered during an EF save
manual derived-property assignment from every handler
calling opaque calculator without declaring hidden DependsOn inputs
using persisted materialized mirror as graph input
AsNoTracking consumer resolver
resolver query that loads the discovery navigation but omits another active reference used by the formula
one SQL query per changed target instead of batching
assuming Map(set) means complete coverage
assuming DiscoverConsumers makes the whole set complete
using DiscoverConsumers to replace relation/projected coverage
using raw SQL / ExecuteUpdate and expecting ChangeTracker-based consistency to notice
sharing one non-thread-safe runtime across concurrent unrelated operations
committing runtime state before database durability
calling CommitAfterDatabaseCommit before transaction commit
inventing unsupported Raffinert APIs
```

If generated code requires one of these, reconsider the model.

---

# 15. Code-generation quality rules

When implementing in a consumer repository:

- use the repository's existing naming/style/DI/test framework;
- use public Raffinert APIs only;
- inspect the installed package/API version instead of assuming unreleased APIs exist;
- prefer expression declarations over duplicating dependency metadata manually;
- keep calculator code deterministic and testable;
- keep persisted mirrors visibly sink-only;
- batch external consumer queries;
- preserve tenant/security filters in resolvers;
- preserve cancellation tokens on async save/query paths;
- make unsupported coverage explicit instead of hiding it;
- explain why each `Complete(set)` assertion is valid;
- explain why each `DiscoverConsumers` query is authoritative;
- do not modify Raffinert library source as part of a downstream integration task.

---

# 16. Required final review before finishing a consumer implementation

Before reporting the task complete, answer all of these from the actual code:

```text
[ ] What exact property changes can affect each derived value?
[ ] Are all hidden opaque calculator inputs declared with DependsOn?
[ ] Which ObjectSets own lifecycle/state?
[ ] Which relations define matching semantics?
[ ] Which invariants are Enforced vs advisory/repair-only?
[ ] Which derived values are Materialized, and are their targets sink-only?
[ ] For every reverse-navigation dependency, how is consumer coverage proven?
[ ] Is every Complete(set) assertion actually true?
[ ] Are all DiscoverConsumers resolvers direct-reference, tracked, batched, authoritative, and evaluation-complete?
[ ] Are relation/projected/unsupported coverage requirements still fail-closed?
[ ] Does every relevant save path go through a correct Raffinert persistence boundary?
[ ] Are database-side invisible mutation paths accounted for?
[ ] Does failure before DB durability leave runtime uncommitted?
[ ] Do tests include unloaded consumers when the application can have unloaded consumers?
[ ] Is persisted state verified independently after success?
```

If any answer is unknown, the implementation is not complete.

---

# 17. References bundled with this skill

Use these when a concrete pattern is needed:

- `references/recipes.md` — domain-neutral consumer code recipes for common modeling/persistence cases.
- `references/verification.md` — test and review checklist for generated integrations.

Prefer these consumer recipes over copying Raffinert's internal test seams or internal runtime types.