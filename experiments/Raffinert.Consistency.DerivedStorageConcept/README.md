# Derived storage and materialization concept

This disposable executable compares three storage policies and an explicit
consumption API without changing Core, EF integration, or public API baselines:

Repository baseline inspected: `6a47cbd9ef6744512105d108c9b3fb3e7339bf7a`.

- A: the runtime cache is authoritative; properties synchronize only at an
  explicit/persistence boundary.
- B: the runtime cache is authoritative; `Get` also synchronizes configured
  properties.
- C-Hybrid: a configured property is treated as storage, while runtime-only
  derived values retain runtime cache storage.
- D: `Evaluate` returns the current runtime-authoritative logical value without
  writing a mirror; `Materialize` evaluates as needed, writes one configured
  target, and returns that same value.

The C simulator deliberately uses the existing runtime underneath for graph
routing and therefore still has an implementation cache. Its policy layer reads
Fresh values from the property, detects external writes, synchronizes Fresh
incremental updates, and marks setter failures Invalid. Eliminating the
underlying duplicate value would require production runtime-state changes; this
experiment estimates that design rather than pretending it already exists.

Build and run outside the solution:

```powershell
dotnet build experiments/Raffinert.Consistency.DerivedStorageConcept/Raffinert.Consistency.DerivedStorageConcept.csproj -c Release
dotnet run --project experiments/Raffinert.Consistency.DerivedStorageConcept/Raffinert.Consistency.DerivedStorageConcept.csproj -c Release --no-build
```

## Existing storage semantics

Inspection of `DerivedRuntimeState.cs`, `DerivedDefinitions.cs`,
`MutationCommit.cs`, and the EF persistence policy established:

- Source-only, relation-backed, derived-to-derived, and projected derived nodes
  all store per-source values and `Fresh`/`Dirty`/`Invalid` state in runtime
  dictionaries.
- All derived kinds cache values after first evaluation. Composed and projected
  computations obtain upstream values from upstream runtime states.
- A missing entry reads as Dirty. Successful computation stores the value and
  Fresh state; a throwing computation does not replace the entry.
- Recognized relation aggregates mutate a Fresh cached value in place and count
  an incremental update. Unsupported or stale cases recompute lazily.
- Source add/remove clears that source's derived cache entry.
- Whole-runtime and affected-source snapshots copy cached values, states, and
  diagnostic counters. Plans execute reversibly, capture a forward patch, then
  restore the pre-plan runtime state.
- EF `Materialize` is adapter configuration, not a property-backed derived
  definition. During policy-aware planning it evaluates affected derived nodes,
  writes direct writable tracked properties, marks them modified, and records
  old property values/modified flags for rollback.
- EF materialization uses planned derived evaluations, whose values come from
  runtime `GetValue` internally. Ordinary public `runtime.Get` does not invoke
  EF materialization.
- Materialized assignments are not reported back as runtime mutations. Current
  EF validation rejects a materialized mirror that also feeds the graph, which
  avoids feedback and duplicate edges.
- The current runtime rollback journal contains runtime-owned cache state, not
  arbitrary domain property state. EF has a separate materialization rollback.

Therefore current production behavior is Model A at the read boundary: after a
stale PriceRate is read, `rate` is 5.5 and Fresh in runtime while
`link.PriceRate` remains 6 until persistence materialization.

## Harness structure

`Harness.cs` builds one real consistency DAG containing:

- PriceRate source-only and runtime-only control values;
- PriceRate -> UnitRate -> LinkValidity -> repair;
- OrderLine/Fulfillment incremental Sum;
- RemainingQuantity -> projected AllocationValidity;
- both derived-handle and direct-property dependency declarations.

The policy layer controls only storage synchronization. It does not implement a
second graph, relation engine, invalidation engine, or repair engine.

The individual `Scenarios/D01...D12` files are executable specifications. D11
uses actual SQLite-backed EF metadata/change tracking; it remains in this
isolated project because the setup is small and avoids concept-only production
tests.

`ModelD.ExplicitEvaluateMaterialize.cs` and `Scenarios/EM01...EM12` reuse that
same DAG and runtime. Model D's optional registry is a stand-in for a Core
materialization adapter: the derived definition remains runtime-authoritative,
and target metadata remains usable for detached objects. Conceptually, putting
the same target metadata directly on the compiled Core definition would also
work but would make setter and materialization concerns part of Core itself.
EF-only metadata cannot provide the demonstrated plain-object operation.

The A/B/C scenarios retain their historical `Get` name. In the Model D
comparison, that operation is the logical-read role now named `Evaluate`.
Model D chooses M1 (reject `Materialize` for runtime-only values) and T1
(`Materialize` writes only the requested target) for the experiment.

## Declaration pressure against API v2

The hybrid API-v2 shape remains readable for runtime-only values:

```csharp
var riskScore = model.Derived(links)
    .Select(CalculateRiskScore)
    .Named("risk-score");
```

Output-mirror semantics read naturally as:

```csharp
var priceRate = links
    .Select(CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");
```

Property-storage semantics require a visibly different declaration:

```csharp
var priceRate = links.Derive(
        target: x => x.PriceRate,
        compute: CalculatePriceRate)
    .Named("price-rate");
```

The experiment does not treat these as aliases. `MaterializeTo` describes an
output mirror; `Derive(target, compute)` would make target ownership, write
protection, atomicity, and snapshot behavior part of the derived definition.

For consumption, the two-line Model D story is:

```csharp
var rate = runtime.Evaluate(priceRate, link);       // logical value; no mirror write
var stored = runtime.Materialize(priceRate, link); // same value + requested mirror write
```

`Evaluate` is deliberately cache-aware, not a force-recompute operation. Its
dogfooded documentation sentence is: "Returns the current logical value of the
derived definition, evaluating it only when its cached value is not Fresh."
`Materialize` was clearer than `Sync` (direction and scope are ambiguous) and
`GetAndApply` (the applied effect is unspecified). `GetState` was useful only in
scenario assertions and remains diagnostics vocabulary rather than a normal
precondition for either operation.

## Running note

The project is intentionally outside `Raffinert.Consistency.sln` and all
package/release workflows. It has no production consumers.
