# Raffinert.Consistency consumer verification checklist

Use this checklist when reviewing AI-generated integration code in a downstream application.

The checklist is deliberately strict. A consumer integration can compile and still be incorrect if it lies about coverage or misses unloaded consumers.

---

## 1. Model correctness

Verify:

```text
[ ] Every Raffinert ObjectSet has a stable application key.
[ ] No mutable semantic field is used as runtime identity.
[ ] Relations encode the real matching semantics exactly once.
[ ] Source-local calculations are source-local in reality.
[ ] Derived-from-derived edges use Using(...) instead of duplicating orchestration.
[ ] Projected dependencies use the projected upstream API rather than hidden manual reads.
[ ] Opaque calculators declare every hidden source-state member with DependsOn(...).
[ ] DependsOn is not being used to pretend external state is tracked.
[ ] Invariant semantics match the business rule.
[ ] Dirty vs Invalid severity is intentional where configured.
```

Red flags:

```text
calculator reads DateTime.Now
calculator queries a database/service
calculator reads a property not declared or analyzable
same matching rule repeated in service and relation
manual setter side effects still maintain the same derived state
```

---

## 2. EF mapping correctness

Verify:

```text
[ ] Map(set) is used for sets whose tracked mutations/lifecycle must be translated.
[ ] Enforce(invariant) is present only for violations that must block SQL.
[ ] Materialize(derived, property) targets a direct writable mapped property.
[ ] Materialized mirror is sink-only.
[ ] No key/generated/dependency member is reused as a mirror target.
[ ] Save behavior is intentional: Validate vs RecalculateAndValidate.
```

Do not accept statements such as:

```text
"Map means all rows are covered"
"Materialized property is now the source of truth"
```

Both are wrong.

---

## 3. Coverage proof

For every active cross-object dependency, write down the proof strategy.

### Whole-set proof

If code uses:

```csharp
scope.Complete(set);
```

require an explicit explanation of why the whole set is authoritative in that operation.

Valid examples:

```text
small bounded aggregate loaded completely
import batch contains the entire consistency partition represented by this set
runtime was seeded with the entire set and mutations are serialized inside the same boundary
```

Invalid explanations:

```text
"otherwise Raffinert throws"
"Map(set) was configured"
"the handler usually loads all of them"
"we only need these rows most of the time"
```

### Targeted direct-reference proof

If roots may be unloaded, verify `DiscoverConsumers` uses:

```text
exact Raffinert ObjectSet
exact direct non-collection EF navigation
tracked query
batched target set
complete authoritative query
all evaluation-required references loaded
```

Do not treat targeted discovery as whole-set completeness.

---

## 4. Discovery resolver review

For every resolver, inspect the actual query.

Example checklist:

```text
[ ] The WHERE clause can return every persisted consumer for every requested target.
[ ] Target IDs are batched in one query shape.
[ ] Tenant/org/dataset/security filters match the real authoritative boundary.
[ ] Soft-delete semantics are deliberate.
[ ] The query is tracked.
[ ] No AsNoTracking is present.
[ ] The discovery navigation is included/loaded where needed.
[ ] Other direct references used by active formulas are also included/loaded.
[ ] The query does not under-fetch because of pagination/Take/First/etc.
[ ] The query does not assume database relationship state overrides current tracked retargeting.
```

Safe:

```text
resolver returns an authoritative superset
```

Unsafe:

```text
resolver returns only the roots currently visible in a UI page
resolver returns Top(100)
resolver applies a business filter unrelated to authoritative consistency
resolver loads Source but formula also reads unloaded Target
```

---

## 5. Unsupported shapes remain fail-closed

Verify AI-generated code did not invent support for:

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

If one of these is required, the generated implementation must either:

```text
provide genuine Complete(set) coverage
redesign the consistency boundary
or explicitly report the feature as unsupported
```

It must not weaken the contract silently.

---

## 6. Persistence ordering

Ordinary stable-key flow should look like:

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

Check that no application code commits Raffinert runtime state before SQL durability.

For manual UoW:

```text
CaptureConsistencyUnitOfWork
transaction begins
optional save for required generated semantic values
PrepareAndPlan
persist outbox/durable policy work
final SaveChanges
transaction commit
CommitAfterDatabaseCommit
Dispatch
```

Reject code that does:

```text
PrepareAndPlan
CommitAfterDatabaseCommit
SaveChanges
```

or any equivalent runtime-before-database ordering.

---

## 7. Runtime lifetime/concurrency

Raffinert runtime is mutable and not thread-safe.

Verify:

```text
[ ] Runtime ownership/lifetime corresponds to an isolated consistency boundary.
[ ] One runtime is not casually registered as an application-wide singleton used concurrently.
[ ] If runtime is long-lived, application has an explicit serialization/partition model.
[ ] Runtime seeding/coverage strategy is clear.
[ ] Cross-process database writers are accounted for.
```

If the agent cannot explain runtime ownership, it should not finalize DI registration.

---

## 8. Invisible database mutations

Search the consumer repository for relevant usage of:

```text
ExecuteUpdate
ExecuteDelete
FromSql / raw SQL mutation
bulk update/delete libraries
stored procedures that mutate managed state
database triggers
Cascade / SetNull delete behavior
message consumers or other processes writing the same rows
```

If these can change Raffinert-relevant state without tracked mutation evidence, require a documented strategy:

```text
exact mutation publication
reconciliation
runtime rebuild/reseed
serialized owner process
or explicit exclusion from the authoritative boundary
```

Do not assume ChangeTracker sees database-side effects.

---

## 9. Required consumer tests

Choose tests based on the real application shape.

### Basic formula

```text
[ ] changing each declared input changes the derived result correctly
[ ] unrelated change does not alter the result
```

### Direct-reference open-world discovery

```text
[ ] runtime starts with only one known root
[ ] DB contains additional consumers
[ ] changed target discovers unloaded consumers
[ ] all affected consumers get correct derived values
[ ] resolver called once per navigation obligation, not once per root
[ ] runtime version increments once after successful save
[ ] persisted values verified from a separate context
```

### Retargeting

```text
[ ] consumer retargeted away is excluded from old target
[ ] consumer retargeted in is included for new target
```

### Evaluation closure

```text
[ ] missing required sibling navigation fails before SQL
[ ] loaded optional null succeeds where formula supports null
[ ] unloaded null fails
[ ] detached required reference fails
```

### Persistence policy

```text
[ ] Validate enforces invariants but does not write mirrors
[ ] RecalculateAndValidate writes mirrors
[ ] enforced violation blocks SQL
```

### Failure

```text
[ ] resolver/planning failure leaves runtime version unchanged
[ ] SQL failure leaves runtime plan uninstalled
[ ] framework-owned mirror writes are restored as documented
[ ] retry behavior is tested if the application intends to reuse the same context
```

---

## 10. Independent persisted-state verification

After a successful integration test, prefer a fresh DbContext:

```csharp
await using var verification = CreateDbContext();

var persisted = await verification.Set<Association>()
    .AsNoTracking()
    .SingleAsync(x => x.Id == id);

Assert.Equal(expected, persisted.UnitRate);
```

This is stronger than asserting only the already-tracked object.

Do not confuse this with the discovery resolver: the resolver itself must remain tracked.

---

## 11. Migration review

When replacing an existing manual maintenance service, ensure the PR explains what old orchestration is replaced.

Typical before:

```text
collect changed endpoint IDs
query affected link rows
union tracked rows
load missing endpoints
recalculate
persist mirror
```

Typical after:

```text
declared DependsOn/direct dependency
DiscoverConsumers
Materialize
consistent save boundary
```

Do not remove manual orchestration until parity tests demonstrate:

```text
same affected rows
same calculation results
same null/zero/error semantics
same persistence behavior
correct unloaded-consumer behavior
```

---

## 12. Final AI reviewer output

A reviewing agent should summarize the integration using this structure:

```text
Consistency graph
    <inputs -> derived -> invariant chain>

Runtime-owned ObjectSets
    <sets and keys>

EF policy
    mapped sets:
    enforced invariants:
    materialized mirrors:

Coverage strategy
    Complete(...): <why valid>
    DiscoverConsumers(...): <navigation + authoritative query>

Save boundary
    <convenience or manual UoW>

Invisible mutation boundary
    <raw SQL/triggers/other writers and mitigation>

Tests proving correctness
    <focused list>

Unsupported/out-of-scope behavior
    <anything intentionally not handled>
```

If the reviewer cannot fill out one of these sections from the code, request correction before approving the generated integration.
