# Codex implementation plan — external consumer discovery hardening completion

Status: **ACTIVE COMPLETION PLAN**

Baseline commit: `730de44756aeced16af8b7bb011ab9e2ed4e0374`

Tasks: **202–210**

This plan supersedes the active status of Tasks 192–201.

Tasks 192–201 were only **partially implemented** by `730de44756aeced16af8b7bb011ab9e2ed4e0374`. The commit contains useful hardening and its exact GitHub Actions CI run is green, but it does not satisfy the previous completion checklist and still contains correctness gaps.

Do not mark external consumer discovery release-ready until Tasks 202–210 are complete.

---

# What `730de44` actually accomplished

Preserve these improvements unless a task below explicitly requires a narrow refactor:

- active persistence definitions now distinguish `Validate` from `RecalculateAndValidate`;
- materialization-only discovery is no longer intended to participate in `Validate` saves;
- external consumer analysis recursively visits upstream derived definitions;
- unsupported navigation root sets are tracked conservatively;
- resolver registrations are validated against EF reference-navigation metadata and exact target CLR type;
- the incomplete-scope error describes both `ConsistencyScope.Complete(set)` and host-owned `DiscoverConsumers(...)`;
- convenience saves capture a rollback handle for framework-written materialized mirrors;
- sync/async convenience saves restore those mirrors when `SaveChanges` throws;
- the dogfood scenario now changes only the shared source value and expects one source-navigation resolver call;
- a dedicated `ExternalConsumerDiscoveryTests.cs` file exists;
- GitHub Actions CI for `730de44756aeced16af8b7bb011ab9e2ed4e0374` is green.

Do not delete or broadly redesign these parts.

---

# Why another completion wave is required

The previous roadmap required semantic separation, fail-closed evaluation closure, a broad EF/ChangeTracker proof matrix, failure/retry proof, manual-UoW proof, and release closeout.

`730de44` added only two dedicated discovery tests:

```text
Source_change_discovers_unloaded_consumers_and_materializes_all_rates_async
Complete_scope_skips_external_resolver
```

That is not enough proof for a feature that changes:

- authoritative scope safety;
- operation-scoped object admission;
- relation membership state;
- causal impact reporting;
- invariant policy behavior;
- EF materialization;
- ChangeTracker overlay semantics;
- persistence failure/retry behavior;
- manual binding-plan behavior.

More importantly, two correctness issues remain in production code.

---

# Blocking finding A — `CoverageAdmission` is still a semantic add

This is the highest-priority defect.

At baseline `730de44`:

```csharp
internal sealed class CoverageAdmission : RuntimeMutation, IAddedMutation
```

and mutation validation still treats every `IAddedMutation` as lifecycle work:

```csharp
var lifecycle = mutations
    .Where(mutation => mutation is IAddedMutation or ObjectRemoved)
    .ToArray();
```

and runtime execution still does:

```csharp
foreach (var mutation in lifecycleMutations)
{
    if (mutation is IAddedMutation added)
        CommitAdd(added, relationDeltas);
    else
        CommitRemove((ObjectRemoved)mutation, relationDeltas);
}
```

`CommitAdd(...)` performs all of these:

```text
ObjectSetRuntime.Add
source lifecycle notification
navigation index add
projection index add
relation AddRight / AddLeft
merge returned relation deltas into semantic relationDeltas
```

A discovered row already existed in the database before the operation. Learning that row is **structural baseline acquisition**, not a domain insertion.

Therefore the current implementation can incorrectly make old persisted relation membership look newly added when Raffinert first discovers a consumer. That can leak into:

- `RelationMutationImpact.AddedPairs`;
- relation-dependent derived propagation;
- invariant reactions;
- repair/immediate-evaluation policy work;
- relation-added diagnostics;
- causal explanations.

The fact that `CoverageAdmission` is excluded from `MutationOriginKind.ObjectAdded` provenance does **not** fix the semantic relation-delta problem.

Tasks below must structurally separate coverage admission from domain lifecycle mutation.

---

# Blocking finding B — evaluation closure is still only half-correct

The request navigation itself now checks EF load state when its CLR value is null.

However the general closure validation still effectively does:

```csharp
foreach (var descriptor in activeDescriptorsForRoot)
{
    var target = MemberReader.Read(descriptor.Navigation, root);
    if (target is null)
        continue;

    if (context.Entry(target).State == EntityState.Detached)
        throw ...;
}
```

That means this can still pass incorrectly:

```text
active derived:
    Association.Source.UnitValue / Association.Target.UnitValue

resolver request:
    Association.Source

host query:
    Includes Source
    DOES NOT Include Target

materialized Association:
    Source != null
    Target == null because it is not loaded
    Target navigation IsLoaded == false
```

The request-navigation check proves only `Source` closure. The later generic closure loop sees `Target == null` and currently skips it.

That is not safe. `null` must mean one of two explicitly distinguished states:

```text
loaded optional null       -> valid closure
unloaded unknown reference -> incomplete closure, fail before SQL
```

No automatic `Load()` is allowed.

---

# Additional incomplete proof from Tasks 192–201

Even where the production change looks directionally correct, there is no dedicated proof yet for most contracts.

Missing proof includes at least:

```text
sync convenience discovery
multi-target batching
safe resolver superset filtering
Validate-only policy applicability
enforced invariant discovery
non-enforced invariant inactivity
unsupported sibling navigation on same set
upstream navigation requirement preservation
same CLR type / different ObjectSet isolation
resolver registration failure matrix
loaded-null vs unloaded-null
retarget-away / retarget-in overlay
Added / Deleted overlay
unknown Modified / Deleted fail-closed
AsNoTracking / detached resolver return
duplicate runtime key
resolver exception atomicity
SQL failure mirror rollback
same-DbContext retry
async cancellation
manual policy-aware UoW discovery
subsequent open-world operation re-discovery
false relation AddedPairs prevention
false policy action prevention
exact binding-plan installation/version proof
```

Green CI is evidence that the current suite passes. It is not evidence that these untested contracts hold.

---

# Frozen boundaries for Tasks 202–210

Do not expand the product while finishing it.

Still out of scope:

- multi-hop external consumer discovery;
- collection-navigation external discovery;
- relation-predicate consumer discovery;
- projected-consumer discovery;
- generated SQL/query resolvers;
- automatic `Include(...)` generation;
- automatic `Reference.Load()` / lazy-loading integration;
- partitioned/key-scoped `ConsistencyScope`;
- non-EF external resolvers;
- CDC/background reconciliation;
- cross-process locking/serialization;
- automatic detection of resolver under-fetch;
- reconstruction of previously unknown existing Modified/Deleted roots;
- runtime identity reconciliation for different CLR instances with the same business key;
- public discovery request/proof/admission types.

Keep the public v1 API shape unchanged:

```csharp
public ConsistencyEfCoreMappings DiscoverConsumers<TRoot, TTarget>(
    ObjectSet<TRoot> roots,
    Expression<Func<TRoot, TTarget?>> navigation,
    Func<DbContext, IReadOnlyCollection<TTarget>, IQueryable<TRoot>> query)
    where TRoot : class
    where TTarget : class;
```

Do not add `Hydrate`, `Rehydrate`, `LoadMissingDependencies`, `ResolveGraph`, public `CoverageAdmission`, public rollback handles, or repository abstractions.

---

# Mandatory implementation order

A weak agent must follow Tasks 202–210 in order.

For every behavior-changing task:

```text
1. add a test that fails for the baseline defect;
2. run that focused test and confirm RED for the intended reason;
3. make the smallest production change that fixes the contract;
4. run focused tests until GREEN;
5. run the whole EF test project;
6. commit that task separately;
7. do not bundle unrelated cleanup/refactoring.
```

Do not skip the RED proof by immediately editing production code.

---

# Task 202 — repair and strengthen the dedicated regression harness

Primary file:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/ExternalConsumerDiscoveryTests.cs
```

The existing file has only two tests. Expand the fixture before changing semantic production code so later tasks have a reliable proof surface.

## Fixture requirements

Keep a small SQLite in-memory domain:

```text
DiscoverySource
    Id
    UnitValue

DiscoveryTarget
    Id
    UnitValue

DiscoveryAssociation
    Id
    SourceId / Source
    TargetId / Target
    UnitRate
```

Add reusable helpers for:

- creating a fresh tracked operation context over the same open SQLite connection;
- creating a completely separate verification context over the same connection;
- independent `AsNoTracking()` persisted-value verification;
- resolver call counters **per navigation**;
- recording target IDs/references passed to each resolver call;
- configuring resolver variants:
  - normal authoritative query;
  - safe superset;
  - missing opposite `Include`;
  - `AsNoTracking`;
  - throwing resolver;
- configuring save behavior;
- configuring enforced invariants when needed by later tasks.

Do not create one giant helper with boolean soup. Prefer small named fixture methods/options whose test intent is visible.

## Replace the weak happy-path proof

`Source_change_discovers_unloaded_consumers_and_materializes_all_rates_async` must explicitly prove:

```text
before save:
    DB contains A/B/C
    ChangeTracker contains only A as Association
    runtime contains only A

