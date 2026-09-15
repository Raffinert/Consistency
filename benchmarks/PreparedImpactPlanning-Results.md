# Prepared impact planning — touched-patch results

Measured 2026-09-15 at baseline `e25b8b1` (the benchmark harness itself is the next commit).

Environment: Windows 11 25H2, Intel Core Ultra 9 275HX (24 physical/logical cores), .NET SDK 10.0.401, .NET 10.0.12 x64 RyuJIT, concurrent workstation GC, BenchmarkDotNet 0.15.8 `ShortRun` (1 launch, 3 warmups, 3 measured iterations).

The 10k/100k fixture has equal-sized relation and navigation populations and fully populated derived caches. Each operation changes one logical source. Values are managed allocation per operation.

| Operation | Population | Mean | Allocated |
|---|---:|---:|---:|
| CommitDetailed Summary | 10k | 13.636 us | 48.02 KB |
| CommitDetailed Summary | 100k | 15.207 us | 48.13 KB |
| CommitDetailed Causal | 10k | 16.664 us | 54.82 KB |
| CommitDetailed Causal | 100k | 16.778 us | 54.64 KB |
| Preview Summary | 10k | 12.052 us | 45.89 KB |
| Preview Summary | 100k | 14.049 us | 45.89 KB |
| Preview Causal | 10k | 14.700 us | 52.45 KB |
| Preview Causal | 100k | 16.425 us | 52.45 KB |
| Plan Summary | 10k | 17.765 us | 60.02 KB |
| Plan Summary | 100k | 18.334 us | 60.02 KB |
| Plan Causal | 10k | 20.472 us | 66.53 KB |
| Plan Causal | 100k | 20.824 us | 66.53 KB |
| Commit(prebuilt Summary plan) | 10k | 46.73 us | 7.66 KB |
| Commit(prebuilt Summary plan) | 100k | 136.22 us | 7.66 KB |
| Commit(prebuilt Causal plan) | 10k | 42.47 us | 7.66 KB |
| Commit(prebuilt Causal plan) | 100k | 138.40 us | 7.66 KB |

The prebuilt-plan timing uses one invocation per iteration and is consequently noisy (BenchmarkDotNet reports the iterations are below its recommended duration). Its allocation result is stable and is the relevant patch-scope signal.

## Repeatable prepared-install components

Measured 2026-09-15 after adding an internal harness that repeatedly exercises the production validation,
install rollback-journal capture, forward-patch application, and rollback restoration paths. The fixture changes
one source, keeps the touched relation/dependency bucket constant, and does not commit the plan, increment the
runtime version, or dispatch callbacks.

| Component | Population | Mean | Allocated |
|---|---:|---:|---:|
| Validate prepared plan/domain assumptions | 10k | 34.33 ns | 344 B |
| Validate prepared plan/domain assumptions | 100k | 37.61 ns | 344 B |
| Capture install rollback journal | 10k | 4.749 us | 23.45 KB |
| Capture install rollback journal | 100k | 5.021 us | 23.29 KB |
| Apply forward patch + restore rollback journal | 10k | 0.915 us | 2.64 KB |
| Apply forward patch + restore rollback journal | 100k | 1.045 us | 2.64 KB |

These repeatable components are approximately population-independent: the 100k means are 1.10x, 1.06x, and
1.14x their 10k counterparts, and allocations do not grow. None approaches the 2x investigation threshold.
The earlier 47us/136us comparison came from one-shot commits whose iteration setup rebuilt a complete runtime
and plan; it is not a reliable signal of population-dependent installation work. No runtime optimization is
justified by these measurements.

## Projected fan-out

| Downstream population | Fan-out | Mean | Allocated |
|---:|---:|---:|---:|
| 10k | 1 | 4.973 us | 22.05 KB |
| 10k | 10 | 7.275 us | 27.94 KB |
| 10k | 100 | 17.875 us | 96.28 KB |
| 100k | 1 | 5.085 us | 22.11 KB |
| 100k | 10 | 8.542 us | 27.94 KB |
| 100k | 100 | 17.763 us | 96.28 KB |

## Interpretation

One-source Preview and Plan allocations are identical when unrelated population grows from 10k to 100k; they do not approach 10x growth. Projected propagation scales with actual fan-out (1, 10, 100), while holding allocation essentially constant for a 10x increase in unrelated downstream population. Repeatable install-component measurements likewise remain approximately flat. This is the expected touched-patch behavior.

Raw BenchmarkDotNet reports were generated under `artifacts/benchmarks-plan`, `artifacts/benchmarks-install`, and `artifacts/benchmarks-fanout`; those generated artifacts are intentionally not versioned.
