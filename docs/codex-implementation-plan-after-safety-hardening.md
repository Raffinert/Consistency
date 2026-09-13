# Codex Implementation Instructions — Post-Safety-Hardening Follow-up

Use this document as the implementation plan for the findings in `docs/architecture-review-after-safety-hardening.md`.

Baseline for this plan: `main` after commit `0efe7fa10f4122e708eb43cf6620c59494d7d045`.

## Objective

Preserve the correctness guarantees added by the recent safety-hardening work while fixing the remaining public-contract gaps and removing scalability costs introduced by whole-runtime snapshotting.

The implementation order is deliberate. Do not start with new relation optimizers or broader feature work.

```text
1. Preserve per-source severity in detailed results
2. Make durable source identity canonical and serialization-safe
3. Define safe policy-dispatch failure/retry semantics
4. Correct the outbox/public integration contract and docs
5. Model EF database-success/runtime-sync-failure explicitly
6. Benchmark the current safety machinery
7. Make lifecycle validation/simulation touched-set-only
8. Replace whole-runtime rollback snapshots with scoped transactional state
9. Separate result data from dispatch capability
10. Generalize the dependency pipeline into a real DAG
11. Separate relation query planning from propagation planning
12. Run core behavior tests on both net8.0 and net10.0
13. Modularize large runtime files and archive superseded roadmaps
```

Treat sections 1–5 as release-blocking P0 work. Sections 6–9 are the next implementation wave. Sections 10–13 should follow only after the P0 contracts and safety-cost measurements are stable.

---

# General implementation rules

1. Preserve the original relation predicate as semantic authority. Access/index plans may narrow candidates but must never redefine relation meaning.
2. Preserve the existing `Dirty` versus `Invalid` distinction.
3. Preserve exception atomicity for runtime-owned state: if `Commit(prepared)` throws, runtime-owned state must remain unchanged.
4. Do not weaken Prepare/Commit domain-drift protection.
5. Do not introduce optimizer structures without benchmark evidence consistent with `docs/optimizer-policy.md`.
6. Keep the core package free of EF Core and dependency-injection dependencies.
7. Keep public API additions minimal. Prefer internal implementation types until a consumer-facing abstraction is clearly required.
8. Update `PublicAPI.Unshipped.txt` deliberately for every public API change.
9. Add regression tests before or together with every semantic change.
10. Keep scan and optimized execution semantically equivalent.

Each task below includes an acceptance gate. Do not mark a task complete until its tests and documentation match that gate.

---

# Task 1 — Preserve per-source severity in `RuntimeApplyResult`

## Problem

Internal propagation is now source-scoped, but `RelationRuntime.CreateDetailedResult(...)` groups impacts by definition and collapses all sources to one strongest severity.

A mutation can correctly produce:

```text
source A -> Dirty
source B -> Invalid
```

while public output effectively becomes:

```text
DerivedMutationImpact
    Severity = Invalid
    Sources = [A, B]
```

The same issue exists for invariant impact output.

That destroys precision exactly at the public integration boundary.

## Required behavior

Public detailed results must preserve source-specific severity.

Preferred shape:

```csharp
public sealed record SourceDependencyImpact(
    object Source,
    DependencySeverity Severity)
{
    public SourceIdentity? SourceIdentity { get; init; }
}
```

Then definition-level result objects can carry:

```csharp
IReadOnlyList<SourceDependencyImpact> Sources
```

Alternative acceptable shape: emit multiple impact records per definition, one per severity bucket. Prefer the first option because it scales to future per-source metadata without duplicating definition metadata.

## Files likely involved

- `src/Raffinert.Relations/PolicyActions.cs`
- `src/Raffinert.Relations/Runtime.cs`
- `src/Raffinert.Relations/PublicAPI.Unshipped.txt`
- tests around `RuntimeApplyResult`

## Tests

Add at least:

```text
one derived definition
source A receives Dirty
source B receives Invalid
one MutationSet produces both
```

Assert public detailed output preserves both severities independently.

Repeat for invariant impacts.

## Acceptance gate

No public result can imply `Invalid` for a source whose computed impact was only `Dirty`.

---