mutation:
    only shared Source.UnitValue changes

save:
    exactly one Source resolver call
    resolver target batch contains exactly Source1 once

successful result:
    ChangeTracker contains A/B/C
    runtime contains A/B/C
    runtime Version incremented exactly once
    tracked UnitRate values correct
    runtime derived values correct
    separate AsNoTracking verification context sees correct persisted UnitRate values
```

If internal test access already exists, use it for runtime-registration assertions. Do not expose new public API only for tests.

## Add characterization tests that should already pass

Add at least:

```text
Source_change_discovers_unloaded_consumers_and_materializes_all_rates_sync
Multiple_changed_sources_are_batched_into_one_source_resolver_call
Safe_resolver_superset_is_filtered_by_current_navigation
Complete_scope_skips_external_resolver_when_runtime_is_actually_complete
```

### Important closed-world test correction

Do **not** assert `ConsistencyScope.Complete(associations)` when runtime contains only one of three persisted associations merely to prove that the resolver is skipped.

`Complete(set)` is a host assertion; such a test would intentionally provide a false assertion and prove only that Raffinert trusts the caller.

For the normal closed-world fast-path test:

```text
load/seed A/B/C into runtime first
assert Complete(associations)
mutate Source
save
assert zero resolver calls
assert correct tracked/runtime/database results
```

A separate test may document that `Complete` is trusted and not DB-verified, but do not use a lie as the canonical fast-path acceptance test.

Commit suggestion:

```text
test: strengthen external discovery regression harness
```

---

# Task 203 — separate structural coverage admission from domain lifecycle mutation

This task fixes the highest-risk correctness bug.

Likely files:

```text
src/Raffinert.Consistency/Change.cs
src/Raffinert.Consistency/Runtime/MutationValidation.cs
src/Raffinert.Consistency/Runtime/MutationCommit.cs
prepared-mutation / planning types touched by validated mutation shape
relevant Core tests
ExternalConsumerDiscoveryTests.cs
```

Names below are conceptual unless an existing internal naming pattern provides a better exact name. Semantics are frozen.

## 203.1 `CoverageAdmission` must stop implementing `IAddedMutation`

Change the model so:

```text
ObjectAdded
    = domain lifecycle mutation

ObjectRemoved
    = domain lifecycle mutation

CoverageAdmission
    = existing persisted object becoming structurally known for this plan
```

`CoverageAdmission` must remain internal.

Do not make `IAddedMutation` mean both concepts.

## 203.2 Split validated mutation state explicitly

`ValidateMutations(...)` must produce at least these distinct categories:

```text
DomainLifecycleMutations
    ObjectAdded
    ObjectRemoved

CoverageAdmissions
    CoverageAdmission

