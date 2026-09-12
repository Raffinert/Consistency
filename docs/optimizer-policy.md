# Optimizer admission policy

The compiled predicate is always the semantic authority. A new access plan is admitted only to solve a
measured workload where existing plans are inadequate; expression recognition alone is not justification.

## Current range decision

The checked-in [range measurement](../benchmarks/RangePlanning-Results.md) compares an equality-prefix
hash plan with a range residual against a forced scan. At 10,000 rules the existing plan measured about
146× faster; at 100,000 rules it measured about 180× faster. A dedicated range tree would add maintenance,
memory, mutation, comparer, and correctness complexity without addressing a demonstrated bottleneck.

Decision: retain range expressions as diagnostic residual metadata and do not add a range index now.

## Evidence required for a new plan

A proposal must include all of the following:

1. A reproducible domain-shaped benchmark under `benchmarks/Raffinert.Relations.Benchmarks`.
2. Current optimized and forced-scan baselines at realistic and stress-scale cardinalities.
3. Key-distribution/selectivity data, mutation-to-query ratio, allocation data, and materialization impact.
4. A clear threshold where the current plan is operationally inadequate, not merely slower in isolation.
5. Deterministic randomized mutation tests proving optimized results equal the semantic reference path.
6. Coverage for nulls, comparer semantics, duplicate values, lifecycle changes, and batched mutations.
7. Structured diagnostic output that identifies the chosen plan and exposes its relevant costs.

Run the existing range benchmark with:

```powershell
dotnet run -c Release --project benchmarks/Raffinert.Relations.Benchmarks -- `
  --filter *RangePlanningBenchmarks* --job short
```

## Candidate workloads

Candidates remain hypotheses until measured:

- a pure range relation with no selective equality prefix;
- a very low-cardinality equality prefix;
- ordered or top-N relation usage;
- prefix/string matching.

Any accepted optimizer must preserve the same public dependency, severity, mutation, and diagnostics
contracts regardless of its internal data structure.
