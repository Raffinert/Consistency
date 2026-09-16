# Codex implementation plan — external consumer discovery and incomplete-graph resolution

Status: **ACTIVE IMPLEMENTATION PLAN**

Baseline commit: `d7abe1379d90d65c4c142ef836d58c0a3a6c31a3`

Tasks: **182–191**

## Why this roadmap exists

Raffinert already maintains dependencies correctly for objects that are present in its runtime graph. The dependency-maintenance dogfood proves that nested source changes can fan out through reverse navigation indexes, that retargeting updates those indexes, and that derived values can be recalculated and materialized through EF.

That is not enough for the real anemic-model problem.

In an anemic/persistence-oriented application, arbitrary code may do:

```csharp
purchaseOrderLine.UnitPrice = 120m;
```

without first loading every `PurchaseOrderInvoiceLine` that points to that purchase-order line. A single PO line may be linked to many invoice lines, and some or all of those links/invoice lines may not be tracked in the current DbContext or registered in the Raffinert runtime.

The real consistency obligation is therefore not:

```text
all PurchaseOrderInvoiceLine objects must always be loaded
```

It is:

```text
for the changed PO line P,
all PurchaseOrderInvoiceLine consumers whose PurchaseOrderLine == P
must be known before the consistency plan is considered authoritative
```

The same applies in the other direction when `InvoiceLine.UnitPrice` changes.

Current reverse navigation indexes are closed-world indexes: they can answer which registered roots point to a target, but they cannot know about roots that the runtime has never seen. `ConsistencyScope.Complete(set)` solves this by letting the host assert that the entire set is already represented, but requiring every mutation path in an anemic application to preload every possible dependent root reintroduces the orchestration burden Raffinert exists to remove.

This roadmap adds a narrow, fail-closed capability for **mutation-driven external consumer discovery**.

The intended contract is:

```text
Raffinert declares/compiles WHAT depends on what.
Raffinert detects a nested-target mutation at the central persistence boundary.
Raffinert determines the exact reverse consumer path whose in-memory coverage is open-world.
The host supplies HOW to query authoritative persisted consumers for that path and target set.
The EF adapter merges persisted consumers with the current ChangeTracker overlay.
Raffinert admits the discovered existing roots only into operation-scoped planning state.
The existing dependency engine is rerun against that expanded state.
Only after SQL succeeds is the exact combined forward patch installed into the live runtime.
```

This is not general runtime hydration, not lazy loading, and not a database repository abstraction.

---

# Production-shaped acceptance scenario

The primary acceptance domain is deliberately shaped like the PriceRate problem while keeping neutral names where practical.

```text
PurchaseOrderLine P
    UnitPrice = 100

PurchaseOrderInvoiceLink A -> P -> InvoiceLine 1
PurchaseOrderInvoiceLink B -> P -> InvoiceLine 2
PurchaseOrderInvoiceLink C -> P -> InvoiceLine 3
```

Only `PurchaseOrderInvoiceLink A` and `InvoiceLine 1` are initially known to the runtime. B/C and InvoiceLine 2/3 exist in SQLite but are not loaded.

The model contains:

```csharp
var priceRate = builder.Derived(links)
    .DependsOn(x => x.InvoiceLine.UnitPrice)
    .DependsOn(x => x.PurchaseOrderLine.UnitPrice)
    .Compute(x => PriceRateCalculator.Calculate(
        x.InvoiceLine.UnitPrice,
        x.PurchaseOrderLine.UnitPrice));
```

The application changes only:

```csharp
purchaseOrderLine.UnitPrice = 120m;
```

No handler/service manually queries links before the mutation.

At `SaveChangesConsistentlyAsync(...)`, Raffinert must:

```text
capture PurchaseOrderLine.UnitPrice mutation
        ↓
recognize dependency path Link.PurchaseOrderLine.UnitPrice
        ↓
build one batched external consumer request for
    root set = links
    navigation = Link.PurchaseOrderLine
    targets = [P]
        ↓
execute the host-owned authoritative query
        ↓
load B/C and the InvoiceLine data needed to evaluate PriceRate
        ↓
merge current tracked overlay
        ↓
plan against A/B/C
        ↓
materialize PriceRate for A/B/C
        ↓
SaveChanges
        ↓
install the exact coverage-expansion + consistency forward patch
```

The final test must prove that all three links have correct tracked/runtime/database `PriceRate` values even though B/C and InvoiceLine 2/3 were absent before the save boundary.

That is the definition of success for this roadmap.

---

# Frozen v1 scope

## Supported

External discovery applies only to **direct reference navigation consumer paths** of the form:

```text
Root.Navigation.TargetMember
```

where:

```text
Root is a Raffinert ObjectSet source
Navigation is exactly one reference navigation/property/field
TargetMember belongs to the referenced target object
```

Examples:

```csharp
link => link.PurchaseOrderLine.UnitPrice
link => link.InvoiceLine.UnitPrice
association => association.SourceItem.UnitValue
association => association.TargetItem.UnitValue
```