PropertyChanges
Collection-derived changes
Provenance
```

Do not encode coverage admissions back into domain lifecycle after validation.

## 203.3 Object-set simulation still validates coverage admissions

A coverage root will become registered in the planned final runtime, so duplicate/final-membership validation is still required.

For object-set simulation:

```text
simulate ObjectAdded as add
simulate CoverageAdmission as structural add
simulate ObjectRemoved as remove
```

Reject:

- null key;
- duplicate key already registered;
- two admissions with equal key but different instance;
- impossible add/remove final membership combinations using existing validation rules.

But simulation semantics must not imply `MutationOriginKind.ObjectAdded`.

## 203.4 Baseline-admit coverage before semantic propagation

Add a narrow internal execution path conceptually equivalent to:

```csharp
CommitCoverageAdmission(CoverageAdmission admission)
```

It must establish the runtime baseline needed for the plan:

```text
ObjectSetRuntime registration
source lifecycle state initialization required by derived/invariant states
navigation indexes
projection indexes
relation runtime membership/indexes
```

For relation baseline establishment:

```text
call existing AddLeft/AddRight or a narrow baseline helper
BUT discard baseline RelationDelta from semantic relationDeltas
```

If existing relation APIs require returning deltas, discard those deltas here. Do not add a public relation API.

## 203.5 Then process real domain lifecycle mutations normally

After structural coverage is present, process:

```text
ObjectAdded
ObjectRemoved
PropertyChange / collection effects
```

with existing semantic behavior.

The changed nested target member must then route through the newly established navigation indexes and mark discovered roots affected.

## 203.6 Patch capture must include structural admissions

Do not accidentally lose reversibility.

Rollback/forward patch capture must treat coverage admissions as touching:

```text
object set entries
navigation index state
projection state
relation baseline state
derived/invariant source runtime state
```

but not as semantic relation additions.

Before SQL:

```text
live runtime must remain at base state
```

After successful DB save + plan install:

```text
discovered roots are registered
baseline relation/index state exists
runtime version increases once
```

After SQL failure:

```text
discovered roots are not registered in live runtime
```

## 203.7 Preserve affected derived/invariant evaluation

Current code may obtain some behavior accidentally because coverage implements `IAddedMutation`.

After the split:

- do **not** auto-evaluate coverage admissions as new objects merely because they were admitted;
- do ensure discovered roots selected by the actual target member mutation appear in derived/invariant propagation and planned evaluations;
- a coverage-only plan without a semantic cause must not manufacture dirty/invalid semantic effects.

## Required tests

Add tests that would fail on `730de44`:

### `Coverage_admission_is_not_reported_as_relation_addition`

Construct a model where a discovered Association participates in a materialized/exact relation.

Before operation:

```text
row and relation membership already exist in DB
row absent from runtime
```

After target member mutation + successful discovery/save:

```text
runtime contains discovered root
relation runtime baseline is correct
RelationMutationImpact.AddedPairs does NOT contain baseline pair merely because row was discovered
Diagnostics.RelationPairsAdded does not count the baseline pair as a business change
```

### `Coverage_admission_has_no_ObjectAdded_origin`

Use causal detail and assert no `MutationOriginKind.ObjectAdded` for the coverage root.

### `Coverage_admission_alone_does_not_emit_policy_work`

Use an invariant/reaction shape where a real add could produce policy work. Prove baseline admission alone is not the cause of repair/immediate work.

### `Target_member_change_still_affects_discovered_root_after_structural_split`

The actual Source/PO price mutation must still produce correct derived/invariant impact and materialization for discovered roots.

### `Coverage_is_installed_only_after_binding_plan_commit`

If practical through manual UoW/internal seams:

```text
PrepareAndPlan -> live runtime does not contain coverage root
DB durability step
CommitAfterDatabaseCommit -> runtime now contains root
Version +1 exactly once
```

Commit suggestion:

```text
fix: separate coverage admission from domain addition
```

Do not continue to Task 204 until these tests are green.

---

# Task 204 — finish EF evaluation-closure proof for every active navigation

Primary files:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ExternalConsumerDiscovery.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreMappings.cs
ExternalConsumerDiscoveryTests.cs
```

## 204.1 Create one closure-check implementation

Do not have one rule for the resolver-request navigation and another weaker rule for other active evaluation navigations.

For every active direct-navigation descriptor required for a discovered root:

1. find the exact registered/validated resolver metadata for `(RootSet, Navigation)`;
2. use its cached exact EF `INavigation`;
3. obtain the exact `NavigationEntry` from the tracked root;
4. inspect current CLR/navigation value and EF load state.

Apply exactly:

```text
CurrentValue != null
    -> referenced target must be tracked by the same DbContext
    -> detached target => fail before planning/SQL

CurrentValue == null && NavigationEntry.IsLoaded == true
    -> legitimate loaded optional-null reference
    -> closure is satisfied

CurrentValue == null && NavigationEntry.IsLoaded == false
    -> unresolved database state
    -> fail before planning/SQL
```

Do not infer `IsLoaded` from CLR null.

Do not call `Load()` automatically.

Do not generate Include paths.

## 204.2 Reuse the same helper for request membership and evaluation closure

The request navigation membership filter still needs current-target comparison, but its loaded/null validation must use the same exact helper rather than a second special-case implementation.

Avoid divergent null semantics.

## Required tests

### `Source_resolver_missing_Target_include_fails_evaluation_closure`

Use active derived:

```csharp
association => association.Source.UnitValue / association.Target.UnitValue
```

Resolver request is `Association.Source`.

Make Source resolver query:

```text
Include(Source)
DO NOT Include(Target)
```

Use a computation that is null-safe if necessary so the test proves the explicit closure guard, not an accidental `NullReferenceException`.

Assert:

```text
clear closure exception before SQL
runtime unchanged
persisted mirror unchanged
```

### `Loaded_optional_null_reference_is_accepted`

Use nullable/optional reference and a null-safe derived expression.

Explicitly establish:

```text
NavigationEntry.IsLoaded == true
CurrentValue == null
```

Then closure must accept it.

### `Non_null_detached_evaluation_target_fails`

