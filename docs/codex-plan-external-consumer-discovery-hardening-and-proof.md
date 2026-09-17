# Codex implementation plan — external consumer discovery hardening and proof

Status: **ACTIVE HARDENING PLAN**

Baseline implementation commit: `b57b4ba98922abbb61c2a86bcdc3b81adf377897`

Tasks: **192–201**

Supersedes the active status of Tasks 182–191. Tasks 182–191 produced the first implementation, but that implementation is not release-ready until this hardening/proof wave is complete.

## Why this follow-up exists

Commit `b57b4ba98922abbb61c2a86bcdc3b81adf377897` implemented the intended high-level shape:

- public `ConsistencyEfCoreMappings.DiscoverConsumers(...)` registration;
- direct-navigation dependency analysis;
- batched resolver calls at the EF save boundary;
- operation-scoped `CoverageAdmission` rather than mutating the live runtime before SQL;
- ChangeTracker overlay filtering;
- sync, async, and manual policy-aware persistence integration;
- an executable SQLite dogfood scenario;
- CI green on the implementation commit.

The direction is correct, but review found correctness gaps and, most importantly, **zero dedicated regression tests were added for the new mechanism**. The executable sample is not sufficient proof for a feature that changes authoritative scope safety, runtime admission semantics, relation impact reporting, materialization, and persistence failure behavior.

This roadmap does not redesign the feature. It hardens the shipped implementation mechanically, proves its contracts, and removes specific unsound behavior.

---

# Review findings that MUST be fixed

Treat the findings below as requirements, not suggestions.

## Finding A — active persistence policy is not used consistently

Current `ConsistencyEfCoreMappings.ActiveConsumerDescriptors(runtime)` always includes descriptors from `_materializations`.

That is wrong for:

```csharp
new ConsistencySaveOptions
{
    SaveBehavior = ConsistencySaveBehavior.Validate
}
```

Current authoritative policy semantics are:

```text
Enforced invariants
    active in Validate
    active in RecalculateAndValidate

Materialized derived values
    inactive in Validate
    active in RecalculateAndValidate
```

`GetScopeGaps(...)` already mostly follows this split, but `ExternalConsumerDiscovery.BuildRequests(...)` and `ValidateEvaluationClosure(...)` currently use all materializations regardless of `SaveBehavior`.

Consequences today can include:

- a Validate-only save executing a materialization-only consumer resolver;
- a Validate-only save failing because a materialization-only resolver/evaluation reference is missing;
- extra database queries that should not exist.

Fix this first and reuse one exact active-policy description everywhere.

## Finding B — NavigationConsumerCoverage suppression is too coarse

Current `GetScopeGaps(...)` builds `capabilityBySet` and suppresses `NavigationConsumerCoverage` at the whole-object-set level.

This is unsafe because the original scope compiler can require navigation consumer coverage through more than one active definition and through upstream derived dependencies. A resolver that covers one direct navigation must not accidentally satisfy an unrelated unsupported/sibling navigation obligation on the same `ObjectSet`.

Example shape that MUST remain fail-closed:

```text
same root set Links

active derived A:
    Link.PurchaseOrderLine.UnitPrice
    -> supported direct resolver exists

active derived/invariant B or upstream dependency:
    Link.A.B.Value
    -> unsupported in v1

result:
    Complete(Links) is still required
    resolver for Link.PurchaseOrderLine MUST NOT suppress that gap
```

Do not decide discoverability from a shallow per-definition analysis grouped only by set id.

## Finding C — evaluation closure currently accepts an unloaded null navigation

Current `ValidateEvaluationClosure(...)` does:

```csharp
var target = MemberReader.Read(descriptor.Navigation, root);
if (target is null) continue;
```

That cannot distinguish:

```text
legitimate loaded optional null
```

from:

```text
reference was never Include/Load-ed and CLR value is null
```

Therefore a resolver can omit an evaluation reference and pass the current guard.

