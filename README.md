# Raffinert.Consistency

**Incremental consistency for .NET object models.**

Raffinert.Consistency lets you declare relations, derived values, and invariants as expression trees.
It analyzes those declarations to build the dependency graph, indexes, reverse navigation, invalidation
rules, and incremental propagation needed to keep a model consistent as objects change.

Instead of manually wiring consistency logic across services and handlers:

```mermaid
flowchart TD
    A[Field changed] --> B[Invalidate calculation]
    B --> C[Invalidate dependent calculation]
    C --> D[Check persisted links]
    D --> E[Schedule repair]
```

you declare the relationships and computations once. The runtime determines **what is affected**, **how
severe the impact is**, and **what work must happen next**.

> You declare domain relationships. Raffinert.Consistency derives the consistency machinery.

The dependency-free core targets .NET 8 and .NET 10. The EF Core adapter targets .NET 10 / EF Core 10.
The project is currently experimental and alpha-oriented.

## Why?

Consistency logic in rich applications tends to spread:

- one service updates a field;
- another remembers to invalidate a calculation;
- another knows which persisted links depend on that calculation;
- another decides whether repair or rematching must happen now or can be deferred.

That works until the domain evolves and one of those paths is forgotten.

Raffinert.Consistency makes the dependency graph explicit and executable. The same expressions that define
relationships and derived values are analyzed to derive indexes, reverse access paths, change impact, and
propagation behavior.

This is especially useful in domains such as procurement, accounting, pricing, inventory, reservations,
allocation, eligibility, planning, and other models where one business change can have several dependent
consequences.

## Core capabilities

- Expression-defined binary relations whose original predicate remains the semantic authority
- Automatic dependency and nested member-path analysis
- Incremental hash indexes and reverse navigation where analysis proves them safe
- Correct scan fallback for unsupported or opaque query expressions
- Lazy derived state with **Fresh**, **Dirty**, and **Invalid** semantics
- Derived-value dependency graphs, including projected cross-object dependencies
- Exact and conservative propagation strategies
- Incremental `Count`, `LongCount`, `Any`, and numeric `Sum` plans
- Invariants with immediate evaluation, invalidation, or deferred repair
- Summary and causal impact reporting
- Binding execution plans for transaction/outbox integration
- Optional EF Core change-tracker integration
- Compiled-model and runtime diagnostics

The core package has no EF Core or dependency-injection dependency.

## EF Core consistent saves

The EF Core adapter can reject configured invariant violations before SQL and persist sink-only mirrors
of affected derived values:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Map(fulfillments)
    .Materialize(availableQuantity, x => x.AvailableQuantity)
    .Enforce(availability);

var scope = new ConsistencyScope()
    .Complete(lines)
    .Complete(fulfillments);

await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    new ConsistencySaveOptions { Scope = scope },
    cancellationToken);
```

This stable-key workflow validates authoritative scope, plans first, saves once, then installs the exact
runtime plan. `Map(...)` translates tracked changes; only `ConsistencyScope.Complete(...)` asserts that a
runtime set has complete coverage. Raffinert does not load missing graph data. See
[EF Core consistency](docs/ef-core-consistency.md) for scope, transactions,
generated keys, and recovery rules, or run the
[`Raffinert.Consistency.EntityFrameworkCore.Sample`](samples/Raffinert.Consistency.EntityFrameworkCore.Sample).

Scope completeness proves data coverage in the runtime; it does not prove mutation coverage for SQL-side
effects. Authoritative saves reject EF-declared `ON DELETE CASCADE` and `ON DELETE SET NULL` paths that can
reach consistency-managed state without tracked mutation evidence. Prefer `ClientCascade` / `ClientSetNull`
with explicitly tracked dependents, or `Restrict` / `NoAction` when deletion must be explicit.

For application-owned transactions, generated semantic values from INSERT or UPDATE, or an outbox, capture a
policy-aware work item before the first save, call `PrepareAndPlan()` when generated values are final, persist the returned plan data, commit the
database transaction, then call `CommitAfterDatabaseCommit()` and `Dispatch()`. This path applies the same
scope, enforcement, and materialization policy as convenience saves.

## What Raffinert.Consistency is not

It is not an ORM, event bus, or general-purpose workflow engine.

It does not replace your domain objects or persisted business relationships. Its job is narrower:
**given a graph of relationships and computations, efficiently determine and maintain the consequences
of change.**

## Start with a relation

Relations are ordinary expression trees:

```csharp
var model = new ConsistencyModelBuilder();

