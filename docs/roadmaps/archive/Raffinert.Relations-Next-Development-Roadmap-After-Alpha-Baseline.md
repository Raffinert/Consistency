# Raffinert.Relations — Next Development Roadmap After the Alpha-Readiness Baseline

## Context

The current `main` branch is no longer a proof of concept. It already contains a broad first implementation of the intended architecture:

- typed `ObjectSet<T>` handles with stable keys;
- binary expression-defined relations;
- safe scan/hash access planning and reverse access for exact propagation;
- nested reference and collection navigation tracking;
- compiled null-safe member readers;
- source-scoped `RelationDelta` / `RelationImpact` propagation;
- derived state with `Fresh`, `Dirty`, and `Invalid` states;
- invariant evaluation and post-commit policy dispatch;
- a focused dependency graph runtime;
- explicit property and collection change reporting;
- normalized/validated `ChangeSet` processing;
- EF Core unit-of-work capture and post-save runtime application;
- randomized full-graph optimized-vs-reference testing;
- propagation and range-planning benchmarks;
- CI restore/build/test/format/pack validation;
- NuGet-ready alpha package metadata.

The repository has therefore reached a different phase.

The next work should prioritize:

```text
semantic safety
    -> transaction/integration guarantees
    -> public policy model
    -> incremental derived computation
    -> API stabilization
    -> measured optimizer expansion
```

Do **not** add more optimizer structures simply because the expression analyzer can recognize them. The current range benchmark already demonstrates that the existing equality-prefix plan is dramatically faster than scan for the measured workload, so a dedicated range index is not currently justified.

---

# 1. P0 — Make incomplete dependency analysis safe by construction

**Status:** Implemented.

Model construction now rejects incomplete derived computations, invariant predicates, and relations
materialized for derived propagation. Direct-query-only opaque relations remain valid. Each cached
definition has an explicit `AllowIncompleteDependencies()` escape hatch whose XML documentation and
`DebugView` output state that cached freshness is not guaranteed; incomplete direct-query relations are
identified separately and make no cached-freshness claim.

This is the highest-priority correctness issue before a public alpha.

The analyzer can currently report:

```text
ContainsOpaqueCode
ContainsExternalState
```

but those flags are mostly diagnostic metadata.

That is not enough once derived values and invariants are cached.

Consider:

```csharp
var total = model.Derived(lines)
    .Using(receipts)
    .Compute((line, matches) => OpaqueTotal(matches));
```

where:

```csharp
static decimal OpaqueTotal(IReadOnlyList<Receipt> matches) =>
    matches.Sum(x => x.Quantity);
```

The analyzer cannot see `Receipt.Quantity` through `OpaqueTotal(...)`.

If `Quantity` changes, the runtime may not identify `total` as affected. A previously cached value can therefore remain `Fresh` even though it is stale.

The same fundamental issue exists for mutable captured/static state:

```csharp
var factor = settings.CurrentFactor;

.Compute((line, matches) =>
    matches.Sum(x => x.Quantity) * factor)
```

The runtime has no automatic mutation signal when external state changes.

## Required semantic contract

The library must never silently present a cached value as reliably `Fresh` when its dependencies are known to be incomplete.

Choose and document a strict default.

### Recommended default

For any **cached dependency consumer** (`DerivedState` and `Invariant`) whose analysis is incomplete:

```text
Build() fails by default
```

with a diagnostic explaining why dependency tracking is incomplete.

Relations used only for direct ad-hoc queries may continue to allow opaque predicates because the original predicate is evaluated at query time.

Relations whose membership is materialized for derived-state propagation need stronger rules.

## Escape hatches

Add explicit APIs for deliberate cases.

Possible direction:

```csharp
var total = model.Derived(lines)
    .Using(receipts)
    .DependsOn<Receipt>(x => x.Quantity)
    .Compute((line, matches) => OpaqueTotal(matches));
```

or:

```csharp
.AllowIncompleteDependencies()
```

with clearly weaker guarantees.

