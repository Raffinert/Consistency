# Architecture

Raffinert.Consistency compiles expression-defined object relationships into a small runtime dependency
graph. The original expressions remain the semantic authority; access and computation plans are
conservative optimizations around them.

## Model compilation

1. `ObjectSet<T>` defines stable object identity through a non-null key. Supported keys are direct
   scalar/value members or tuple/anonymous composites of direct scalar/value members; navigation,
   method-call, captured/static-state, and collection-derived keys are rejected at model construction.
2. `Relation<TLeft, TRight>` analyzes its predicate for member paths, equality join keys, residual
   semantics, and dependency completeness.
3. Derived expressions add source, relation-membership, related-item, and upstream-derived dependencies.
4. Invariant expressions add dependencies on source state and one or more derived values.
5. `Build()` compiles explicit dependency nodes and edges, rejects cycles, and assigns deterministic
   topological order.
6. `Build()` rejects incomplete cached dependency consumers unless explicitly opted into weaker
   guarantees, chooses access/computation plans, and emits structured diagnostics.

Hash and scan access plans only change candidate lookup. The compiled predicate always filters the
candidate set, and dependency severity is configured independently of the access plan.

## Dependency completeness vs data-scope completeness

Dependency completeness asks whether expression analysis discovered every member and external input that
can affect semantics. It controls whether cached consumers are safe and whether an explicit
`AllowIncompleteDependencies()` opt-in is required. Data-scope completeness instead asks whether a runtime
contains every object needed to make an authoritative cross-object claim. Solving either problem does not
solve the other.

Core derives data-scope requirements from compiled topology. A source-only derived value requires no
complete object set. A relation-backed value requires both relation source and target sets. Same-source
composition inherits the transitive union of upstream requirements. Projected composition inherits those
requirements and adds its consumer set because reverse projection must find every consumer. Invariants
take the deterministic, de-duplicated union of their upstream requirements.

The host supplies proof with `ConsistencyScope.Complete(set)`. The proof is an assertion; Core and the EF
adapter do not inspect a database to verify it. The EF adapter gates only configured persistence policies:
enforced invariants in both save modes and materialized derived values in `RecalculateAndValidate` mode.
It rejects missing coverage before planning, mirror writes, and SQL. Whole-set completeness is intentionally
coarse in this version. Partition- and key-scoped completeness are not implemented.

## Runtime mutation pipeline

`MutationSet` combines object lifecycle, property, and collection signals from one domain operation:

```text
validate + normalize
    -> prepare against runtime version
    -> commit object/navigation/relation state
    -> propagate derived/invariant impacts
    -> return structured requests
    -> dispatch optional application callbacks
```

Domain objects must already contain their new values. The runtime owns indexes and cached dependency
state; it does not mutate or roll back the domain model. Preparation is side-effect-free. Commit rejects
a prepared mutation if another commit advanced `ConsistencyRuntime.Version`.

## Relation propagation and materialization

Direct query-only relations maintain their selected access index but do not retain all matching pairs.
Relation consumers choose a propagation plan independently of query access. Exact propagation retains
bidirectional membership for source-precise deltas and incremental aggregates. An explicit
`PreferConservativePropagation()` is a consumer preference for a full-recompute consumer that retains no permanent pairs and invalidates a safe source
superset. Hash joins use the union of stored old-key and current new-key source candidate buckets for
right-side moves; scan/opaque relations fall back to all registered left sources when narrower routing
cannot be proven.
The original predicate remains authoritative when a lazy value is recomputed. Inspect
`compiled.Diagnostics`, `DebugView`, and `runtime.Diagnostics.Relations` to make this tradeoff visible.

## Derived state and policy

Derived caches transition monotonically among `Fresh`, `Dirty`, and `Invalid` until recomputed.
Derived values may compute directly from a source, consume a relation, or compose one/two upstream
derived values. The alpha projected-composition contract selects a non-null target through one direct
tracked reference member; that
target must belong to the exact upstream object set. Final batch state enforces lifecycle integrity, and
a maintained reverse projection index routes changes in proportion to actual fan-out. Seed construction
establishes the same structural index baseline without a mutation version or policy wave. Impacts
propagate source-by-source in topological order; strongest severity wins while
recomputation remains lazy. Composed builders expose the same direct-source `Impact(...)` configuration
as source-only and relation-backed builders; inherited upstream severity remains monotonic.
Direct source members may additionally declare typed old/new classifiers. Classification uses the
normalized net transition during commit, applies only to members tracked by the computation, and merges
multiple changes with `Invalid` dominance. Classifiers are deterministic, side-effect-free model logic.
Per-derived impact configuration determines whether membership additions/removals and related-item
changes make a cache stale or unusable. Exact standalone `Count`, `LongCount`, parameterless `Any`, and
numeric `Sum` expressions may opt into incremental maintenance; all other expressions use the original
full computation.

Invariant state merges impacts from all upstream derived values and can be marked, evaluated immediately, or represented as a
repair request. `ApplyDetailed` exposes requests as data for an outbox/queue. In-process callbacks run
only through explicit post-commit dispatch.

## EF Core boundary

The EF adapter translates tracked entity lifecycle, scalar/reference changes, and collection resets into
the same core mutation protocol. Relationship evidence is captured before EF change detection can discard
old owned/reference targets. The adapter's `Enforce` and `Materialize` mappings are persistence policy;
the core remains EF-agnostic. Materialized properties are sink-only and cannot feed a Relations key,
relation, derived value, invariant, or projected selector.

