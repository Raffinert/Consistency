# Codex implementation plan — generated-value fixup and set-scoped persistence safety

Status: **ACTIVE IMPLEMENTATION PLAN**

Baseline commit: `419d547b926c8bba2e61a5f08134b93e1e9e25bc`

Tasks: **145–151**

This is a narrow correctness continuation after Tasks 137–144. The previous wave correctly added reverse-navigation scope requirements and a policy-aware manual persistence unit of work. Do not turn this plan into a new feature wave.

Two concrete holes remain in the generated-value/manual-persistence contract:

1. the manual workflow captures scalar/FK `NewValue` evidence **before** the first `SaveChanges`, but EF can legitimately replace a temporary generated principal key and propagate the final value into an existing dependent FK before `PrepareAndPlan`; Core strict-new-value validation then sees a different value and rejects a valid workflow;
2. generated-value readiness currently watches only store-generated **object-set key members**, even though any store-generated member used by a relation, derived value, invariant, or projected selector can make pre-save planning semantically wrong.

There is also one adjacent metadata correctness problem that must be fixed before generated-value readiness reuses member-usage metadata: `ConsistencyRuntime.GetMemberUsage(set, member)` currently searches relation/derived/invariant usage globally by `MemberInfo` and does not always constrain usage to the supplied object set. Two object sets over the same CLR type can therefore contaminate each other's persistence-policy checks.

The goal of this wave is:

> Preserve authoritative pre-save mutation evidence while allowing only **proven EF-generated value/fixup transitions** to be finalized after the first database save, without weakening Core `StrictNewValue` validation for caller-owned mutations.

---

# 1. Confirmed baseline — do not redesign

At the baseline commit the following are already correct and must remain correct:

- `ConsistencyScope` is an explicit host assertion; Raffinert does not query the database to prove completeness.
- convenience saves and `ConsistencySaveChangesInterceptor` validate mappings/scope before semantic planning and SQL;
- `CaptureConsistencyUnitOfWork(...)` is the policy-aware manual path for application-owned transactions, generated keys, and outbox work;
- the application owns the database transaction;
- `PrepareAndPlan()` performs policy-aware planning and materialization but no SQL;
- `CommitAfterDatabaseCommit()` installs the retained runtime plan only after caller-declared database durability;
- `Dispatch()` is resumable after callback failure;
- low-level `ConsistencyUnitOfWork` and `ChangeTrackerAdapter.CaptureUnitOfWork` remain policy-agnostic primitives;
- Core `ChangeValidationMode.StrictNewValue` rejects drift between reported `PropertyChange.NewValue` and the current CLR value;
- registered runtime object-set keys are immutable;
- materialized CLR/EF properties remain sink-only mirrors;
- Tasks 137–144 completed with green CI/RC proof.

Do not weaken any of those contracts to solve generated-value fixup.

---

# 2. The exact bug to reproduce

The current high-level manual path does approximately:

```text
DetectChanges
-> validate policy/scope
-> capture MutationSet with PropertyChange(old, current)
-> capture generated object-set-key candidates
-> begin caller transaction
-> first SaveChanges
   -> database generates principal key
   -> EF replaces temporary principal key
   -> EF propagates final key into dependent FK
-> PrepareAndPlan
   -> Core StrictNewValue validates captured dependent FK NewValue
   -> current final FK != captured temporary FK
   -> InvalidOperationException
```

Example:

```text
Existing Parent A: Id = 1
Existing Child: ParentId = 1
New Parent B: Id = temporary -2147482647

Caller:
  child.Parent = parentB

Capture before first SaveChanges:
  PropertyChange Child.ParentId: 1 -> -2147482647
  navigation evidence: Parent A -> Parent B

After first SaveChanges:
  Parent B.Id = 42
  Child.ParentId = 42

Current strict check:
  actual 42 != reported -2147482647
  -> reject
```

That transition is not caller drift. It is a predictable EF store-generated-key propagation step.

The unsafe workaround is **not** to disable strict validation or to re-read every current value after the first save. Doing so would silently accept arbitrary caller mutations that happened after capture.

---

# 3. Frozen architectural decisions

The implementation agent has no design discretion on these points.

## 3.1 Preserve old evidence; finalize only proven generated/fixup new values

