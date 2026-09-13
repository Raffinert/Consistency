# Codex Implementation Plan — Post-DAG Verification Hardening

Status: **Completed 2026-09-13.** This file is retained as execution history, not active instructions.

Baseline: `main` at `db1d3488f05963c97eb806ce8943505609d37d70`.

This document supersedes the previous “Tasks 10–13” execution instructions. The large architecture wave is now substantially implemented: source-only derived values, compiled DAG metadata, derived-on-derived composition, multi-input invariants, independent relation propagation planning, conservative no-pair propagation, multi-target core tests, benchmark coverage, and roadmap archival all exist on `main`.

However, verification against the previous acceptance gates found several important gaps. Do not treat the architecture as finished merely because the corresponding commits exist. The next work is hardening and completing the implemented design before adding another broad feature family.

## Verified current state

Completed and working on the current baseline:

```text
source-only derived values
one- and two-upstream derived composition
single- and two-upstream invariants
compiled dependency graph metadata + cycle detection
exact and conservative relation propagation modes
exact pair materialization only when required by consumers
conservative mode with zero retained relation pairs
exact-vs-conservative benchmark baseline
core tests on net8.0 and net10.0
EF Core tests isolated on net10.0
roadmap archive and updated documentation navigation
```

Current CI for the baseline is green.

The following previous acceptance requirements are **not fully satisfied yet**:

```text
explicit topological runtime propagation
proof that DAG work is order-independent
full conservative-propagation correctness matrix
candidate-scoped conservative routing for right-side changes
randomized DAG + conservative oracle equivalence
physical split of the large runtime/derived/test catch-all files
consistent direct-source severity configuration for composed derived values
an internal dependency input model that cannot represent invalid nullable states
```

Implement the remaining work in this order:

```text
14  Make runtime DAG propagation explicitly topological and deterministic
15  Complete conservative propagation routing and correctness coverage
16  Add randomized DAG/conservative/oracle verification
17  Normalize dependency-input and composed-derived policy contracts
18  Finish source/test modularization and remove dead compatibility paths
19  Run alpha contract/API/performance stabilization
```

Do not start new optimizer families or distributed/runtime features before Tasks 14–19 are green.

---

# Global rules

1. The original relation predicate remains semantic authority.
2. Conservative propagation may over-invalidate but must never miss an affected source.
3. `Invalid` dominates `Dirty` for the same `(node, source)`; different sources retain independent severity.
4. Preserve lazy recomputation. Propagation changes freshness state; it does not eagerly recompute derived values unless an invariant reaction explicitly evaluates them.
5. Preserve commit exception atomicity and Prepare/Commit domain-drift protection.
6. Preserve durable identity, data-only `RuntimeApplyResult`, and resumable dispatch behavior.
7. Core remains free of EF Core and DI dependencies.
8. Public API changes require deliberate `PublicAPI.Unshipped.txt` updates and tests.
9. Do not add `.Using(...)` overloads for arities 3..8 as a shortcut. The internal graph already supports multiple edges; public API growth must be deliberate.
10. Every semantic change must pass both net8.0 and net10.0 core suites.

---

# Task 14 — Make DAG propagation explicitly topological

## Why this is still open

`CompiledDependencyGraph` computes a deterministic topological order, but `DependencyGraphRuntime.ApplyChangeImpacts(...)` currently builds a `HashSet<DerivedNode>` and then iterates that set. The implementation commonly behaves in insertion order on current runtimes, but topological correctness must not rely on `HashSet` enumeration behavior.

The previous acceptance gate was:

```text
topological propagation explicit
```

That is not fully true yet.

## Required change

Treat `_derivedNodes` as the authoritative topological sequence.

Use sets only for membership/selection, not execution order.

Preferred shape:

```text
seed impacted derived nodes into HashSet
expand downstream closure

foreach node in compiled topological derived order:
    if node not impacted: continue
    clear/apply direct + relation impact
    merge inherited upstream impact

seed invariants from impacted derived nodes/direct members
foreach invariant in compiled topological invariant order:
    if invariant not impacted: continue
    apply inherited/direct impact
```

`ExpandDownstream(...)` should not depend on a lucky ordering either. Implement it as an explicit queue/stack over compiled downstream adjacency, or iterate the topological node list until closure is complete in a way whose correctness is obvious.

