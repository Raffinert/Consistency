# Exact versus conservative propagation benchmark

Measured 2026-09-13 with BenchmarkDotNet 0.15.8 on .NET 10.0.12. The short job used
one warmup and three measured iterations. Every source and item initially matches, so
the exact plan retains `SourceCount * ItemCount` pairs while the conservative plan
retains zero permanent pairs. Each operation moves one item between matching and
non-matching keys; read variants then request one affected derived count.

| Sources | Items | Exact pairs | Exact mutation | Conservative mutation | Exact allocation | Conservative allocation |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 10 | 1,000 | 64.32 us | 27.91 us | 226.10 KB | 107.24 KB |
| 100 | 100 | 10,000 | 132.53 us | 31.02 us | 946.36 KB | 116.75 KB |
| 1,000 | 10 | 10,000 | 497.78 us | 227.00 us | 1.92 MB | 0.80 MB |
| 1,000 | 100 | 100,000 | 1.90 ms | 300.85 us | 8.69 MB | 0.81 MB |

At 1,000 × 100, mutation plus one lazy read measured 1.96 ms / 8.69 MB for exact and
283.06 us / 0.81 MB for conservative. These results are workload-specific: conservative
propagation invalidates a safe superset and can move cost to later reads, while exact
propagation provides precise deltas required by incremental aggregates.

Run the matrix with:

```powershell
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks -c Release -- `
  --filter *PropagationPlanBenchmarks* --job short --warmupCount 1 --iterationCount 3
```

## Selective candidate routing

Measured on the same environment after candidate-scoped conservative routing. This workload has
10,000 sources distributed uniformly across 100 or 1,000 keys and moves one right object between
two keys. Consequently, only the union of the old and new buckets is affected: 200 sources for 100
keys and 20 sources for 1,000 keys. Conservative mode still retains zero exact pairs.

| Sources | Keys | Candidate sources | Exact mutation | Conservative mutation | Exact allocation | Conservative allocation |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 | 100 | 200 | 5.586 ms | 1.552 ms | 4.09 MB | 1.69 MB |
| 10,000 | 1,000 | 20 | 5.879 ms | 2.319 ms | 4.32 MB | 1.90 MB |

Mutation plus one lazy read measured 5.637 ms / 4.09 MB exact versus 1.455 ms / 1.69 MB
conservative at 100 keys, and 5.617 ms / 4.32 MB exact versus 2.307 ms / 1.90 MB
conservative at 1,000 keys. The three-iteration short job has wide confidence intervals, so these
figures are a regression baseline rather than a general performance claim.

Run the selective matrix with:

```powershell
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks -c Release -- `
  --filter *SelectivePropagationPlanBenchmarks* --job short --warmupCount 1 --iterationCount 3
```
