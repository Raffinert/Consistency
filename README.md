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

## Implemented

- Typed object sets with stable keys
- Binary relations whose original compiled predicate remains the semantic authority
- Dependency and nested member-path analysis
- Automatic single and composite hash indexes for safe equality joins
- Explicit scan and hash-join access planning
- Reverse hash access for exact derived-state propagation
- Ordinal and ordinal-ignore-case comparer-aware string joins
- Correct scan fallback for opaque or unsupported predicates
- Incremental add, remove, and scalar-property index maintenance
- Shared arbitrary-depth reverse navigation for nested paths
- Access-impact and semantic-impact reporting
- Atomic property `ChangeSet` application
- Bidirectional relation queries
- Lazy derived state with distinct fresh, dirty, and invalid states
- Documented monotonic state transitions with explicit recomputation and revalidation recovery
- Policy-driven dependency severity, independent of scan/hash access planning
- Exact source-scoped derived invalidation backed by bidirectional relation membership
- Unified relation-impact snapshots for delta, semantic, and access propagation
- Source lifecycle cleanup for derived, invariant, and materialized relation state
- Role-aware dependency analysis for derived computations and invariant predicates
- LINQ dependency extraction for common aggregate, filter, and projection operators
- Invariant evaluation with immediate, dirty, and invalidation policies
- Post-commit immediate evaluation and deduplicated repair-request dispatch
- A separate EF Core change-tracker adapter package
- A BenchmarkDotNet benchmark project
- Human-readable compiled model diagnostics through `DebugView`

## Further work

- Collection navigation
- Range access plans (range expressions are currently diagnostic metadata)
- Stricter `ChangeSet` validation and documented atomicity
- Scheduling and domain-specific repair policies

The core package has no EF Core or dependency-injection dependency.

`RelationRuntime` is not thread-safe. Mutations and queries must be externally synchronized.

Immediate invariant evaluations and repair callbacks run only after runtime-owned state has committed.
If a callback throws, the operation surfaces that exception but does not roll back the committed runtime state.