# Task 2 — Make `SourceIdentity` actually durable

## Problem

Current `SourceIdentity` contains:

```csharp
string? ObjectSetKey
Type SourceType
object SourceKey
```

and reports `IsDurable` based only on whether the object set is named.

That is not enough for durable queue/outbox use. `SourceKey` can be an arbitrary boxed value, including tuple/anonymous composite shapes whose runtime representation is not a stable cross-process contract. `Type` is also an in-process runtime type object rather than a durable transport identifier.

## Required direction

Separate in-process identity from canonical durable identity.

Introduce a serialization-safe representation such as:

```csharp
public sealed record DurableSourceIdentity(
    string ObjectSetKey,
    string SourceType,
    IReadOnlyList<SourceKeyPart> KeyParts);

public sealed record SourceKeyPart(
    string? Name,
    string Type,
    string Value);
```

Exact naming is flexible, but requirements are not:

- deterministic for the same logical source;
- independent of object reference identity;
- independent of anonymous-type runtime names;
- composite keys represented explicitly by parts;
- culture-invariant formatting;
- documented supported key value types;
- failure is explicit when a key cannot be represented durably.

Do not silently call `ToString()` on arbitrary key objects and call the result durable.

For alpha, it is acceptable to support a conservative set of key types:

```text
string
Guid
integer types
bool
enum
DateOnly / DateTime / DateTimeOffset if needed and canonicalized
simple tuple/composite of supported scalar parts
```

If the existing object-set key validator allows a value type that cannot be serialized canonically, either extend canonicalization or report that the source has no durable external identity.

## Definition identity

For cross-process requests, require both:

```text
DefinitionKey is non-null
Source durable object-set key is non-null
source key is canonically serializable
```

Expose a single property/method that answers this full condition. Do not keep `IsDurable` meaning only “object set has a name.”

## Tests

Cover:

- scalar `Guid` key;
- scalar string key;
- composite key;
- invariant culture behavior;
- unnamed object set;
- unsupported key representation;
- logically identical model rebuilt in a different declaration order.

## Acceptance gate

A durable policy request can be serialized, persisted, deserialized after restart/deployment, and still identify the same logical definition and source without relying on runtime `Type`, anonymous-type identity, ordinal IDs, or object references.

---

# Task 3 — Define policy-dispatch partial-failure and retry semantics

## Problem

Current dispatch marks a prepared/result dispatch as completed before callbacks execute. If callback N throws:

```text
callbacks 1..N-1 may already have executed
callback N failed
callbacks N+1..end did not execute
retry is rejected as already dispatched
```

That is a dangerous ambiguous state.

## Required contract

Make dispatch semantics explicit and testable.

Recommended model: dispatch individual policy actions with stable action identity/status and only mark each action completed after successful invocation.

A simpler acceptable alpha contract is:

- dispatch is ordered;
- on failure, successful prior actions are recorded as completed;
- failed and later actions remain pending;
- retry continues only pending actions;
- a successfully completed action is never invoked twice by retry;
- runtime state is never rolled back because policy dispatch is post-commit.

Do not pretend that arbitrary application callbacks are globally atomic.

## Suggested internal model

```text
PolicyDispatchState
    actions[]
        Pending
        Completed
        Failed(last exception metadata optional)
```

`PreparedMutation` or a dedicated dispatch handle may own this state temporarily. This should later align with Task 9.

## Tests

Configure three callbacks/actions:

```text
A succeeds
B throws
C would succeed
```

First dispatch:

```text
A called once
B called once and throws
C not called
```

Retry:

```text
A not called again
B retried
C runs only after B succeeds
```

Also test all-success one-shot behavior.

## Acceptance gate

A dispatch exception never leaves the caller unable to determine or safely continue the remaining work.

---

# Task 4 — Correct the outbox/public integration contract

## Problem

README currently demonstrates persisting:

```csharp
outbox.Add(request.InvariantId, request.Source, request.Reason);
```

That uses exactly the fields that should remain in-process/local:

- ordinal `InvariantId`;
- object reference `Source`.

## Required change

After Task 2, update the recommended integration example to persist only durable fields.

Conceptually:

```csharp
foreach (var request in result.RepairRequests)
{
    var durable = request.GetDurableIdentity();
    outbox.Add(
        durable.DefinitionKey,
        durable.Source,
        request.Reason);
}
```

Exact API can differ.

Document explicitly:

```text
numeric IDs -> diagnostics/in-process only
object Source -> convenience/in-process only
DefinitionKey + canonical SourceIdentity -> external persistence
```

If the request is not durable, persistence helper/API should fail explicitly rather than silently degrade.

## Files

- `README.md`
- `docs/architecture.md`
- possibly `docs/purchase-order-example.md`
- public XML comments for policy request types

## Acceptance gate

No documentation recommends persisting ordinal IDs or domain object references as durable request identity.

---

# Task 5 — Model EF database-success/runtime-sync-failure explicitly

## Problem

The EF adapter can now reach a distinct failure state:

```text
SaveChanges succeeded
RelationRuntime.Commit(prepared) failed
runtime rollback succeeded
```

The database is ahead of the runtime. This is not an ordinary SaveChanges failure and should not be exposed as if nothing special happened.

## Required behavior

Introduce a dedicated exception or result that distinguishes this state.

Example concept:

```csharp
public sealed class RelationRuntimeSynchronizationException : Exception
{
    public bool DatabaseOperationSucceeded { get; }
    public long RuntimeVersion { get; }
}
```

Do not include the exact shape unless justified, but the consumer must be able to distinguish:

```text
DB failure -> runtime was never committed
runtime synchronization failure after successful DB operation -> application reconciliation/rebuild required
policy callback failure -> runtime already committed
```

For explicit/ambient transactions, retain the documented rule that runtime commit occurs only after the actual transaction commits.

## Reconciliation guidance

Document the safe response to synchronization failure:

```text
1. do not retry the database command blindly
2. rebuild/reconcile runtime state from authoritative domain/database state
3. prepare a new mutation only after reconciliation
```

If practical, expose enough structured context to help the application log/telemetry identify the failed unit of work.

## Tests

Inject a runtime commit failure after successful SQLite `SaveChanges` and assert:

- database row/value is committed;
- runtime version/state did not partially advance;
- adapter throws the dedicated synchronization failure;
- policy dispatch does not run.

## Acceptance gate

Consumers can reliably distinguish “database write failed” from “database write succeeded but relation runtime synchronization failed.”

---

# Task 6 — Benchmark the safety machinery before optimizing it

## Problem

Exception atomicity currently captures a whole-runtime snapshot before every commit.

Snapshotting includes object sets, relation indexes/materialization, navigation indexes, derived caches, invariant state, and diagnostics. A single tiny mutation may therefore cost O(total retained runtime state) in copy/allocation work.

Prepare/commit lifecycle validation also copies set state more than once.

## Required benchmark suite

Add BenchmarkDotNet scenarios that vary retained state while keeping mutation size constant.

Suggested dimensions:

```text
registered objects: 1k / 10k / 100k
materialized pairs: low / medium / dense
number of derived cached sources
single-property mutation
single add/remove
```

Measure:

```text
Apply/Commit latency
allocated bytes
snapshot-related allocation proportion if practical
```

Add a baseline path that measures equivalent mutation without rollback snapshot only inside benchmark/test-only code; do not expose unsafe production mode.

## Acceptance gate

The repository contains measured evidence showing when whole-runtime snapshot cost becomes significant enough to justify Task 8.

---

# Task 7 — Make lifecycle simulation touched-set-only

## Problem

Both validation and prepared-domain validation currently create simulations for all object sets even when a mutation touches one set.

## Required change

Create simulations lazily only for sets referenced by lifecycle mutations.

Conceptually:

```csharp
GetSimulation(set)
    => simulations.GetOrAdd(set, s => new ObjectSetSimulation(s, _sets[s]));
```

Do the same in `PreparedMutation.ValidateDomainState`.

This is low risk and should happen before the larger transactional-state refactor.

## Tests

Existing correctness tests should continue unchanged. Add diagnostics/test hooks only if needed to prove unrelated sets are not copied; avoid public API solely for testing.

## Acceptance gate

Preparing/validating one-set lifecycle work does not clone every unrelated object set.