The feature contract explicitly says the host query owns evaluation closure. Raffinert must fail clearly when the active dependency needs a reference that is not loaded. It must still allow a legitimately loaded optional-null navigation.

## Finding D — CoverageAdmission is currently treated too much like ObjectAdded

`CoverageAdmission` implements `IAddedMutation`, and the runtime commit path sends every `IAddedMutation` through `CommitAdd(...)`.

Structural admission of an already-existing persisted root is **not a domain ObjectAdded event**.

Coverage admission must establish baseline runtime knowledge:

```text
object-set registration
navigation indexes
projection indexes
relation/index baseline membership
derived/invariant source state needed for planning
```

but it must not by itself report semantic business addition:

```text
no ObjectAdded mutation origin
no fake relation AddedPairs solely because the runtime learned an existing row
no policy work solely because an old persisted row became known
```

The actual changed target member must remain the semantic cause of the recalculation/invariant impact.

Do not solve this by removing the object from planning. The discovered root must still be present in reversible planning state and in the final forward patch after successful SQL.

## Finding E — convenience-save SQL failure can poison same-DbContext retry

Planning materializes calculated mirrors before SQL. With discovery, previously unknown `Unchanged` roots become tracked, then materialization changes their sink property and makes them `Modified`.

If `SaveChanges`/`SaveChangesAsync` fails:

```text
runtime remains at pre-plan state
but DbContext keeps discovered roots
and framework-written materialized mirror changes remain Modified
```

On a retry the current discovery guard can then see an existing `Modified` root that is not registered in the runtime and fail closed.

For the convenience save API, framework-introduced materialization writes MUST be reversible when SQL fails. Do not roll back arbitrary user domain mutations. Roll back only writes performed by Raffinert materialization during planning, restoring both CLR value and EF `IsModified` state captured immediately before each framework write.

The manual persistence unit of work remains an explicit advanced workflow. Do not invent a broad public transaction API in this wave; document its failure/abandon boundary if same-context rollback is not already supported.

## Finding F — public resolver registration is under-validated

`DiscoverConsumers(...)` currently accepts a direct property/field expression, but the feature depends on EF relationship metadata and tracking/fixup semantics.

At EF mapping validation time, require the registered member to resolve to an exact mapped **reference navigation** on the root EF entity type.

Also validate the resolver target type contract. Do not allow a registration whose `TTarget` only happens to be assignable through a base/interface while the compiled descriptor expects another exact target CLR type, unless the implementation deliberately defines and tests that variance. For v1, prefer exact target CLR type equality.

Do not generate Includes and do not query automatically.

## Finding G — error text is stale

`IncompleteConsistencyScopeException` still says, in effect, that callers must seed/maintain authoritative runtime coverage and that Raffinert will not auto-load missing objects.

After `DiscoverConsumers`, that message is incomplete. Update it to explain the two supported choices without implying generated loading:

```text
- assert closed-world coverage with ConsistencyScope.Complete(set), or
- for an eligible direct reference-navigation dependency, register a host-owned DiscoverConsumers query.
```

Keep the statement that Raffinert does not invent database queries and cannot prove host under-fetch.

---

# Frozen boundaries for Tasks 192–201

Do NOT expand the feature while hardening it.

Still out of scope:

- multi-hop external discovery;
- collection-navigation discovery;
- relation predicate consumer discovery;
- projected-consumer discovery;
- generated queries;
- automatic Include generation;
- lazy loading integration;
- partitioned `ConsistencyScope`;
- non-EF resolvers;
- CDC/background reconciliation;
- cross-process locking/serialization;
- automatic repair of raw SQL/trigger/other-process mutations;
- previously unknown existing Modified/Deleted root reconstruction;
- runtime identity reconciliation across different CLR instances with the same key;
- public discovery request/proof types.

Keep the public v1 method shape:

```csharp
public ConsistencyEfCoreMappings DiscoverConsumers<TRoot, TTarget>(
    ObjectSet<TRoot> roots,
    Expression<Func<TRoot, TTarget?>> navigation,
    Func<DbContext, IReadOnlyCollection<TTarget>, IQueryable<TRoot>> query)
    where TRoot : class
    where TTarget : class;
```

Do not add alternate public loader/hydration APIs.

---

# Required implementation model

## 1. One active-policy model, reused everywhere

Create/refactor one internal way to obtain the definitions whose authoritative state matters for the current save behavior.

Conceptually:

```text
Active policy definitions
    = all enforced invariants
    + materialized derived definitions only when RecalculateAndValidate
```

Do not keep one policy selection implementation in `GetScopeGaps(...)` and a different one in `ActiveConsumerDescriptors(...)`.

The exact active set must feed:

- scope-gap evaluation;
- external coverage capability calculation;
- request construction;
- evaluation-closure validation.

A weak agent must not copy/paste four slightly different loops.

## 2. Compile navigation coverage over the same dependency closure as scope requirements

The existing `ConsistencyScopeRequirementCompiler` recursively follows upstream derived dependencies. External discoverability must match that authoritative dependency closure.

Implement one internal compiler/data model whose output for each active top-level definition records every navigation-consumer obligation reachable through the definition's dependency closure.

A useful internal shape is conceptually:

```text
ActiveDefinitionCoverage
    TopLevelDefinition
    NavigationRequirements[]

NavigationRequirement
    RootSet
    Descriptor?       // direct reference path supported by discovery v1
    IsUnsupported     // reverse-navigation coverage exists but is not externally discoverable in v1
```

Names are not frozen; semantics are.

Rules:

- traverse upstream derived dependencies exactly once with reference-identity cycle protection;
- use merged inferred + explicit dependency analysis;
- direct two-segment reference navigation paths produce descriptors;
- any reverse-owner coverage path outside frozen v1 becomes `IsUnsupported = true`;
- no relation/projected requirement is converted into a navigation resolver;
- do not parse strings/member names.

Then decide a `NavigationConsumerCoverage` gap for a set as externally satisfiable only when **every active navigation-consumer obligation for that set in the full active closure is supported and has its exact resolver**.

This is intentionally conservative. If there is any ambiguity, keep the gap and require `Complete(set)`.

## 3. Request construction uses the same active closure

Build runtime requests only from supported descriptors in the current active-policy closure.

For every captured `PropertyChange`:

```text
match exact target member
match compatible/exact target CLR instance
skip root set if ConsistencyScope.Complete(rootSet)
```

Batch exactly by:

```text
ObjectSet definition reference
+
Navigation MemberInfo
```

Deduplicate targets by CLR reference identity.

No resolver call with an empty target batch.

No resolver call for inactive materializations/non-enforced invariants.

## 4. Validate EF resolver metadata once, before executing resolver queries

During mapping/persistence validation, for each registered resolver:

1. prove the root set belongs to this runtime;
2. find the EF entity type for `RootSet.ObjectType`;
3. resolve exactly one EF `INavigation` whose `PropertyInfo` or `FieldInfo` equals the registered `MemberInfo`;
4. reject if none;
5. reject collection navigation;
6. require the navigation target CLR type to equal the resolver's frozen `TTarget` type for v1;
7. retain/reference the validated EF navigation metadata internally so discovery does not repeatedly guess from member names.

Do not require the resolver query to Include anything here; query execution determines evaluation closure later.

## 5. Evaluation closure distinguishes unloaded from loaded-null

For every admitted/discovered root and every active direct-navigation descriptor needed for evaluation on that root set:

```text
resolve the exact EF NavigationEntry from validated INavigation metadata
```

Then apply:

```text
if CurrentValue is non-null:
    target entity must be tracked by the same DbContext

if CurrentValue is null AND NavigationEntry.IsLoaded == true:
    accept as legitimate current null

if CurrentValue is null AND NavigationEntry.IsLoaded == false:
    fail before SQL with a dedicated/clear invalid-operation message
```