Use the same closure logic for rollback snapshot selection so commit failure restores every downstream node that could have been touched.

## Source-wave semantics

For each downstream node/source:

```text
merge all upstream causes
Invalid > Dirty
apply state transition once per wave when practical
```

Do not recompute values during propagation.

Diamond example:

```text
A
├─> B ─┐
└─> C ─┴─> D
```

A single source mutation must leave `D` with one merged final severity, independent of whether B or C was discovered first.

## Tests

Add tests where mutation ordering intentionally seeds graph nodes in awkward order:

```text
batch change directly affecting downstream source member first,
then upstream member

same batch in reversed mutation order
```

Assert identical final states and detailed impacts.

Also cover:

```text
3+ level chain
fan-out
fan-in diamond
Invalid + Dirty merge
multiple sources with different severities
unrelated branch remains Fresh
source removal through composed downstream values
rollback snapshot restores every touched downstream node
```

If practical, add an internal test-only graph visit counter and assert a node/source is not processed repeatedly for each incoming edge. Do not add public API only for tests.

## Cleanup

If `ApplyRelationImpacts(...)` is now dead after unified `ApplyChangeImpacts(...)`, remove it rather than maintaining two propagation algorithms.

## Acceptance gate

Runtime execution order is explicitly derived from compiled topological order and no correctness property relies on `HashSet` enumeration order or mutation declaration order.

Suggested commit:

```text
fix: execute dependency propagation in topological order
```

---

# Task 15 — Complete conservative propagation

## Current limitation

The current implementation successfully avoids `_rightsByLeft/_leftsByRight` pair retention, but for conservative right-side changes it frequently does:

```text
right changed/added/removed
 -> affect every registered left source
```

This is safe but leaves substantial performance on the table. A reverse access plan and left-side reverse index already exist, so the engine can usually identify a much smaller safe candidate set without storing exact matching pairs.

The previous Task 11B also required a broader correctness matrix than the current focused tests provide.

## 15A — Introduce conservative candidate routing

The goal is:

```text
no permanent pair materialization
+
no false negatives
+
use candidate buckets when provably safe
```

### Left/source change

The affected left is already known. Affect that source only.

### Right add

After the right object is indexed, use the reverse candidate plan to find lefts sharing the new join key.

The candidate bucket is sufficient; applying the predicate is optional. A superset is acceptable.

If reverse candidate access is unavailable (scan/opaque relation), fall back to all left sources.

### Right remove

Before removing the right from its access index, use the stored indexed key (`_keys[right]`) to retrieve the old left candidate bucket from `_leftIndex`.

Important: the domain object may already be mutated in property-change scenarios, so do not reconstruct the old key from the current object when the runtime already has the old indexed key.

For lifecycle removal the old candidate bucket should normally be available directly.

### Right key/property change

A key move can affect both:

```text
old candidates whose match disappeared
new candidates whose match appeared
```

Capture the old candidate bucket before `ReindexRight(...)`, then capture the new candidate bucket after reindexing. Affect their union.

This avoids the classic false-negative bug of routing only by the new key.

### Residual predicate changes

If join-key access exists and a non-key residual member changes, the same candidate bucket is a safe superset. Do not require exact pair membership.

If no safe candidate plan exists, use all lefts.

### Nested/navigation dependencies

When the affected right relation root is discovered through navigation indexes, use its stored old index key before refresh/reindex and its new key afterward. If old routing cannot be proven safe, broaden to all lefts rather than guessing.

### Derived item dependencies

A derived computation can depend on item members that are not relation-predicate members (for example `Sum(item.Quantity)`). In conservative mode `GetLeftsForRights(...)` must not blindly become “all lefts” when a safe current candidate bucket is available.

For a changed relation key, combine:

```text
old/new relation affected-source candidates
+
current item candidate sources
```

so both membership change and item-value change semantics remain safe.

## 15B — Represent conservative routing explicitly

Do not overload `RelationDelta` with hidden assumptions if that becomes confusing.

A small internal concept is acceptable, for example:

```text
ConservativeRelationImpact
    AffectedLeftCandidates
```

or a relation-state method that captures old/new candidate sets during the commit plan.

The important boundary is:

```text
query access state
!= exact pair membership state
!= conservative candidate-routing state
```

## 15C — Full correctness matrix

Add exact/conservative/fresh-scan comparisons for:

```text
left add
left remove
left predicate-key change
left residual change
right add
right remove
right old-key -> new-key change
right residual predicate change
right item-only derived dependency change
composite join key move
ordinal-ignore-case string key move
nested right navigation member change
right navigation reference replacement
collection/reset-driven relation impact
mixed MutationSet add + remove + property change
multiple right objects changing in one batch
```

For every scenario:

1. exact plan is the precise reference runtime;
2. conservative plan may mark more sources;
3. a source whose observable derived value changes in the oracle must never remain falsely Fresh in conservative mode;
4. after lazy recomputation, exact, conservative, and fresh-scan values are equal.

## 15D — Selectivity benchmark

The current dense benchmark has every source matching every item. Add a complementary selective workload, for example:

```text
10k / 100k sources
100 / 1k distinct join keys
small candidate bucket per key
one right key move
```

Measure:

```text
number of sources invalidated
mutation latency
allocation
lazy read cost
```

Record before/after numbers in `benchmarks/PropagationPlans-Results.md`.

## Acceptance gate

Conservative propagation retains zero exact pairs, proves no false negatives across the required matrix, and uses reverse candidate buckets instead of all-source invalidation whenever the relation plan makes that safe.

Suggested commits:

```text
perf: route conservative propagation through candidate buckets
test: complete conservative propagation oracle matrix
bench: measure selective conservative propagation
```

---

# Task 16 — Randomized DAG and propagation equivalence

## Current gap

`RandomizedFullGraphTests` currently exercises optimized-vs-scan relation behavior around a single relation-backed derived value and invariant. It does not validate the newly added composed DAG or conservative propagation mode.

The previous final gate required:

```text
randomized optimized-vs-oracle tests cover DAG and propagation modes
```

That remains incomplete.

## Required randomized scenarios

Add a deterministic randomized scenario containing at least:

```text
source-only derived A
relation-backed derived B
composed derived C = f(A, B)
second branch D
fan-in E = f(C, D)
multi-input invariant over two downstream values
```

Run equivalent models with:

```text
exact/hash
exact/forced-scan
conservative/hash where legal
conservative/forced-scan where legal
```

Maintain an independent plain-C# oracle that recalculates expected values from current domain objects.

## Operations

Randomize:

```text
source add/remove
source direct member change
right add/remove
right join-key change
right residual change
right aggregate-value change
nested navigation value/reference change
collection reset where applicable
multi-change MutationSet batches
```

Use several fixed seeds. Failures must print seed + operation trace.

## Assertions before recomputation

The conservative runtime does not need the same exact dirty set as exact propagation, but it must satisfy:

```text
if oracle value changed for source S,
then every cached dependent value for S that could be stale is not falsely Fresh
```

Exact and conservative severity may differ only where conservative information is intentionally less precise and the configured semantic policy permits that difference; `Invalid` must never be weakened to `Dirty` when inherited from an invalid upstream.

## Assertions after recomputation

All runtimes converge to the same values and invariant truth values as the oracle.

Also verify:

```text
no duplicate repair request per invariant/source/wave
removed sources leave no derived/invariant cache entries
unrelated branches remain unaffected
```

## Acceptance gate

The newest architecture is covered by deterministic randomized equivalence tests on both net8.0 and net10.0.

Suggested commit:

```text
test: add randomized DAG propagation equivalence
```

---

# Task 17 — Normalize dependency input and policy contracts

Do this after Tasks 14–16 so refactoring is protected by stronger behavior tests.

## 17A — Remove invalid nullable `DerivedInput` states

Current internal shape is effectively:

```csharp
DerivedInput(IRelationDefinition? Relation, IDerivedDefinition? Upstream)
```

which permits invalid combinations:

```text
both null
both non-null
```

Replace it with an internal discriminated shape, for example:

```text
DerivedInput
  RelationInput(Relation)
  UpstreamInput(Derived)
```

or equivalent sealed internal records/interfaces.

Pattern matching should make relation/upstream assumptions explicit.

Do not expose this internal representation publicly.

## 17B — Remove the singular invariant `Derived` compatibility assumption

Multi-input invariants currently retain a singular `Derived` property primarily to obtain source metadata.

Add an explicit invariant `SourceSet` contract and make all runtime/durable-identity/diagnostic code use it.

Keep:

```text
UpstreamDerived[]
```

as the semantic dependency list.

