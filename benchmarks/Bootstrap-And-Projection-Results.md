# Bootstrap and projected dependency benchmark

The benchmark suite now contains `BootstrapAndProjectionBenchmarks` with 10,000 and 100,000-object
authoritative startup cases. Run it on the release machine before publishing an alpha:

```text
dotnet run -c Release --project benchmarks/Raffinert.Relations.Benchmarks -- --filter *BootstrapAndProjection*
```

`ProjectedPropagationBenchmarks` adds the corresponding 10k/100k downstream-source matrix with fan-out
1/10/100. Projection propagation is reverse-indexed, so routing enumerates only the changed target's bucket.
Committed measurements below are produced on the documented development host rather than treated as SLAs.

Environment: Windows 11 25H2, Intel Core Ultra 9 275HX, .NET SDK 10.0.401 / runtime 10.0.12,
BenchmarkDotNet `ShortRun`, concurrent workstation GC.

| Operation | Population | Fan-out | Mean | Allocated |
|---|---:|---:|---:|---:|
| Projected propagation | 10,000 | 1 | 4.121 us | 18.91 KB |
| Projected propagation | 10,000 | 10 | 5.642 us | 22.11 KB |
| Projected propagation | 10,000 | 100 | 11.630 us | 58.70 KB |
| Projected propagation | 100,000 | 1 | 4.188 us | 19.00 KB |
| Projected propagation | 100,000 | 10 | 6.360 us | 22.11 KB |
| Projected propagation | 100,000 | 100 | 11.133 us | 58.70 KB |
| Bootstrap | 10,000 | n/a | 2.758 ms | 10.20 MB |
| Bootstrap | 100,000 | n/a | 75.671 ms | 97.16 MB |

The near-identical 10k/100k propagation rows at equal fan-out demonstrate that ordinary upstream
mutation routing scales with the impacted reverse bucket, not total downstream population.