var requests = model.Objects<RequestLine>()
    .Key(x => x.Id);

var orderLines = model.Objects<OrderLine>()
    .Key(x => x.Id);

var candidates = model.Relation(requests, orderLines)
    .Where((request, line) =>
        request.OrderNumber == line.OrderNumber &&
        request.ItemNumber == line.ItemNumber);

var compiled = model.Build();
var runtime = compiled.CreateRuntime();

runtime.Add(requests, request);
runtime.Add(orderLines, orderLine);

var related = runtime.Related(candidates, request);
```

Raffinert.Consistency analyzes the predicate and derives hash access paths where it can do so safely.
Unsupported expressions retain the original compiled predicate and fall back to scanning rather than
changing semantics.

## Report ordinary domain changes

Domain objects remain ordinary objects. Mutate them normally, then report what changed:

```csharp
var oldItemNumber = request.ItemNumber;
request.ItemNumber = "ITEM-2";

runtime.Apply(
    Change.Property(
        requests,
        request,
        x => x.ItemNumber,
        oldItemNumber,
        request.ItemNumber));
```

`Change.Property` observes a mutation that has already happened; it does not mutate the object itself.
The runtime validates the change and updates only the runtime-owned state affected by it.

A domain operation can report lifecycle, property, and collection changes together:

```csharp
runtime.Apply(MutationSet.Create(
    Change.Add(orderLines, addedLine),
    Change.Property(requests, request, x => x.ItemNumber, oldItem, request.ItemNumber),
    Change.CollectionReset(order, x => x.Lines),
    Change.Remove(orderLines, removedLine)));
```

The complete mutation set is validated before runtime-maintained state is committed.

## Derived values

Relations become more useful when they feed derived state.

For example, an order line can derive its fulfilled quantity from matching fulfillments:

```csharp
var fulfilledQuantity = model.Derived(orderLines)
    .Using(fulfillments)
    .Incrementally()
    .Compute((line, matches) =>
        matches.Sum(fulfillment => fulfillment.Quantity));
```

Recognized exact aggregates can update already-fresh cache entries directly from relation/item deltas.
Unrecognized expressions retain the original compiled computation as the semantic fallback.

Derived values can depend on other derived values and form a compiled dependency DAG:

```csharp
var remainingQuantity = model.Derived(orderLines)
    .Using(fulfilledQuantity)
    .Compute((line, fulfilled) =>
        line.OrderedQuantity - fulfilled);
```

They can also consume upstream values through tracked object references:

```csharp
var allocationValidity = model.Derived(allocations)
    .Using(allocation => allocation.OrderLine, remainingQuantity, unitRate)
    .Compute((allocation, remaining, rate) =>
        allocation.ReservedQuantity <= remaining &&
        allocation.CapturedRate == rate);
```

A change can therefore propagate through a graph such as:

```mermaid
flowchart LR
    F[Fulfillment change] --> FQ[FulfilledQuantity]
    FQ --> RQ[RemainingQuantity]
    UR[UnitRate] --> AV[AllocationValidity]
    RQ --> AV
    AV --> INV[Invariant / repair decision]
    INV --> R[Schedule repair]
```

The application does not need to manually orchestrate every edge in that graph.

## Dirty vs Invalid

A dependency becoming stale is not always the same as becoming unsafe.

Raffinert.Consistency distinguishes those cases:

- **Dirty** — the cached value must be recomputed when freshness matters.
- **Invalid** — the value must not be relied upon before recomputation or revalidation.

A relation-backed derived value can classify different kinds of impact independently:

```csharp
var fulfilled = model.Derived(orderLines)
    .Using(fulfillments)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Compute((line, matches) =>
        matches.Sum(fulfillment => fulfillment.Quantity));
