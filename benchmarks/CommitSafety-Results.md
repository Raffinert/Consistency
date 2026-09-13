# Commit safety benchmark baseline

Measured on 2026-09-13 with BenchmarkDotNet 0.15.8, .NET 10.0.12, Windows 11,
and an Intel Core Ultra 9 275HX. The short job used one warmup and three measured
iterations, so these results establish direction and scale rather than release-grade
microbenchmark confidence.

The workload retains one relation, one cached derived source, a variable number of
objects and exact materialized pairs, then applies the same unrelated single-property
mutation. `CommitWithoutSnapshotBenchmarkOnly` disables rollback snapshots through
an internal benchmark-only hook. It is not a supported production mode.

| Retained objects | Pairs | With snapshot | Without snapshot | With allocation | Without allocation |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 1 | 225.5 us | 121.8 us | 813.32 KB | 489.83 KB |
| 1,000 | 1,000 | 222.7 us | 125.2 us | 880.04 KB | 489.83 KB |
| 10,000 | 1 | 7.88 ms | 1.67 ms | 7.30 MB | 4.28 MB |
| 10,000 | 1,000 | 10.46 ms | 1.65 ms | 7.41 MB | 4.28 MB |
| 100,000 | 1 | 74.22 ms | 20.48 ms | 69.20 MB | 38.23 MB |
| 100,000 | 1,000 | 74.42 ms | 20.57 ms | 69.29 MB | 38.23 MB |

The full 18-case result matrix is emitted by:

```powershell
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks -c Release -- `
  --filter *CommitSafetyBenchmarks* --job short --warmupCount 1 --iterationCount 3
```

## Conclusion

The fixed mutation scales with unrelated retained state even without snapshotting,
which confirms the all-set prepared-domain simulation cost targeted by Task 7. The
additional snapshot cost is also material: at 100,000 retained objects it adds about
54 ms and 31 MB per mutation. This is sufficient evidence to proceed with touched-set
validation and scoped rollback state while preserving exception atomicity.

## After touched-set validation and scoped snapshots

The same matrix was rerun after Tasks 7 and 8. The safe path remains effectively flat
across 1,000, 10,000, and 100,000 unrelated retained objects and across 1, 100, and
1,000 retained pairs. At 100,000 objects it measured 2.20–2.22 us and 11.97 KB versus
1.96–2.00 us and 10.47 KB for the benchmark-only unsafe comparison. Rollback safety
therefore adds roughly 0.25 us and 1.5 KB to this fixed mutation instead of scaling with
the unrelated retained graph.