The convenience save methods and interceptor validate authoritative data scope and prepare before `SaveChanges`, commit after success, and
dispatch last. They reject ambient/external transactions and store-generated Relations identities. Those
cases require the explicit transaction and captured-unit workflow, with runtime commit only after database
commit. The adapter never auto-loads missing graph state. Cross-object enforcement and materialization
require the host to seed complete runtime coverage and declare it with `ConsistencyScope`; `Map(...)` alone
is change translation, not coverage proof. Full operational details are in
[EF Core consistency](ef-core-consistency.md).

`PreviewDetailed` predicts against the already-mutated, prepared domain state, restores runtime-owned state,
and returns only a `RuntimeApplyResult`. It is diagnostic and non-binding: a later normal commit may execute
semantic code again. Do not use it when durable external work requires exact parity with the later runtime
installation.

`PlanDetailed` executes semantic classification and propagation once, restores runtime-owned state, and
returns a binding `PreparedImpactPlan`. `plan.Result` is the exact detailed result associated with its retained
forward patch. A later `Commit(plan)` installs that patch without rerunning semantic model code. This is the
required contract for same-database atomic outbox work whose rows depend on exact result parity.

When `PlannedInvariantEvaluationMode.Affected` is requested, affected invariant predicates are evaluated
while the reversible planned final state is installed. Their evaluation records and resulting cache state
are captured in the same forward patch. `HasInvariantViolations` can gate external durability, and
`Commit(plan)` installs the precomputed state without rerunning predicates. Discarding a plan restores only
runtime-owned state; callers must rollback or reconcile mutated application objects and EF tracking state.

`HasInvariantViolations` reports violations only among sources evaluated for that plan; it is not a global
scan of every registered invariant source. `InvariantEvaluations` contains propagation-selected affected
sources plus applicable newly added sources. Its `Source` is an in-process reference, so durable workflows
should use `SourceIdentity` data. The caller decides whether a violation blocks persistence and remains
responsible for transaction management. Domain-level `Unknown` behavior is modeled by the Boolean invariant
(for example, mapping Unknown to non-blocking); the planning API itself does not define tri-state semantics.

For application-assigned keys, use the binding sequence:

```csharp
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
unit.Prepare(runtime);

await using var transaction = await context.Database.BeginTransactionAsync();
await context.SaveChangesAsync();

var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal);
if (plan is not null)
    PersistDurablePolicyWork(context, plan.Result.GetDurablePolicyWork());
await context.SaveChangesAsync();

await transaction.CommitAsync();
unit.Commit(runtime);
unit.Dispatch(runtime);
```

For store-generated keys, capture the unit before the first `SaveChanges`, begin the database transaction,
and perform the first save so final keys and relationship fixup are available. Only then call `Prepare(runtime)`
and `PlanDetailed(runtime)`, persist durable work from `plan.Result`, perform the second save, and commit the
database transaction. Finally call `Commit(runtime)` (which installs the retained plan) and `Dispatch`. Do not
prepare while store-generated identities are still temporary or default.

Planning is post-domain-mutation prediction, not a hypothetical what-if overlay.
`RuntimeApplyResult` remains rich in-process impact and causal diagnostic data. `GetDurablePolicyWork()`
strictly projects its policy requests to data-only definition keys, durable source identities, and repair
reasons for outbox or queue scheduling. It rejects the entire batch if any request is not durable. This API
does not declare the complete causal result to be a stable wire format.
Lifecycle object-set entries and projection sources are captured as touched-state journals, so a small plan
does not clone those complete registries. Relation, navigation, and dependency rollback scopes remain
selected from the affected execution graph and preserve exception atomicity.

That ordering assumes stable application-assigned object-set keys. With store-generated keys, capture the
unit before saving, run the first `SaveChanges` inside the transaction, and only then prepare and plan so
final generated keys and relationship fixup become the binding identities. Registered runtime keys never
change. EF reference changes carry the actual tracked old principal when it is unambiguous and fail capture
when it is not; unavailable history is never represented as a real `null` old value.

If a convenience save completes in the database but runtime synchronization fails, the adapter throws
`ConsistencyRuntimeSynchronizationException` with the unchanged runtime version. This state requires runtime
reconciliation/rebuild from authoritative data, not a blind database-command retry. A later policy callback
failure is different: runtime state is already committed and dispatch resumes from the failed action.

Core behavior and API contracts are tested on .NET 8 and .NET 10. The EF Core adapter targets and is
tested on .NET 10.

## Committed impact explanations

Each commit produces an explicit ephemeral relation/dependency/policy result. `ApplyDetailed` converts
that value into immutable summary data; a later mutation cannot alter it. Causal detail is opt-in through
`RuntimeImpactDetailLevel.Causal` and records normalized mutation origins plus direct source, relation,
and immediate-upstream causes. Conservative candidate causes are labeled `Conservative`; transitive
paths are represented by upstream edges rather than copied into every impact. Basic `Apply` constructs
neither public summary arrays nor causal records.

## Durable integration identity

Ordinal definition IDs are compact identifiers scoped to one compiled model and may change when model
declaration order changes. Definitions used across process boundaries should be assigned unique logical
keys with `Named(...)`. Numeric IDs and live `Source` references remain in-process-only. For persistence,
`GetDurableIdentity()` requires both a named invariant and a named source set and returns canonical scalar
key parts, stable type tokens, and invariant-culture values. It throws rather than degrading to ordinal,
object-reference, runtime `Type`, anonymous-type identity, or arbitrary `ToString()` semantics.