For external state, consider an explicit invalidation token/source:

```csharp
var rates = model.ExternalDependency("Rates");

var converted = model.Derived(lines)
    .Using(receipts)
    .DependsOn(rates)
    .Compute(...);

runtime.Invalidate(rates);
```

Do not invent implicit observation of arbitrary captured objects.

## Tests

Add regression tests proving:

1. opaque derived computation is rejected by default;
2. opaque invariant predicate is rejected by default;
3. opaque relation used only for direct querying remains allowed and semantically correct;
4. opaque relation consumed by derived state is rejected or explicitly opted into weaker guarantees;
5. manual `.DependsOn(...)` closes the missing dependency and restores precise invalidation;
6. mutable external state cannot silently preserve `Fresh` without an explicit invalidation contract.

## Completion gate

A public user must be able to understand, from the model API and diagnostics, whether freshness is:

```text
fully tracked
explicitly supplemented
or intentionally not guaranteed
```

---

# 2. P0 — Unify all runtime mutations into one batch model

**Status:** Implemented.

`MutationSet` now unifies object additions/removals, property changes, and collection changes. The
runtime validates and normalizes the entire set before changing runtime-owned state, commits relation
and navigation updates as one logical operation, and performs one dependency/policy propagation pass
after the final state is available. The EF adapter also applies each captured unit of work through one
strictly validated mutation batch.

`ChangeSet` currently batches property changes, while:

```text
Add
Remove
CollectionAdd
CollectionRemove
CollectionReset
```

are separate runtime operations.

This prevents a whole domain/unit-of-work mutation from being validated and committed as one runtime transaction.

It is especially visible in the EF Core adapter: a captured `RelationUnitOfWork` currently applies:

```text
additions
property ChangeSet
collection changes
removals
```

as separate runtime commits.

If one later step fails, earlier runtime-owned state may already have changed.

## Target abstraction

Introduce a broader mutation batch, for example:

```text
RuntimeMutationSet
```

containing:

```text
ObjectAdded
ObjectRemoved
PropertyChanged
CollectionChanged
```

Possible public API:

```csharp
var mutations = MutationSet.Create(
    Change.Add(lines, line),
    Change.Property(...),
    Change.CollectionAdd(...),
    Change.Remove(receipts, receipt));

runtime.Apply(mutations);
```

`ChangeSet` can remain as a convenience property-only API or become a compatibility wrapper over the general mutation set.

## Required behavior

One mutation batch should:

```text
1. validate every mutation
2. normalize repeated member/collection changes
3. compute all navigation/relation deltas
4. prepare all runtime-owned updates
5. commit them as one logical runtime operation
6. produce policy actions
7. dispatch policy actions only after commit
```

No intermediate derived/invariant policy state should be observable between mutations in the same batch.

## Completion gate

A multi-entity domain operation can be represented and applied to the runtime as one atomic runtime-owned mutation.

---

# 3. P0 — Add prepare/commit semantics for external unit-of-work integrations

**Status:** Implemented.

`RelationRuntime.Prepare` performs whole-batch validation without mutating runtime state and returns a
single-use `PreparedMutation` tied to the current monotonically increasing runtime version. `Commit`
rejects stale or foreign preparations, advances runtime state without application callbacks, and
`Dispatch` runs those callbacks afterward. The EF sync and async save helpers now prepare before the
database call, commit only after success, and then dispatch.

The EF adapter correctly captures changes before `SaveChanges` and applies them only after database success.

However, post-database runtime application can still fail because of:

```text
duplicate runtime keys
invalid membership/registration
strict value validation
ambiguous mappings
runtime mutation conflicts
```

At that point the database has already committed and the in-memory relation runtime may require reconciliation.

The README already documents this limitation, but the engine can do better.

## Recommended architecture

Split runtime application into:

```text
Prepare
Commit
Dispatch
```

Conceptually:

```csharp
var prepared = runtime.Prepare(mutations);

await dbContext.SaveChangesAsync();

runtime.Commit(prepared);
prepared.PolicyActions.Dispatch();
```