The corresponding registered discovery path is the direct reference prefix:

```csharp
link => link.PurchaseOrderLine
link => link.InvoiceLine
association => association.SourceItem
association => association.TargetItem
```

Supported definition roles in this wave:

- source-derived dependencies (`ExpressionParameterRole.DerivedSource`);
- invariant-source dependencies (`ExpressionParameterRole.InvariantSource`) when the invariant is enforced by EF persistence policy.

The same discovery registration may satisfy any number of derived/invariant definitions that use the same exact root set + direct reference navigation.

## Explicitly out of scope

Do **not** implement these in Tasks 182–191:

- multi-hop reverse discovery such as `Root.A.B.Value`;
- collection-navigation discovery;
- relation predicate source/target discovery;
- projected-consumer discovery;
- arbitrary expression-to-SQL translation;
- generated resolver queries;
- automatic `Include(...)` generation;
- non-EF persistence resolvers;
- distributed/cache/CDC discovery;
- database trigger or raw-SQL mutation discovery;
- background runtime reconciliation;
- partition/key-scoped `ConsistencyScope` redesign;
- making the entire object set complete after a targeted lookup;
- pretending an under-fetching host query can be detected generally;
- cross-process serialization/locking guarantees.

Unsupported paths remain fail-closed through the existing authoritative-scope mechanism. `ConsistencyScope.Complete(set)` remains a valid closed-world fast path.

---

# Frozen public EF API

Add discovery registration to `ConsistencyEfCoreMappings` so it is configured once with the persistence policy rather than remembered in every handler/save call.

Use exactly this public method shape for v1:

```csharp
public ConsistencyEfCoreMappings DiscoverConsumers<TRoot, TTarget>(
    ObjectSet<TRoot> roots,
    Expression<Func<TRoot, TTarget?>> navigation,
    Func<DbContext, IReadOnlyCollection<TTarget>, IQueryable<TRoot>> query)
    where TRoot : class
    where TTarget : class;
```

Required usage:

```csharp
var ef = new ConsistencyEfCoreMappings()
    .Map(links)
    .Materialize(priceRate, link => link.PriceRate)
    .DiscoverConsumers(
        links,
        link => link.PurchaseOrderLine,
        (db, purchaseOrderLines) =>
        {
            var ids = purchaseOrderLines.Select(x => x.Id).ToArray();
            return db.Set<PurchaseOrderInvoiceLink>()
                .Where(x => ids.Contains(x.PurchaseOrderLineId))
                .Include(x => x.PurchaseOrderLine)
                .Include(x => x.InvoiceLine);
        })
    .DiscoverConsumers(
        links,
        link => link.InvoiceLine,
        (db, invoiceLines) =>
        {
            var ids = invoiceLines.Select(x => x.Id).ToArray();
            return db.Set<PurchaseOrderInvoiceLink>()
                .Where(x => ids.Contains(x.InvoiceLineId))
                .Include(x => x.PurchaseOrderLine)
                .Include(x => x.InvoiceLine);
        });
```

Then ordinary application code remains:

```csharp
purchaseOrderLine.UnitPrice = 120m;

await db.SaveChangesConsistentlyAsync(runtime, ef, cancellationToken: ct);
```

Do not introduce public alternatives in this wave:

```text
LoadMissingDependencies
HydrateRuntime
Rehydrate
Repository
AutoInclude
GetScopeRequirements
ResolveGraph
CompletePartialSet
```

`DiscoverConsumers` describes exactly the host capability being registered: an authoritative reverse lookup from target objects to consumer roots.

---

# Resolver contract

A `DiscoverConsumers` query is an application-owned **authoritative persisted-consumer query** for the exact registered root set and direct navigation.

For targets `T1..Tn`, the query must return at least every persisted root `R` in the application's authoritative database boundary for which:

```text
R.Navigation ∈ { T1..Tn }
```

The query may return a safe superset. Raffinert filters the result using current tracked navigation values before planning.

Raffinert can validate:

- returned CLR type;
- exact object-set identity;
- duplicate runtime keys;
- whether roots are tracked by the same DbContext;
- whether current navigation values correspond to requested targets after ChangeTracker overlay;
- whether required evaluation references are available/tracked sufficiently for evaluation.

Raffinert cannot generally prove that the query did not omit a persisted row. Completeness of the query result remains a host assertion, just as whole-set completeness is currently a host assertion.

The resolver must not mutate domain values.

The resolver must not call `SaveChanges`.

The resolver must not manually add objects to the consistency runtime.

---

# Two different meanings of completeness

Keep these concepts separate throughout implementation and documentation.

## 1. Consumer coverage

For mutation:

```text
PurchaseOrderLine P.UnitPrice changed
```

and path:

```text
PurchaseOrderInvoiceLink.PurchaseOrderLine.UnitPrice
```

consumer coverage means:

> every effective `PurchaseOrderInvoiceLink` whose current `PurchaseOrderLine` is P is represented in operation-scoped planning state.

It does **not** mean every link in the database is loaded.

## 2. Evaluation closure

After B/C are discovered, Raffinert must be able to evaluate:

```csharp
link.InvoiceLine.UnitPrice / link.PurchaseOrderLine.UnitPrice
```

Therefore the resolver query must load/fix up the references required by the active computation.

Consumer coverage answers:

```text
Did we find every affected root?
```

Evaluation closure answers:

```text
Can every discovered root actually be evaluated safely?
```

Do not collapse these into one boolean or one vague "hydrated" state.

---

# Closed-world and open-world coverage coexist

`ConsistencyScope.Complete(set)` remains unchanged.

It means:

```text
closed-world proof:
all roots in this object set for the authoritative boundary are already represented
```

When `scope.Complete(links)` is present:

- no external consumer query for `links` is necessary;
- existing navigation indexes remain authoritative;
- existing behavior remains unchanged.

`DiscoverConsumers` provides:

```text
open-world proof capability:
for an exact direct navigation and the current target batch,
ask the host for every persisted consumer and merge it into operation-scoped planning
```

A successful targeted discovery **must never call or imply**:

```csharp
scope.Complete(links);
```

It proves only consumer coverage for the exact navigation + target set + current operation.

---

# ChangeTracker overlay semantics

A database query alone is not the effective consumer set because the same unit of work may contain pending relationship/lifecycle mutations.

For a request `(RootSet, Navigation, Targets)` compute:

```text
EffectiveConsumers =
    PersistedConsumersFromResolver
    UNION tracked Added/current consumers
    UNION tracked retargeted-in current consumers
    MINUS tracked Deleted consumers
    MINUS tracked retargeted-away consumers
```

Use **current CLR/EF navigation state** to decide final membership after the resolver query has materialized/fixed up entities.

Examples that must be covered by tests:

```text
DB: X -> PO1
Current tracked: X -> PO2

request for PO1:
    resolver may return X from persisted DB state
    effective result MUST exclude X

request for PO2:
    DB query may not return X
    tracked overlay MUST include X if X is already an admissible tracked participant
```

However, v1 must fail closed rather than fake baseline semantics for an existing Modified/Deleted root that is not already registered in the runtime. See the direct-participant rule below.

---

# Direct-participant rule for v1

This roadmap solves unloaded **unchanged consumer roots** discovered because another target object changed.

It does not silently pretend that an already-mutated, previously unknown existing root has a trustworthy historical baseline.

Rules:

```text
resolver-discovered Unchanged existing root not in runtime
    -> may be admitted as existing coverage

tracked Added root
    -> remains a real ObjectAdded business mutation; do not baseline-admit it

tracked Modified/Deleted existing root already registered in runtime
    -> normal existing semantics

tracked Modified/Deleted existing root NOT registered in runtime
    -> fail before SQL with a clear exception/message
```

Reason: admitting a previously unknown modified root from its current CLR state loses authoritative old navigation/relation state. Solving that correctly requires original-state reconstruction and possibly loading old principals; that is a separate roadmap.

Do not paper over this by admitting current state and dropping the captured mutation.

The primary PriceRate fan-out case does not need this unsupported path: the changed object is the PO/Invoice endpoint; the discovered links are persisted and unchanged.

---

# Discovery request identity

Internally compile a stable descriptor for every eligible direct navigation dependency:

```text
ConsumerRootSet
ConsumerNavigationMember
TargetClrType
TargetMember
Definition(s) using it
```

For runtime requests, batch by:

```text
exact ObjectSet definition identity
+
exact navigation MemberInfo
```

Targets are deduplicated by CLR reference identity for the operation.

Example mutations:

```text
PO1.UnitPrice changed
PO2.UnitPrice changed
PO3.UnitPrice changed
Invoice10.UnitPrice changed
Invoice11.UnitPrice changed
```

must produce exactly two resolver calls:

```text
Link.PurchaseOrderLine targets [PO1, PO2, PO3]
Link.InvoiceLine       targets [Invoice10, Invoice11]
```

Do not execute one query per changed target.

Do not key resolver registrations by CLR type alone because multiple exact object sets of the same CLR type are supported.

Do not key by derived definition because one reverse lookup may serve multiple derived/invariant definitions.

---

# Model-analysis eligibility rules

Add one internal compiler/analyzer for external direct-navigation consumer descriptors. Reuse existing `TrackedExpressionDependency`, `DependencyPath`, `DependencyPathNavigation`, and model analysis metadata.

An eligible dependency path in v1 must satisfy all of these:

```text
role is DerivedSource or InvariantSource
path root set is known exactly
path has exactly 2 member segments
segment 0 is a reference navigation according to existing navigation classification
segment 0 is NOT a collection
segment 1 is the changed target member
no opaque/string/member-name parsing
```

For:

