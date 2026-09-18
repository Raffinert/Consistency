# Raffinert.Consistency consumer verification checklist

Use this checklist when reviewing AI-generated integration code in a downstream application using the **0.2 API line**.

A consumer integration can compile and still be incorrect if it lies about dependency tracking, materialization, authoritative coverage, or persistence ordering.

---

## 1. API-line check

Reject generated code that still uses removed v1 API:

```text
Using(...)
Compute(...)
Incrementally()
ConsistencyRuntime.Get(...)
ConsistencyEfCoreMappings.Materialize(...)
DerivedUsingBuilder
InvariantUsingBuilder
```

Expected v2 vocabulary:

```text
From(...)
DependsOn(...)
Select(...)
Sum / Count / LongCount / Any
MaterializeTo(...)
Evaluate(...)
Materialize(...)
```

If a generated example contains mixed v1/v2 syntax, fix it before reviewing deeper semantics.

---

## 2. Model correctness

Verify:

```text
[ ] Every Raffinert ObjectSet has a stable application key.
[ ] No mutable semantic field is used as object-set identity.
[ ] Same-CLR object sets are not treated as interchangeable.
[ ] Relations encode matching semantics exactly once.
[ ] Source-local calculations are source-local in reality.
[ ] Derived-from-derived edges use From(handle).
[ ] Projected dependencies use From(selector, handle).
[ ] Opaque calculators declare every hidden modeled member with DependsOn(...).
[ ] DependsOn is not used as a substitute for logical value flow.
[ ] DependsOn is not used to pretend external state is tracked.
[ ] Materialized mirror properties are not used as graph inputs.
[ ] Invariant semantics match the business rule.
[ ] Dirty vs Invalid is intentional where configured.
```

Red flags:

```text
calculator reads DateTime.Now
calculator queries a database/service
calculator reads undeclared opaque state
same matching rule repeated in service and relation
downstream derived value reads a MaterializeTo property instead of From(handle)
manual setter side effects still maintain the same derived state
```

---

## 3. `From` vs `DependsOn`

For every derived definition, classify each dependency.

### `From(...)`

Use for explicit logical value flow:

```csharp
model.Derived(lines)
    .From(priceRate)
    .Select((line, rate) => ...);
```

The downstream calculator must receive the logical upstream value.

### `DependsOn(...)`

Use for member reads hidden inside ordinary code:

```csharp
model.Derived(lines)
    .DependsOn(x => x.Product.Price)
    .Select(Calculate);
```

`DependsOn` affects change tracking; it does not add calculator arguments.

Review failure:

```text
MaterializeTo(x => x.PriceRate)
...
DependsOn(x => x.PriceRate)
```

when the intended dependency is the logical PriceRate node. That must be `From(priceRate)`.

---

## 4. Recognized aggregate check

Preferred relation aggregate declarations use:

```text
From(relation).Sum(...)
From(relation).Count()
From(relation).LongCount()
From(relation).Any()
```

Verify:

```text
[ ] no Incrementally() appears
[ ] recognized operator matches intended semantics
[ ] Impact policy is attached deliberately
[ ] additive/removal/item-change behavior matches Dirty/Invalid requirements
```

For a custom relation calculation, `From(relation).Select(...)` is valid, but do not claim it is a recognized incremental aggregate unless the operator actually is one.

---

## 5. Materialization correctness

Materialization belongs to the Core derived declaration:

```csharp
var total = model.Derived(records)
    .Select(...)
    .MaterializeTo(x => x.Total);
```

Verify:

```text
[ ] MaterializeTo targets a direct writable property on the derived source object.
[ ] TValue and target property type are compatible.
[ ] Mirror is sink-only.
[ ] No object-set key uses the mirror.
[ ] No relation/invariant/DependsOn/projected selector reads the mirror as semantic input.
[ ] EF mappings do not call removed .Materialize(...).
```

Do not accept:

```text
"Materialized property is the source of truth"
"MaterializeTo means the POCO getter is always current"
```

Both are wrong.

---

## 6. Evaluate vs Materialize

Review runtime calls carefully.

### Logical read

```csharp
runtime.Evaluate(derived, source);
```

