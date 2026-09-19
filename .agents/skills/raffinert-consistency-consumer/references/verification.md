# Raffinert.Consistency consumer verification checklist

Use this checklist when reviewing integration code. A configuration can compile and still be incorrect if dependency tracking, materialization, authoritative coverage, runtime ownership, or persistence ordering is wrong.

---

## 1. Model correctness

Verify:

```text
[ ] Every ObjectSet has a stable application key.
[ ] Mutable semantic fields are not used as object-set identity.
[ ] Same-CLR object sets are not treated as interchangeable.
[ ] Relations encode matching semantics exactly once.
[ ] Source-local calculations are source-local in reality.
[ ] Derived-to-derived edges use From(handle).
[ ] Projected dependencies use From(selector, handle).
[ ] Opaque calculators declare every hidden modeled member read with DependsOn(...).
[ ] DependsOn is not substituted for logical value flow.
[ ] External state is not presented as if Raffinert tracks it automatically.
[ ] Materialized mirror properties are not graph inputs.
[ ] Invariant semantics match the business rule.
[ ] Dirty vs Invalid severity is deliberate.
```

Red flags:

```text
calculator reads current time or randomness
calculator queries a service/database during evaluation
calculator reads undeclared opaque state
matching logic is duplicated outside the relation
downstream derived value reads a physical mirror instead of From(handle)
manual setter side effects maintain the same derived state independently
```

---

## 2. From vs DependsOn

Classify every dependency.

### Logical value flow

```csharp
model.Derived(lines)
    .From(priceRate)
    .Select((line, rate) => ...);
```

The calculator receives the current logical upstream value.

### Opaque member read

```csharp
model.Derived(lines)
    .DependsOn(x => x.Product.Price)
    .Select(Calculate);
```

`DependsOn` affects change tracking; it does not add calculator arguments.

If a downstream node needs a value represented by a materialized property, the dependency should point to the logical derived handle rather than the physical mirror.

---

## 3. Relation aggregate semantics

Preferred relation aggregate declarations:

```text
From(relation).Sum(...)
From(relation).Count()
From(relation).LongCount()
From(relation).Any()
```

Verify:

```text
[ ] Operator matches business semantics.
[ ] Impact policy is attached deliberately where needed.
[ ] Membership add/remove/item-change transitions match Dirty/Invalid requirements.
[ ] Debug/diagnostic plan reports the expected aggregate strategy when inspected.
```

For custom relation calculations, `From(relation).Select(...)` is appropriate when no recognized aggregate expresses the rule.

---

## 4. Materialization correctness

Physical representation is declared on the derived definition:

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
[ ] Object-set keys do not use the mirror.
[ ] Relations do not use the mirror as semantic input.
[ ] DependsOn does not point at the mirror for a value that has a logical derived handle.
[ ] Invariants consume logical graph values.
[ ] Projected selectors do not use a mirror as a substitute for logical ownership.
```

A materialized property is not automatically current merely because it has been declared as a representation.

---

## 5. Evaluate vs Materialize

### Logical value

```csharp
runtime.Evaluate(derived, source);
```

Verify it does not write the physical mirror.

### One representation

```csharp
runtime.Materialize(derived, source);
```

Verify only the requested configured representation is synchronized.

### Every representation on one object

```csharp
runtime.Materialize(source);
```

Interpret this narrowly:

```text
synchronize configured materialized representations physically located on this object
```

It does not imply graph-wide physical writes or repair dispatch.

If application code directly reads a mirror, identify the boundary that guarantees it has been materialized first.

---

## 6. Invariant correctness

```csharp
model.Invariant(set)
    .From(derived)
    .Must(...);