```csharp
link => link.PurchaseOrderLine.UnitPrice
```

compile:

```text
RootSet     = links
Navigation  = PurchaseOrderInvoiceLink.PurchaseOrderLine
TargetType  = PurchaseOrderLine
TargetMember= PurchaseOrderLine.UnitPrice
```

Do not add a second expression parser. Use existing normalized dependency metadata.

For explicit `DependsOn(...)`, use the merged analysis exactly like inferred dependencies; explicit and inferred paths must behave identically.

---

# Persistence-policy applicability

Discovery should only be required for definitions that the current EF persistence policy intends to make authoritative.

Use the same policy split as current scope safety:

```text
Enforced invariants:
    relevant for Validate and RecalculateAndValidate

Materialized derived values:
    relevant only for RecalculateAndValidate
```

A derived definition that exists in the model but is not materialized by the current `ConsistencyEfCoreMappings` must not force consumer discovery in normal persistence.

A non-enforced invariant must not force discovery.

Do not broaden this roadmap into automatic discovery for every runtime query/Get/Evaluate call.

This wave is specifically about the EF authoritative persistence boundary.

---

# Scope-gate semantics

Current `NavigationConsumerCoverage` is whole-set conservative. Keep existing `RelationSourceCoverage`, `RelationTargetCoverage`, and `ProjectedConsumerCoverage` behavior unchanged.

For `NavigationConsumerCoverage` in the active EF policy:

A root set is accepted when either:

```text
A. ConsistencyScope.Complete(rootSet) is present
```

or:

```text
B. every active navigation-consumer dependency for that root set is v1-eligible
   AND an exact DiscoverConsumers registration exists for every required direct navigation
```

This is deliberately conservative in v1.

If `PriceRate` depends on both:

```text
Link.PurchaseOrderLine.UnitPrice
Link.InvoiceLine.UnitPrice
```

then both reverse navigations must have discovery resolvers registered before whole-set `Complete(links)` can be omitted.

Even if the current mutation happens to touch only PO price, a missing InvoiceLine resolver remains a configuration gap in this wave. Mutation-specific capability relaxation may be considered later.

If any active navigation dependency is multi-hop/collection/otherwise unsupported, `Complete(rootSet)` remains required.

Existing users supplying `Complete(rootSet)` must observe no new query and no changed result.

---

# Operation-scoped coverage admission

Do not use normal public:

```csharp
runtime.Add(...)
```

for discovered persisted roots.

`runtime.Add` means a domain lifecycle mutation. A discovered root already existed in persistence; the runtime is merely learning about it.

Add an internal-only concept such as:

```text
CoverageAdmission
```

with:

```text
ObjectSet definition
Existing instance
```

Do not expose this as a public domain mutation type.

Coverage admission must establish the same baseline runtime knowledge needed for correct planning:

- object-set registration/key identity;
- source lifecycle baseline state for derived/invariants;
- navigation indexes;
- projection indexes where applicable;
- relation membership/index baseline where applicable.

But admission itself must not appear as:

- `ObjectAdded` in causal output;
- a business lifecycle mutation;
- a repair request merely because it was discovered;
- a policy callback;
- a durable business mutation.

---

# Reversible planning — mandatory design

Do **not** mutate the live runtime permanently before SQL and then try to remember how to undo discovery on database failure.

Reuse the existing reversible/binding-plan architecture.

The required planning shape is:

```text
base runtime V
    ↓
capture coverage rollback state
    ↓
temporarily admit discovered existing roots as baseline knowledge
    ↓
capture coverage forward patch
    ↓
prepare/plan the real business mutation against expanded state
    ↓
capture ordinary business forward patch
    ↓
restore runtime completely to base state V
    ↓
execute SQL
    ↓ success
atomically install:
    coverage forward patch
    + business forward patch
    ↓
increment runtime Version once
    ↓
dispatch only business policy actions
```

On SQL failure:

```text
runtime remains exactly at base state/version
no discovered root is installed
no policy action is dispatched
```

On runtime installation failure after SQL:

- restore the pre-install runtime state;
- throw `ConsistencyRuntimeSynchronizationException` exactly as current persistence contract requires;
- do not retry SQL blindly.

Do not create a second shadow runtime or copy the entire model graph for planning.

Prefer extending existing touched-state/forward-patch machinery so coverage admission participates in the same atomic installation boundary.

---

# Version semantics

The combined successful operation increments `ConsistencyRuntime.Version` exactly once.

Temporary reversible admission during planning does not increment the live version because it is restored before control returns from planning.

The binding plan remains tied to the original base version.

After another committed runtime operation advances the version, the combined discovery/business plan must be stale exactly like current `PreparedImpactPlan` behavior.

Do not add a separately user-visible "coverage version" in this wave.

---

# Evaluation-closure validation

A resolver may find every root but still fail to load data needed by the derived/invariant expression.

For each newly admitted root and every active definition that caused discovery, validate enough of the relevant reference path to avoid silently evaluating an unloaded optional navigation as `null`.