The capture boundary remains before the first `SaveChanges` because old scalar/reference/collection evidence can disappear after EF accepts changes.

For a property change:

```text
OldValue = authoritative pre-operation evidence captured before SaveChanges
NewValue = captured caller-intended value, except for a narrowly proven EF generated/fixup transition
```

When EF replaces a temporary FK with a final generated principal key, update only the captured **new** value used to build the Core mutation. Never replace the captured old value.

## 3.2 Do not weaken Core `StrictNewValue`

Core stays unchanged unless a failing test proves an actual Core defect.

Do not:

- change `StrictNewValue` to best-effort;
- skip `ValidateCurrentValues` for EF;
- add a global `IgnoreGeneratedValueDrift` mode;
- compare only object identity and ignore scalar drift;
- re-capture all property changes after SaveChanges.

The EF adapter must present Core with a finalized, truthful mutation batch, then Core strict validation must remain active.

## 3.3 Generated-value readiness is semantic, not only identity-based

A store-generated EF property requires the first save before authoritative planning when the property participates in the selected consistency model semantics.

Relevant usage includes at least:

```text
ObjectSet key
Relation dependency
Derived dependency
Invariant dependency
Projected selector
```

A generated property unused by the consistency graph must not force a two-save workflow merely because EF marks it `ValueGenerated`.

Materialization targets remain independently rejected when store-generated.

## 3.4 Member usage must be object-set scoped

`GetMemberUsage(set, member)` must answer usage of `member` **as reachable from that exact object set's semantic role**.

Two sets over the same CLR type are distinct:

```csharp
var activeLines = model.Objects<Line>().Key(x => x.Id);
var archivedLines = model.Objects<Line>().Key(x => x.Id);
```

Usage in `activeLines` must not contaminate `archivedLines` merely because both use the same `PropertyInfo`.

## 3.5 Do not infer arbitrary database behavior

The adapter may finalize a captured value only when EF metadata + tracked-entry evidence proves the transition came from store-generated key/value propagation.

If proof is ambiguous, fail closed with a clear exception. Do not guess.

## 3.6 No transaction ownership

The high-level manual unit still does not begin, commit, or roll back the database transaction.

Do not add hidden transactions or retry loops.

---

# 4. Hard stops / non-goals

Do not implement in Tasks 145–151:

- partition/key/tenant-scoped `ConsistencyScope`;
- EF auto-loading;
- distributed runtime synchronization;
- generic persistence workflow engine;
- automatic transaction ownership;
- transaction commit detection through provider-specific hacks;
- weakening `StrictNewValue`;
- automatic domain rollback;
- arbitrary post-save mutation recapture;
- support for changing registered runtime keys;
- broad public API redesign;
- package publication/version bump/tag/release.

Do not remove low-level APIs.

---

# Task 145 — Add decisive red proofs before changing production code

## Goal

Prove the exact missing cases against SQLite before implementing a fix.

Create a dedicated file, suggested:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/GeneratedValueFixupTests.cs
```

Do not hide these cases inside the existing large SQLite test file.

## 145.1 Existing dependent retargeted to new generated principal

Model:

```text
Parent
  Id int database-generated PK / consistency key

Child
  Id int stable existing PK
  ParentId int FK
  Parent navigation
```

Consistency model:

```csharp
var parents = model.Objects<Parent>().Named("parents").Key(x => x.Id);
var children = model.Objects<Child>().Named("children").Key(x => x.Id);
var relation = model.Relation(parents, children)
    .Where((parent, child) => parent.Id == child.ParentId);
var childCount = model.Derived(parents)
    .Using(relation)
    .Compute((_, matches) => matches.Count);
