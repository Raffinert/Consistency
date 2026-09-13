# Basic, summary, and causal apply benchmark

Measured 2026-09-13 with BenchmarkDotNet 0.15.8 on .NET 10.0.12. The short job used one
warmup and three measured iterations, so these figures are regression baselines rather than
general latency claims.

| Changes | Apply | Summary | Causal | Apply allocation | Summary allocation | Causal allocation |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 77.90 us | 90.53 us | 110.57 us | 14.83 KB | 17.52 KB | 18.95 KB |
| 10 | 100.33 us | 99.90 us | 145.87 us | 37.76 KB | 42.63 KB | 55.90 KB |
| 100 | 307.95 us | 389.20 us | 480.03 us | 273.62 KB | 300.68 KB | 431.78 KB |

The allocation separation confirms that basic `Apply` does not build summary records and summary
mode does not build mutation origins or causal records.

```powershell
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks -c Release -- `
  --filter *ApplyDetailBenchmarks* --job short --warmupCount 1 --iterationCount 3
```