A non-null navigation whose target is detached from this context must fail.

### `All_required_evaluation_references_loaded_passes`

Normal Include closure remains green.

Commit suggestion:

```text
fix: enforce complete EF evaluation closure for discovery
```

---

# Task 205 — prove active-policy and exact scope-substitution matrix

The `730de44` implementation is directionally better here. Do not rewrite it unless a required test exposes a defect.

Add dedicated tests for every policy contract.

## Active policy tests

### Materialization-only + `Validate`

```text
model contains derived
mappings Materialize(derived,...)
resolver registered
SaveBehavior = Validate
matching nested target member changes
```

Assert:

```text
resolver calls = 0
materialized mirror is not recalculated by Raffinert
no NavigationConsumerCoverage gap is demanded solely for that inactive materialization
```

### Same mapping + `RecalculateAndValidate`

Assert matching resolver runs and mirror updates.

### Enforced invariant + `Validate`

An enforced invariant whose source dependency requires external consumer discovery must execute resolver discovery when matching target member changes.

### Non-enforced invariant

Definition existing in model but not `Enforce(...)` must cause zero resolver calls and zero persistence-scope obligation.

### Unmaterialized derived

Derived definition existing in model but absent from `Materialize(...)` must cause zero resolver calls in normal EF persistence.

## Scope substitution tests

### `Supported_direct_navigation_with_resolver_substitutes_only_navigation_coverage`

Supported direct `Root.Source.Value` + exact resolver may remove the NavigationConsumerCoverage gap for that obligation.

### `Supported_and_unsupported_navigation_on_same_set_remains_fail_closed`

Same root set has:

```text
Root.Source.Value               supported + resolver present
Root.A.B.Value                  unsupported v1 reverse shape
```

Without `Complete(rootSet)`:

```text
IncompleteConsistencyScopeException
resolver callback count = 0 if scope validation happens before resolver execution
no SQL
runtime unchanged
```

### `Upstream_navigation_requirement_is_preserved`

A top-level materialized/enforced definition depends on upstream derived state whose source navigation creates the coverage requirement. Missing/unsupported upstream coverage must not disappear.

### `All_supported_navigation_descriptors_require_exact_resolvers`

If two active direct navigations on same set require discovery and only one resolver is registered, fail closed.

### `Same_CLR_type_in_two_ObjectSets_is_isolated`

Resolver for exact set A must not satisfy set B.

### Relation/projected safety

Prove `DiscoverConsumers` does not suppress:

```text
RelationSourceCoverage
RelationTargetCoverage
ProjectedConsumerCoverage
```

Commit suggestion:

```text
test: prove external discovery policy and scope matrix
```

---

# Task 206 — prove ChangeTracker overlay, batching, and resolver misuse

Do not change v1 boundaries just to make these cases succeed.

## Overlay tests

### Retargeted away

```text
DB: X.Source = S1
tracked current: X.Source = S2
request targets: S1
resolver query may return X from persisted S1 membership
```

Effective current consumer set must exclude X for S1.

### Retargeted in

```text
DB: X.Source = S1
tracked current: X.Source = S2
request targets: S2
```

If X is already a valid registered existing runtime participant, ChangeTracker overlay must include it even though DB query for S2 cannot yet see the pending retarget.

### Added consumer

Tracked Added root pointing to requested target remains real domain `ObjectAdded` work.

Assert it is not converted to CoverageAdmission semantics and normal add impacts still work.

### Deleted consumer

Tracked Deleted consumer must be excluded from the effective current consumer set.

### Unknown existing Modified/Deleted root

If an existing Modified or Deleted consumer is not registered in runtime, preserve the v1 fail-closed rule.

Do not reconstruct historical state in this roadmap.

## Resolver misuse tests

Add:

```text
AsNoTracking/detached returned root -> fail before SQL
wrong exact ObjectSet -> fail
same key / different instance collision -> fail
safe superset -> filter, do not fail
missing resolver for required descriptor -> fail before SQL
resolver callback/query throws -> propagate original exception; runtime unchanged
```

If wrong CLR root type cannot be produced through the public generic query type, test the internal collection seam only if useful; do not expose new public API for that artificial case.

## Batching tests

### Same navigation, multiple targets

Three changed Source objects -> exactly one Source resolver invocation with three distinct target references.

### Same navigation, multiple matching target members

If two changed members on the same target object both map through the same reverse navigation, still issue one resolver request for that navigation/target reference.

### Two navigations

Source and Target changes in one operation -> exactly one resolver call per navigation, not one per property/consumer.