Must not write the POCO mirror.

### One mirror

```csharp
runtime.Materialize(derived, source);
```

Synchronizes the requested configured representation.

### All mirrors on one object

```csharp
runtime.Materialize(source);
```

Verify object materialization is being interpreted narrowly:

```text
all configured materialized representations ON THIS OBJECT
```

It must not be described as:

```text
repair whole graph
materialize dependency objects
materialize downstream objects
```

If application code directly reads a mirror, establish where materialization/freshness is guaranteed first.

---

## 7. Invariant correctness

Expected value-flow form:

```csharp
model.Invariant(set)
    .From(derived)
    .Must(...);
```

Verify:

```text
[ ] Must is the real business truth condition.
[ ] EF Enforce is used only when violation must block SQL.
[ ] Repair/reaction remains separate from materialization.
[ ] Materialize does not silently dispatch repair.
```

---

## 8. EF mapping correctness

Verify:

```text
[ ] Map(set) is used for sets whose tracked mutations/lifecycle must be translated.
[ ] Enforce(invariant) is present only for violations that must block SQL.
[ ] Core MaterializeTo descriptors are used for mirrors.
[ ] Save behavior is intentional: Validate vs RecalculateAndValidate.
```

Do not accept:

```text
"Map means all rows are covered"
"EF mappings own materialization definitions"
```

Both are wrong in the 0.2 API model.

---

## 9. Coverage proof

For every active cross-object dependency, write down the proof strategy.

### Whole-set proof

If code uses:

```csharp
scope.Complete(set);
```

require an explicit explanation of why the entire set is authoritative in that operation.

Valid examples:

```text
small bounded aggregate loaded completely
complete import batch for the represented consistency partition
runtime seeded with the complete set and writes serialized inside one owner boundary
```

Invalid explanations:

```text
"otherwise Raffinert throws"
"Map(set) was configured"
"handler usually loads all of them"
```

### Targeted direct-reference proof

If roots may be unloaded, verify `DiscoverConsumers` uses:

```text
exact Raffinert ObjectSet
exact direct non-collection EF navigation
tracked query
batched target set
authoritative query
all references required for evaluation loaded
```

Targeted discovery is not whole-set completeness.

---

## 10. Discovery resolver review

For every resolver:

```text
[ ] WHERE can return every persisted consumer for every requested target.
[ ] Target IDs are batched.
[ ] Tenant/org/dataset/security filters match the authoritative boundary.
[ ] Soft-delete semantics are deliberate.
[ ] Query is tracked.
[ ] No AsNoTracking root is returned.
[ ] Discovery navigation is loaded where needed.
[ ] Other references used by active formulas are loaded too.
[ ] No Take/First/paging under-fetch can occur.
[ ] Query does not assume database relationship state beats current tracked retargeting.
```

Safe:

```text
resolver returns an authoritative superset
```

Unsafe:

```text
resolver returns current UI page
resolver returns Top(N)
resolver filters out valid consumers for convenience
resolver loads Source while formula also needs unloaded Target
```

---

## 11. Unsupported shapes remain fail-closed

Verify generated code did not invent support for:

```text
multi-hop external consumer discovery
collection-navigation discovery
relation-source coverage via DiscoverConsumers
relation-target coverage via DiscoverConsumers
projected-consumer coverage via DiscoverConsumers
automatic Include generation
automatic Reference.Load
automatic lazy-loading correctness
automatic host-query completeness proof
```

If required, use genuine `Complete(set)` coverage, redesign the consistency boundary, or report the scenario as unsupported.

Do not weaken the contract silently.

---

## 12. Persistence ordering

Ordinary flow:

```text
POCO changes
    ↓
SaveChangesConsistently[Async]
    ↓
scope/discovery validation
    ↓
plan
    ↓
invariant/materialization policy
    ↓
SQL
    ↓
exact runtime install
    ↓
dispatch
```

Runtime state must not be committed before SQL durability.

Manual UoW:

```text
CaptureConsistencyUnitOfWork
transaction begins
optional save for required generated semantic values
PrepareAndPlan
persist durable repair/outbox work
final SaveChanges
transaction commit
CommitAfterDatabaseCommit
Dispatch
```