```

Typed source-member policies can also classify value transitions—for example, a quantity decrease can be
`Invalid` while an increase remains merely `Dirty`.

## Invariants and repair

Invariants consume direct or derived state and can react when correctness is affected. Reactions can be
immediate or represented as deferred repair work.

This lets a domain model express patterns such as:

```mermaid
flowchart LR
    Q[Order quantity decreased] --> A[RemainingQuantity invalid]
    A --> L[Existing allocation may be invalid]
    L --> R[Schedule repair]
```

without embedding the repair orchestration into every command handler that can affect quantity.

## Explain what changed

Basic `Apply` performs the runtime update without building diagnostic impact data.

When impact needs to be inspected or persisted, use a detailed operation:

```csharp
var application = runtime.ApplyDetailed(
    mutations,
    RuntimeImpactDetailLevel.Causal);

Console.WriteLine(
    RuntimeImpactTraceRenderer.Render(application.Result));
```

Detailed results can include relation pair deltas, affected derived values, invariant impacts, policy
requests, mutation origins, and deterministic direct-cause records.

For durable background work, project the in-process result to strict data-only work:

```csharp
DurablePolicyWork durableWork =
    application.Result.GetDurablePolicyWork();
```

Stable definition names and canonical source identities are required when data crosses a process boundary.

## Transaction and outbox integration

External persistence can separate mutation preparation, runtime commit, and callback dispatch:

```csharp
var prepared = runtime.Prepare(
    mutations,
    ChangeValidationMode.StrictNewValue);

await database.SaveChangesAsync();

runtime.Commit(prepared);
runtime.Dispatch(prepared);
```

When durable external work must exactly match the runtime result, use a binding plan:

```csharp
var plan = runtime.PlanDetailed(
    prepared,
    RuntimeImpactDetailLevel.Causal);

PersistDurablePolicyWork(
    plan.Result.GetDurablePolicyWork());

await database.CommitAsync();

runtime.Commit(plan);
runtime.Dispatch(plan);
```

`PlanDetailed` is binding: semantic classification and propagation are executed once, runtime state is
restored, and the plan retains the forward state needed by the later commit. `Commit(plan)` installs that
same result rather than rerunning semantic code.

`PreviewDetailed` is intentionally different: it is diagnostic and non-binding, so a later ordinary commit
may execute semantic code again.

If the business database commits but runtime installation later fails, the database is authoritative;
rebuild or reconcile the runtime from durable state rather than retrying the database mutation blindly.

## EF Core integration

The EF Core adapter can translate change-tracker state into the same core mutation model.

`SaveChangesConsistently` / `SaveChangesConsistentlyAsync` provide policy-aware one-save ordering. Advanced
transaction/outbox and generated-value workflows should call `CaptureConsistencyUnitOfWork(...)`, then use
`PrepareAndPlan`, caller-owned database commit, `CommitAfterDatabaseCommit`, and `Dispatch`.

`ChangeTrackerAdapter.CaptureUnitOfWork` is a lower-level, policy-agnostic runtime binding primitive. It does
not apply `ConsistencyEfCoreMappings.Enforce`, `Materialize`, or `ConsistencyScope` and is not the recommended
authoritative EF persistence workflow.

Generated semantic values from INSERT or UPDATE are supported by the manual workflow, including
existing-dependent FK fixup when final principal keys become available only after the first `SaveChanges`
inside a database transaction. Convenience APIs fail closed when SQL must run before a semantic value is
final. The captured pre-SQL value remains authoritative; only provider-generated transitions may be
finalized afterward, and unrelated changes after capture remain rejected by strict validation. Tracked
nested dependency targets do not need their own `ObjectSet` mapping for scalar change routing. Updating an
existing Raffinert identity with a store-generated value is unsupported.

Automatic synchronization covers tracked lifecycle/scalar/navigation/collection changes and narrowly proven
same-entry generated values or FK fixup. Database triggers that mutate other rows, raw SQL,
`ExecuteUpdate`/`ExecuteDelete`, change-tracker-bypassing bulk libraries, external writers, and manual data
fixes are outside that contract. Supply exact authoritative mutations or rebuild/reseed/reconcile the runtime
before relying on it after such writes.

See the architecture and example documentation for the exact transaction boundaries and recovery rules.

### Dogfooding: dependency maintenance in an anemic model

A domain-neutral SQLite example shows how changes to ordinary navigation targets identify affected associations,
recalculate a derived `UnitRate`, and persist its mirror without handler-specific invalidation/query orchestration.
The sample also demonstrates mutation-driven, batched external-consumer discovery for an intentionally incomplete
operation graph; the host supplies the authoritative EF queries.
See the [dogfooding explanation](docs/anemic-model-dependency-maintenance-example.md) and run the
[dependency-maintenance sample](samples/Raffinert.Consistency.DependencyMaintenanceSample).

## Exact vs conservative propagation

Reusable calculation methods can hide source dependencies from expression analysis. For source-derived values,
declare every hidden member path explicitly:

```csharp
var score = model.Derived(items)
    .DependsOn(x => x.InputA)
    .DependsOn(x => x.Config.Value)
    .Compute(x => ExistingCalculator.Calculate(x.InputA, x.Config.Value));