V1 rules:

- the registered consumer navigation itself must have a current target that is one of the requested targets after overlay filtering;
- the root must be tracked by the exact DbContext;
- non-null referenced target objects used by relevant direct-navigation dependencies must be tracked by the same DbContext;
- if a required direct reference navigation is `null`, EF must provide authoritative evidence that the reference is loaded; otherwise fail rather than treating unknown as null;
- resolver queries may use `Include(...)`, explicit loading, or prior tracking/fixup to satisfy closure;
- Raffinert does not auto-generate Include paths.

Add a dedicated exception/message for insufficient evaluation closure only if existing exceptions cannot represent it clearly. Do not silently evaluate incomplete roots.

The PriceRate acceptance query should load both `PurchaseOrderLine` and `InvoiceLine` references.

---

# Transaction/concurrency boundary

External discovery proves consumer completeness only for the host's authoritative read boundary.

It does not by itself stop another process from inserting/removing/retargeting a consumer after the discovery query and before this operation commits.

Document this explicitly.

Do not claim database-global serializable consistency unless the host has supplied an appropriate transaction/locking/partition-ownership strategy.

This roadmap does not redesign the existing transaction API. Existing manual policy-aware transaction workflow remains the place for hosts that require stronger transaction isolation.

Do not add distributed locks, CDC, or provider-specific locking hints.

---

# Task 182 — freeze production-shaped failing tests first

Before product implementation, add focused failing EF tests in a new file such as:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/ExternalConsumerDiscoveryTests.cs
```

Use SQLite in-memory with separate seed and operation DbContexts so initial runtime incompleteness is real.

## 182.1 PO price fans out to unloaded links

Database:

```text
PO P price=100
IL1 price=50
IL2 price=25
IL3 price=10
A -> P/IL1
B -> P/IL2
C -> P/IL3
```

Operation DbContext/runtime initially know only:

```text
P
A
IL1
```

B/C/IL2/IL3 are not tracked and not runtime-registered.

Change:

```csharp
P.UnitPrice = 200m;
```

With `DiscoverConsumers(links, x => x.PurchaseOrderLine, ...)` and matching InvoiceLine resolver configured, consistent save must discover B/C, evaluate all rates, materialize, save, and install runtime coverage.

Assert tracked/runtime/database values for A/B/C.

Assert exactly one PO-side resolver query call for target P.

## 182.2 Invoice price fans out to unloaded link

Mirror the scenario for `InvoiceLine.UnitPrice` and `Link.InvoiceLine` discovery.

## 182.3 multiple changed PO lines batch into one resolver call

Change PO1/PO2/PO3 prices in one unit of work.

Assert one resolver call with all three target references and all affected links updated.

## 182.4 whole-set scope short-circuits discovery

Seed complete link runtime and pass `Scope.Complete(links)`.

Assert resolver factory/query is not invoked.

## 182.5 no discovery before central save boundary

Mutate target scalar and assert no query occurs until consistent save/planning begins.

Do not require handlers to call discovery manually.

Commit tests before implementation if repository workflow allows tests-red commits; otherwise keep the first implementation commit mechanically paired with these tests.

---

# Task 183 — compile direct external consumer descriptors

Add an internal model/compiler component, recommended location:

```text
src/Raffinert.Consistency/Model/ExternalConsumerDiscoveryDescriptors.cs
```

Suggested internal record:

```csharp
internal sealed record ExternalConsumerDescriptor(
    IObjectSetDefinition RootSet,
    MemberInfo Navigation,
    Type TargetType,
    MemberInfo TargetMember,
    object Definition);
```

Exact shape may vary but do not expose internal model definitions publicly.

Compile descriptors from merged expression analysis.

Required tests:

- inferred `x.Parent.Value` compiles;
- explicit `DependsOn(x => x.Parent.Value)` compiles identically;
- direct root scalar does not compile a resolver descriptor;
- nested `x.Parent.Owner.Value` is marked unsupported for external discovery;
- collection navigation is unsupported;
- duplicate dependencies dedupe deterministically;
- same direct navigation used by two derived values compiles one capability path but retains both consumers internally if needed for closure/policy applicability.

Do not alter expression completeness rules.

---

# Task 184 — add `DiscoverConsumers` registration

Primary file:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreMappings.cs
```

Implement exactly the frozen public method.

Registration validation:

```text
null roots/navigation/query -> ArgumentNullException
navigation body must be a direct property/field reference rooted at lambda parameter
conversion may be stripped
root itself is invalid
method call/binary/conditional/captured expression invalid
collection navigation invalid
TTarget must be a reference type
same exact root set + member registered twice -> InvalidOperationException
same CLR root type in different ObjectSets -> allowed
```

Store registrations by exact `IObjectSetDefinition` reference + exact `MemberInfo`.

Do not key by type/name/string path.

At EF mapping validation time, fail if a resolver's ObjectSet belongs to another compiled model.