`Prepare(...)` must perform every validation and deterministic calculation that can fail without mutating runtime-owned state.

`Commit(...)` should be designed to be effectively non-failing under the runtime's single-threaded/external-lock contract.

Application callbacks still remain post-commit and may fail independently.

## EF integration target

```text
DetectChanges
    -> Capture EF unit of work
    -> Prepare Raffinert mutation
    -> database SaveChanges
    -> Commit prepared Raffinert mutation
    -> dispatch policy actions
```

If the database save fails, discard the prepared runtime mutation.

If the runtime is externally modified between prepare and commit, detect the version mismatch and require a new preparation.

## Runtime version

Consider an internal monotonically increasing runtime version:

```text
PreparedMutation.BaseVersion
RelationRuntime.Version
```

so stale prepared mutations cannot be committed accidentally.

## Completion gate

After successful `Prepare`, runtime commit should not discover ordinary model-validation errors after the database has already committed.

---

# 4. P0 — Expose dependency severity as a real public semantic policy

**Status:** Implemented.

`DerivedUsingBuilder.Impact(...)` exposes domain-level severity configuration for membership additions,
membership removals, and changes to related items. `DependencySeverity.Dirty` and `Invalid` map to the
existing monotonic cache-state transitions, mixed deltas select the strongest severity, and tests prove
the behavior is identical for hash and scan access plans. The internal low-level policy remains private.

`Dirty` vs `Invalid` has correctly been decoupled from hash/join/access-plan mechanics.

Today, however, the dependency impact policy is internal and the default relation-membership impact is `Dirty`.

That means the most important domain use case is not yet expressible publicly:

```text
some changes merely make a derived result stale
some changes revoke correctness immediately
```

For PO/Invoice/GR matching, for example:

```text
new/additive candidate
    -> Dirty may be enough

removed/decreased correctness input
    -> existing persisted match may be Invalid immediately
```

## Recommended API direction

Do not expose `IDependencyImpactPolicy` directly as the first public API.

Prefer domain-level configuration attached to the dependent definition.

Example direction:

```csharp
var received = model.Derived(poLines)
    .Using(receipts)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Dirty))
    .Compute(...);
```

or a smaller initial API if necessary.

`RelationImpact` already knows added/removed pairs, so severity can depend on delta direction without knowing the optimizer.

## Keep policy orthogonal to access planning

This must remain true:

```text
Hash vs Scan
```

must never decide:

```text
Dirty vs Invalid
```

## Completion gate

A normal package consumer can configure domain correctness severity without InternalsVisibleTo, custom forks, or optimizer-specific APIs.

---

# 5. P1 — Promote policy actions/repair requests to a public result model

**Status:** Implemented.

`ApplyDetailed` now commits synchronously and returns a public `RuntimeApplyResult` without invoking
application callbacks. It exposes `ChangeImpact`, relation pair deltas, derived/invariant impacts,
deduplicated repair requests, and immediate-evaluation requests using deterministic definition IDs and
public data-only records. `DispatchPolicies()` remains an explicit one-shot convenience; asynchronous
queue/outbox work can consume the request data independently after commit.

Policy execution is now correctly deferred until after runtime-owned state commits.

The next step is to stop making callbacks the only useful integration surface.

Current application-facing behavior still centers on:

```csharp
.ScheduleRepairWith(Action<TSource>)
```

A production system may instead need:

```text
queue a message
schedule background work
record an outbox event
collect repairs and execute later
run asynchronously
```

## Target API

Expose an apply result, for example:

```csharp
RuntimeApplyResult result = runtime.ApplyDetailed(mutations);
```

with public, stable data such as:

```text
ChangeImpact
Affected relations
Derived impacts
Invariant impacts
Repair requests
Immediate-evaluation requests
```

Then offer convenience dispatch separately:

```csharp
result.DispatchPolicies();
```

or a configured dispatcher.

Do not expose internal definition/runtime-state types directly in the public result.

