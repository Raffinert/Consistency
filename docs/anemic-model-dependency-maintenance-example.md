# Dogfooding: dependency maintenance in an anemic model

## The problem

The entities in this example are ordinary persistence-oriented POCOs. A caller can change a value directly:

```csharp
source.UnitValue = 20m;
```

The arithmetic for `UnitRate` is small. The difficult question is: which associations now contain stale values?
The answer must account for shared source and target objects, relationship retargeting, tracked state, and the
persisted mirror.

## Typical manual orchestration

A hand-written maintenance service usually has to:

- collect changed source IDs;
- collect changed target IDs;
- collect directly changed associations;
- query associations touching those IDs;
- union queried associations with already-tracked associations;
- resolve or load missing endpoints;
- recalculate each affected `UnitRate`;
- write the persisted value.

That list must remain synchronized with every property and navigation that participates in the derived value.

## Raffinert declaration

The executable sample declares the dependency graph once. `SourceItem` and `TargetItem` remain ordinary EF-tracked
navigation targets; the explicit declarations make the dependencies hidden by the reusable calculator complete.

```csharp
var associations = builder.Objects<Association>()
    .Key(x => x.Id);

var unitRate = builder.Derived(associations)
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Select(a => UnitRateCalculator.Calculate(
        a.SourceItem.UnitValue,
        a.TargetItem.UnitValue))
    .MaterializeTo(a => a.UnitRate);

var mappings = new ConsistencyEfCoreMappings()
    .Map(associations)
    .DiscoverConsumers(associations, a => a.SourceItem, (db, sources) =>
        db.Set<Association>().Where(a => sources.Select(s => s.Id).Contains(a.SourceItemId))
            .Include(a => a.SourceItem).Include(a => a.TargetItem))
    .DiscoverConsumers(associations, a => a.TargetItem, (db, targets) =>
        db.Set<Association>().Where(a => targets.Select(t => t.Id).Contains(a.TargetItemId))
            .Include(a => a.SourceItem).Include(a => a.TargetItem));
```

After an ordinary mutation, the EF adapter performs the consistent save:

```csharp
source.UnitValue = 20m;
await db.SaveChangesConsistentlyAsync(...);
```

## What disappeared

| Responsibility | Manual approach | Raffinert approach |
|---|---|---|
| Change detection | Application orchestration | EF change tracker translated to mutations |
| Reverse affected-association discovery | ID collection and queries | Declared dependency graph and reverse navigation index |
| Relationship retargeting maintenance | Explicit repair of indexes | Runtime routing updates from navigation changes |
| Derived recomputation selection | Loop over a hand-built affected set | Incremental impact propagation |
| Persisted mirror update | Application writes each mirror | Core `MaterializeTo` descriptor consumed by EF |

## What did not disappear

- The host still owns authoritative runtime coverage.
- `new ConsistencyScope().Complete(associations)` is an assertion, not a database query.
- For an active direct-navigation dependency, the EF persistence boundary can use a host-supplied
  `DiscoverConsumers` query to resolve missing persisted consumers for the current target batch. The host
  still owns query completeness and evaluation closure.
- Database changes invisible to EF or Raffinert still require the documented reconciliation or rebuild boundary.
- The calculator and business formula still exist and should remain explicit.
- This example demonstrates dependency maintenance; it is not a generic replacement for repositories or querying.

## Why this matters most in anemic models

A rich model can centralize some mutation consequences in behavioral methods. In an anemic model, setters are used
from many handlers, services, and imports, so remembering to recalculate a dependent value spreads quickly.
Raffinert lets entities stay ordinary POCOs while cross-object consequences are declared externally once. It does
not replace rich-domain modeling or event-driven integration.

## Executable sample

Run the [dependency-maintenance sample](../samples/Raffinert.Consistency.DependencyMaintenanceSample/Program.cs)
to execute the SQLite scenarios and their self-verifying assertions.

The incomplete-graph scenario starts with one association in the runtime and tracker. The handler changes
only the shared source scalar; the registered EF resolvers batch-load the remaining persisted consumers at
the save boundary. No handler-specific changed-ID collection or dependent-association query is required.

External discovery is limited to the exact direct-navigation target batch requested by the current mutation.
It does not make the complete association set authoritative: `ConsistencyScope.Complete(associations)` remains
the closed-world alternative. Resolver completeness and the database read boundary remain host responsibilities,
including the usual concurrency/isolation caveat for consumer membership changes made concurrently elsewhere.

## What the executable proves

The SQLite sample verifies the complete persistence path:

- ordinary POCO mutations are captured through EF;
- affected derived values produce the correct results;
- materialized `UnitRate` mirrors agree between the tracked entity, Raffinert runtime, and database;
- relationship retargeting causes subsequent calculations to use the new endpoint;
- null and zero-denominator behavior is preserved.

The executable intentionally does not inspect internal `Fresh`/`Dirty` state to prove that unaffected associations
were never invalidated. Selective invalidation and reverse-navigation retargeting are covered separately by the
Core tests for explicit derived dependencies, where `DerivedValueState.Fresh` and `Dirty` are asserted directly.
