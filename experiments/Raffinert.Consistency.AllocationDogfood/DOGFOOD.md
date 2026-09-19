# Allocation consistency dogfood second-pass findings

## Fixed by framework changes

- Relation aggregate policies now support strongly typed directional item-member classifiers. Fulfillment and
  allocation quantity increases classify as `Invalid`; decreases classify as `Dirty`; membership add/remove
  policies remain independent.
- Repairability is immutable model metadata configured with `RepairWhenViolated()`. A repair request is emitted
  only after an affected repairable invariant is evaluated and its predicate returns false. Safe capacity
  increases and safe quantity decreases therefore produce no repair work.
- `ApplyDetailed` and the EF save result expose structured `RepairRequestInfo` data. The compiled model contains
  no application callback or mutable repair queue; the allocation application translates requests into local
  repair requirements and owns replacement selection.
- `FromMembership(candidateSupplies, allocation => allocation.Demand, allocation => allocation.Supply)` reuses
  the existing candidate relation and its indexes. Allocation compatibility has one semantic authority: the
  candidate relation predicate.
- Projected membership tracks both selected references and changes that alter the underlying relation. Its
  derived value participates in invariant propagation and causal traces.
- The rich `Supply.ChangeCapacity` method remains Raffinert-free. EF continues to observe ordinary tracked
  mutations at the `SaveChangesAsync` boundary.

## Still awkward

- Structured requests identify a framework invariant and source; the application still translates that generic
  identity into a domain-specific repair requirement before choosing a replacement.
- Core POCO callers must report mutations explicitly, while EF callers rely on tracked changes and save-time
  translation. The distinction is honest but requires two integration habits.
- The compact projected-membership declaration is clear for this case, but larger models still need explicit
  names and diagnostics to make cross-object ownership easy to follow.
- Causal traces identify definitions, members, relation pairs, and sources. They do not yet include the evaluated
  business operands that explain a numeric failure in domain terms.

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