Use stable typed handles/IDs.

## Async policy integration

If async scheduling is introduced later, keep the relation/runtime commit synchronous and deterministic.

Async belongs after commit:

```text
commit
  -> requests
  -> async dispatcher
```

not inside graph propagation.

---

# 6. P1 — Incremental derived computation plans

**Status:** Implemented.

Derived definitions can opt into conservative planning with `Incrementally()`. Exact standalone
`Count`, `LongCount`, parameterless `Any`, and direct non-null numeric `Sum` expressions maintain fresh
cached values from relation membership and item-value deltas in O(delta)/O(1) work. Unsupported shapes
fall back to the original compiled computation. `DebugView` reports the selected plan, and a forced-full
reference path plus randomized mutation tests verify equivalence.

The engine is now good at identifying exactly which sources are affected, but recomputing a derived value still executes the full user computation over:

```csharp
relationState.Related(source)
```

For aggregates such as:

```csharp
matches.Count()
matches.Sum(x => x.Quantity)
```

this means rebuilding/querying the complete related set after every invalidation.

The library now has enough information to do better:

```text
RelationDelta
ExpressionDependencyAnalysis
LINQ semantics
source-scoped impact
```

## Goal

Introduce optional incremental computation plans for recognized safe derived expressions.

Start narrowly.

### First candidates

```text
Count
LongCount
Sum over numeric scalar
Any
```

Example:

```csharp
matches.Sum(x => x.Quantity)
```

can update from:

```text
added relation item
removed relation item
Quantity old -> new
```

without enumerating all matches.

## Architecture

Keep the original compiled computation as semantic authority/fallback.

Conceptually:

```text
DerivedComputationPlan
  FullRecomputePlan
  IncrementalCountPlan
  IncrementalSumPlan
```

Planner selection must be conservative.

If exact incremental equivalence is uncertain, use full recomputation.

## Important rule

Optimization must not change semantics, exactly as with relation access planning.

Add forced-full-recompute equivalence tests analogous to forced-scan relation tests.

## Completion gate

Recognized aggregate derived values can update in O(delta) while remaining equivalent to full recomputation.

---

# 7. P1 — Distinguish cached relation membership from public relation query planning

**Status:** Implemented.

Compiled diagnostics identify `None` versus `ExactPropagation` materialization. Per-relation runtime
snapshots expose deterministic IDs, access/reverse-index entry counts, exact pair counts, average fan-out,
and advisory density warnings. `RuntimeDiagnosticOptions` configures pair/fan-out thresholds without
introducing hard limits, making the O(left × right) memory tradeoff visible to consumers.

Relations consumed by derived state currently enable exact propagation and maintain bidirectional membership.

This is correct, but the runtime should make the memory/performance contract explicit.

Introduce internal diagnostics such as:

```text
Relation materialization: None / ExactPropagation
Forward index bytes/counts
Reverse index entries
Materialized pair count
```

Add to `DebugView` and `RuntimeDiagnostics`.

This matters because a dense relation can contain:

```text
O(left * right)
```

pairs even when ordinary query access is fast.

## Guardrails

Consider configurable diagnostics thresholds—not hard runtime limits initially—for suspicious density:

```text
large materialized pair count
very high average fan-out
```

The user should be able to see when exact propagation trades CPU for substantial memory.

---

# 8. P1 — Strengthen collection semantics

**Status:** Implemented.

Collection dependency membership is now explicitly documented and tested as a reference-identity set:
equal distinct items remain distinct, duplicate references do not create multiplicity, and ordering or
replacement changes use reset/property signals. Coverage includes nested collection paths, detached-item
mutations, shared items across roots, reset/reordering, and collection plus lifecycle changes in one
mutation batch. The obsolete collection-navigation README backlog entry was removed.

Collection navigation is now implemented, so remove it from README `Further work`.

Before considering collection support mature, explicitly define and test:

```text
reference identity vs value equality
set vs multiset semantics
duplicate references
ordered collections
collection replacement
collection reset
nested collection paths
item removal followed by item mutation
same item owned by multiple roots
```

The current navigation index intentionally stores collection membership as reference-identity sets.

Document that contract or add a different abstraction if multiplicity/order is intended to affect dependency semantics.

## Mutation batching

Collection changes should be included in the general mutation batch from section 2 so:

```text
property + collection + lifecycle
```

changes from one domain operation propagate only once.

---

# 9. P1 — Harden the EF Core adapter against real transaction scenarios

**Status:** Implemented.

SQLite in-memory relational coverage now exercises constraint/save failure, rollback and post-commit
manual application, explicit transactions, `SaveChanges(false)`, concurrency conflicts, store-generated
keys, cascade deletion, owned-value changes, many-to-many skip navigations, and repeated saves in one
context. Collection capture now includes modified skip navigations on unchanged principals. The README
documents generated-key timing, the difference between SaveChanges success and transaction durability,
and the manual prepare/commit/dispatch boundary required for externally controlled transactions.

After prepare/commit exists, expand EF integration tests beyond InMemory-style happy paths.

Use SQLite in-memory relational tests for transaction behavior.

Cover:

```text
SaveChanges failure
transaction rollback
explicit transaction
SaveChanges(false) / AcceptAllChanges behavior
concurrency exception
store-generated keys
added entity whose key changes from temporary -> permanent
cascade deletes
owned/complex changes
many-to-many relationship changes
multiple SaveChanges in one DbContext
```

## Store-generated key problem

A major design question is object-set identity for newly added EF entities whose stable key is database-generated.

Current core semantics require a stable non-null key at runtime registration.

Define an explicit integration strategy:

```text
apply additions only after generated key is available
```

which aligns naturally with post-save commit, but must be tested thoroughly.

## Database transaction boundary

If `SaveChanges` participates in an externally controlled transaction that has not committed yet, successful `SaveChanges` does **not** necessarily mean durable database commit.

Document the difference between:

```text
SaveChanges succeeded
```

and:

```text
ambient/explicit transaction committed
```

Longer term, provide integration guidance/API for applying prepared runtime changes after the actual transaction commit boundary when needed.

---

# 10. P1 — Public diagnostics and explainability

**Status:** Implemented.

`CompiledRelationModel.Diagnostics` exposes immutable structured records for object sets, relations,
derived values, and invariants, including deterministic IDs, dependencies, plans, completeness,
materialization, LINQ semantics, and configured policy behavior. `RuntimeDiagnostics` now counts
predicate evaluations, reindexed/affected roots, pair additions/removals, full recomputations,
incremental updates, and emitted policy requests since reset, alongside per-relation density data.
`DebugView` remains the human-readable view of the same model concepts.

`DebugView` is useful, but the engine now has enough planning/propagation machinery that structured diagnostics will be valuable.

Consider public read-only diagnostics models for:

```text
Object sets
Relations
Access plans
Dependency completeness
Derived dependencies
Invariant dependencies
Materialization mode
LINQ semantics
```

Possible API:

```csharp
CompiledModelDiagnostics diagnostics = compiled.Diagnostics;
```

Keep `DebugView` as the human-readable rendering of the structured model.

Runtime diagnostics should expose counters relevant to incremental behavior:

```text
predicate evaluations
reindexed roots
affected source roots
relation pairs added/removed
derived full recomputations
incremental derived updates
policy requests emitted
```

This makes performance regressions observable without BenchmarkDotNet.

---

# 11. P1 — API compatibility baseline before alpha publication

**Status:** Implemented.

Both package projects now run `Microsoft.CodeAnalysis.PublicApiAnalyzers` under the existing
warnings-as-errors build and carry checked-in nullable-aware public API baselines. Signature additions,
removals, and changes require an explicit baseline update and are therefore visible in review/CI. The
current fluent surface was reviewed; `ObjectSetBuilder<T>` intentionally remains public as the transient
type-safe key-configuration stage that produces the stable `ObjectSet<T>` handle.