Do not call `Load()` automatically.

Do not treat `null` as proof of closure.

Add tests for both unloaded-null failure and loaded-null success.

## 6. Separate structural coverage admission from business ObjectAdded semantics

Do not continue using one semantic path merely because both operations add an object to `ObjectSetRuntime`.

Keep a private/internal representation such as `CoverageAdmission`, but after validation split normalized work into at least:

```text
Domain lifecycle mutations
    ObjectAdded
    ObjectRemoved

Coverage admissions
    existing persisted roots learned for this operation

Property/collection changes
```

`CoverageAdmission` may participate in object-set simulation so duplicate keys and planning membership are validated, but it must not become `MutationOriginKind.ObjectAdded`.

During reversible execution, apply coverage admissions as **baseline structural admission** before semantic dependency propagation:

```text
register root in ObjectSetRuntime
initialize derived/invariant source runtime state needed for a registered source
add navigation memberships
add projection memberships
establish relation runtime membership/indexes
```

When relation-state APIs return deltas while establishing this baseline, DO NOT merge those baseline deltas into externally reported/semantic `relationDeltas`.

Then process real business lifecycle/property/collection mutations normally.

Required observable behavior:

```text
coverage-only admission of an existing persisted row
    -> root is present in final runtime after successful plan install
    -> no ObjectAdded origin
    -> no RelationMutationImpact.AddedPairs caused solely by learning the row
    -> no repair/immediate policy request caused solely by learning the row
```

For the PriceRate case, `PO.UnitPrice` remains the cause that makes all discovered link derived values affected.

Do not remove discovered roots from `EvaluateAffectedDerived`/`EvaluateAffectedInvariants` accidentally. If current lifecycle-based evaluation selection depended on treating coverage as `IAddedMutation`, replace that accidental coupling with an explicit `coverageAdmissions` source list.

## 7. Preserve binding-plan atomicity

Keep this invariant:

```text
before SQL:
    live runtime is unchanged

planning:
    admissions + business mutation execute reversibly
    exact forward patch is captured
    runtime rolls back to base state

SQL succeeds:
    install exact forward patch once
    increment runtime version once

SQL fails:
    never install coverage/business patch
```

Add direct tests around runtime version and registration to prove it.

Do not introduce `runtime.Add(...)` from discovery code.

---

# Task 192 — add a dedicated discovery regression suite BEFORE fixing behavior

Create:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/ExternalConsumerDiscoveryTests.cs
```

Do not hide these tests inside the sample.

Build a compact reusable SQLite fixture with:

```text
SourceItem
    Id
    UnitValue

TargetItem
    Id
    UnitValue

Association
    Id
    SourceItemId / SourceItem
    TargetItemId / TargetItem
    UnitRate materialized mirror
