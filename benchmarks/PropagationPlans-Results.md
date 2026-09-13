# Exact versus conservative propagation benchmark

Measured 2026-09-13 with BenchmarkDotNet 0.15.8 on .NET 10.0.12. The short job used
one warmup and three measured iterations. Every source and item initially matches, so
the exact plan retains `SourceCount * ItemCount` pairs while the conservative plan
retains zero permanent pairs. Each operation moves one item between matching and
non-matching keys; read variants then request one affected derived count.

| Sources | Items | Exact pairs | Exact mutation | Conservative mutation | Exact allocation | Conservative allocation |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 10 | 1,000 | 44.32 us | 45.96 us | 217.30 KB | 102.84 KB |
| 100 | 100 | 10,000 | 95.07 us | 26.93 us | 937.39 KB | 112.10 KB |
| 1,000 | 10 | 10,000 | 470.70 us | 290.79 us | 1.84 MB | 0.76 MB |
| 1,000 | 100 | 100,000 | 1.70 ms | 262.70 us | 8.61 MB | 0.77 MB |

At 1,000 × 100, mutation plus one lazy read measured 1.72 ms / 8.61 MB for exact and
206.10 us / 0.77 MB for conservative. These results are workload-specific: conservative
propagation invalidates a safe superset and can move cost to later reads, while exact
propagation provides precise deltas required by incremental aggregates.

Run the matrix with:

```powershell
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks -c Release -- `
  --filter *PropagationPlanBenchmarks* --job short --warmupCount 1 --iterationCount 3
```