Remove the singular compatibility property if no longer required.

## 17C — Make direct-source severity configurable on composed derived values

Current source-only/relation-backed builders expose impact configuration, but composed derived builders use a hard-coded default impact policy. This matters for domain rules such as:

```text
Quantity decrease
 -> AvailableQuantity must become Invalid
```

when `AvailableQuantity` is itself composed from an upstream derived value plus direct source members.

Support a consistent shape such as:

```csharp
model.Derived(lines)
    .Using(received)
    .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
    .Compute((line, receivedValue) => line.Quantity - receivedValue);
```

and the two-upstream equivalent.

Inherited upstream severity must remain monotonic:

```text
upstream Invalid -> downstream cannot downgrade it to Dirty
```

The configured source severity applies to direct source-member dependencies of the composed expression.

## 17D — Do not expand public arity yet

Keep one/two-upstream public composition for this alpha unless a concrete use case requires more.

The internal graph/input representation must remain arbitrary-edge-capable so adding a better variadic/chain API later does not require another runtime redesign.

Do not add repetitive 3/4/5/6/7/8 generic overload families in this task.

## Tests

Cover:

```text
composed direct-source Dirty
composed direct-source Invalid
Invalid upstream + Dirty direct source => Invalid
multiple upstream severity merge
source identity/durable request source set for multi-input invariant
invalid input shape impossible by construction
```

## Acceptance gate

Internal dependency definitions cannot represent contradictory input kinds, multi-input invariants have an explicit source-set contract, and composed derived values can express the same direct-source correctness severity as source-only values.

Suggested commit:

```text
refactor: normalize dependency input contracts
feat: configure composed derived source severity
```

---

# Task 18 — Finish maintainability cleanup

The previous source-organization commits moved files into responsibility folders, but the main catch-all files remain larger than before the cleanup wave:

```text
Runtime/RelationRuntime.cs      ~77 KB
DerivedState.cs                 ~39 KB
DerivedStateTests.cs            ~66 KB
RuntimeTests.cs                 ~33 KB
```

This does not satisfy the previous acceptance criterion that a contributor should not need to read a ~70 KB catch-all runtime file to locate one responsibility.

This task is behavior-preserving.

## Production split

Recommended direction:

```text
Model/
  CompiledRelationModel.cs
  RelationModelBuilder.cs
  ObjectSets.cs
  DerivedDefinitions.cs
  InvariantDefinitions.cs
  CompiledDependencyGraph.cs

Runtime/
  RelationRuntime.cs              // public orchestration/query surface only
  MutationValidation.cs
  MutationCommit.cs
  RuntimeRollbackState.cs
  ObjectSetRuntime.cs
  RelationRuntimeState.cs
  RelationQueryRuntime.cs

Dependencies/
  DependencyGraphRuntime.cs
  NavigationIndexRegistry.cs
  ImpactResolver.cs

Derived/
  DerivedBuilders.cs
  DerivedRuntimeState.cs
  InvariantRuntimeState.cs
```

Using `partial RelationRuntime` is acceptable when it keeps private state cohesive and avoids artificial helper objects. Do not change the public namespace merely because files move.

Separate `CompiledRelationModel` from `RelationRuntime`.

Move `ObjectSetRuntime`, `RelationRuntimeState`, `CompositeKey`, and equality helpers out of the main runtime file.

Split `DerivedState.cs` into builders/handles, definitions, derived runtime states, and invariant runtime states.

Remove dead methods discovered during the split instead of carrying historical paths forward.

## Test split

Split `DerivedStateTests.cs` by behavior, for example:

```text
DerivedRelationTests.cs
IncrementalAggregateTests.cs
InvariantPolicyTests.cs
DependencyCompletenessTests.cs
```

`SourceDerivedTests.cs` and `DerivedDagTests.cs` already exist; keep new DAG tests there or in focused graph files.

Split `RuntimeTests.cs` into mutation validation, lifecycle, rollback/prepare, and query/runtime API tests.

Avoid a test inheritance framework. Small duplicated setup is preferable to opaque fixture machinery.

## Acceptance gate

Repository folders represent real responsibility boundaries, not just renamed locations; no core catch-all file dominates several independent concerns; and tests are discoverable by behavior.

Suggested commits:

```text
refactor: split runtime responsibilities
refactor: split derived and invariant implementation
 test: organize runtime and derived scenarios
```

