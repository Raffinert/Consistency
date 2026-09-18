# Type-system and diagnostic pressure test

These snippets are intentionally non-compiling or model-invalid documentation.
They are not included as C# sources.

| Misuse | A | B | C | D/current boundary |
|---|---|---|---|---|
| Combine values from different CLR source types without projection | Compile-time generic mismatch | Compile-time generic mismatch | Compile-time generic mismatch | Compile-time generic mismatch |
| Combine different sets of the same CLR source type | Builder rejects during declaration | Builder rejects during declaration | Builder rejects during declaration | Builder rejects during declaration |
| Aggregate from the wrong relation orientation | `PerLeft` only returns a left-owned value; wrong owner is a compile-time mismatch | `SumByLeft` names and types orientation | `Derived(left).From(relation)` generic mismatch for a right source | Protected context helper requires the relation's left set |
| Project through a navigation of the wrong target type | Compile-time selector mismatch | Compile-time selector mismatch | Compile-time selector mismatch | Compile-time selector mismatch |
| Use a value owned by another model | Same wrapper types permit it; existing builder rejects during declaration | Same | Same | Existing builder rejects during declaration |
| Attach membership impact to a non-relation dependency | API does not expose membership methods on ordinary `ValueProvider` | API exposes relation impact only on `DomainRelation` | Membership policy is reachable only after `From(relation)` | `SumByLeft` accepts relation impact; direct `Select` accepts source impact, so the shared production policy builder still exposes all methods |
| Attach source-member transition policy to an unrelated CLR type | Compile-time expression mismatch | Compile-time expression mismatch | Compile-time expression mismatch | Compile-time expression mismatch |
| Materialize onto the wrong entity/value type | EF generic signature catches most mismatches; incompatible property conversions fail compilation | Same | Same | Same; assignable-but-invalid mappings remain adapter validation |
| Create a cycle between derived nodes | No forward references in prototype; a future descriptor API would need `Build()` validation | Same | Same | Same |

Representative invalid shapes:

```csharp
// Different owners: generic arguments do not unify.
lineRemaining.Combine(allocationValidity);

// Wrong projected target: Allocation.OrderLine is OrderLine, not Association.
associationRate.For(allocations, allocation => allocation.OrderLine);

// Wrong materialization source/property pair: generic arguments do not unify.
mappings.Materialize(associationRate, (OrderLine line) => line.UnitRate);
```

The most actionable compiler messages come from B/C because their type names
describe domain ownership (`ObjectValue<OrderLine, decimal>` or
`HybridValue<OrderLine, decimal>`). A's errors mention nested provider types.
Model-identity and cycle errors necessarily remain model-construction errors
unless identity is promoted into generated nominal types, which would add much
more ceremony than this experiment justifies.