Add PublicAPI.Unshipped entry only for the intended method/API surface.

---

# Task 185 — build batched mutation-specific discovery requests

Add an internal runtime method that receives the frozen captured mutation batch plus the active descriptor set and produces requests without mutating runtime state.

Suggested internal request:

```csharp
internal sealed record ExternalConsumerRequest(
    IObjectSetDefinition RootSet,
    MemberInfo Navigation,
    Type TargetType,
    IReadOnlyList<object> Targets);
```

Algorithm:

```text
for each PropertyChange
    find active eligible descriptors where
        descriptor.TargetMember == change.Member
        descriptor.TargetType accepts change.Instance
    group by exact (RootSet, Navigation)
    add change.Instance target by reference identity
sort deterministically
```

Rules:

- if `ConsistencyScope.Complete(RootSet)` is present, omit requests for that set;
- one target appears once per request;
- one navigation gets one request per operation;
- unrelated changes generate no request;
- object lifecycle/nav-root changes do not themselves generate external target discovery in this wave.

Expose no public "GetRequirements" API in this task.

---

# Task 186 — integrate resolver capability with scope safety

Refactor `ConsistencyPersistencePolicyEngine.CaptureAndValidate` / `ConsistencyEfCoreMappings.GetScopeGaps` so existing non-navigation scope rules stay unchanged while `NavigationConsumerCoverage` can be satisfied by complete resolver capability.

Do not simply delete `NavigationConsumerCoverage` gaps.

For each active policy definition/root set:

```text
if Scope.Complete(set)
    satisfied
else if every active navigation consumer dependency is v1-eligible
        and exact resolver exists for every required direct navigation
    capability satisfied; operation still must execute requests before planning
else
    existing IncompleteConsistencyScopeException before SQL
```

Required regression matrix:

- no scope + no resolvers -> same fail-closed behavior as today;
- only one of two PriceRate navigations registered -> fail;
- both registered -> scope gate passes to discovery stage;
- unsupported multi-hop dependency + resolver for first hop -> still fail;
- complete scope -> passes and performs no discovery;
- relation/projected scope requirements unaffected.

Keep `ConsistencyScopeRequirementKind` unchanged in this wave unless tests prove a public diagnostic addition is necessary.

---

# Task 187 — execute EF discovery and merge ChangeTracker overlay

Add a focused internal component such as:

```text
src/Raffinert.Consistency.EntityFrameworkCore/ExternalConsumerDiscovery.cs
```

For each request:

1. locate exact resolver registration;
2. invoke query factory with typed target references;
3. execute query (`ToList` / `ToListAsync` according to save path);
4. validate every returned root is tracked by the exact DbContext;
5. union relevant already-tracked roots mapped to the exact ObjectSet;
6. apply current-state navigation filter against request targets;
7. exclude `Deleted` roots;
8. keep `Added` roots as business additions, not coverage admissions;
9. reject unknown existing `Modified`/`Deleted` roots not already runtime-registered;
10. dedupe roots by reference and validate runtime key identity;
11. validate evaluation closure;
12. return only unchanged existing roots that require coverage admission plus any already-known roots for completeness accounting.

A resolver returning a superset is allowed; current-navigation filtering determines effective consumers.

A resolver returning zero rows is valid and constitutes host assertion that persisted consumer set is empty for those targets.

Tests must cover:

- zero-consumer resolution;
- safe superset filtering;
- tracked retargeted-away known root excluded;
- Added current consumer remains business add;
- unknown Modified existing root fails closed;
- AsNoTracking/untracked returned root fails;
- duplicate key with different CLR reference fails;
- resolver exception/cancellation occurs before SQL and leaves runtime unchanged.

---

# Task 188 — implement reversible coverage admission in Core

Core remains persistence-agnostic. Add only internal primitives required to plan with operation-scoped existing-root admissions.

Do not add EF references.

Introduce an internal coverage-admission representation distinct from `ObjectAdded`.

Required baseline admission semantics:

```text
register object-set identity/key
initialize source lifecycle state
index navigations
index projections
establish relation baseline membership
NO business policy dispatch
NO ObjectAdded causal origin
NO business lifecycle impact
```

Implement reversible admission using the existing touched-state/rollback/forward-patch infrastructure.

Preferred architecture:

```text
CaptureCoverageRollback(admissions)
ApplyCoverageAdmissionsAsBaseline(admissions)
CaptureCoverageForward(admissions, rollback)
... run normal business PlanDetailed against expanded state ...
RestoreCoverageRollback
```

Do not increment runtime version during temporary planning.

Add focused Core tests proving:

- temporary admission makes reverse navigation lookup see the root;
- after planning returns, live runtime does not contain the admitted root;
- derived/invariant baseline state created by admission is restored on rollback;
- no policy callback/request is emitted merely due admission;
- no ObjectAdded causal origin appears;
- duplicate key/different reference is rejected before state is left changed;
- coverage forward patch can be installed and rolled back atomically.