### Complete scope

An honestly complete `Complete(set)` operation -> zero resolver calls.

Commit suggestion:

```text
test: prove discovery overlay batching and fail-closed resolver rules
```

---

# Task 207 — finish convenience-save failure rollback and same-context retry

`730de44` added framework-materialization rollback when `context.SaveChanges*()` throws. Keep that design but close the remaining failure window and prove it.

Likely files:

```text
ConsistencyPersistencePolicy.cs
ConsistencySave.cs
ExternalConsumerDiscoveryTests.cs or focused persistence failure test file
```

## 207.1 Restore framework writes on any planning failure after they were applied

Current shape applies mirrors, stores the rollback handle, then calls:

```csharp
context.ChangeTracker.DetectChanges();
```

If an exception occurs **after** framework mirror writes but before `PrepareAndPlan(...)` returns the rollback handle to the caller, framework writes must be restored.

Restructure narrowly so:

```text
before first Raffinert mirror write:
    capture previous value + IsModified

any exception after writes during planning:
    restore Raffinert writes
    rethrow
```

Do not restore arbitrary user domain changes.

## 207.2 Make rollback safe to call once or defensively idempotent

The handle is internal. Prefer simple idempotent/single-use-safe semantics so nested failure paths cannot accidentally restore twice into a wrong state.

Restore in reverse write order.

## 207.3 Prove SQL-failure behavior

Use an existing test interceptor or SQLite trigger/constraint to force SQL failure **after planning/materialization occurred**.

For sync and async convenience APIs assert:

```text
runtime Version unchanged
coverage roots not registered in live runtime
framework-written UnitRate CLR values restored
UnitRate PropertyEntry.IsModified restored to its pre-framework value
original user Source.UnitValue mutation remains present
original user source PropertyEntry remains Modified as appropriate
```

Then remove/disable the injected database failure and retry using the **same DbContext and same runtime**.

Assert:

```text
retry does not fail because discovered roots were left Modified by Raffinert
discovery/planning can run again
save succeeds
runtime installs coverage once
Version increments once from original base version
persisted/tracked/runtime mirrors correct
```

## 207.4 Cancellation

If async cancellation happens after framework writes but before database success, apply the same framework-write restoration rule wherever the library owns the exception boundary.

Do not claim a timing-specific cancellation case is proven if the test only cancels before discovery starts.

Commit suggestion:

```text
fix: complete discovery materialization rollback semantics
```

---

# Task 208 — prove manual UoW and binding-plan atomicity parity

The advanced workflow must be tested explicitly; do not infer it from convenience-save tests.

Required successful flow:

```text
CaptureConsistencyUnitOfWork(...)
    ↓
PrepareAndPlan()
    - performs external discovery
    - evaluates/materializes as defined by policy
    - live runtime remains unchanged
    ↓
caller performs database durability step
    ↓
CommitAfterDatabaseCommit()
    - installs exact prepared forward state
    - discovered roots become runtime members
    - Version increments once
    ↓
Dispatch()
```

Assert before commit:

```text
live runtime does not contain newly discovered B/C
Version unchanged
```

Assert after commit:

```text
runtime contains B/C
Version +1 exactly once
no false relation AddedPairs after Task 203
```

## Manual failure contract

Do **not** invent a broad public `Abort`, rollback transaction manager, or context-rewind API in this wave.

Document the existing advanced boundary clearly:

```text
After a manual UoW has been planned, caller-owned database failure means that planned UoW must not be committed.
Unless an existing explicit recovery primitive already guarantees safe restoration, abandon/recreate the manual operation/context/UoW before retrying.
```

The library cannot observe arbitrary external transaction failure after `PrepareAndPlan()`.

## Open-world proof is per operation

Add a two-save test:

```text
operation 1 discovers current consumers successfully
later a new persisted consumer may exist
operation 2 changes the same target again
```

Without `Complete(set)`, resolver must run again.

Targeted discovery in operation 1 must never be cached as whole-set completeness.

## Async cancellation atomicity

Cancellation during resolver query must leave:

```text
runtime unchanged
Version unchanged
no forward patch installed
```

If resolver materialization has tracked entities before cancellation, that alone must not imply runtime admission.

Commit suggestion:

```text
test: prove discovery binding-plan workflow parity
```

---

# Task 209 — documentation and sample contract cleanup

Keep the improved source-only dogfood scenario from `730de44`.

Update authoritative documentation after behavior/tests are stable.

Files:

```text
README.md
docs/anemic-model-dependency-maintenance-example.md
docs/architecture.md
docs/codex-plan-external-consumer-discovery-hardening-completion.md
docs/roadmaps/README.md at final closeout
```

Document these exact boundaries:

```text
DiscoverConsumers is targeted open-world consumer coverage for one operation.
It does not make an ObjectSet complete.
Resolver completeness remains a host assertion.
Raffinert validates returned/tracked/evaluation-shape safety but cannot detect general under-fetch.
Resolver query/write concurrency and isolation remain host/database responsibilities.
Unsupported multi-hop/collection/projected/relation discovery remains fail-closed and may require Complete(set).
Database mutations invisible to EF/Raffinert still require reconciliation/rebuild/publication.
CoverageAdmission is structural baseline knowledge, not a domain ObjectAdded event.
```

Also document convenience-vs-manual failure behavior accurately after Task 207/208.

Do not claim:

- cross-process consistency;
- under-fetch detection;
- automatic Includes;
- lazy database hydration;
- durable whole-set completeness from targeted discovery.

The sample proves end-to-end result/persistence behavior. Dedicated tests prove semantic isolation, overlay, policy, and failure contracts.

Commit suggestion:

```text
docs: define external discovery completion boundaries
```

---

# Task 210 — release proof and roadmap closeout

Do not close Tasks 202–210 based only on focused tests or a green push CI from an earlier incomplete commit.

## Public API gate

Expected intentional public API remains only the already-added:

```text
ConsistencyEfCoreMappings.DiscoverConsumers<TRoot,TTarget>(...)
```

No public exposure of:

```text
CoverageAdmission
ExternalConsumerDescriptor
ExternalConsumerAnalysis
ExternalConsumerRequest
validated EF navigation wrappers
materialization rollback handles
coverage proof structures
```

Update API baselines only if intentional public API actually changed.

## Required local verification

Run exactly:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj \
  -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj \
  -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Do not record a command as passed if it was skipped.

## Exact remote proof

After the final implementation commit is pushed:

1. capture the exact SHA;
2. inspect the GitHub Actions run whose `head_sha` equals that exact SHA;
3. require completed + success;
4. record that SHA in this roadmap;
5. update `docs/roadmaps/README.md` with the exact proof SHA.

Do not use a previous green commit as proof for later docs/implementation changes.

## Roadmap history

At closeout record:

```text
Tasks 182–191
    first implementation at b57b4ba...

Tasks 192–201
    partially implemented hardening at 730de44...
    CI green but incomplete proof/semantic separation
    superseded by Tasks 202–210

Tasks 202–210
    final hardening completion at <FINAL_SHA>
```

Do not rewrite history to say `730de44` completed Tasks 192–201.

---

# Required final test inventory

Tasks 202–210 are incomplete until dedicated automated tests cover all of the following.

## Core happy path

- async open-world source mutation discovers unloaded consumers;
- sync equivalent;
- tracked values correct;
- runtime values correct;
- independent `AsNoTracking` DB values correct;
- runtime Version increments exactly once.

## Active persistence policy

- Validate ignores materialization-only discovery;
- RecalculateAndValidate activates materialization discovery;
- enforced invariant discovery is active;
- non-enforced invariant is inactive;
- unmaterialized derived definition is inactive.

## Scope safety

- supported direct resolver substitutes eligible navigation coverage;
- unsupported sibling navigation on same set remains fail-closed;
- upstream derived navigation requirement is retained;
- every active direct descriptor requires its exact resolver;
- same CLR type / different ObjectSet isolation;
- relation source/target/projected coverage is never substituted.

## Evaluation closure

- request navigation unloaded-null fails;
- different required evaluation navigation unloaded-null fails;
- loaded legitimate null succeeds for null-safe computation;
- non-null detached target fails;
- complete Include closure succeeds.

## Structural admission semantics

- discovered root is absent from live runtime before plan install;
- discovered root is present after successful binding-plan commit;
- no `ObjectAdded` origin for coverage root;
- no semantic relation `AddedPairs` solely from baseline admission;
- relation-added diagnostics do not count baseline discovery as business addition;
- no policy request solely because an existing persisted row became known;
- actual changed target member still affects discovered root.

## ChangeTracker overlay

- retarget-away excluded;
- retarget-in included when root is already a valid registered participant;
- Added remains real domain add;
- Deleted excluded;
- unknown existing Modified root fails closed;
- unknown existing Deleted root fails closed.

## Resolver misuse/failure