```

Initial authoritative runtime/database:

```text
Parent A Id=1
Child C Id=10 ParentId=1
```

Operation:

```text
add Parent B with database-generated Id
C.Parent = B
capture policy-aware consistency work BEFORE first SaveChanges
begin transaction
first SaveChanges -> B gets final Id; C.ParentId is fixed up to final Id
PrepareAndPlan
```

The current baseline should fail at `PrepareAndPlan()` because the pre-save temporary FK no longer equals the current final FK.

The final implementation must make this succeed without disabling strict validation.

Positive assertions after DB commit + runtime commit:

- `Parent B.Id` is final/non-temporary;
- child is related to B;
- child is no longer related to A;
- affected derived/invariant state is based on B's final key;
- runtime version advances once;
- no semantic predicate is rerun during `CommitAfterDatabaseCommit()`.

## 145.2 Arbitrary post-capture caller drift must still fail

Use a non-generated ordinary property, e.g. `Child.Quantity`.

Sequence:

```text
capture with Quantity = 2
first SaveChanges only for unrelated/generated-key finalization
caller changes Quantity to 3 after capture
PrepareAndPlan
```

Expected:

```text
InvalidOperationException from strict new-value validation
runtime unchanged
```

This test is mandatory. Any implementation that makes both EF fixup and arbitrary drift pass is wrong.

## 145.3 Generated semantic member that is not the object-set key

Model an entity with:

```text
BusinessId Guid/string = stable application consistency key
DatabaseSequence int = ValueGeneratedOnAdd / database default/generated
```

Use `BusinessId` as `ObjectSet` key.
Use `DatabaseSequence` in a relation, source-derived value, or enforced invariant.

Capture a manual work item and call `PrepareAndPlan()` **before** first `SaveChanges`.

Expected after the fix:

```text
ConsistencyStoreGeneratedValueNotReadyException
```

(or the exact public name chosen in Task 148).

This proves readiness is based on semantic member usage, not only object-set identity.

Then first-save inside a transaction, call `PrepareAndPlan()` again using a fresh work item as required by the state contract, and prove planning uses the final database-generated value.

## 145.4 Generated member unused by consistency semantics does not block

Add a `ValueGeneratedOnAdd` property not referenced by:

- set key;
- relation;
- derived expression;
- invariant;
- projected selector.

Stable-key `PrepareAndPlan()` may proceed before SQL.

This prevents the implementation from pessimistically blocking every EF generated property.

## 145.5 Same CLR type / two object sets contamination proof

Create two object sets over the same CLR type and map them with selectors.

Use `Line.SomeProperty` as a dependency only in Set A.
Try to materialize `SomeProperty` or another relevant member for Set B according to the exact sink-only test scenario.

The result must depend on Set B's own model usage, not global `PropertyInfo` occurrence.

At baseline this should expose the current global member-usage behavior where applicable.

### Task 145 gate

Do not change production behavior until the generated-FK retarget test and at least one set-scoping test demonstrate the baseline gap.

---

# Task 146 — Make model member usage exact and object-set scoped

## Goal

Fix internal metadata before reusing it for generated-value readiness.

Current shape:

```csharp
internal ModelMemberUsageKind GetMemberUsage(
    IObjectSetDefinition set,
    MemberInfo member)
```

Keep the API internal. Correct its semantics.

## 146.1 Define root-set-aware usage rules

### ObjectSetKey

Set only when:

```text
ReferenceEquals(requested set, key owner set)
and requested member is a key member
```

### RelationDependency

For each relation dependency path:

```text
RootParameterIndex == 0 -> root set = relation.LeftSet
RootParameterIndex == 1 -> root set = relation.RightSet
```

Count the member only when the path is rooted in the requested set.

Do not mark Set B because the same CLR `PropertyInfo` appears in a relation rooted in Set A.

### DerivedDependency

Use dependency role to resolve root set:

```text
DerivedSource -> definition.SourceSet
RelationItem  -> the exact relation input RightSet
```

Only count the dependency when its resolved root set is the requested set.

If a definition shape has no unambiguous relation-item root, fail during model compilation/validation rather than guessing here.

### InvariantDependency

`InvariantSource` dependencies are rooted in `invariant.SourceSet`.

### ProjectedSelector

Projected selector member usage is rooted in the projected consumer definition's source set.

## 146.2 Centralize root-set resolution

Do not duplicate role-to-set rules in three unrelated places.

Add a small internal helper/model-inspection utility that can answer:

```csharp
IObjectSetDefinition? ResolveDependencyRootSet(...)
```

or compile member-usage metadata once when the model/runtime is built.

A compiled lookup is preferred if it makes later generated-value readiness mechanical:

```text
(set definition, MemberInfo) -> ModelMemberUsageKind
```

Do not key by CLR `Type` alone.

## 146.3 Preserve sink-only materialization checks

Existing materialization validation must continue rejecting a target when that exact `(set, member)` feeds the consistency graph.

Add regression tests for:

- actual same-set dependency -> rejected;
- same CLR member used only by different object set -> not rejected;
- projected selector scoped correctly;
- relation left/right same CLR type but distinct sets;
- two selectors mapping same CLR entity type to different object sets.

No public API required for Task 146.

---

# Task 147 — Introduce an immutable pre-save EF mutation evidence snapshot

## Goal

The policy-aware manual workflow needs richer evidence than an already-materialized Core `MutationSet`.

Do not change the public low-level `ChangeTrackerAdapter.CaptureUnitOfWork` behavior in this task.

Add an **internal** snapshot used by the high-level EF persistence path.

Suggested concepts:

```csharp
internal sealed class CapturedEfMutationSnapshot
{
    public bool HasChanges { get; }
    public IReadOnlyList<CapturedEfMutation> Mutations { get; }

