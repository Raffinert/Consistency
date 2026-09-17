# Compiled dependency DAG benchmark results

Measured 2026-09-17 against production implementation commit
`12294c06a7124e999c28a47b7bd38cbf2ec98cec`; the benchmark harness is added by the following
Task 262 commit.

Environment: Windows 11 25H2, Intel Core Ultra 9 275HX (24 physical/logical cores), .NET SDK
10.0.401, .NET 10.0.12 x64 RyuJIT, concurrent workstation GC, BenchmarkDotNet 0.15.8. The
short job used one launch, one warmup, and three measured iterations.

| Operation | Topology | Mean | Allocated |
|---|---|---:|---:|
| Compile deep DAG | 128-node chain | 3.024 ms | 1,287.32 KB |
| Apply root change | 32-node chain | 48.49 us | 216.65 KB |
| Apply root change | 128-node chain | 194.76 us | 827.28 KB |
| Apply root change | 32 diamond layers (97 nodes) | 147.97 us | 641.76 KB |
| Apply root change | 128-node sparse branching DAG | 182.06 us | 824.89 KB |
| Prepare binding plan | 128-node chain | 258.15 us | 1,213.43 KB |

The 128-node chain has four times as many nodes as the 32-node chain and measured 4.02 times the
apply time and 3.82 times the allocation. The 97-node layered diamond and 128-node sparse DAG stay
in the same node/edge-proportional range. This is consistent with one traversal plus source-mapping
work and shows no repeated fixed-point scaling signal. The short-run confidence intervals are wide,
so these values are a regression baseline rather than a general performance claim.

Reproduce with:

```powershell
dotnet run --project benchmarks/Raffinert.Consistency.Benchmarks/Raffinert.Consistency.Benchmarks.csproj `
  -c Release -- --filter *DependencyDagBenchmarks* --job short --warmupCount 1 `
  --iterationCount 3 --artifacts artifacts/benchmarks-dependency-dag
```

Generated BenchmarkDotNet reports under `artifacts/benchmarks-dependency-dag` are intentionally not
versioned.