If existing patch abstractions cannot safely capture baseline admission without global snapshots, extend touched-state capture rather than implementing a second runtime.

---

# Task 189 — create a combined coverage + business binding plan

Add an internal combined plan used by EF persistence. Do not expose it publicly unless unavoidable.

It must bind:

```text
Base runtime version
Coverage admissions/forward patch
Prepared business mutation
Business forward patch
Invariant evaluations
Derived evaluations
Policy actions
```

Planning sequence:

```text
validate admissions
capture/apply temporary coverage baseline
prepare business mutation against expanded runtime state
plan business mutation with existing PlanDetailed semantics
capture exact coverage forward patch
restore runtime to original base state
return combined binding plan
```

Installation after DB durability:

```text
validate same runtime/base version
capture one install rollback boundary covering admissions + business patch
install coverage patch
install business patch
increment version exactly once
mark business prepared/plan committed
return existing RuntimeApplyResult
```

Failure at any installation step restores the original runtime state.

Dispatch invokes only the business policy actions.

Tests:

- planning is non-mutating to live runtime;
- SQL failure leaves runtime unchanged/version unchanged;
- successful install adds discovered roots and business effects together/version +1;
- injected failure after coverage patch but before business patch restores both;
- stale combined plan after unrelated runtime version advance is rejected;
- semantic code is not rerun during commit of the binding plan.

---

# Task 190 — integrate both EF save workflows and dogfood the full scenario

## Convenience save

Refactor `ConsistencyCoordinator.Prepare` / async equivalent in this order:

```text
DetectChanges
store-side/generated-value guards
capture/freeze the business mutation batch BEFORE discovery queries
capture active persistence policy
validate closed/open coverage capability
build discovery requests
execute resolver queries
merge tracker overlay / validate closure
plan with coverage admissions
apply materializations from the combined plan
DetectChanges again
SaveChanges
install combined runtime plan
Dispatch
```

It is critical that entities loaded by resolver queries are not accidentally recaptured as business mutations merely because they became tracked after the original mutation snapshot.

Provide sync and async execution using the same resolver registration/query semantics. Async path must use EF async query execution and propagate cancellation.

## Policy-aware manual unit of work

Preserve generated-key/manual-transaction workflows.

`ConsistencyPersistenceUnitOfWork` must perform discovery during `PrepareAndPlan()` / a new async counterpart only after generated semantic values are proven ready, and before the binding plan is finalized.

If adding `PrepareAndPlanAsync(CancellationToken)` is necessary for async discovery, add it deliberately and update PublicAPI. Do not force users to manually call a separate discovery method that can be forgotten.

The state machine must still fail closed and must not execute SQL itself.

## Dogfood sample

Upgrade `samples/Raffinert.Consistency.DependencyMaintenanceSample` so at least one scenario starts with an intentionally incomplete runtime/DbContext graph and demonstrates external discovery.

Required scenario:

```text
one endpoint price/value changes
multiple persisted consumer roots exist
only one consumer initially known
remaining consumers + opposite endpoints initially unloaded
handler performs only scalar mutation
SaveChangesConsistentlyAsync discovers missing consumers
all mirrors become correct
```

Keep existing selectivity/retargeting proof boundaries accurate. Do not claim the executable proves internal Fresh/Dirty behavior unless it explicitly asserts it.

Update `docs/anemic-model-dependency-maintenance-example.md` with before/after orchestration showing that application handlers no longer collect changed IDs or query dependent links manually.

---

# Task 191 — hardening, API baseline, docs, release proof

## Required negative tests

- missing resolver registration;
- partial resolver capability for a multi-navigation materialized derived value;
- unsupported multi-hop path without complete scope;
- untracked resolver result;
- null required reference not authoritatively loaded;
- duplicate runtime key/different reference;
- unknown modified existing root;
- resolver query throws;
- cancellation;
- SQL failure after successful discovery;
- runtime installation failure after SQL success;
- stale combined plan;
- complete scope prevents resolver query;
- irrelevant registered resolver not called;
- inactive/non-materialized derived definition does not cause discovery.

## Diagnostics

Add lightweight counters only if they materially help prove no N+1 behavior. Preferred minimal additions if needed:

```text
ConsumerDiscoveryRequests
ConsumerDiscoveryTargets
CoverageAdmissions
```

Do not overload existing `AffectedSources`/`RelationPairsAdded` to mean discovery.

Do not expose raw SQL or resolver internals in diagnostics.

## Documentation wording

Document:

> Raffinert can maintain direct-navigation dependent state without requiring every consumer root to be preloaded. At the EF persistence boundary it can batch authoritative reverse-consumer queries supplied by the host, merge the current tracked overlay, and plan against the resulting operation-scoped consumer coverage. Raffinert does not invent database queries and cannot prove that a host query did not under-fetch.

Also document:

> `ConsistencyScope.Complete(set)` remains the closed-world alternative. Targeted discovery does not make the whole set complete.