    public ConsistencyUnitOfWork FinalizeForPlanning(DbContext context);
}
```

Exact names may vary.

## 147.1 Snapshot requirements

At capture time preserve:

- mapped object-set resolution;
- entity reference;
- entity state;
- lifecycle add/remove evidence;
- scalar property member;
- original scalar value;
- current scalar value at capture;
- EF `IProperty` metadata where applicable;
- whether the captured current value is temporary;
- FK metadata for FK scalar changes;
- tracked principal/dependent identity needed to prove generated-key propagation;
- reference navigation old/current object evidence;
- collection reset evidence;
- enough information to reconstruct the exact Core mutation order used today.

Do not retain only strings/property names when EF metadata/reference identity is available.

## 147.2 Finalization rule

`FinalizeForPlanning(context)` builds the Core `MutationSet` immediately before `runtime.Prepare(... StrictNewValue)`.

For ordinary caller-owned property changes:

```text
OldValue = captured original
NewValue = captured current
```

For a proven EF-generated FK fixup:

```text
OldValue = captured original
NewValue = final current FK value
```

Only the new value may be finalized.

## 147.3 Proof required for FK fixup

A captured FK new value may be replaced by the current final value only if all are true:

1. the captured property is an EF FK property;
2. capture recorded that its principal key source was temporary/store-generated;
3. capture retained the exact tracked principal object/reference involved in the relationship;
4. after first save, the principal generated key is final/non-temporary;
5. current dependent FK equals that principal's current final key value for the corresponding key component;
6. the relationship/navigation still points to the same intended principal, or equivalent unambiguous relationship evidence proves the same target;
7. the dependent instance is still the same tracked instance expected by the snapshot.

If any check fails, do not rewrite the captured new value. Let strict validation reject drift or throw an adapter-specific ambiguity exception.

## 147.4 Composite FKs

Handle composite FK components as one relationship decision.

Do not finalize one component while another is ambiguous.

If full composite proof is too invasive for this wave, fail closed with a dedicated `NotSupportedException`/documented adapter exception and add a test. Do not silently partially normalize.

## 147.5 Preserve navigation evidence

Do not recapture old reference values after SaveChanges.

The whole reason the snapshot exists is to preserve pre-save relationship evidence.

Existing owned optional-reference, retarget, collection add/remove/reset, and delete tests must stay green.

## 147.6 Snapshot immutability

After capture, adding new mappings, changing selectors, or mutating `ConsistencyScope` must not reinterpret already captured mutation evidence.

The existing immutable persistence-policy test remains required.

---

# Task 148 — Generalize store-generated readiness to semantic generated values

## Goal

Prevent authoritative planning before every store-generated EF value that can affect selected consistency semantics is final.

## 148.1 Replace key-only terminology internally

Current helper name:

```text
ConsistencyGeneratedKeyGuard
```

Generalize to something like:

```text
ConsistencyGeneratedValueGuard
```

Public existing exception for convenience key cases may remain for compatibility if desired, but new behavior must have accurate terminology.

## 148.2 Candidate selection

For each tracked mapped entry/property with:

```text
ValueGenerated != Never
```

resolve the mapped `ObjectSet` and exact CLR `MemberInfo`.

Ask the corrected set-scoped model usage metadata from Task 146.

The property is semantically relevant when usage includes any of:

```text
ObjectSetKey
RelationDependency
DerivedDependency
InvariantDependency
ProjectedSelector
```

Ignore generated properties with `ModelMemberUsageKind.None`.

## 148.3 Ready/not-ready semantics

A relevant generated value is not ready when EF still considers it temporary or when provider metadata/value state proves generation is pending.

Do not use `Equals(default(T))` as the sole criterion for every provider/type.

Prefer EF state first:

```text
PropertyEntry.IsTemporary
ValueGenerated metadata
entity state
before/after save value-generation behavior
```

For value types where EF can legitimately generate a default value, default equality must not be treated as universal proof of pending generation.

If a provider-independent exact readiness test is impossible for some metadata shape, fail conservatively only for Added entries whose relevant property is configured for store generation and not demonstrably final.

Document the rule.

## 148.4 Public exception

Add a precise public exception for the generalized manual-path case, recommended:

```csharp
public sealed class ConsistencyStoreGeneratedValueNotReadyException : Exception
{
    public Type EntityType { get; }
    public string PropertyName { get; }
}
```

Constructor can remain internal.

Message:

```text
Store-generated consistency input 'Entity.Property' is not final yet. Save inside the current database transaction to obtain generated values/fixup before PrepareAndPlan().
```

Do not call every generated input a "key".

Decide compatibility for `ConsistencyStoreGeneratedKeyNotReadyException` deliberately:

- if no package has been published and repository policy permits cleanup, replace it and update shipped API baseline;
- otherwise derive/retain it only if the repository's API policy supports that without awkward inheritance.

Do not create aliases automatically. Check release history first.

## 148.5 Convenience save behavior

Convenience save must still reject when an Added entity has a semantically relevant generated value that is not final and planning before SQL would be unsafe.

The message should direct users to `CaptureConsistencyUnitOfWork(...)`.

A client-generated `ValueGeneratedOnAdd` value that EF marks final/non-temporary may remain eligible for one-save convenience behavior.

## 148.6 Tests

At minimum:

- generated ObjectSet key -> blocked before first save;
- stable ObjectSet key + generated relation member -> blocked;
- stable key + generated derived dependency -> blocked;
- stable key + generated invariant source dependency -> blocked;
- unused generated property -> not blocked;
- client-final generated value -> behavior documented and tested if practical;
- two object sets same CLR type -> only the exact set's generated semantic usage blocks.

---

# Task 149 — Wire finalized mutation evidence into policy-aware manual persistence

## Goal

Make the public manual path support generated key/value propagation while preserving strict caller-drift rejection.

## 149.1 Capture flow

Change high-level:

```csharp
context.CaptureConsistencyUnitOfWork(runtime, mappings, options)
```

to capture:

1. mapping/model ownership;
2. immutable persistence-policy snapshot;
3. authoritative scope precondition;
4. generated semantic-value candidates;
5. immutable `CapturedEfMutationSnapshot`;
6. the context/runtime references needed by the state machine.

Do not immediately collapse the high-level workflow to a low-level `ConsistencyUnitOfWork` if doing so freezes temporary FK values that EF must later finalize.

Low-level public `ChangeTrackerAdapter.CaptureUnitOfWork` remains unchanged.

## 149.2 PrepareAndPlan flow

Required ordering:

```text
require Captured state
-> verify relevant store-generated values are final
-> finalize captured EF mutation evidence using only proven generated/fixup transitions
-> build low-level ConsistencyUnitOfWork
-> Prepare(runtime) with StrictNewValue
-> PlanDetailed with captured persistence policy
-> filter enforced violations
-> apply selected materializations
-> DetectChanges
-> retain exact low-level unit/plan
-> state = Planned
```

If any step fails:

```text
state = Faulted
runtime unchanged
```

Adapter-owned mirror rollback semantics remain unchanged.

## 149.3 Stable-key no-first-save path

A work item with no pending relevant generated values must still support:

```text
capture
-> PrepareAndPlan
-> one database SaveChanges
-> database commit
-> CommitAfterDatabaseCommit
-> Dispatch
```

Do not force every manual path into two saves.

## 149.4 Generated-value two-phase path

Supported:

```text
capture before first save
-> begin transaction
-> first SaveChanges to finalize relevant generated values and EF fixup
-> PrepareAndPlan using finalized mutation evidence
-> persist mirrors/outbox/other durable policy work
-> second SaveChanges if needed
-> commit database transaction
-> CommitAfterDatabaseCommit
-> Dispatch
```

## 149.5 No hidden post-capture domain drift

Add a test where generated fixup happens correctly but a second unrelated caller mutation also happens before planning.

The generated fixup is accepted; the unrelated drift is rejected.

This mixed test is mandatory because it proves normalization is selective.

## 149.6 First-save failure

If the first `SaveChanges` fails before planning:

- runtime remains unchanged;
- work item remains in `Captured` unless the caller invokes `PrepareAndPlan` and fails;
- documentation recommends abandoning/reloading the context for uncertain provider state;
- no adapter code pretends to roll back caller objects.

Do not add automatic retry.

---

# Task 150 — Complete integration proofs and repair stale safety documentation

## Goal

Make the public contract match the implementation exactly.

## 150.1 SQLite integration matrix

Add/retain decisive real SQLite cases:

1. existing dependent retargeted to newly inserted generated-key principal succeeds;
2. strict unrelated post-capture drift still fails;
3. generated semantic non-key member requires first save;
4. unused generated property does not require first save;
5. generated principal + added dependent still succeeds;
6. generated principal + existing dependent + materialized derived mirror persists correct final value;
7. generated principal + existing dependent + enforced invariant evaluates final relationship, not temporary key state;
8. rollback after invariant violation leaves DB/runtime unchanged;
9. post-DB runtime install failure keeps DB durable and reports synchronization exception;
10. dispatch retry remains resumable.

## 150.2 Convenience/interceptor parity

For any generated-value case that must be rejected in one-save mode, test both:

```text
SaveChangesConsistently
ConsistencySaveChangesInterceptor
```

with equivalent exception semantics.

## 150.3 Documentation corrections already visible at baseline

Fix current stale statements.

### `docs/architecture.md`

This sentence is no longer universally correct:

```text
A source-only derived value requires no complete object set.
```

Replace with the precise rule:

```text
A direct source-local computation that reads only scalar/value members of the known source needs no complete-set proof. A computation or invariant that traverses reverse-indexed navigation paths requires complete coverage of its consumer/root set.
```

Mention `NavigationConsumerCoverage` explicitly.

### `docs/ef-core-consistency.md`

Add an authoritative-scope navigation example:

```csharp
var price = model.Derived(lines).Compute(line => line.Product.Price);
```

Explain why a product-price change requires complete `lines` coverage even though the terminal changed object is Product.

### root `README.md`

The later `## EF Core integration` section still recommends advanced workflows by explicitly capturing the low-level `ConsistencyUnitOfWork`.