Reject any runtime-before-database ordering.

---

## 13. Runtime lifetime/concurrency

Raffinert runtime is mutable and not thread-safe.

Verify:

```text
[ ] Runtime ownership corresponds to a defined consistency boundary.
[ ] Runtime is not casually registered as a concurrently used application singleton.
[ ] Long-lived runtime has explicit serialization/partitioning.
[ ] Seeding/rebuild strategy is clear.
[ ] Cross-process writers are accounted for.
```

If runtime ownership cannot be explained, do not approve DI registration.

---

## 14. Invisible database mutations

Search for:

```text
ExecuteUpdate
ExecuteDelete
raw SQL mutation
bulk update/delete libraries
stored procedures
triggers
Cascade / SetNull database effects
other services/processes writing the same rows
```

If they can change modeled state without tracked evidence, require a strategy:

```text
exact mutation publication
reconciliation
runtime rebuild/reseed
single serialized owner
explicit exclusion from the authoritative boundary
```

Do not assume ChangeTracker sees database-side effects.

---

## 15. Required consumer tests

Choose tests based on the real integration.

### Logical value flow

```text
[ ] changing every declared input changes the value correctly
[ ] unrelated change does not
[ ] From(handle) consumes current logical value even when mirror is stale
```

### Materialization

```text
[ ] Evaluate does not write mirror
[ ] targeted Materialize writes only requested mirror
[ ] object Materialize writes all applicable mirrors on requested source
[ ] Fresh logical cache can restore corrupt mirror without recomputation
[ ] materialization does not dispatch repair
```

### Relation aggregate

```text
[ ] Sum/Count/LongCount/Any expected values
[ ] membership add/remove/item change follows configured severity
```

### Direct-reference open-world discovery

```text
[ ] runtime starts with only one/subset of roots
[ ] DB has additional consumers
[ ] changed target discovers unloaded consumers
[ ] all affected roots receive correct values/mirrors
[ ] resolver called per navigation obligation, not per root
[ ] persisted values verified from fresh DbContext
```

### Retargeting

```text
[ ] consumer retargeted away excluded from old target
[ ] consumer retargeted in included for new target
```

### Persistence policy

```text
[ ] Validate enforces invariants but does not write mirrors
[ ] RecalculateAndValidate writes affected mirrors
[ ] enforced violation blocks SQL
```

### Failure

```text
[ ] resolver/planning failure leaves runtime uncommitted
[ ] SQL failure leaves runtime plan uninstalled
[ ] framework-owned mirror writes restore as documented
[ ] retry behavior tested if same context is reused
```

---

## 16. Independent persisted-state verification

After a successful integration test, prefer a fresh `DbContext`:

```csharp
await using var verification = CreateDbContext();

var persisted = await verification.Set<Association>()
    .AsNoTracking()
    .SingleAsync(x => x.Id == id);

Assert.Equal(expected, persisted.CombinedValue);
```

This proves database state, not merely the already-tracked POCO.

The discovery resolver itself must still use tracked entities.

---

## 17. Migration review

When replacing a manual maintenance service, explain what orchestration disappears.

Typical before:

```text
collect changed endpoint IDs
query affected root rows
union tracked rows
load missing endpoints
recalculate
write mirror
```

Typical after:

```text
DependsOn / From declarations
Select / recognized aggregate
MaterializeTo
DiscoverConsumers where needed
consistent save boundary
```

Do not remove manual orchestration until parity tests demonstrate:

```text
same affected rows
same logical calculation
same null/zero/error semantics
same persisted outcomes
correct unloaded-consumer behavior
```

---

## 18. Final reviewer output

A reviewing agent should summarize:

```text
Consistency graph
    <inputs -> DependsOn/From -> derived -> invariant>

Runtime-owned ObjectSets
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
    <convenience or manual UoW>

Runtime ownership
    <lifetime + synchronization>

Invisible mutation boundary
    <raw SQL/triggers/other writers and mitigation>

Tests proving correctness
    <focused list>

Unsupported/out-of-scope behavior
    <anything intentionally not handled>
```

If the reviewer cannot fill these sections from the code, request correction before approving the integration.
