# Codex implementation plan — generated UPDATE values and unmapped dependency capture

Status: **ACTIVE IMPLEMENTATION PLAN**

Baseline commit: `bc7840893fb633106f8a8203e4987d5315fc9e13`

Tasks: **152–158**

## Goal

Close two persistence-correctness holes that remain after Tasks 145–151:

1. the policy-aware manual EF snapshot currently drops scalar changes from tracked entities that are not mapped to an `ObjectSet`, even when those entities are nested dependency targets such as `line.Product.Price`;
2. store-generated semantic values that are generated on UPDATE are not guarded or reconciled. The current generated-value guard scans only `Added` entries, so a computed/trigger-generated member on an existing row can change during `SaveChanges` after Raffinert already planned against the old value.

This is a correctness wave. Do not add new domain features.

## Non-negotiable rules

- Preserve Core `ChangeValidationMode.StrictNewValue` for authoritative EF persistence.
- Do not solve generated values by recapturing the complete EF change tracker after `SaveChanges`.
- Preserve immutable pre-save old-value evidence.
- Only store-generated transitions proven by EF metadata and the captured database operation may be finalized after SQL.
- Arbitrary caller mutations after capture must still fail.
- `Map(set)` remains change-routing metadata, not a declaration that every semantic dependency target must itself be an object set.
- Nested dependency targets may be tracked EF entities without a Raffinert `ObjectSet` mapping.
- Do not auto-load anything from the database.
- Do not weaken authoritative-scope checks.
- Do not change relation, derived, invariant, planning, or dispatch semantics except where required to represent the missing EF mutations correctly.
- Do not publish packages, create tags, or create a release in this roadmap.

---

# Task 152 — Add failing proofs before implementation

Create focused SQLite tests first. They must fail against baseline `bc784089...` for the expected reason.

Recommended file:

`tests/Raffinert.Consistency.EntityFrameworkCore.Tests/GeneratedUpdateAndUnmappedDependencyTests.cs`

## 152.1 Manual workflow must capture an unmapped nested dependency target

Model:

```csharp
sealed class OrderLine
{
    public int Id { get; set; }
    public Product Product { get; set; } = null!;
    public int ProductId { get; set; }
    public decimal PriceMirror { get; set; }
}

sealed class Product
{
    public int Id { get; set; }
    public decimal Price { get; set; }
}
```

Raffinert model:

```csharp
var lines = model.Objects<OrderLine>().Key(x => x.Id);
var currentPrice = model.Derived(lines)
    .Compute(line => line.Product.Price);
```

Important: do **not** declare an `ObjectSet<Product>` and do **not** call `mappings.Map(products)`.

EF mapping:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Materialize(currentPrice, line => line.PriceMirror);
```

Seed runtime with complete `OrderLine` coverage. Change only:

```csharp
product.Price = 25m;
```

Capture with:

```csharp
var work = db.CaptureConsistencyUnitOfWork(
    runtime,
    mappings,
    new ConsistencySaveOptions
    {
        Scope = new ConsistencyScope().Complete(lines)
    });
```

Required assertions:

- `work.HasChanges == true`;
- `PrepareAndPlan()` contains the affected `OrderLine` derived evaluation;
- `PriceMirror` becomes `25m`;
- after DB commit + `CommitAfterDatabaseCommit()` the runtime returns `25m`;
- no `ObjectSet<Product>` is introduced just to make the test pass.

Baseline failure should demonstrate that `CapturePolicyAwareSnapshot` currently skips the unmapped `Product` entry.

## 152.2 Convenience/manual parity for unmapped dependency changes

Use the same model and prove all supported high-level paths observe the same `Product.Price` mutation:

- `SaveChangesConsistently`;
- `ConsistencySaveChangesInterceptor`;
- policy-aware `CaptureConsistencyUnitOfWork` manual path.

The manual path must no longer be weaker than the convenience path.

## 152.3 Store-generated semantic value on UPDATE

Use a real SQLite stored computed column or another provider-supported database-generated-on-update value.

Preferred entity:

```csharp
sealed class ComputedRecord
{
    public int Id { get; set; }
    public int Input { get; set; }
    public int DatabaseComputed { get; private set; }
    public int Mirror { get; set; }
}
```

Configure `DatabaseComputed` with SQLite generated-column SQL, for example equivalent to:

```sql
Input * 2
```

and EF metadata that results in `ValueGenerated.OnAddOrUpdate` / update generation.

Raffinert:

```csharp
var records = model.Objects<ComputedRecord>().Key(x => x.Id);
var computed = model.Derived(records).Compute(x => x.DatabaseComputed);
var valid = model.Invariant(records).Using(computed)
    .Must((_, value) => value <= 20);
