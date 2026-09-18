# Consistency API v2 concept dogfood

This is a disposable design experiment. It is not a package, is not included in
`Raffinert.Consistency.sln`, and does not propose a production API winner.

The frozen repository baseline was `9082e8450395cd3e3082b75122ae54e7a470ba6e`.
That differs from the plan's reviewed baseline
`2e5f77e68f6c12fb75d5900a8c192e792a9ab26a`; the intervening commits only added
the API-v2 concept plan and its optional YAML extension. No production source or
public API baseline changed between those commits.

Build and execute the experiment explicitly:

```powershell
dotnet build experiments/Raffinert.Consistency.ApiV2Concept/Raffinert.Consistency.ApiV2Concept.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.ApiV2Concept/Raffinert.Consistency.ApiV2Concept.csproj -c Release --no-build
```

## Existing semantic inventory

The current public surface and dogfood scenarios establish these required
concepts:

- `ObjectSet<T>` gives a root set stable identity and a typed key.
- `Relation<TLeft, TRight>` preserves binary orientation and pair deltas.
- `Derived<TSource, TValue>` owns a typed value per source object.
- `Invariant<TSource>` owns validation and policy reaction per source object.
- Direct member dependencies are inferred from expressions.
- Nested member dependencies can be inferred, while `DependsOn` completes opaque
  calculator declarations.
- Relation-backed derived values consume explicit left-oriented relation matches.
- Incremental `Count`, `LongCount`, `Any`, and `Sum` are recognized execution
  plans selected today through `.Incrementally()`.
- Upstream derived dependencies compile to explicit DAG edges.
- Projected cross-object derived dependencies retain a typed source-to-owner
  selector and reverse routing.
- `Dirty` and `Invalid` are separate dependency severities.
- Source-member transition severity can inspect old and new values.
- Invariant repair scheduling is policy downstream of pure computation.
- Stable definition names are explicit strings, independent of local variables.
- EF `Map`, `Materialize`, and `Enforce` configure persistence separately.
- `DiscoverConsumers` supplies authoritative reverse-navigation discovery.
- `ConsistencyScope.Complete` states authoritative set coverage.

All four facades translate directly to these existing public APIs. They contain
no engine, manual invalidation, reflection, `dynamic`, or untyped graph nodes.

## Shared scenarios

`Common/ScenarioExpectations.cs` runs the same assertions for Current and A-D:

- S1 computes a direct remaining quantity.
- S2 exercises shared nested-navigation fan-out, navigation retargeting, and EF
  materialization while `Association` remains the only consistency root.
- S3 checks relation membership and the recognized incremental `Sum` plan.
- S4 checks a derived-to-derived edge.
- S5 checks typed `Allocation -> OrderLine` projection and reverse routing.
- S6 checks Dirty on an increase and Invalid on a decrease/cancellation.
- S7 checks one durable repair request and deferred callback dispatch.
- S8 exercises separate EF materialization and invariant enforcement mappings.
- S9 checks explicit durable definition keys.

## Current API baseline

The baseline file intentionally uses the current API without improving it.
Counts below use nonblank declaration lines and count semantic API concepts, not
formatting tokens.

| Scenario | Declaration lines | Distinct concepts | Ownership visible | Edge/orientation visible | Policy locality | Strategy leakage |
|---|---:|---:|---|---|---|---|
| S1 | 4 | 5 | Yes, `Derived(simpleLines)` | Direct expression only | N/A | None |
| S2 | 8 | 7 | Yes, association set | Nested paths visible in `DependsOn` | N/A | None |
| S3 | 13 | 9 | Yes, `Derived(lines)` | Relation left/right and `Using` visible | Adjacent on relation input | `.Incrementally()` |
| S4 | 9 | 7 | Yes | `Using(fulfilledQuantity)` visible | Adjacent, though mixed with computation chain | None |
| S5 | 8 | 7 | Yes | Typed projection and both upstreams visible | Adjacent | None |
| S6 | included in S3-S5 | 5 policy operations | Yes | Classified edge must be inferred from builder stage | Adjacent | None |
| S7 | 5 | 6 | Yes | `Using(allocationValidity)` visible | Repair remains after invariant definition | None |
| S8 | 7 core mapping operations | 7 | Mapping set explicit | Adapter boundary visible | Enforcement is adapter policy | None |
| S9 | one `.Named` per node | 1 | N/A | N/A | N/A | None |

## Candidate character

Variant A uses the smallest generic vocabulary: `ObjectProvider`,
`ValueProvider`, `Combine`, `For`, and `PerLeft`. Its happy path is compact, but
the wrapper types become generic-heavy and same-CLR-type/different-set ownership
is still checked by the existing builder rather than by C#.

Variant B retains domain-specific node families: `ObjectSetNode`, `ObjectValue`,
`DomainRelation`, projected values, and domain invariants. `SumByLeft` makes
orientation explicit, and `SelectWith` puts projection on the consuming set.

Variant C retains `model.Derived(source)` and current relation/invariant
organization, adding only `From(...).Sum(...)` and typed composition. It has the
smallest conceptual migration:

