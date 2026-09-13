# Propagation precision benchmark

Measured 2026-09-13 with BenchmarkDotNet 0.15.8 on .NET 10.0.12. The short job used
one warmup and three measured iterations. Fixture registration is submitted as one `MutationSet`
so setup remains linear and is excluded from the measured mutation.

The workload changes nested right-side join keys with 10,000 or 100,000 sources spread across
100 candidate buckets. It retains two exact derived consumers and reports mutation diagnostics.

| Sources | Changed rights | Mean | Allocation |
| ---: | ---: | ---: | ---: |
| 10,000 | 1 | 2.163 ms | 2.21 MB |
| 10,000 | 10 | 2.230 ms | 2.85 MB |
| 10,000 | 100 | 7.541 ms | 9.72 MB |
| 100,000 | 1 | 14.538 ms | 20.90 MB |
| 100,000 | 10 | 20.664 ms | 26.33 MB |
| 100,000 | 100 | 94.766 ms | 81.68 MB |

The short-job confidence intervals are wide for the largest cases, so these figures are regression
baselines rather than general performance claims.

Run the matrix with:

```powershell
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks -c Release -- `
  --filter *PropagationPrecisionBenchmarks* --job short --warmupCount 1 --iterationCount 3
```