```

Ordinary analyzable expressions need no declarations. `DependsOn` augments inferred dependencies, including
nested reverse-navigation routing and scope requirements; it is a model-author assertion that all hidden source
dependencies are declared. It does not make mutable external/static state safe. `AllowIncompleteDependencies`
remains the explicit weaker-freshness escape hatch.

Correctness and storage strategy are separate concerns.

Exact propagation can retain matching relation pairs and invalidate only exact affected sources.
Full-recompute consumers can instead opt into conservative propagation, avoiding permanent pair retention
while invalidating a safe source superset.

The original relation predicate remains the semantic authority in either case.

Query access, reverse candidate access, and propagation strategy are reported independently in compiled
diagnostics.

## Dependency analysis safety

Cached derived computations, invariants, and relations used for propagation require complete dependency
analysis by default.

`Build()` rejects opaque code or mutable captured/static state when the runtime could not guarantee cache
freshness. Direct-query-only relations may remain opaque because their predicate is evaluated at query time.

Deliberate prototypes can opt into weaker guarantees with `AllowIncompleteDependencies()`. Diagnostics make
that weaker contract visible.

## Diagnostics and performance

`compiled.DebugView` provides a human-readable description of the compiled model.

`compiled.Diagnostics` exposes structured object-set, relation, derived-value, invariant, access-plan,
propagation, and completeness information.

`runtime.Diagnostics` exposes incremental-work counters and relation statistics such as index sizes,
materialized pair count, fan-out, and density warnings.

The repository also contains BenchmarkDotNet benchmarks and recorded results for propagation precision,
planning, bootstrap/projection, commit safety, and related runtime costs.

## Runtime contracts worth knowing

- Domain mutation is external; change objects report mutations that have already happened.
- Registered object keys are immutable. Remove/re-add when identity genuinely changes.
- Mutation sets are validated atomically before runtime-owned state is committed.
- Collection navigation is explicit through add/remove/reset change records.
- A prepared mutation is versioned and rejected if the runtime advances before commit.
- Policy callbacks are dispatched only after runtime-owned state commits.
- `ConsistencyRuntime` is not thread-safe; callers must externally synchronize mutations and queries.

The detailed edge-case contracts live in the architecture documentation rather than this landing page.

## Documentation

- [Architecture and runtime contracts](docs/architecture.md)
- [End-to-end order fulfillment and allocation example](docs/order-fulfillment-example.md)
- [Current implementation roadmap](docs/roadmaps/README.md)
- [Measured-workload optimizer policy](docs/optimizer-policy.md)
- [Release and versioning process](RELEASING.md)
- [Changelog](CHANGELOG.md)

## Project status

Raffinert.Consistency is experimental. The core behavior suite runs on .NET 8 and .NET 10; the EF Core
integration suite targets .NET 10. CI restores, builds, tests, formats, packs, and validates the public API
and package metadata. Main-branch CI produces package artifacts but does not publish them automatically.
