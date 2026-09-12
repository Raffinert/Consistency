# Range planning measurement

Measured with BenchmarkDotNet 0.15.8, .NET 10.0.12, ShortRun, on an Intel Core Ultra 9 275HX.
The predicate was:

```csharp
source.SupplierId == rule.SupplierId &&
rule.ValidFrom <= source.Date &&
source.Date < rule.ValidTo
```

| Rules | Equality hash prefix + range residual | Full scan | Full scan / hash prefix |
| ---: | ---: | ---: | ---: |
| 10,000 | 258.3 ns | 37.87 us | 146.8x |
| 100,000 | 2.197 us | 395.10 us | 179.9x |

The existing equality-prefix plan narrows the candidate population enough that a dedicated range index
is not justified by this workload. Range expressions remain diagnostic residual metadata. Reconsider a
range structure only when a measured workload has low-cardinality/no equality prefixes and demonstrates
material residual-scan cost.

Run again with:

```powershell
dotnet run -c Release --project benchmarks/Raffinert.Relations.Benchmarks -- --filter *RangePlanningBenchmarks* --job short
```