Replace that guidance with the policy-aware:

```csharp
CaptureConsistencyUnitOfWork(...)
```

and clearly label `ChangeTrackerAdapter.CaptureUnitOfWork` as low-level/policy-agnostic.

### exception wording

`ConsistencyUnsupportedTransactionException` currently says:

```text
use the manual ConsistencyUnitOfWork workflow
```

Change it to direct users to the public policy-aware `CaptureConsistencyUnitOfWork(...)` workflow.

## 150.4 XML docs

Document generated-value finalization precisely on:

- `CaptureConsistencyUnitOfWork`;
- `ConsistencyPersistenceUnitOfWork.PrepareAndPlan`;
- generalized generated-value exceptions.

State that capture preserves pre-save evidence and accepts only proven EF-generated fixup transitions before planning.

Do not claim arbitrary changes between capture and plan are accepted.

---

# Task 151 — Final release-candidate proof and roadmap closeout

## Goal

Close this wave only with local + remote proof.

## 151.1 Required local commands

Run repository-standard commands, including at least:

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release --no-build -f net8.0
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release --no-build -f net10.0
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release --no-build
dotnet format Raffinert.Consistency.sln --verify-no-changes --no-restore
```

Also run the repository's package/API verification and both samples using the existing release-candidate script/workflow contract.

## 151.2 Public API baselines

If a new public generated-value exception is added, update `PublicAPI.Shipped.txt` according to current pre-publication repository policy.

Keep `PublicAPI.Unshipped.txt` at the repository's expected empty/header state.

No accidental public helpers for internal EF snapshot/fixup machinery.

## 151.3 Required remote proof

Push implementation, wait for CI success, then run/verify the existing release-candidate workflow.

Record in `docs/release-candidate-verification.md`:

- implementation head SHA;
- CI run URL + success;
- RC run URL + success;
- artifact name/id;
- core net8 test count;
- core net10 test count;
- EF/SQLite test count;
- package/API consumer verification;
- explicit statement that existing-dependent generated-key fixup and semantic non-key generation are covered.

## 151.4 Roadmap closeout

Only after all gates pass:

- mark this plan `COMPLETED`;
- add implementation head and CI/RC proof at the top;
- update `docs/roadmaps/README.md` from Active to Completed;
- do not publish NuGet packages;
- do not create a tag/release.

---

# 5. Required failure matrix

Before closeout the following table must be represented by tests and/or explicit documentation:

| Case | Before DB durability | Runtime | Expected behavior |
|---|---|---|---|
| Missing scope | No SQL from consistency workflow | Unchanged | `IncompleteConsistencyScopeException` |
| Relevant generated value still temporary | No planning | Unchanged | generated-value-not-ready exception |
| EF temporary FK becomes proven final FK | Finalizable | Unchanged until commit | accepted and strict validation passes |
| Arbitrary property drift after capture | No authoritative plan | Unchanged | strict validation exception |
| Ambiguous generated FK fixup | No authoritative plan | Unchanged | fail closed, never guess |
| Enforced invariant violated after final fixup | transaction rollbackable | Unchanged | `ConsistencyInvariantViolationException` |
| Mirror setter fails | transaction rollbackable | Unchanged | restore adapter-owned mirror writes |
| Database save/commit fails | not durable | Unchanged | caller rollback/recovery |
| DB durable, runtime install stale/fails | Durable | Pre-commit runtime | `ConsistencyRuntimeSynchronizationException` |
| Dispatch fails | Durable | Committed | retry `Dispatch()` only |

---

# 6. Anti-shortcut checklist

The implementation is **wrong** if any of these appear:

```text
ChangeValidationMode.Default used to avoid temporary-FK mismatch
StrictNewValue disabled for manual generated-key workflow
all PropertyChange.NewValue values replaced from current CLR state after first SaveChanges
all ValueGenerated properties force two-save workflow regardless of semantic usage
member usage looked up only by CLR Type or MemberInfo without ObjectSet identity
old FK/reference evidence re-read after SaveChanges
Map(set) treated as scope completeness
DbContext.ChangeTracker treated as authoritative data coverage
a hidden database query used to prove scope/fixup
CommitAfterDatabaseCommit called automatically before caller transaction commit
```

The intended implementation preserves this invariant:

> The adapter may correct captured **new** mutation evidence only for a transition it can prove was produced by EF store-generated-value propagation. Every other post-capture mutation remains subject to strict drift rejection.

---

# 7. Expected final architecture

After Tasks 145–151 the EF persistence boundary should be:

```text
caller mutates domain graph
        |
        v
CaptureConsistencyUnitOfWork
  - detect/capture old relationship evidence
  - snapshot selected persistence policy
  - validate authoritative scope
  - snapshot semantic generated-value candidates
  - snapshot immutable pre-save EF mutation evidence
        |
        +---------------- stable/final values ------------------+
        |                                                       |
        v                                                       |
PrepareAndPlan                                                 |
        ^                                                       |
        |                                                       |
        +-- generated values pending --> caller first SaveChanges
                                      -> EF generates final values
                                      -> EF performs relationship fixup
                                      -> finalize only proven generated/fixup NewValues
                                      -> strict Core Prepare
                                      -> exact PlanDetailed

plan valid
  -> persist mirrors/outbox/other DB work
  -> caller commits DB transaction
  -> CommitAfterDatabaseCommit installs exact runtime patch
  -> Dispatch callbacks / resumable policy work
```

No caller must manually remember to patch temporary FKs, and no safety check is weakened to make generated keys work.