The packages now have `0.1.0-alpha.1` metadata but publication is intentionally disabled.

Before publishing, explicitly review the public API surface.

## Add API approval tests

Use a mechanism such as:

```text
PublicApiAnalyzers
```

or another simple checked-in API baseline.

The goal is not to freeze the API permanently; it is to make changes deliberate.

Review names and shapes including:

```text
RelationModelBuilder
ObjectSetBuilder<T>
ObjectSet<T>
Relation<TLeft,TRight>
Derived<TSource,TItem,TValue>
Invariant<TSource,TItem,TValue>
Change
ChangeSet / future MutationSet
ChangeImpact
RuntimeDiagnostics
InvariantReaction
```

## Decide whether builder is worth keeping public

Current usage:

```csharp
var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
```

already returns `ObjectSet<T>` after `.Key(...)`, so the builder is mostly a fluent transient type.

That is fine, but decide intentionally before alpha.

---

# 12. P2 — Multi-targeting decision

**Status:** Implemented.

The dependency-free core now targets both `net8.0` and `net10.0`; the implementation audit found no
.NET 10-only dependency in its public/runtime behavior. The EF adapter remains on `net10.0` with EF Core
10, while tests and benchmarks run on .NET 10. CI build/pack compiles and packages both core target
frameworks, and the README records this support policy.

The project currently targets `.NET 10` only.

Before broader OSS adoption, decide intentionally whether the core should target:

```text
net10.0 only
```

or also a stable lower target such as:

```text
net8.0
```

Do not multi-target automatically.

First check whether implementation features actually require .NET 10.

The core library is conceptually useful to applications that may not upgrade immediately, while the EF adapter can independently target compatible EF versions.

Document the support policy either way.

---

# 13. P2 — NuGet/release hardening

**Status:** Implemented.

Both packages now use deterministic/CI build settings, current GitHub SourceLink, embedded repository
URL/branch/commit metadata, MSBuild package validation, and portable `.snupkg` symbol packages. CI uploads
both package kinds but has no publication step. `CHANGELOG.md` contains alpha release notes, while
`RELEASING.md` defines prerelease/SemVer policy, API-baseline promotion, verification, tagging, and a
manual scoped-key NuGet publication checklist.

Before the first public package release, add:

```text
SourceLink / repository commit metadata
symbols package (.snupkg)
deterministic build settings
release notes
package validation
versioning policy
```

Consider `dotnet package validate` / API compatibility validation as appropriate.

Keep publication manual for the first alpha.

Do not auto-publish every `main` commit.

---

# 14. P2 — Documentation cleanup and one end-to-end domain example

**Status:** Implemented.

The README backlog and feature summary are current and now route detailed material to `docs/`.
`docs/architecture.md` describes compilation, mutation phases, exact materialization, dependency policy,
diagnostics, and the EF transaction boundary. `docs/purchase-order-example.md` presents the complete PO
line/goods-receipt quantity flow with explicit `MutationSet`/structured repair handling and the EF adapter.
Executable end-to-end tests prove quantity update, cancellation, exact source impact, invalidation/repair,
and equivalent EF-driven propagation.

The current README `Further work` still lists collection navigation although collection navigation is implemented.

Clean stale roadmap text before alpha.

Then add one end-to-end example that demonstrates why the library exists.

Recommended PO / Invoice / Goods Receipt example:

```text
PO line
  -> matching goods receipts relation
  -> ReceivedQuantity derived value
  -> quantity invariant
  -> change propagation after GR quantity update/cancellation
  -> exact affected source
  -> optional invalidation/repair request
```

Show both:

```csharp
explicit Change/MutationSet
```

and:

```csharp
EF Core adapter
```

without turning README into a full manual.

Move detailed architecture to `docs/` once the public API stabilizes.

---

# 15. P2 — Continue optimizer work only from measured workloads

**Status:** Implemented (decision and guardrail; no speculative index added).

