# Raffinert.Relations

Raffinert.Relations is an experimental declarative dependency engine for .NET object models.

Relationships are defined as expression trees. The library analyzes those expressions to derive dependencies, access paths, indexes, and change impact, allowing relationship queries to be maintained incrementally without repeating the business rule in index or refresh configuration.

## Example

```csharp
var model = new RelationModelBuilder();

var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
var poLines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);

var matches = model.Relation(invoices, poLines).Where((invoice, poLine) =>
    invoice.PurchaseOrderNumber == poLine.PurchaseOrderNumber &&
    invoice.ItemNumber == poLine.ItemNumber);

var runtime = model.Build().CreateRuntime();
runtime.Add(invoices, invoice);
runtime.Add(poLines, poLine);

var related = runtime.Related(matches, invoice);

var oldItem = poLine.ItemNumber;
poLine.ItemNumber = "ITEM-2";
runtime.Apply(Change.Property(poLines, poLine, x => x.ItemNumber, oldItem, poLine.ItemNumber));
```

`Change.Property` observes a mutation that has already happened; it never mutates the domain object.
The object-set argument can be omitted when the instance belongs to exactly one registered set.
An object's declared key is immutable while the object is registered. Reported key changes are rejected;
remove and re-add the object when its identity genuinely needs to change.

A `ChangeSet` is fully validated before runtime-maintained indexes and dependency state are updated.
Repeated changes to one instance/member must form a contiguous value chain (`A -> B`, `B -> C`) and
are normalized to their net effect; conflicting chains are rejected. Domain mutation remains external
and is not transactional. Pass `ChangeValidationMode.StrictNewValue` to `Apply` to additionally verify
that each member currently equals its reported final new value.

Collection navigation is explicit: mutate the domain collection first, then report it with
`Change.CollectionAdd`, `Change.CollectionRemove`, or `Change.CollectionReset`. The runtime maintains
owner/item reverse navigation so later item-property changes resolve affected owners incrementally.

## Implemented

- Typed object sets with stable keys
- Separate mutable object-set builders and stable `ObjectSet<T>` runtime handles
- Binary relations whose original compiled predicate remains the semantic authority
- Dependency and nested member-path analysis
- Automatic single and composite hash indexes for safe equality joins
- Explicit scan and hash-join access planning
- Reverse hash access for exact derived-state propagation
- Ordinal and ordinal-ignore-case comparer-aware string joins
- Correct scan fallback for opaque or unsupported predicates
- Incremental add, remove, and scalar-property index maintenance
- Explicit collection add/remove/reset with incremental owner/item navigation
- Shared arbitrary-depth reverse navigation for nested paths
- Compiled null-safe member-path and cached single-member readers
- Access-impact and semantic-impact reporting
- Deterministic, atomically validated property `ChangeSet` application
- Bidirectional relation queries
- Lazy derived state with distinct fresh, dirty, and invalid states
- Documented monotonic state transitions with explicit recomputation and revalidation recovery
- Policy-driven dependency severity, independent of scan/hash access planning
- Exact source-scoped derived invalidation backed by bidirectional relation membership
- Unified relation-impact snapshots for delta, semantic, and access propagation
- Focused member/relation/derived/invariant/policy dependency-graph propagation
- Source lifecycle cleanup for derived, invariant, and materialized relation state
- Role-aware dependency analysis for derived computations and invariant predicates
- LINQ dependency extraction for common aggregate, filter, and projection operators
- Explicit membership/item/ordering semantics for selection, cardinality, distinct, paging, and containment operators
- Invariant evaluation with immediate, dirty, and invalidation policies
- Post-commit immediate evaluation and deduplicated repair-request dispatch
- A separate EF Core change-tracker adapter package
- EF Core unit-of-work capture with entity lifecycle, relationship resets, explicit set mapping, and post-save apply
- A BenchmarkDotNet benchmark project
- Measured range-planning benchmark (current equality-prefix strategy retained)
- Human-readable compiled model diagnostics through `DebugView`
- Deterministic full-graph randomized optimized-versus-scan correctness coverage

## Further work

- Range access plans (range expressions are currently diagnostic metadata)
- Collection navigation
- Scheduling and domain-specific repair policies

The core package has no EF Core or dependency-injection dependency.

The EF Core adapter captures a `RelationUnitOfWork` before `SaveChanges`, then applies it only after the
database operation succeeds. `SaveChangesAndApply`/`SaveChangesAndApplyAsync` provide this ordering.
Added and deleted entities require a `RelationUnitOfWorkMappings` entry; selectors disambiguate CLR types
used by multiple object sets. Modified scalars, references, owned entries, and collection resets are
translated through the same core change contracts. Runtime application failure after database success
is surfaced and requires application-level reconciliation; it cannot roll back the database transaction.

`RelationRuntime` is not thread-safe. Mutations and queries must be externally synchronized.

Immediate invariant evaluations and repair callbacks run only after runtime-owned state has committed.
If a callback throws, the operation surfaces that exception but does not roll back the committed runtime state.
