# Allocation consistency dogfood fourth-pass findings

## Fixed by framework changes

- Relation aggregate policies now support strongly typed directional item-member classifiers. Fulfillment and
  allocation quantity increases classify as `Invalid`; decreases classify as `Dirty`; membership add/remove
  policies remain independent.
- Repairability is immutable model metadata configured with `RepairWhenViolated()`. A repair request is emitted
  only after an affected repairable invariant is evaluated and its predicate returns false. Safe capacity
  increases and safe quantity decreases therefore produce no repair work.
- Core exposes structured repair data through `ApplyDetailed(...).Result.RepairRequests`. An ordinary EF save
  rejected by an enforced invariant exposes the relevant requests through
  `ConsistencyInvariantViolationException.RepairRequests`. The compiled model contains no application callback
  or mutable repair queue; the allocation application translates requests into local repair requirements and
  owns replacement selection.
- `FromMembership(candidateSupplies, allocation => allocation.Demand, allocation => allocation.Supply)` reuses
  the existing candidate relation and its indexes. Allocation compatibility has one semantic authority: the
  candidate relation predicate.
- Projected membership tracks both selected references and changes that alter the underlying relation. Its
  derived value participates in invariant propagation; causal relation causes identify the underlying relation
  plus both projection selector paths.
- The rich `Supply.ChangeCapacity` method remains Raffinert-free. EF continues to observe ordinary tracked
  mutations at the `SaveChangesAsync` boundary.

## Eager repair proof

`RepairWhenViolated()` is not merely a label on a future handler. To prove whether repair is needed, Raffinert
evaluates every affected source of a repair-enabled invariant during mutation application or planning. Upstream
derived values needed by the predicate therefore become `Fresh` even when the incoming dependency impact was
`Dirty` or `Invalid`. A request is emitted only when that evaluated predicate is false, and at most once per
invariant/source in one operation result.

An invariant without `RepairWhenViolated()` retains its configured lazy reaction semantics unless another
explicit policy requests evaluation, such as EF enforcement or `EvaluateImmediately`.

## Still awkward

- Structured requests identify a framework invariant and source; the application still translates that generic
  identity into a domain-specific repair requirement before choosing a replacement.
- Core POCO callers must report mutations explicitly, while EF callers rely on tracked changes and save-time
  translation. The distinction is honest but requires two integration habits.
- The full-runtime reseed remains as a correctness/performance baseline, but the rejected-save scenario now uses
  the experimental proposed-state view. The application must obtain a fresh view after each repair mutation.
- The compact projected-membership declaration is clear for this case, but larger models still need explicit
  names and diagnostics to make cross-object ownership easy to follow.
- Causal traces identify definitions, members, relation pairs, and sources. They do not yet include the evaluated
  business operands that explain a numeric failure in domain terms.

## Repair reason semantics

`RepairRequestInfo.Reason` is the severity propagated by the current operation, not a projection of the
invariant's previous cached state. An unevaluated invariant therefore reports `Invalid` for an invalid incoming
impact and `Dirty` for a dirty incoming impact. Multiple causes for one invariant/source pair merge with
`Invalid` dominating `Dirty`; request deduplication remains scoped to one operation.

## Proposed-state repair gap

The EF tracked graph already contains proposed CLR values when Raffinert plans a save. If an enforced invariant
rejects that plan, the committed runtime intentionally remains at the durable baseline, but application repair
still needs relation, derived-value, and invariant queries against the proposed tracked state. The current
baseline solution creates a second runtime and seeds it from every object in `DbSet.Local` before running the
repair query. That is correct for this complete tracked graph, but it repeats O(graph) object admission and index
construction. The fourth-pass experiment compares that baseline with a read-only proposed-state view backed by
the rejected prepared plan. The full-runtime reseed remains in place as the baseline comparison path.

## Proposed-state experiment result

The internal preview reuses the prepared plan's final object-set overlay and current proposed CLR values. It builds
isolated derived/invariant query caches and evaluates relations against the final proposed object membership and
relation predicates. It does not install the forward patch into the committed runtime, rebuild all relation indexes,
dispatch policy work, materialize mirrors, or own a repair workflow. Runtime version, committed relation/index
state, and committed derived/invariant state remain unchanged. Runtime advancement, prepared-domain drift, or a
relevant EF tracked mutation invalidates the view deterministically.

The rejected EF flow is now:

```text
tracked mutation
  -> prepare binding plan
  -> enforced invariant rejection
  -> scoped session retains rejected plan + mutation fingerprint
  -> application obtains proposed-state view
  -> application selects and mutates one replacement
  -> fresh preview/re-plan on the next attempt
  -> SQL success
  -> committed runtime plan installation and dispatch
```

The application still owns candidate selection, ordering, unresolved behavior, and convergence. The baseline
`ProcessCurrentGraph(...)` full reseed path remains for comparison and future fallback testing.

### Benchmark result

Five measured iterations after one warm-up produced these representative medians on the local development
machine. Setup is the time to create a query context; query is the first remaining-capacity query.

| graph size | full reseed setup/query | preview setup/query | full allocated bytes | preview allocated bytes |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 0.503 / 0.025 ms | 0.016 / 0.026 ms | 455,256 | 6,000 |
| 1,000 | 4.148 / 0.069 ms | 0.016 / 0.078 ms | 4,126,144 | 13,200 |
| 10,000 | 37.890 / 0.363 ms | 0.025 / 0.651 ms | 39,923,760 | 85,200 |

Preview creation and allocations are materially smaller and nearly independent of graph size. Its first relation
query is slightly slower at 10,000 because the prototype scans proposed candidates rather than maintaining a
second index. This is sufficient evidence to keep the experiment, but not to graduate a public API before query
workload and relation-overlay semantics are broadened.

## API decision: KEEP EXPERIMENTAL

The proposed-state view removes full graph reseeding from the rejected-save dogfood path, reuses prepared-plan
validation and lifecycle evidence, and has explicit isolation and staleness tests. It remains internal/experimental:
the prototype intentionally has only `Evaluate`, `Related`, and state reads; it has no materialization, dispatch,
persistence, transaction, or workflow behavior. A future public API should be designed only after measuring real
repair query workloads and deciding whether indexed relation overlays are needed.

## Fundamental limitations

- `ConsistencyScope.Complete(...)` is a host assertion about loaded coverage, not proof that a query fetched every
  database row. The runtime cannot detect an under-fetching host.
- The dependency-free core runtime cannot observe arbitrary POCO mutation without a reported `Change`; that is
  the responsibility of a mutation source or adapter.
- Replacement ranking, convergence policy, and unresolved-repair workflow remain application concerns. Core
  consistency does not become a workflow engine.
- Persistence can enforce the configured invariant and preserve runtime/SQL transaction boundaries, but it
  cannot infer untracked SQL-side effects outside the adapter's supported evidence model.

## Architectural result after second pass

The original four gaps are resolved in the framework and dogfood: directional relation-item impact is typed,
repair requests follow evaluated violations, callback/queue state is gone, and compatibility consumes the
candidate relation instead of duplicating its predicate. The resulting boundary is:

```text
domain model       local business meaning and admissibility
Consistency        dependency graph, propagation, freshness, evaluation, repair data
application        repair decisions, replacement ranking, convergence
EF adapter         tracked-change translation and persistence boundary
```

The dogfood now expresses the same domain with fewer semantic duplicates and with repair work visible in the
operation result. The remaining awkwardness is integration ceremony and diagnostics depth, not a reason to move
business reallocation into the framework.
