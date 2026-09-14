# Bootstrap and projected dependency benchmark

The benchmark suite now contains `BootstrapAndProjectionBenchmarks` with 10,000 and 100,000-object
authoritative startup cases. Run it on the release machine before publishing an alpha:

```text
dotnet run -c Release --project benchmarks/Raffinert.Relations.Benchmarks -- --filter *BootstrapAndProjection*
```

This change intentionally records the reproducible benchmark shape rather than claiming portable timing
numbers from an interactive development host. Projected fan-out is guarded by focused source-scoping tests;
the implementation currently scans the downstream object set and is an explicit optimization target after
the alpha correctness contract is stabilized.
