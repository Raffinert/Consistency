# Allocation consistency dogfood findings

## What was easy

- Stable-key object sets and expression relations mapped directly to demand, supply, allocation, and
  fulfillment objects.
- Recognized `Sum` aggregates produced the expected incremental computation plans and handled add, remove,
  move, and quantity changes without application-maintained totals.
- `From` composed fulfillment and allocation totals into remaining capacity without reading physical mirror
  properties. `Evaluate` and `Materialize` kept logical state and storage synchronization visibly separate.
- Direct source-member classification expressed capacity decreases as `Invalid` and increases as `Dirty`.
- Causal impact output identified the source member, upstream derived nodes, affected invariant, and relation
  membership changes without using internal APIs.
- The EF adapter fit normal application flow: inject the narrow runtime for pre-save reads and use ordinary
  `SaveChangesAsync`. Invariant rejection, SQL rollback, retry, late tracking, and save-time materialization
  remained transactional.
- A rich `Supply.ChangeCapacity` method needed no Raffinert call. The dependency consequence was still found
  at the runtime or EF boundary.

## What was awkward

- Candidate selection and current-allocation compatibility are conceptually one rule, but required two
  relations with repeated resource/date matching. The second relation exists only to put the selected
  allocation on the left side of a consumable derived value.
- Repair callbacks are strongly typed to an invariant source, but an application handling multiple invariant
  kinds still needs an adapter such as `RepairRequirement` to carry business context into one work queue.
- `ScheduleRepairWith` represents required work before knowing whether re-evaluation would still pass. This is
  safe, but processing must re-evaluate and discard conservative requests.
- EF setup is intentionally explicit: every object set participating in this closed-world model must be loaded
  and declared complete. That is correct authority ownership, but appreciable ceremony for a small model.
- The full causal trace is structural. To explain “3 fulfilled + 5 allocated exceeds capacity 6,” application
  diagnostics must add the evaluated business values themselves.

## What could not be expressed cleanly

- Relation aggregate policy has one `ItemChanged` severity. It cannot classify fulfillment or allocation
  quantity changes from old/new values, so both increases and decreases use conservative `Invalid`. Decreases
  release capacity but still create urgent repair work.
- `ScheduleRepairWith` escalates inherited `Dirty` impact to `Invalid`. Consequently, even a capacity increase
  whose direct classifier correctly returns `Dirty` emits a conservative repair request. There is no public
  policy that both preserves lazy dirty state and schedules work only when later evaluation actually violates
  the invariant.
- Candidate-relation membership cannot be consumed directly as “this allocation's selected supply remains in
  its demand's candidates.” The compatibility relation therefore duplicates candidate matching semantics.
- A repair callback identifies the invariant and its source but does not provide a typed domain-level repair
  command spanning supply-capacity and allocation-compatibility cases. The sample translates callbacks into
  application-owned requirements.
- `ConsistencyScope.Complete` is a host assertion, not a proof that its query loaded every database row. The
  runtime correctly rejects missing declarations, but cannot detect a lying or under-fetching host.
- Public diagnostics report dependency causes and affected object counts, not the evaluated operands that make
  a business explanation complete.

These gaps were documented rather than addressed with sample-specific observers or a core API redesign. The
experiment keeps the simplest safe behavior: conservative invalidation and application-owned repair.

## Architectural result

1. **Did Raffinert remain useful after introducing domain mutation methods?** Yes. `ChangeCapacity` owns local
   argument validation while Raffinert still discovers cross-object totals, compatibility, and persistence
   consequences. The method does not manually invoke validation or repair.
2. **Which invariants belong naturally inside the domain object or aggregate?** Local admissibility, such as
   rejecting a negative capacity argument, belongs in `Supply`. If a future aggregate owned a guaranteed-complete
   allocation collection, some capacity logic could move there too. This model deliberately does not grant that
   ownership.
3. **Which dependencies remain naturally external?** Candidate membership, fulfillment and allocation sums,
   remaining capacity across independently loaded rows, and selected-supply compatibility all cross object or
   persistence boundaries and remain natural consistency-model concerns.
4. **Did the consistency model create hidden coupling harder to understand than explicit orchestration?** Some.
   Named definitions and traces make the main DAG clearer than scattered handlers, but the duplicated
   compatibility predicate and repair-policy escalation are non-obvious coupling that must be documented.
5. **Did business behavior leak into Raffinert configuration?** Dependency and safety semantics necessarily live
   there; replacement ranking does not. The “lowest eligible supply” choice remains ordinary application code.
6. **Is the repair boundary clean?** Mostly. Raffinert identifies affected invariant sources and the application
   chooses and applies reallocation. Translating source callbacks into richer business requirements is extra
   boundary ceremony, and conservative requests require re-evaluation.
7. **Would another mechanism be simpler?** A computed property is simpler only when all inputs are already owned
   and loaded by one aggregate. A domain event could trigger reallocation explicitly, but every mutation path
   would then need to remember to publish it. For this externally composed graph, declarative dependency
   discovery remains useful despite the documented gaps.