```

Verify:

```text
[ ] Must represents the actual business truth condition.
[ ] Enforce is configured only when violation must block persistence.
[ ] Repair/reaction policy is explicit where required.
[ ] Materialization and repair remain separate operations.
```

---

## 7. EF policy

For injected EF applications, additionally verify:

```text
[ ] AddRaffinertConsistency<TDbContext>(...) is registered once with the compiled model and mappings.
[ ] No second Raffinert EF registration exists in the same IServiceCollection.
[ ] The application service injects only DbContext + ConsistencyRuntime.
[ ] Runtime and DbContext resolution works in either order without a circular dependency.
[ ] The service runtime and EF session/interceptor runtime are reference-equal within a scope.
[ ] A new scope receives a new runtime and DbContext.
[ ] Mapped entities tracked before and after runtime resolution are baseline-admitted automatically.
[ ] Runtime first binding happens before mapped tracked state is mutated.
[ ] Clean late binding is supported and dirty late binding is rejected explicitly.
[ ] Dirty unrelated entities outside the consistency graph do not block first binding.
[ ] Application code does not call runtime.Add/runtime.Apply for ordinary EF mutations.
```

Verify:

```text
[ ] Map(set) covers sets whose tracked changes/lifecycle must enter the consistency protocol.
[ ] Enforce(invariant) is present only for persistence-blocking rules.
[ ] Physical mirrors are declared with MaterializeTo on Core definitions.
[ ] Save behavior is intentional: Validate or RecalculateAndValidate.
```

`Map(set)` is mutation translation policy, not proof that every persisted row is represented.

---

## 8. Authoritative coverage proof

For every active cross-object dependency, write down the proof strategy.

### Whole-set proof

If code declares:

```csharp
scope.Complete(set);
```

require an explicit explanation of why every relevant object in that set is represented for this operation.

Valid examples:

```text
small bounded aggregate loaded completely
complete import batch for the represented partition
runtime seeded with the whole authoritative set and mutations serialized inside one owner boundary
```

Insufficient evidence:

```text
the handler usually loads most rows
Map(set) is configured
only currently visible rows are needed by the UI
```

### Targeted direct-reference proof

For `DiscoverConsumers`, verify:

```text
exact Raffinert ObjectSet
exact supported direct reference navigation
tracked query
batched target set
authoritative query
all references required for evaluation loaded
```

Targeted discovery does not establish whole-set completeness.

---

## 9. Discovery resolver review

For every resolver:

```text
[ ] WHERE can return every persisted consumer for every requested target.
[ ] Target IDs are batched.
[ ] Tenant/org/dataset/security filters match the authoritative boundary.
[ ] Soft-delete semantics are intentional.
[ ] Query is tracked.
[ ] Required references are loaded.
[ ] No paging/Take/First can under-fetch authoritative consumers.
[ ] Current tracked retargeting is respected.
```

Safe:

```text
authoritative superset
```

Unsafe:

```text
current UI page
Top(N)
convenience filters that can omit valid consumers
roots lacking references required for active evaluation
```

---

## 10. Unsupported discovery shapes remain fail-closed

Do not assume automatic support for:

```text
multi-hop external consumer discovery
collection-navigation discovery
relation-source coverage through direct-reference discovery
relation-target coverage through direct-reference discovery
projected-consumer completeness through direct-reference discovery
automatic Include generation
automatic Reference.Load
automatic lazy-loading correctness
automatic proof that a host query is complete
```

When the required shape is outside supported discovery semantics, establish genuine authoritative coverage or redesign the consistency boundary.

---

## 11. Persistence ordering

Injected EF flow:

```text
tracked mutation
    ↓
optional runtime.Materialize(entity)
    ↓
pending plan / requested physical mirrors
    ↓
ordinary SaveChanges[Async]
    ↓
SQL success
    ↓
install exact runtime plan and dispatch once
```

Verify:

```text
[ ] Materialize(entity) does not advance runtime state or dispatch callbacks.
[ ] Materialize(entity) leaves committed navigation/projection indexes at the durable baseline before SQL.
[ ] Pending plans are invalidated when later mapped tracking changes baseline/coverage state.
[ ] Stale pending mirror writes are restored before a replacement plan is prepared.
[ ] SaveChanges reuses the pending plan when semantic tracked inputs are unchanged.
[ ] Repeated Materialize calls without intervening tracking reuse the same pending plan.
[ ] An intervening semantic change discards and rebuilds the pending plan.
[ ] Library-owned mirror writes are excluded from semantic mutation fingerprinting.
[ ] Failed SQL does not commit, rebase indexes, or dispatch the pending plan and a retry rebuilds safely.
```

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
runtime installation
    ↓
dispatch
```