---

# Task 19 — Alpha contract and performance stabilization

Do this only after Tasks 14–18.

## Public API review

Because the public surface is still unshipped, now is the cheapest time to fix confusing contracts.

Review at least:

```text
DerivedBuilder / DerivedUsingBuilder / DerivedUpstreamBuilder naming
Incrementally()
PreferConservativePropagation()
Impact(...)
RuntimeApplication / PolicyDispatchHandle
Relation propagation diagnostics names
multi-input diagnostic shapes
```

Do not rename for aesthetics alone. Change only APIs whose meaning is ambiguous or inconsistent after the new architecture.

## Documentation truth pass

Update README and architecture docs so they clearly state:

```text
relation query access plan and propagation plan are independent
conservative propagation retains no exact pair membership
conservative invalidation may be a superset
source-only and composed derived values form a DAG
public composition currently supports one/two upstream values
core behavior runs on net8 + net10
EF adapter remains net10
```

The old Tasks 10–13 wording should no longer read as active implementation instructions after this plan completes.

## Performance gates

Re-run and record:

```text
commit safety benchmarks
exact-vs-conservative dense benchmark
selective conservative benchmark
existing propagation precision benchmark
```

Add a small DAG propagation benchmark if measurements show graph traversal cost is becoming material. Do not optimize the graph without evidence.

## Full validation

Run:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Review package validation and public API analyzer output deliberately.

## Final hardening checklist

```text
[x] runtime DAG execution is explicitly topological
[x] mutation ordering cannot change DAG semantics
[x] diamond severity merges once and Invalid dominates Dirty
[x] conservative right changes use safe candidate routing when possible
[x] old-key and new-key candidate sets are both considered
[x] scan/no-safe-routing fallback remains all-source conservative
[x] no conservative false negatives across deterministic matrix
[x] randomized tests cover composed DAG + conservative propagation
[x] randomized tests pass on net8 and net10
[x] composed derived direct-source severity is configurable
[x] internal derived input representation has no invalid nullable state
[x] multi-input invariant has explicit SourceSet
[x] exact incremental aggregates retain exact propagation semantics
[x] RuntimeApplyResult remains data-only
[x] policy dispatch remains resumable
[x] durable identities still canonicalize/round-trip
[x] failed Commit restores touched runtime state
[x] EF database-success/runtime-sync-failure remains distinct
[x] RelationRuntime/DerivedState catch-all files are split by responsibility
[x] oversized test files are split by behavior
[x] benchmark documentation reflects post-hardening numbers
[x] README/architecture/public diagnostics match actual behavior
[x] CI is green
```

## Recommended commit sequence

```text
1. fix: execute dependency propagation in topological order
2. test: harden DAG ordering and severity merge cases
3. perf: route conservative propagation through candidate buckets
4. test: complete conservative propagation oracle matrix
5. bench: measure selective conservative propagation
6. test: add randomized DAG propagation equivalence
7. refactor: normalize dependency input contracts
8. feat: configure composed derived source severity
9. refactor: split runtime responsibilities
10. refactor: split derived and invariant implementation
11. test: organize runtime and derived scenarios
12. docs: stabilize post-DAG alpha contracts
```

Each commit should build and pass the focused tests relevant to that change. Run the full matrix after each semantic phase (Tasks 14, 15, 16, 17) and before the final documentation commit.

---

# Deferred strategic follow-up — do not implement in this plan

After the alpha-hardening gate, the highest-value product-oriented extension is likely **impact causality/explainability**, not another join optimizer.

Conceptually:

```text
change
 -> affected relation/root
 -> derived A Dirty because ...
 -> derived B Invalid because upstream A ...
 -> invariant C requires repair because ...
```

That can later support diagnostics, audit, admin tooling, and agent-assisted “what will this change affect?” workflows.

Do not start a preview/simulation API yet. The existing mutation model assumes domain state has already changed, so true pre-mutation simulation needs a deliberate overlay/staging design rather than pretending `ApplyDetailed` is a dry run.

# Completion criterion

This hardening plan is complete when the new DAG/propagation architecture is explicitly deterministic, conservative propagation has proven no-false-negative behavior and useful candidate precision, the internal contracts no longer carry legacy single-input assumptions, and the repository structure is maintainable enough to stabilize the first serious alpha API without another architecture rewrite.
