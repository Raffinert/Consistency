# Architecture

Raffinert.Relations compiles expression-defined object relationships into a small runtime dependency
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
a prepared mutation if another commit advanced `RelationRuntime.Version`.

## Relation propagation and materialization

Direct query-only relations maintain their selected access index but do not retain all matching pairs.
Relation consumers choose a propagation plan independently of query access. Exact propagation retains
bidirectional membership for source-precise deltas and incremental aggregates. An explicit
`Conservatively()` full-recompute consumer retains no permanent pairs and invalidates a safe source
superset. Hash joins use the union of stored old-key and current new-key source candidate buckets for
right-side moves; scan/opaque relations fall back to all registered left sources when narrower routing
cannot be proven.
The original predicate remains authoritative when a lazy value is recomputed. Inspect
`compiled.Diagnostics`, `DebugView`, and `runtime.Diagnostics.Relations` to make this tradeoff visible.

## Derived state and policy

Derived caches transition monotonically among `Fresh`, `Dirty`, and `Invalid` until recomputed.
Derived values may compute directly from a source, consume a relation, or compose one/two upstream
derived values. Impacts propagate source-by-source in topological order; strongest severity wins while
recomputation remains lazy. Composed builders expose the same direct-source `Impact(...)` configuration
as source-only and relation-backed builders; inherited upstream severity remains monotonic.
Per-derived impact configuration determines whether membership additions/removals and related-item
changes make a cache stale or unusable. Exact standalone `Count`, `LongCount`, parameterless `Any`, and
numeric `Sum` expressions may opt into incremental maintenance; all other expressions use the original
full computation.

Invariant state merges impacts from all upstream derived values and can be marked, evaluated immediately, or represented as a
repair request. `ApplyDetailed` exposes requests as data for an outbox/queue. In-process callbacks run
only through explicit post-commit dispatch.

## EF Core boundary

The EF adapter translates tracked entity lifecycle, scalar/reference changes, and collection resets into
the same core mutation protocol. The convenience save methods prepare before `SaveChanges`, commit after
success, and dispatch last. For an externally controlled database transaction, use the captured unit of
work manually and commit runtime state only after the actual database transaction commits.

If a convenience save completes in the database but runtime synchronization fails, the adapter throws
`RelationRuntimeSynchronizationException` with the unchanged runtime version. This state requires runtime
reconciliation/rebuild from authoritative data, not a blind database-command retry. A later policy callback
failure is different: runtime state is already committed and dispatch resumes from the failed action.

Core behavior and API contracts are tested on .NET 8 and .NET 10. The EF Core adapter targets and is
tested on .NET 10.

## Durable integration identity

Ordinal definition IDs are compact identifiers scoped to one compiled model and may change when model
declaration order changes. Definitions used across process boundaries should be assigned unique logical
keys with `Named(...)`. Numeric IDs and live `Source` references remain in-process-only. For persistence,
`GetDurableIdentity()` requires both a named invariant and a named source set and returns canonical scalar
key parts, stable type tokens, and invariant-culture values. It throws rather than degrading to ordinal,
object-reference, runtime `Type`, anonymous-type identity, or arbitrary `ToString()` semantics.