---

# Task 8 — Replace whole-runtime rollback snapshots with scoped transactional state

Do this only after Task 6 provides measurements.

## Goal

Retain the current exception-atomic contract while making cost proportional primarily to touched runtime structures.

## Preferred architecture

Move from:

```text
capture entire runtime
mutate live state
restore everything on failure
```

toward one of:

```text
A. staged mutation plan
   evaluate fallible domain code first
   build deltas
   apply deterministic state changes

B. scoped undo journal
   record inverse operations only for touched structures
   rollback inverses in reverse order
```

Prefer staged deltas where practical because they create a cleaner boundary between fallible semantic evaluation and deterministic state mutation. A hybrid is acceptable.

## Required coverage

The transactional mechanism must cover:

- object-set add/remove;
- forward/reverse relation indexes;
- exact materialized relation pairs;
- navigation indexes;
- derived caches and states;
- invariant states;
- `LastRelationImpacts`;
- diagnostics counters;
- policy request accumulation.

## Fault-injection tests

Keep and expand current failure tests. Throw from:

```text
relation predicate
key reader/property getter
navigation member reader
derived aggregate selector
custom equality/key operation if supported
```

For every failure assert exact pre-commit state restoration and successful subsequent mutation.

## Performance acceptance gate

For a fixed small mutation, commit cost should no longer scale linearly with unrelated retained runtime state solely because rollback safety is enabled.

---

# Task 9 — Separate `RuntimeApplyResult` data from dispatch capability

## Problem

`RuntimeApplyResult` is described as stable result data but contains an `Action` closure that retains dispatch capability and can retain the prepared mutation/runtime graph.

## Required direction

Separate immutable data from in-process execution capability.

Preferred conceptual API:

```csharp
var application = runtime.ApplyDetailed(mutations);
RuntimeApplyResult result = application.Result;
PolicyDispatchHandle dispatch = application.Dispatch;
```

or:

```csharp
var result = runtime.ApplyDetailed(mutations);
runtime.DispatchPolicies(result.DispatchToken);
```

Choose the smallest clean API.

Requirements:

- result data does not retain the runtime merely to support dispatch;
- serialization of result data has no implication that callbacks can later be executed;
- dispatch state/retry semantics from Task 3 remain supported;
- existing simple `Apply(...)` remains convenient.

## Acceptance gate

Holding a pure result object does not keep an otherwise-unreferenced runtime alive through a hidden delegate.

---

# Task 10 — Generalize the dependency pipeline into a real DAG

Do not implement this before P0 and safety-cost work stabilizes.

## Current ceiling

The model is still structurally centered on:

```text
one relation -> one derived -> invariant
```

The public handle simplification now makes broader composition possible.

## Staged implementation

### 10A — source-only derived values

Support:

```csharp
model.Derived(poLines)
    .Compute(line => line.Quantity * line.UnitRate);
```

### 10B — derived-on-derived

Support composition such as:

```csharp
model.Derived(poLines)
    .Using(receivedQuantity, reservedQuantity)
    .Compute((line, received, reserved) =>
        line.Quantity - received - reserved);
```

### 10C — invariants over multiple upstream values

Support invariants whose correctness depends on more than one derived value.

## Internal requirements

Implement explicit dependency edges and compile:

- cycle detection;
- topological ordering;
- source-scoped work queue;
- impact merging;
- each node processed at most once per wave unless an intentional fixed-point model is later introduced.

Do not solve this by adding `Using` overloads 2–8 as the architecture. Design the internal dependency-node/input model first, then add a small ergonomic typed surface.

## Acceptance gate

A derived value can depend on multiple upstream derived values without inventing fake relations.

---

# Task 11 — Separate relation access plans from propagation plans

## Problem

Using a relation in derived state currently implies exact bidirectional pair materialization.

That is precise but can be expensive for dense relations.

## Required abstraction

Keep two independent concerns:

```text
RelationAccessPlan
    how queries find candidates

RelationPropagationPlan
    how dependency changes discover affected sources
```

Initial propagation modes may be:

```text
ExactMaterialized
ConservativeSourceInvalidation
```