```

Use the same conceptual domain as the dogfood sample, but test helpers must be deterministic and independent.

At minimum, first commit characterization tests for behavior that should remain:

1. `Source_change_discovers_unloaded_consumers_and_materializes_all_rates_async`
   - DB has A/B/C pointing to Source1;
   - current context/runtime initially know only A;
   - mutate only `Source1.UnitValue`;
   - exactly one source-navigation resolver call;
   - B/C become tracked because of resolver;
   - tracked/runtime/database UnitRate correct for A/B/C;
   - runtime version increments exactly once.

2. sync equivalent using `SaveChangesConsistently`.

3. `Multiple_changed_targets_for_same_navigation_are_batched_into_one_resolver_call`.

4. `Complete_scope_skips_external_resolver`.

5. `Safe_resolver_superset_is_filtered_by_current_navigation`.

Do not alter production behavior just to make tests easy.

Commit suggestion:

```text
test: characterize external consumer discovery
```

---

# Task 193 — fix active-policy applicability

Refactor active definition/coverage selection so `ConsistencySaveBehavior` is explicit input.

Required tests:

1. Materialized derived only + `Validate`:
   - resolver callback counter remains zero;
   - no discovery query runs;
   - materialized value is not recalculated;
   - no navigation scope gap is demanded solely by that materialization.

2. Same mapping + `RecalculateAndValidate`:
   - resolver runs when matching mutation exists;
   - value recalculates.

3. Enforced invariant + `Validate`:
   - resolver runs when needed.

4. Non-enforced invariant:
   - no resolver call.

5. Derived definition exists in model but is not materialized:
   - no resolver call.

After this task there must be exactly one source of truth for active persistence definitions.

Commit suggestion:

```text
fix: scope external discovery to active EF policy
```

---

# Task 194 — make scope substitution requirement-safe

Replace shallow `capabilityBySet` logic with the full active dependency-closure model described above.

Required tests MUST include:

1. `Supported_direct_navigation_with_resolver_can_replace_navigation_complete_scope`.

2. `Supported_and_unsupported_navigation_requirements_on_same_set_remain_fail_closed`:
   - one active dependency `Root.Direct.Value` with resolver;
   - one active dependency on same root set requiring `Root.A.B.Value` or another unsupported v1 reverse path;
   - no `Complete(rootSet)`;
   - `IncompleteConsistencyScopeException` before any resolver query and before SQL.

3. `Upstream_derived_navigation_requirement_is_not_lost`:
   - downstream materialized/enforced node depends on upstream derived;
   - upstream creates navigation consumer coverage;
   - if that upstream requirement is unsupported/missing resolver, save fails closed.

4. `All_navigation_requirements_in_active_closure_must_have_resolvers_before_gap_is_suppressed`.

5. Same CLR type in two different ObjectSets:
   - resolver registered for set A must not satisfy set B.

Never suppress `RelationSourceCoverage`, `RelationTargetCoverage`, or `ProjectedConsumerCoverage` through `DiscoverConsumers`.

Commit suggestion:

```text
fix: bind discovery coverage to exact active requirements
```

---

# Task 195 — harden resolver registration and evaluation closure

Implement EF navigation metadata validation and exact target-type validation.

Required registration tests:

1. direct mapped reference property succeeds;
2. mapped reference backing field succeeds if existing EF conventions/tests support field members;
3. scalar property passed as navigation fails during mapping validation;
4. collection navigation fails;
5. unrelated/unmapped member fails;
6. target generic type mismatch fails clearly;
7. duplicate exact root-set/navigation registration still fails.

Required evaluation-closure tests:

1. resolver returns consumer but omits Include/Load for another required evaluation reference:
   - root is tracked;
   - required reference CLR value null and `IsLoaded == false`;
   - fail before SQL with clear message.

2. optional reference is explicitly loaded and legitimately null:
   - `IsLoaded == true`;
   - closure guard accepts it;
   - downstream expression/test must be null-safe if it is expected to evaluate.

3. non-null evaluation target is detached => fail.

4. all required references included/tracked => pass.

Do not auto-load missing references.

Commit suggestion:

```text
fix: prove EF evaluation closure for discovered consumers
```

---

# Task 196 — remove semantic ObjectAdded leakage from CoverageAdmission

Refactor mutation normalization/execution exactly as described in the structural admission section.

Add Core-level tests if possible so the semantic distinction is proven independently of EF. If the admission type must remain internal to EF/Core integration, use `InternalsVisibleTo` existing test access rather than exposing a public API.

Required assertions for a discovered unchanged existing root:

```text
runtime contains root after committed binding plan
runtime did not contain root before SQL/plan commit
runtime version increased once
MutationOrigins contains no ObjectAdded for coverage root
RelationMutationImpact.AddedPairs does not contain pairs solely established as baseline for coverage root
coverage admission alone does not emit policy requests
```

Then add the actual target scalar change and assert:

```text
derived/invariant impact includes discovered root
cause is the target member mutation / normal dependency route
materialized value is correct
```

If relation baseline APIs cannot currently establish membership without exposing a delta, add a narrow internal method or discard the returned baseline delta. Do not change public relation APIs.

Be careful with source initialization:

- discovered roots need derived/invariant source state so `Get`, planned evaluation, and policy evaluation work;
- do not suppress that initialization merely to avoid semantic add reporting.

Commit suggestion:

```text
fix: separate coverage admission from domain addition
```

---

# Task 197 — prove ChangeTracker overlay and resolver misuse matrix

Add tests for the effective-consumer formula.

## Overlay cases

### Retargeted away

```text
DB: X -> Source1
tracked current: X -> Source2
request targets: Source1
```

Resolver can return X from DB, but X must not be admitted/affected as Source1 consumer.

### Retargeted in

```text
DB: X -> Source1
tracked current: X -> Source2
request targets: Source2
```

If X is already a valid registered existing participant, it must be considered through tracked overlay even if DB query for Source2 cannot return it yet.

### Added

Tracked Added consumer pointing at requested target remains a real `ObjectAdded` business mutation; it must not become `CoverageAdmission`.

### Deleted

Tracked Deleted consumer is excluded from current effective consumers.

### Previously unknown Modified/Deleted existing root

If it is not already registered in runtime, fail before SQL. Preserve this v1 boundary; do not reconstruct history.

## Resolver misuse cases

Add tests for:

- AsNoTracking/detached returned root => fail;
- returned root maps to wrong exact ObjectSet => fail;
- duplicate runtime key with different instance => fail;
- wrong CLR root type if reachable through internal test seam => fail;
- resolver returns safe superset => filter, do not fail;
- missing resolver for a required supported descriptor => fail before any SQL;
- resolver callback throws => propagate before SQL, runtime unchanged.

Commit suggestion:

```text
test: prove discovery overlay and fail-closed resolver rules
```

---

# Task 198 — restore framework materializations when convenience SQL fails

Refactor materialization application so planning can return/carry a private rollback handle for **framework-written sink mirrors**.

Suggested internal data per write:

```text
entity/source reference
PropertyInfo
PropertyEntry
old CLR/current value
old IsModified
```

Rules:

1. capture old value and `IsModified` immediately before Raffinert writes the mirror;
2. if planning itself throws, preserve existing rollback behavior;
3. if convenience `context.SaveChanges()` throws, restore all Raffinert-applied mirror writes in reverse order;
4. if convenience `SaveChangesAsync()` throws/cancels, same restoration;
5. do not restore arbitrary user changes;
6. discovered entities may remain tracked after failure; that is acceptable;
7. after rollback, a same-DbContext retry with the original user mutation must be able to run discovery/planning again instead of failing merely because Raffinert left the discovered root Modified;
8. successful SQL must not run the mirror rollback.

Required tests:

- inject SQLite/database failure after planning and before successful commit;
- runtime version unchanged;
- discovered root not registered in runtime;
- framework mirror CLR value restored;
- mirror `IsModified` restored;
- original user mutation still present;
- second save attempt after removing the injected failure succeeds.

Do this for sync and async convenience paths if the test harness permits the same fault mechanism.

Manual `ConsistencyPersistenceUnitOfWork`:

- do not invent an implicit rollback after the caller's external SQL because the library cannot observe arbitrary external failure;
- document that a planned manual UoW is single-use and the caller must abandon/recreate it after a failed database operation unless an existing explicit rollback primitive already covers it;
- keep discovery itself integrated in `PrepareAndPlan()`.

Commit suggestion:

```text
fix: rollback materialized mirrors when consistent save fails
```

---

# Task 199 — sync/async/manual/batching parity proof

Build a parity matrix; do not assume the sample covers it.

Required tests:

```text
Convenience sync
    discovery + materialization works

