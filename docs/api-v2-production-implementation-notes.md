# API v2 production implementation notes

API v2 is the declaration and consumption facade over the existing consistency engine. It does not introduce
a second graph or execution runtime.

## Production primitive mapping

| Candidate concept | Existing production primitive | Production change |
| --- | --- | --- |
| `Derived(set)` | `ConsistencyModelBuilder`, `ObjectSet<T>` and stable set definition identity | Add staged `DependsOn`, `From`, and `Select` entry points. |
| `DependsOn(path)` | Expression dependency analysis and tracked member paths | Compose declared paths with inferred dependencies and permit opaque method groups only when paths are declared. |
| Local `From(handle)` | Composed derived definitions and compiled DAG ordering | Add the v2 alias and preserve exact object-set and model identity checks. |
| Projected `From(selector, handle)` | Projected derived inputs and reverse projection indexes | Add the v2 alias and a mixed projected/local stage without an adapter graph node. |
| Relation `From(relation)` | Relation-backed derived definitions | Add a relation-valued stage exposing recognized aggregate operators. |
| `Sum`, `Count`, `LongCount`, `Any` | Existing incremental aggregate planners | Compile recognized operators directly to their existing plans without requiring `.Incrementally()`. |
| `Impact` | `DerivedImpactPolicy` and typed source-member classifiers | Retain dependency-specific semantic diagnostics and apply group policy to each represented dependency edge. |
| `MaterializeTo` | Derived definition identity plus compiled property access | Add one Core-owned descriptor containing exact source set, target, read/write delegates, and equality behavior. |
| `Evaluate` | Existing cache state machinery | Expose the non-writing logical read operation. |
| Targeted/object `Materialize` | Derived state lookup and runtime object-set membership | Add exact-target and indexed source-object synchronization with evaluate-first/write-second rollback. |
| Invariant `From` | Existing invariant derived-value composition | Add a thin alias; `Must` and repair scheduling remain separate. |
| EF materialization | Existing affected-derived persistence planning | Consume Core descriptors automatically. |
| Logical diagnostics | Existing compiled model IDs and debug view | Retain direct, local-derived, projected-derived, and relation-valued edge kinds plus materialization metadata. |

## Runtime semantics

`Evaluate` returns a Fresh logical value and never writes a domain property. Targeted `Materialize` evaluates
one definition and synchronizes only its configured target. Object `Materialize` uses runtime-maintained
instance-to-object-set membership and object-set-to-descriptor indexes, so lookup is O(1) plus the number of
configured targets for that source membership rather than a model scan.

Object materialization prepares every logical value before its first physical write. If a setter fails,
already-written physical values are restored while successfully prepared logical caches remain Fresh for a
retry. Equal targets are not assigned. Physical scope never expands to projected dependency objects, and
materialization does not dispatch repair callbacks.

The runtime remains single-threaded by contract. These rollback semantics do not claim cross-thread atomicity.

## Validation and diagnostics

The implementation rejects wrong-set and cross-model handles, invalid projected ownership, duplicate targets
within the same exact object set, non-direct or non-writable targets, mirror properties reused as logical source
dependencies, incomplete opaque calculators, targeted materialization of runtime-only values, and unknown
source instances.

`CompiledConsistencyModel.Diagnostics` exposes semantic dependency kinds and materialization descriptors.
`DebugView` includes a logical API-v2 section with stable names, `depends-on`, `from`, `projection`, recognized
operator plan, and `materializes-to` entries. Existing low-level diagnostics remain available.

## API surface

The public declaration vocabulary is `From`, `DependsOn`, `Select`, recognized aggregate operators,
`MaterializeTo`, and invariant `From`/`Must`. Pre-v2 compatibility aliases were removed for the `0.2` line;
all declarations continue to compile into the same production definitions and execution plans.