Exact materialization remains required for features such as precise pair deltas and incremental aggregate updates.

Conservative invalidation may be preferable for dense relations where full recomputation on demand is cheaper than storing every pair.

Do not auto-switch based on runtime density in the first implementation. Make the plan explicit/compiled and benchmark both modes.

## Acceptance gate

A full-recompute derived value over a dense relation is not forced to retain O(number of matching pairs) propagation state when a safe conservative mode is selected.

---

# Task 12 — Run core behavior tests on both supported TFMs

## Problem

The core package targets `net8.0;net10.0`, but the combined test project targets only `net10.0` because it also references the EF Core 10 adapter.

## Required structure

Split tests so core behavior executes on both supported core TFMs.

Suggested layout:

```text
tests/Raffinert.Relations.Tests
    TargetFrameworks: net8.0;net10.0
    references core only

tests/Raffinert.Relations.EntityFrameworkCore.Tests
    TargetFramework: net10.0
    references EF adapter + core
```

Move EF-specific tests to the second project.

Keep randomized equivalence and core runtime tests in the multi-targeted project.

## CI acceptance gate

CI visibly executes the core test suite under both `net8.0` and `net10.0` and EF tests under `net10.0`.

---

# Task 13 — Maintainability cleanup after behavior stabilizes

## Runtime file split

The implementation has outgrown a few very large source files. Split by responsibility without changing namespaces/public behavior unnecessarily.

Suggested direction:

```text
Model/
  CompiledRelationModel.cs
  CompiledDiagnosticsBuilder.cs

Runtime/
  RelationRuntime.cs
  MutationPreparation.cs
  MutationCommit.cs
  ObjectSetRuntime.cs
  RelationRuntimeState.cs

Dependencies/
  DependencyGraphRuntime.cs
  NavigationIndexRegistry.cs
  ImpactResolver.cs

Planning/
  RelationAccessPlans.cs
  DerivedComputationPlans.cs

Diagnostics/
  RuntimeDiagnostics.cs
  DebugViewRenderer.cs
```

Do not combine this file movement with semantic changes unless necessary.

## Roadmap cleanup

Move superseded root-level roadmap documents into something like:

```text
docs/roadmaps/archive/
```

Keep the active architecture review and this implementation plan easy to find.

## Acceptance gate

A new contributor can identify the current implementation plan immediately, and source files align with the conceptual architecture closely enough to review one concern at a time.

---

# Required implementation sequence and commit discipline

Use focused commits. Suggested sequence:

```text
fix: preserve source-scoped detailed impact severity
feat: add canonical durable source identity
fix: make policy dispatch resumable after callback failure
docs: use durable identity in outbox guidance
feat: expose EF runtime synchronization failure
bench: measure commit safety overhead
perf: simulate only touched object sets
perf: scope rollback state to touched runtime structures
refactor: separate result data from dispatch capability
feat: support dependency DAG composition
feat: separate relation propagation planning
build: test core on net8 and net10
refactor: modularize runtime and archive historical plans
```

Do not squash unrelated semantic changes into one large refactor.

After each semantic commit run:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test Raffinert.Relations.sln -c Release --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

When public API changes, verify and deliberately update the PublicApiAnalyzers baselines.

---

# Release gate after this plan

Before considering the next alpha baseline ready, all of these should be true:

```text
[ ] detailed impacts preserve severity per source
[ ] durable request identity is canonical and serialization-safe
[ ] callback failure has explicit resumable/known dispatch semantics
[ ] docs never recommend persisting ordinal IDs/object references
[ ] EF adapter distinguishes DB success + runtime sync failure
[ ] commit-safety overhead has benchmark evidence
[ ] lifecycle validation copies only touched sets
[ ] rollback safety no longer requires unconditional whole-runtime snapshots, if benchmarks justify the refactor
[ ] RuntimeApplyResult is data-first and does not hide a runtime-retaining callback capability
[ ] core behavioral tests execute on net8 and net10
[ ] any DAG/propagation-plan expansion is covered by randomized equivalence tests
[ ] public API baseline and README match actual guarantees
```

The priority throughout is **correctness contracts first, measured scalability second, broader expressiveness third**.