| Scenario | Current operations | Variant C operations |
|---|---|---|
| S1 | `Derived + Compute` | `Derived + Select` (or `From + Select` for composition) |
| S2 | `Derived + DependsOn + Compute` | `Derived + DependsOn + Select` |
| S3 | `Derived + Using + Impact + Incrementally + Compute` | `Derived + From + Impact + Sum` |
| S4 | `Derived + Using + Impact + Compute` | `Derived + From + Select` |
| S5 | `Derived + Using(projected pair) + Impact + Compute` | `Derived + From(projected pair) + Impact + Select` |
| S6 | current impact builder | same impact builder |
| S7 | `Invariant + Using + Must + Named + ScheduleRepairWith` | same concepts |
| S8 | EF mapping adapter | same adapter |
| S9 | `.Named` | `.Named` |

Variant D organizes graph nodes as context properties. That improves reuse and
discovery after construction, but the manual context has more constructor
ceremony. Property organization is evaluated; source-generated registration and
property-name-derived identity are not implemented.

## Syntax alternatives explored

| Decision | Alternatives considered | Executable choice and observation |
|---|---|---|
| Set creation | `Objects<T>()`, `Set<T>()` | `Objects<T>()`; it matches current vocabulary and avoids confusing a definition with a collection. |
| Relation creation | `RelateTo`, `Relate`, `model.Relation` | A uses `RelateTo`, B uses `Relate`, C uses `model.Relation`; all remain readable. |
| Left aggregate | `PerLeft().Sum`, `SumByLeft`, `Left.Sum` | A uses `PerLeft().Sum`, B uses `SumByLeft`, C uses source-owned `From(...).Sum`; plain ambiguous `Sum` is not exposed on a relation. |
| Direct value | `Value`, `Select` | A uses `Value`; B-D use `Select`. `Value` is clearer for a per-object scalar, while `Select` composes more uniformly. |
| Composition | provider `Combine`, set-owned `From`, current `Using` | A/B use `Combine`, C uses `From`; all preserve typed source ownership in their wrapper types. |
| Projection | `value.For(set, selector)`, `set.SelectWith(selector, values)`, `Derived(set).From(selector, values)` | A, B, and C deliberately use one each. `SelectWith` most clearly places ownership on the consumer; `For` reads naturally but produces the noisiest generic type. |
| Invariant | `value.Must`, `set.Invariant(value)`, `model.Invariant(set).Using(value)` | All executable variants retain an explicit source-owning invariant operation. A/B shorten `Using`; C stays close to current. |
| Naming | `Named`, `WithName`, `Define(name, ...)` | Explicit `.Named(string)` or context constructor string is retained. Property/variable magic is rejected. |

The alternative `GroupByLeft` was rejected because no grouping key is created;
the relation is already oriented. `ForEachLeft` suggests execution rather than a
declarative aggregate. The opposite A projection form
`allocations.SelectWith(...)` is represented by Variant B instead of duplicated
inside A.

## Recorded gaps

- **SYNTAX-ONLY GAP:** explicit `Sum` can hide production `.Incrementally()` in
  every facade, but the adapter must still call it because the current builder
  uses it to request recognized aggregate planning.
- **TYPE-SYSTEM GAP:** A-C can encode CLR owner types, but not the identity of two
  distinct object sets with the same CLR type. The current builder rejects a
  cross-set combination at declaration time.
- **TYPE-SYSTEM GAP:** cycles are not expressible through the one-pass executable
  facade declarations; a future forward-reference mechanism would require
  `Build()` cycle diagnostics rather than ordinary C# typing.
- **PUBLIC-FACADE GAP:** candidate wrappers need to retain raw public handles for
  EF mapping. This is manageable inside the concept, but a production API must
  define an intentional adapter interop surface rather than expose wrapper
  internals.
- No S1-S9 case exposed a **SEMANTIC GAP** in the existing engine.

## Optimization vocabulary audit

The candidate declarations contain no `Cache`, `Invalidate`, `Recompute`,
`Scan`, or `Index`. `Dirty` and `Invalid` remain required semantic contracts,
not execution words. The adapters call `Incrementally()` only as an
implementation bridge; exposing it in normal candidate declarations would be
implementation leakage. The executable `Sum` proves the adapter can select the
current `IncrementalSum(Fulfillment.Quantity)` plan without showing that word to
the user. `Count`, `LongCount`, and `Any` have the same current recognized-plan
shape but were not needed by S1-S9; a production facade would need explicit
typed operators for all four.

## Rejected LINQ/IQueryable pseudo-variant

The model is not an `IQueryable<T>` or `IEnumerable<T>`. Ordinary LINQ does not
encode stable object-set identity, relation orientation and pair deltas, reverse
routing, Dirty/Invalid impact, invariant reaction, repair scheduling, durable
definition identity, or authoritative scope requirements. Familiar operator
names are useful only when Raffinert-specific types preserve those semantics.

## Scope boundaries

The project remains outside the solution and all package/release workflows.
Task 276's optional YAML/portable-model spike was not executed; mandatory Tasks
264-275 do not depend on it.