Convenience async
    discovery + materialization works
    cancellation reaches ToListAsync and does not mutate runtime

Manual policy-aware UoW
    CaptureConsistencyUnitOfWork
    PrepareAndPlan performs discovery
    caller persists
    CommitAfterDatabaseCommit installs exact plan

Closed-world Complete(rootSet)
    zero resolver calls in all applicable paths

Open-world same target in a later save
    resolver is called again unless Complete(rootSet) is supplied
```

The last rule matters: learning consumers in one operation does not create a durable proof that no new database consumers appeared later.

Also test batching:

- three SourceItem mutations -> one `Association.SourceItem` call with three distinct target references;
- two changed members on the same target that share the same reverse navigation -> still one resolver call;
- SourceItem and TargetItem mutations -> one call per navigation, not per property;
- deterministic request ordering by object-set id/navigation remains stable if tests depend on it.

Commit suggestion:

```text
test: prove discovery persistence workflow parity
```

---

# Task 200 — tighten dogfood scenario and documentation

The current sample mutates both the shared source and the known target and therefore expects two resolver calls. That demonstrates two registrations, but it obscures the primary production-shaped acceptance case.

Change the main external-discovery dogfood scenario to:

```text
three persisted associations share Source/PO line P
only first association initially loaded/registered
mutate ONLY P.UnitValue/UnitPrice
SaveChangesConsistentlyAsync
exactly ONE resolver invocation for Association.SourceItem / Link.PurchaseOrderLine
all three mirrors update correctly
unloaded opposite endpoints are loaded only because host resolver includes them
```

If desired, keep a second smaller test (not necessarily sample output) for the two-navigation batching case.

Update:

- `README.md`;
- `docs/anemic-model-dependency-maintenance-example.md`;
- `docs/architecture.md` if authoritative runtime/persistence wording needs to mention targeted open-world consumer coverage;
- `IncompleteConsistencyScopeException` message.

Documentation must state explicitly:

```text
DiscoverConsumers does not make a set complete.
Resolver completeness remains a host assertion.
Resolver query + write concurrency/isolation remains a host/database boundary.
Database mutations invisible to EF/Raffinert still require reconciliation/rebuild/publication.
Unsupported navigation shapes still require Complete(set).
```

Do not claim the executable proves under-fetch detection or cross-process consistency.

Commit suggestion:

```text
docs(sample): tighten external discovery guarantees
```

---

# Task 201 — public API, release proof, and roadmap closeout

## Public API

Expected intentional public API from Tasks 182–201 remains only the already-added registration method unless a pre-existing public surface necessarily changes:

```text
Raffinert.Consistency.EntityFrameworkCore.ConsistencyEfCoreMappings.DiscoverConsumers<TRoot,TTarget>(...)
```

Do NOT expose:

```text
CoverageAdmission
ExternalConsumerDescriptor
ExternalConsumerAnalysis
ExternalConsumerRequest
resolver metadata wrappers
materialization rollback handles
```

Update `PublicAPI.Unshipped.txt` only for intentional public changes.

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

Do not mark the roadmap complete if any command is skipped or red.

## Remote proof

After pushing the final implementation commit:

- verify the GitHub Actions CI run for that exact commit;
- record the exact successful commit SHA in both this roadmap and `docs/roadmaps/README.md`;
- do not write “remote green” based only on classic commit statuses; inspect the Actions run.

---

# Required test inventory before closeout

The final repository must contain dedicated automated proof for all of these categories:

## Policy applicability

- Validate ignores materialization-only discovery.
- RecalculateAndValidate activates materialization discovery.
- enforced invariant discovery active.
- non-enforced/inactive definitions ignored.

## Coverage safety

- supported direct resolver substitutes only eligible navigation coverage;
- unsupported sibling path on same set prevents substitution;
- upstream requirement is preserved;
- same CLR type/different ObjectSet isolation;
- relation/projected coverage never substituted.

## Request batching

- same nav/multiple targets -> one call;
- same nav/multiple matching members -> one call;
- two navs -> two calls;
- Complete(set) -> zero calls.

## Evaluation closure

- missing Include/unloaded null fails;
- loaded legitimate null accepted;
- detached required target fails;
- correct Include closure passes.

## Overlay

- retarget-away excluded;
- retarget-in included when already valid participant;
- Added stays domain add;
- Deleted excluded;
- unknown Modified/Deleted fails closed.

## Coverage semantics

- discovered unchanged root installed only after successful commit;
- no false ObjectAdded origin;
- no false relation AddedPairs;
- no policy action caused only by baseline admission;
- actual target mutation still affects discovered root.

## Failure semantics

- resolver failure: no SQL/runtime change;
- planning failure: no live-runtime change;
- SQL failure: no live-runtime admission;
- convenience SQL failure restores Raffinert-written mirrors;
- same-context convenience retry can succeed;
- forward-patch install failure retains existing runtime synchronization exception semantics.

## Workflow parity

- sync convenience;
- async convenience;
- cancellation;
- manual UoW;
- subsequent open-world operation re-runs resolver.

---

# Anti-shortcut rules for weak agents

Do not “fix” tests by doing any of the following:

- adding `ConsistencyScope.Complete(rootSet)` to an open-world discovery test;
- preloading all roots before the tested mutation;
- calling resolver manually from the handler/test before `SaveChangesConsistently`;
- weakening assertions from runtime+tracked+database to database only;
- removing the unknown Modified/Deleted fail-closed guard;
- converting CoverageAdmission back to public ObjectAdded;
- marking relation baseline pairs as business AddedPairs;
- making `Validate` recalculate materializations;
- treating `null` navigation as loaded without checking EF load state;
- auto-calling `Reference.Load()` or generating Includes;
- swallowing resolver/query exceptions;
- mutating the live runtime before SQL to simplify implementation;
- using CLR type as resolver identity instead of exact ObjectSet + navigation;
- suppressing a scope gap merely because any resolver exists on the same set;
- adding public API to expose internal discovery machinery;
- changing unsupported multi-hop/collection/projected/relation behavior from fail-closed to best-effort.

If an implementation obstacle makes one of these temptingly easy, stop and document the obstacle rather than weakening the contract.

---

# Final acceptance checklist

Tasks 192–201 are complete only when all are true:

- [ ] dedicated `ExternalConsumerDiscoveryTests` exists;
- [ ] active persistence policy is a single source of truth;
- [ ] Validate-only saves execute zero materialization-only resolver queries;
- [ ] scope substitution is based on the full active dependency closure, not shallow set capability;
- [ ] supported + unsupported navigation obligations on the same set remain fail-closed;
- [ ] resolver registration is validated against exact EF reference navigation metadata;
- [ ] unloaded-null required evaluation navigation fails before SQL;
- [ ] loaded legitimate null is distinguishable and accepted;
- [ ] CoverageAdmission is structural baseline knowledge, not semantic ObjectAdded;
- [ ] no false ObjectAdded origins for discovered persisted roots;
- [ ] no false relation AddedPairs solely from coverage admission;
- [ ] discovered roots are still recalculated/evaluated when the actual target member changes;
- [ ] live runtime remains unchanged until SQL succeeds;
- [ ] successful SQL installs admission + business effects in one binding-plan commit/version increment;
- [ ] sync, async, manual, cancellation and closed-world paths have tests;
- [ ] ChangeTracker overlay retarget/add/delete cases have tests;
- [ ] unknown existing Modified/Deleted root still fails closed;
- [ ] convenience SQL failure restores framework-written materialization mirrors;
- [ ] same-context convenience retry after SQL failure is proven;
- [ ] production-shaped sample mutates only the shared source/PO price and uses one resolver call;
- [ ] stale incomplete-scope error/docs are corrected;
- [ ] no accidental public API beyond intended `DiscoverConsumers`;
- [ ] full local verification passes;
- [ ] GitHub Actions for the final exact commit is green;
- [ ] Tasks 182–191 are recorded as implemented-but-hardened-by this wave, and Tasks 192–201 are closed with exact proof SHA.

Until this checklist is complete, treat external consumer discovery as implemented prototype behavior, not a finished release contract.
