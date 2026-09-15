# EF Core consistency benchmark results

Date: 2026-09-15  
Baseline commit: `8283f7f`  
BenchmarkDotNet: 0.15.8, ShortRun (3 measured iterations)  
Environment: Windows 11, Intel Core Ultra 9 275HX, .NET 10.0.12, workstation GC

These exploratory measurements separate EF tracked-state work from Relations affected-plan work. They
are not release-grade statistical runs; the short job reported wide confidence intervals.

| Component | Population | Detail | Mean | Allocated |
|---|---:|---|---:|---:|
| EF capture | 10,000 | Summary | 42.849 ms | 19.61 MB |
| EF capture | 10,000 | Causal | 45.389 ms | 19.61 MB |
| EF capture | 100,000 | Summary | 50.103 ms | 196.08 MB |
| EF capture | 100,000 | Causal | 49.416 ms | 196.08 MB |
| Relations affected plan | 10,000 | Summary | 351.0 us | 31.98 KB |
| Relations affected plan | 10,000 | Causal | 502.8 us | 37.77 KB |
| Relations affected plan | 100,000 | Summary | 471.1 us | 31.98 KB |
| Relations affected plan | 100,000 | Causal | 476.1 us | 37.77 KB |
| Mirror write + EF DetectChanges | 10,000 | Summary | 14.101 ms | 3.59 MB |
| Mirror write + EF DetectChanges | 10,000 | Causal | 5.245 ms | 3.59 MB |
| Mirror write + EF DetectChanges | 100,000 | Summary | 11.957 ms | 35.86 MB |
| Mirror write + EF DetectChanges | 100,000 | Causal | 12.539 ms | 35.86 MB |

## Interpretation

EF capture and `DetectChanges` enumerate tracked entries, so their allocations scale with the tracked
population. They must not be described as affected-wave complexity. Once capture/preparation is excluded,
the Relations affected plan for one touched source remains approximately flat from 10k to 100k registered
sources, including allocations. Summary versus Causal changes the retained result detail but does not cause
a global source scan.

Reproduce with:

```powershell
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks/Raffinert.Relations.Benchmarks.csproj `
  -c Release -- --filter "*EfCoreConsistencyBenchmarks*" --job short --exporters markdown
```