- detached/AsNoTracking root fails;
- wrong exact ObjectSet fails;
- duplicate runtime key fails;
- safe superset filters correctly;
- missing required resolver fails before SQL;
- throwing resolver propagates and leaves runtime unchanged.

## Batching

- multiple targets / same navigation -> one resolver call;
- multiple matching members / same target+navigation -> one call;
- two navigations -> one call per navigation;
- honest `Complete(set)` -> zero calls.

## Persistence failures

- planning failure after framework materialization restores framework sink writes;
- sync SQL failure restores mirror value and `IsModified`;
- async SQL failure restores mirror value and `IsModified`;
- original user mutation remains intact;
- live runtime remains unchanged after failed SQL;
- same-context retry succeeds after failure is removed.

## Workflow parity

- convenience sync;
- convenience async;
- cancellation leaves runtime unchanged;
- manual UoW performs discovery during planning;
- manual plan installs coverage only after caller-declared DB durability;
- later open-world operation re-runs discovery.

---

# Anti-shortcut rules for weak agents

These are hard constraints.

Do **not** “make the tests green” by:

- adding `ConsistencyScope.Complete(rootSet)` to an open-world test;
- asserting `Complete(rootSet)` while runtime is knowingly incomplete in the canonical fast-path test;
- preloading all consumers before the tested mutation;
- manually calling the resolver from the handler/test;
- manually adding discovered rows to runtime before save;
- changing `CoverageAdmission` back into `ObjectAdded` semantics;
- keeping `CoverageAdmission : IAddedMutation` and merely hiding its public impact output;
- filtering fake relation `AddedPairs` only at rendering time while semantic propagation still saw them;
- disabling relation-dependent tests to avoid structural-admission work;
- treating CLR `null` as proof that an EF reference is loaded;
- auto-calling `Reference.Load()`;
- generating `Include(...)` automatically;
- weakening the unknown existing Modified/Deleted fail-closed rule;
- swallowing resolver/query exceptions;
- mutating live runtime before SQL succeeds;
- using CLR type alone as resolver identity;
- suppressing relation/projected scope gaps through `DiscoverConsumers`;
- exposing internal admission/coverage/rollback types publicly;
- replacing tracked+runtime+independent-DB assertions with database-only assertions;
- declaring success because GitHub Actions is green while required tests are absent;
- closing this roadmap without recording the exact final green SHA.

If one required contract is hard to implement, document the blocker. Do not silently weaken the contract.

---

# Final acceptance checklist

Tasks 202–210 are complete only when every checkbox is true:

- [ ] `ExternalConsumerDiscoveryTests` contains the full required matrix, not only happy-path tests;
- [ ] canonical closed-world test uses honestly complete runtime coverage;
- [ ] `CoverageAdmission` no longer implements/uses domain `IAddedMutation` semantics;
- [ ] validation carries coverage admissions separately from domain lifecycle mutations;
- [ ] structural admission establishes object/navigation/projection/relation/source baseline state;
- [ ] structural baseline relation deltas are not semantic `AddedPairs`;
- [ ] no policy work is emitted solely because an old persisted row was discovered;
- [ ] actual target scalar mutation still affects discovered roots;
- [ ] every active evaluation reference distinguishes loaded-null from unloaded-null;
- [ ] no automatic navigation loading was added;
- [ ] Validate-only persistence ignores materialization-only discovery;
- [ ] unsupported/upstream/sibling navigation obligations remain fail-closed;
- [ ] ChangeTracker retarget/add/delete cases are proven;
- [ ] unknown existing Modified/Deleted roots remain fail-closed;
- [ ] resolver misuse/failure cases are proven;
- [ ] batching is proven per exact ObjectSet + navigation;
- [ ] framework mirror writes are restored on planning failure after writes;
- [ ] framework mirror writes are restored on sync/async SQL failure;
- [ ] same-DbContext convenience retry succeeds after failed SQL;
- [ ] live runtime remains unchanged before successful DB durability;
- [ ] successful binding-plan install registers discovered roots and increments Version exactly once;
- [ ] manual UoW discovery/commit boundary is proven and documented;
- [ ] targeted discovery is explicitly per-operation and never promoted to whole-set completeness;
- [ ] docs accurately describe host completeness/isolation/reconciliation responsibilities;
- [ ] no accidental public API expansion;
- [ ] full local verification matrix is green;
- [ ] GitHub Actions for the exact final implementation SHA is completed successfully;
- [ ] roadmap README records Tasks 192–201 as partial/superseded, not completed;
- [ ] roadmap README records Tasks 202–210 completion with exact proof SHA.