```

Seed existing row:

```text
Input = 3
DatabaseComputed = 6
Mirror = 6
```

Then change:

```text
Input = 4
```

The database changes `DatabaseComputed` from `6` to `8` during UPDATE.

Tests must prove:

- convenience save rejects **before SQL** because a semantic store-generated update value is not final pre-SQL;
- interceptor reports the same exception and leaves DB/runtime unchanged;
- low-level `SaveChangesAndApply` / `SaveChangesAndApplyAsync` also fail closed rather than silently installing stale runtime state;
- policy-aware manual workflow supports it:
  1. capture before SQL;
  2. begin transaction;
  3. first `SaveChanges` obtains `DatabaseComputed = 8`;
  4. `PrepareAndPlan()` plans with `8`, not `6`;
  5. mirror is set to `8`;
  6. persist mirror and commit transaction;
  7. runtime commit installs the same result.

## 152.4 Update-generated semantic value must create a runtime mutation

After successful manual workflow assert that runtime-owned dependency state actually observes the generated transition.

Do not accept a test that only checks the CLR property or database row. Assert at least one of:

- derived cached value is fresh and equals the generated final value;
- affected invariant evaluation uses the final value;
- causal/detail output contains the generated member transition when causal detail is requested.

## 152.5 Unrelated post-capture drift remains rejected

Combine a generated-on-update value with a normal caller-owned semantic member.

After first `SaveChanges`, mutate the normal member again before `PrepareAndPlan()`.

Expected:

```text
EF-generated member finalization: allowed
unrelated caller drift: rejected by StrictNewValue
```

No test may disable strict validation.

---

# Task 153 — Restore policy-aware capture parity for unmapped tracked entities

Current problem in `ChangeTrackerAdapter.CapturePolicyAwareSnapshot`:

```csharp
var mapping = mappings.Resolve(entry);
if (mapping is null) continue;
```

This is incorrect for scalar dependency targets that are tracked by EF but intentionally have no `ObjectSet` mapping.

## Required behavior

For every tracked `Modified` entry:

- if it resolves to an object-set mapping, capture a set-scoped `PropertyChange` using `mapping.Property(...)`;
- if it does not resolve to a mapping, capture an unscoped `Change.Property(entity, member, oldValue, newValue)` exactly like the existing low-level capture path;
- additions/removals still require object-set mappings because object lifecycle belongs to a specific Raffinert object set;
- generated-fixup metadata may be null for unmapped entries unless the new generated-value logic in later tasks explicitly proves it is needed.

Do not introduce a fake object set for nested targets.

Refactor shared scalar-capture code if useful so these two paths cannot drift again:

```text
CaptureUnitOfWork
CapturePolicyAwareSnapshot
```

The desired invariant is:

> Given the same EF tracker state, policy-aware capture must contain every runtime-relevant scalar/reference/collection mutation that low-level capture would contain, plus extra immutable EF evidence required for authoritative planning.

Add a regression test comparing normalized mutation consequences rather than private implementation types.

---

# Task 154 — Generalize semantic generated-value detection to ADD and UPDATE

The current `ConsistencyGeneratedValueGuard.CaptureCandidates` only scans:

```csharp
Entries().Where(x => x.State == EntityState.Added)
```

Replace this with operation-aware candidate capture.

## Candidate operation kinds

Internal representation should distinguish at least:

```text
OnAdd
OnUpdate
```

A generated property is relevant only when all of the following are true:

1. EF metadata says it can be generated for the current operation;
2. the member participates in Raffinert semantics;
3. the current EF entry operation can actually cause generation.

Use EF metadata, not property-name heuristics.

Conceptual helpers:

```csharp
GeneratesOnAdd(IProperty property)
GeneratesOnUpdate(IProperty property)
```

based on `ValueGenerated` and save behavior.

## Semantic usage must work for mapped roots and unmapped nested targets

Do not regress Tasks 145–151 set scoping.

Introduce an internal runtime query that can distinguish:

- exact root-set usage when an entry resolves to a mapped set;
- nested navigation-target usage when the entry has no object-set mapping;
- union of both when a mapped entity is also used as a nested dependency target.

Do **not** return to global CLR-member matching for root object sets.

Suggested internal shape only; exact internal naming is flexible:

```csharp
ModelMemberUsageKind GetTrackedMemberUsage(
    IObjectSetDefinition? mappedSet,
    MemberInfo member);