Runtime state must not be committed before database durability.

Manual unit of work:

```text
CaptureConsistencyUnitOfWork
transaction begins
optional save for semantic store-generated values
PrepareAndPlan
persist durable repair/outbox work
final SaveChanges
transaction commit
CommitAfterDatabaseCommit
Dispatch
```

---

## 12. Runtime lifetime and concurrency

Raffinert runtime is mutable and not thread-safe.

Verify:

```text
[ ] Runtime ownership corresponds to a defined consistency boundary.
[ ] Runtime is not casually shared concurrently across unrelated operations.
[ ] Long-lived runtime has explicit serialization or partitioning.
[ ] Seeding/rebuild strategy is clear.
[ ] Cross-process writers are accounted for.
```

---

## 13. Invisible database mutations

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

If these affect modeled state without tracked mutation evidence, require an explicit strategy:

```text
exact mutation publication
reconciliation
runtime rebuild/reseed
single serialized owner
explicit exclusion from the consistency boundary
```

---

## 14. Required tests

### Logical value flow

```text
[ ] changing each modeled input changes the result correctly
[ ] unrelated changes do not alter the result
[ ] From(handle) consumes current logical value while physical mirror is stale
```

### Materialization

```text
[ ] Evaluate leaves mirror unchanged
[ ] targeted Materialize synchronizes only requested representation
[ ] object Materialize synchronizes all applicable representations on requested source
[ ] Fresh logical cache can restore a corrupted mirror without recalculation
[ ] materialization does not execute repair
```

### Relation aggregate

```text
[ ] Sum/Count/LongCount/Any produce expected values
[ ] membership add/remove/item change follows configured severity
```

### Direct-reference discovery

```text
[ ] runtime starts with a subset of root consumers
[ ] database contains additional authoritative consumers
[ ] target mutation discovers unloaded consumers
[ ] affected consumers receive correct logical values and mirrors
[ ] resolver batches work by discovery obligation
[ ] persisted results are verified from a fresh DbContext
```

### Retargeting

```text
[ ] consumer retargeted away is excluded from old target
[ ] consumer retargeted in is included for new target
```

### Persistence policy

```text
[ ] Validate enforces invariants without synchronizing mirrors
[ ] RecalculateAndValidate synchronizes affected mirrors
[ ] enforced violation blocks persistence
```

### Failure

```text
[ ] resolver/planning failure does not install runtime state
[ ] SQL failure leaves runtime plan uninstalled
[ ] framework-owned physical writes are restored according to materialization semantics
[ ] retry behavior is tested when the same context is intentionally reused
```

---

## 15. Independent persisted-state verification

Prefer a fresh `DbContext` after a successful integration operation:

```csharp
await using var verification = CreateDbContext();

var persisted = await verification.Set<Association>()
    .AsNoTracking()
    .SingleAsync(x => x.Id == id);

Assert.Equal(expected, persisted.CombinedValue);
```

This proves database state rather than only the already-tracked object.

---

## 16. Replacing manual maintenance orchestration

When a declarative graph replaces manual recalculation code, require parity evidence for:

```text
same affected roots
same logical calculations
same null/zero/error semantics
same persisted outcomes
correct unloaded-consumer behavior
correct repair/invariant behavior
```

Remove duplicate manual orchestration only after parity is demonstrated.

---

## 17. Final reviewer output

Summarize:

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

Runtime ownership
    <lifetime + synchronization>

Invisible mutation boundary
    <other writers and mitigation>

Tests proving correctness
    <focused list>
```

If one of these sections cannot be explained from the implementation, the integration is not yet sufficiently specified.
