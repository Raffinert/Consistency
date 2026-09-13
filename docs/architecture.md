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
3. Derived expressions add source, relation-membership, and related-item dependencies.
4. Invariant expressions add dependencies on source state and a derived value.
5. `Build()` rejects incomplete cached dependency consumers unless explicitly opted into weaker
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

## Exact propagation and materialization

Direct query-only relations maintain their selected access index but do not retain all matching pairs.
A relation consumed by derived state enables `ExactPropagation`, including reverse access and
bidirectional membership. That enables source-precise invalidation and O(delta) aggregate maintenance at
the cost of O(left × right) memory in a dense relation. Inspect `compiled.Diagnostics`, `DebugView`, and
`runtime.Diagnostics.Relations` to make this tradeoff visible.

## Derived state and policy

Derived caches transition monotonically among `Fresh`, `Dirty`, and `Invalid` until recomputed.
Per-derived impact configuration determines whether membership additions/removals and related-item
changes make a cache stale or unusable. Exact standalone `Count`, `LongCount`, parameterless `Any`, and
numeric `Sum` expressions may opt into incremental maintenance; all other expressions use the original
full computation.

Invariant state inherits derived impacts and can be marked, evaluated immediately, or represented as a
repair request. `ApplyDetailed` exposes requests as data for an outbox/queue. In-process callbacks run
only through explicit post-commit dispatch.

## EF Core boundary

The EF adapter translates tracked entity lifecycle, scalar/reference changes, and collection resets into
the same core mutation protocol. The convenience save methods prepare before `SaveChanges`, commit after
success, and dispatch last. For an externally controlled database transaction, use the captured unit of
work manually and commit runtime state only after the actual database transaction commits.

## Durable integration identity

Ordinal definition IDs are compact identifiers scoped to one compiled model and may change when model
declaration order changes. Definitions used across process boundaries should be assigned unique logical
keys with `Named(...)`. Policy requests then include the invariant `DefinitionKey` and a `SourceIdentity`
formed from the named source object set, its CLR type, and the registered stable object key. Only a
`SourceIdentity` whose `IsDurable` property is true is suitable for persistence across deployments.
