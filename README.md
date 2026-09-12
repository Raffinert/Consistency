# Raffinert.Relations

Raffinert.Relations is an experimental declarative dependency engine for .NET object models.

Relationships are defined as expression trees. The library analyzes those expressions to derive dependencies, access paths, indexes, and change impact, allowing relationship queries to be maintained incrementally without repeating the business rule in index or refresh configuration.

## Example

```csharp
var model = new InvariantModelBuilder();

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

## Implemented

- Typed object sets with stable keys
- Binary relations whose original compiled predicate remains the semantic authority
- Dependency and nested member-path analysis
- Automatic single and composite hash indexes for safe equality joins
- Correct scan fallback for opaque or unsupported predicates
- Incremental add, remove, and scalar-property index maintenance
- One-reference reverse navigation for nested indexed paths
- Human-readable compiled model diagnostics through `DebugView`

## Planned

- Arbitrary-depth and collection reverse navigation
- Derived state with fresh, dirty, and invalid states
- Invariant evaluation and repair policies
- Batch change sets and an optional EF Core adapter

The core package has no EF Core or dependency-injection dependency.