And concurrency caveat:

> Consumer discovery is authoritative only within the database/read boundary supplied by the host. Concurrent external changes to consumer membership require an appropriate transaction/isolation/partition strategy or later reconciliation.

## Verification

Run at minimum:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Then run the repository's existing package/release-candidate verification exactly as CI does. Do not publish NuGet packages, create tags, or create releases.

Record local proof and remote CI/RC proof according to the repository's existing roadmap convention.

---

# Mandatory acceptance matrix

Before marking Tasks 182–191 complete, every row below must be proven by an automated test or executable dogfood assertion.

| Scenario | Required outcome |
| --- | --- |
| PO endpoint scalar changes; unknown persisted links exist | all persisted current consumers discovered and recalculated |
| Invoice endpoint scalar changes; unknown persisted links exist | all persisted current consumers discovered and recalculated |
| multiple targets same reverse navigation | one batched resolver query |
| whole set declared complete | zero discovery queries |
| resolver returns safe superset | non-consumers filtered by current navigation |
| resolver returns zero consumers | valid complete targeted resolution |
| Added consumer in current UoW | treated as business add, not coverage admission |
| known root retargeted away | excluded from old target effective consumers |
| unknown Modified/Deleted existing root | fail before SQL |
| resolver result untracked | fail before SQL |
| missing evaluation closure | fail before SQL |
| discovery query throws/cancels | no SQL, runtime unchanged |
| SQL fails after discovery/planning | runtime unchanged, admissions not installed |
| SQL succeeds + runtime patch install succeeds | admissions + business effects installed atomically, version +1 |
| runtime patch install fails after SQL | runtime restored, synchronization exception |
| stale plan | rejected |
| unsupported multi-hop navigation | requires `Complete(set)` |
| relation/projected completeness | unchanged from current behavior |
| explicit `DependsOn` path | same discovery behavior as inferred path |

---

# Anti-shortcut rules

The implementation agent must obey all of these.

- **DO NOT** query the database from Core.
- **DO NOT** auto-generate resolver SQL or Includes.
- **DO NOT** mark an ObjectSet complete after a targeted resolver query.
- **DO NOT** require handlers/services to call discovery manually before mutation.
- **DO NOT** use `runtime.Add` to represent persisted roots learned during discovery.
- **DO NOT** emit ObjectAdded policy/causal semantics for coverage admission.
- **DO NOT** permanently mutate runtime coverage before SQL durability.
- **DO NOT** capture resolver-loaded Unchanged entities as business mutations.
- **DO NOT** accept unknown Modified/Deleted existing roots by pretending current state is their historical baseline.
- **DO NOT** infer query completeness from row counts.
- **DO NOT** use CLR type alone as resolver identity.
- **DO NOT** execute one resolver query per target.
- **DO NOT** weaken relation/projected scope requirements.
- **DO NOT** silently support multi-hop/collection discovery in this wave.
- **DO NOT** clear `ContainsExternalState` or alter expression completeness semantics.
- **DO NOT** create a second dependency propagation engine.
- **DO NOT** create a second full runtime/graph implementation.
- **DO NOT** publish packages/releases.

If targeted consumer discovery cannot be implemented by extending existing dependency-path metadata, navigation-index semantics, and reversible forward-patch planning, stop and document the blocker before introducing a parallel architecture.

---

# Expected architecture after this roadmap

```text
ordinary POCO mutation
        ↓
EF ChangeTracker capture
        ↓
active derived/invariant dependency descriptors
        ↓
exact direct reverse-consumer requests
        ↓
+-------------------------------+
| closed-world?                 |
| Scope.Complete(rootSet)       |
+-------------------------------+
        | yes                 | no
        |                     ↓
        |              host-owned resolver
        |                     ↓
        |              persisted consumers
        |                     +
        |              ChangeTracker overlay
        |                     ↓
        |              coverage admissions
        +-----------+---------+
                    ↓
          reversible expanded planning
                    ↓
          existing ImpactResolver /
          NavigationIndexRegistry /
          DependencyGraphRuntime
                    ↓
          derived/invariant evaluation
                    ↓
              materialization
                    ↓
                   SQL
                    ↓
        exact combined patch install
                    ↓
                 dispatch
```

The new layer only resolves open-world reverse-consumer coverage. Once roots are known, existing Raffinert machinery remains the semantic authority.

---

# Definition of done

This roadmap is complete only when the dependency-maintenance dogfood can honestly demonstrate the production-shaped claim:

> A PO/Invoice endpoint can be mutated through an ordinary anemic EF code path without preloading every dependent link. At the central consistency save boundary, Raffinert uses declared dependency paths to request the exact missing reverse-consumer coverage from host-provided EF queries, plans against all affected links, and persists/materializes the correct values without handler-specific changed-ID collection or dependency-query orchestration.

The result must remain fail-closed for unsupported graph shapes and unprovable coverage.