```

Requirements:

- root usage remains exact-set scoped;
- nested segment usage may be discovered from compiled relation/derived/invariant paths;
- projected selector usage remains represented;
- same CLR type in multiple sets must keep the Task 145–151 regression green.

## Generated object-set key on UPDATE

A registered object-set key is immutable in Core. If EF metadata says an existing object's Raffinert key can be store-generated on UPDATE, the manual workflow cannot safely mutate runtime identity in place.

Fail closed with an explicit public exception rather than pretending the normal manual workflow supports it.

Recommended name:

```csharp
ConsistencyStoreGeneratedIdentityUpdateNotSupportedException
```

Include:

```csharp
Type EntityType
string PropertyName
```

Throw before SQL whenever possible.

Do not implement automatic remove/re-add identity migration in this roadmap.

---

# Task 155 — Capture pre-save evidence for generated UPDATE semantics

For an existing tracked entity whose semantic property will be generated by UPDATE, the pre-save snapshot must retain enough information to create the missing runtime mutation after SQL.

Example:

```text
captured before SQL:
DatabaseComputed = 6
Input changes 3 -> 4

first SaveChanges:
DatabaseComputed becomes 8

finalized mutation:
DatabaseComputed 6 -> 8
```

## Required snapshot evidence

For each relevant generated-on-update semantic property capture at least:

- entity reference;
- optional exact object-set mapping;
- EF `IProperty` metadata;
- semantic `MemberInfo`;
- pre-save value (`6` above);
- operation kind (`OnUpdate`);
- enough EF entry identity to prove the same tracked entity/property is being finalized.

Do not use EF `OriginalValue` after `SaveChanges` as the only source of the old value. `AcceptAllChanges` may have advanced EF snapshots. The authoritative old value for Raffinert is the one captured before SQL.

## Finalization after SQL

At `PrepareAndPlan()`:

- first confirm every required generated value is final;
- for generated-on-update candidates, compare captured pre-save value with final current value;
- when changed, synthesize a `PropertyChange`:
  - mapped entry -> `mapping.Property(...)`;
  - unmapped nested target -> unscoped `Change.Property(...)`;
- when unchanged, no synthetic mutation is needed;
- merge with captured ordinary mutations deterministically;
- duplicate mutation for the same instance/member must normalize to one valid transition or throw if contradictory.

Then pass the finalized mutation set through normal Core strict validation.

No special weak-validation path is allowed.

---

# Task 156 — Define readiness and save-path behavior consistently

## Convenience APIs

All pre-SQL convenience paths must reject a semantic value whose final value can only be known after the SQL operation:

```text
SaveChangesConsistently
SaveChangesConsistentlyAsync
ConsistencySaveChangesInterceptor
SaveChangesAndApply
SaveChangesAndApplyAsync
```

Use existing exception families where semantically correct:

```text
ConsistencyStoreGeneratedKeyRequiresManualWorkflowException
ConsistencyStoreGeneratedValueRequiresManualWorkflowException
```

For update-generated identity use the explicit unsupported exception from Task 154.

No convenience API may silently plan using the old generated value.

## Manual API

`CaptureConsistencyUnitOfWork` is the supported authoritative path.

`PrepareAndPlan()` before the database-generated value becomes final must throw:

```csharp
ConsistencyStoreGeneratedValueNotReadyException
```

After first `SaveChanges` it must succeed and use the final generated value.

## Empty / non-semantic generated values

Do not force a two-save workflow merely because EF has generated columns somewhere on the entity.

A generated property that does not participate in:

```text
ObjectSet key
relation dependency
derived dependency
invariant dependency
projected selector
nested dependency target
```

must remain irrelevant to Raffinert planning.

Keep the existing unused-generated-value regression green.

---

# Task 157 — Documentation, API baseline, and full proof matrix

Update current documentation:

- `README.md`
- `docs/architecture.md`
- `docs/ef-core-consistency.md`
- XML docs on affected public APIs/exceptions
- `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` as required

Documentation must explicitly say:

1. generated semantic inputs can occur on INSERT **or UPDATE**;
2. convenience APIs fail closed when the final semantic value is only known after SQL;
3. the policy-aware manual workflow captures old semantic state before SQL and may finalize only provider-generated transitions after SQL;
4. arbitrary post-capture mutation remains invalid;
5. nested navigation dependency targets do not need their own Raffinert object-set mapping for scalar change routing;
6. store-generated update of an existing Raffinert identity is unsupported in this version.

## Required test matrix

At minimum keep/add tests for:

| Scenario | Extension | Interceptor | SaveChangesAndApply | Manual |
|---|---:|---:|---:|---:|
| Add generated semantic key | reject | reject | reject | supported |
| Add generated semantic non-key | reject | reject | reject | supported |
| Existing dependent FK fixup to generated principal | n/a/pre-SQL impossible | n/a | n/a | supported |
| Update-generated semantic non-key | reject | reject | reject | supported |
| Update-generated Raffinert identity | explicit unsupported | explicit unsupported | explicit unsupported | explicit unsupported |
| Generated-but-unused member | normal | normal | normal | normal |
| Unmapped nested scalar target change | observed | observed | observed | observed |
| Generated finalization + unrelated post-capture drift | n/a | n/a | n/a | drift rejected |

Where a cell is not meaningful, document why instead of fabricating a test.

## Specific no-regression gates

Keep green:

- Tasks 130–136 scope tests;
- Tasks 137–144 manual persistence/generated-key tests;
- Tasks 145–151 generated FK fixup and same-CLR-set tests;
- materialization rollback tests;
- generated key rollback tests;
- runtime synchronization failure tests;
- public API analyzer.

---

# Task 158 — Release proof and roadmap closeout

Run from a clean checkout of the implementation head:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Also run:

- both executable samples;
- `dotnet pack` through the normal repository release workflow;
- package metadata/API verification;
- fresh package-consumer tests;
- normal GitHub CI;
- manual `Release candidate verification` workflow on the exact implementation head.

Only after both remote workflows are green:

- mark this plan `COMPLETED`;
- record exact implementation SHA, CI run ID, RC run ID, test counts, and artifact ID;
- update `docs/release-candidate-verification.md`;
- move the roadmap index from active to completed.

Do not publish NuGet packages in this task.

---

# Required commit boundaries

Use these commit boundaries unless a compile-fix must be folded into the immediately preceding commit:

```text
1. test(ef): expose generated update and unmapped dependency gaps
2. fix(ef): preserve unmapped dependency mutations in manual capture
3. fix(core): expose tracked semantic member usage safely
4. fix(ef): reconcile generated semantic update values
5. test(ef): complete generated update persistence matrix
6. docs: document generated update persistence contract
7. chore: record generated update safety release proof
```

Do not squash the first failing-proof commit into the implementation commit. The history should show the reproduced bug separately.

---

# Acceptance criteria

The roadmap is complete only when all are true:

```text
Policy-aware manual capture no longer drops scalar changes solely because an EF entity lacks an ObjectSet mapping.

A nested dependency like line.Product.Price works when only OrderLine is a Raffinert ObjectSet and Product is merely a tracked navigation target.

Store-generated semantic values produced by UPDATE are detected.

All convenience persistence paths fail closed before SQL when such values are not yet final.

The manual workflow captures the pre-SQL value and synthesizes the final generated transition after SQL.

Core StrictNewValue remains enabled.

Arbitrary post-capture caller drift is still rejected.

Generated-but-unused EF values do not force manual persistence.

Store-generated UPDATE of an existing Raffinert identity is explicitly rejected as unsupported.

Same-CLR multiple ObjectSet scoping from Tasks 145–151 remains correct.

CI and the manual RC workflow pass on the exact implementation head.
```