The existing equality-prefix/range-residual measurement remains decisive, so no range tree was added.
`docs/optimizer-policy.md` records the decision and requires a reproducible domain workload, current-plan
and forced-scan baselines, selectivity/allocation/materialization evidence, an operationally meaningful
threshold, structured plan diagnostics, and randomized mutation equivalence before any future access
plan is accepted. Candidate workloads remain explicitly hypotheses until measured.

The existing range benchmark gives a useful precedent:

```text
10k rules: equality-prefix + range residual ~146x faster than scan
100k rules: equality-prefix + range residual ~180x faster than scan
```

Therefore do **not** implement a range tree now.

Future access-plan work should start with a benchmark reproducing a real bad case.

Possible future candidates only when measured:

```text
pure range relation with no selective equality prefix
very low-cardinality equality prefix
ordered/top-N relation usage
prefix/string matching
```

Every optimizer addition must preserve:

```text
optimized result == semantic reference result
```

under randomized mutation sequences.

---

# Recommended execution order

Execute future work in this order:

```text
1. Safe incomplete-dependency contract
2. Unified mutation batch across property/collection/lifecycle changes
3. Prepare/commit runtime mutation semantics
4. EF adapter prepare-before-save / commit-after-success integration
5. Public dependency severity configuration
6. Public policy-action / repair-request result model
7. Incremental derived computation plans (Count/Sum/Any first)
8. Relation materialization diagnostics and density visibility
9. Collection semantic hardening
10. EF relational/transaction integration tests
11. Structured model/runtime diagnostics
12. Public API approval baseline
13. Target-framework support decision
14. NuGet/release hardening
15. Documentation cleanup + end-to-end PO/Invoice/GR example
16. Only then reconsider additional access plans from measured workloads
```

---

# Next Codex task — implement only this

The next Codex task should be intentionally narrow:

> Make incomplete dependency analysis safe for cached derived state and invariants.

## Scope

1. Add model-build validation for dependency completeness of:
   - derived computation expressions;
   - invariant predicates;
   - relations that are consumed by derived state/materialized exact propagation.

2. By default reject a model where cached freshness cannot be maintained because analysis contains:

```text
ContainsOpaqueCode
ContainsExternalState
```

unless an explicit supported escape hatch is configured.

3. Keep opaque relations that are used **only for direct relation queries** valid and scan-backed.

4. Add the smallest reasonable explicit escape hatch for the prototype.

Preferred first implementation:

```text
AllowIncompleteDependencies()
```

with clearly documented weaker guarantees.

Do **not** implement full manual `.DependsOn(...)` or external dependency tokens in the same task unless they fall out trivially from the design.

5. Extend `DebugView` so incomplete/allowed dependency tracking is obvious.

6. Add regression tests proving:
   - opaque derived computation rejected by default;
   - captured/external derived dependency rejected by default;
   - opaque invariant rejected by default;
   - direct-query opaque relation remains valid;
   - relation consumed by derived state respects the stricter rule;
   - explicit opt-in permits build but does not falsely claim complete tracking.

7. Keep all existing optimized-vs-reference tests green.

8. Run CI-equivalent commands locally:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test Raffinert.Relations.sln -c Release --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build
```

## Explicit non-goals for this task

Do not implement yet:

```text
general MutationSet
prepare/commit
manual DependsOn expressions
external invalidation tokens
incremental Sum/Count plans
range indexes
async repair dispatch
```

The purpose of the next commit is to close a semantic correctness hole before extending the public surface further.

---

# Review summary

The project has crossed the line from experimental relation indexing into a real incremental dependency/consistency engine.

The most important remaining risk is no longer lack of features. It is whether the library can make strong, understandable guarantees when expression analysis is incomplete and when runtime state is synchronized with an external transaction.

The next architectural milestone should therefore be:

```text
No silent stale Fresh state
+
One mutation transaction across all change kinds
+
Prepare-before-external-commit semantics
```

After those guarantees are solid, incremental aggregate computation and public policy semantics become the highest-value capability expansions.
