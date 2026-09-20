using System.Diagnostics;

namespace Raffinert.Consistency.AllocationDogfood;

internal static class ProposedStateBenchmark
{
    private static readonly int[] Sizes = [100, 1_000, 10_000];

    public static void Run()
    {
        Console.WriteLine("Core proposed-state repair query benchmark (5 measured iterations; lower is better)");
        Console.WriteLine("size | full reseed setup/query ms | preview setup/query ms | full bytes | preview bytes");
        foreach (var size in Sizes)
        {
            var fixture = BenchmarkFixture.Create(size);
            _ = fixture.MeasureBaseline();
            _ = fixture.MeasurePreview();
            var baseline = Enumerable.Range(0, 5).Select(_ => fixture.MeasureBaseline()).ToArray();
            var preview = Enumerable.Range(0, 5).Select(_ => fixture.MeasurePreview()).ToArray();
            Console.WriteLine(
                $"{size,5} | {Median(baseline, value => value.SetupMs),8:0.000}/" +
                $"{Median(baseline, value => value.QueryMs),8:0.000} | " +
                $"{Median(preview, value => value.SetupMs),8:0.000}/" +
                $"{Median(preview, value => value.QueryMs),8:0.000} | " +
                $"{Median(baseline, value => value.Bytes),10:0} | " +
                $"{Median(preview, value => value.Bytes),10:0}");
        }
    }

    private static double Median<T>(IReadOnlyList<T> values, Func<T, double> selector) =>
        values.Select(selector).OrderBy(value => value).ElementAt(values.Count / 2);

    private sealed record Measurement(double SetupMs, double QueryMs, long Bytes);

    private sealed class BenchmarkFixture
    {
        private BenchmarkFixture(
            AllocationConsistencyModel model,
            Supply supply,
            Demand[] demands,
            Supply[] supplies,
            Allocation[] allocations,
            Fulfillment[] fulfillments,
            ConsistencyRuntime runtime,
            PreparedImpactPlan plan)
        {
            Model = model;
            Supply = supply;
            Demands = demands;
            Supplies = supplies;
            Allocations = allocations;
            Fulfillments = fulfillments;
            Runtime = runtime;
            Plan = plan;
        }

        private AllocationConsistencyModel Model { get; }
        private Supply Supply { get; }
        private Demand[] Demands { get; }
        private Supply[] Supplies { get; }
        private Allocation[] Allocations { get; }
        private Fulfillment[] Fulfillments { get; }
        private ConsistencyRuntime Runtime { get; }
        private PreparedImpactPlan Plan { get; }

        public static BenchmarkFixture Create(int size)
        {
            var model = new AllocationConsistencyModel();
            var day = new DateOnly(2026, 1, 15);
            var demand = new Demand
            {
                Id = 1,
                ResourceCode = "A",
                Date = day,
                RequestedQuantity = size
            };
            var supply = new Supply
            {
                Id = 1,
                ResourceCode = "A",
                Date = day,
                Capacity = size + 1m
            };
            var supplies = new[] { supply, new Supply
            {
                Id = 2,
                ResourceCode = "A",
                Date = day,
                Capacity = size + 1m
            }};
            var allocations = Enumerable.Range(0, size)
                .Select(id => new Allocation
                {
                    Id = id + 1,
                    DemandId = demand.Id,
                    Demand = demand,
                    SupplyId = supply.Id,
                    Supply = supply,
                    Quantity = 1m
                }).ToArray();
            var demands = new[] { demand };
            var fulfillments = Array.Empty<Fulfillment>();
            var runtime = model.Compiled.CreateRuntime(seed =>
            {
                seed.Add(model.Demands, demands);
                seed.Add(model.Supplies, supplies);
                seed.Add(model.Allocations, allocations);
                seed.Add(model.Fulfillments, fulfillments);
            });
            supply.Capacity = size - 1m;
            var plan = runtime.PlanDetailed(
                runtime.Prepare(MutationSet.Create(Change.Property(
                    model.Supplies, supply, value => value.Capacity, size + 1m, size - 1m))),
                RuntimeImpactDetailLevel.Summary,
                PlannedInvariantEvaluationMode.Affected,
                PlannedDerivedEvaluationMode.Affected);
            return new BenchmarkFixture(
                model, supply, demands, supplies, allocations, fulfillments, runtime, plan);
        }

        public Measurement MeasureBaseline()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var setupStart = Stopwatch.GetTimestamp();
            var baseline = Model.Compiled.CreateRuntime(seed =>
            {
                seed.Add(Model.Demands, Demands);
                seed.Add(Model.Supplies, Supplies);
                seed.Add(Model.Allocations, Allocations);
                seed.Add(Model.Fulfillments, Fulfillments);
            });
            var setupMs = ElapsedMilliseconds(setupStart);
            var queryStart = Stopwatch.GetTimestamp();
            RunRepairDecision(baseline);
            var queryMs = ElapsedMilliseconds(queryStart);
            return new Measurement(setupMs, queryMs,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        public Measurement MeasurePreview()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var setupStart = Stopwatch.GetTimestamp();
            using var preview = Runtime.CreatePreview(Plan);
            var setupMs = ElapsedMilliseconds(setupStart);
            var queryStart = Stopwatch.GetTimestamp();
            RunRepairDecision(preview);
            var queryMs = ElapsedMilliseconds(queryStart);
            return new Measurement(setupMs, queryMs,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        private static double ElapsedMilliseconds(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;

        private void RunRepairDecision(ConsistencyRuntime runtime)
        {
            _ = runtime.Evaluate(Model.CapacityInvariant, Supply);
            var allocation = runtime.Related(Model.SupplyAllocations, Supply)
                .OrderBy(value => value.Id).First();
            var replacement = runtime.Related(Model.CandidateSupplies, allocation.Demand)
                .Where(value => value.Id != allocation.SupplyId)
                .OrderBy(value => value.Id)
                .First(value => runtime.Evaluate(Model.RemainingCapacity, value) >= allocation.Quantity);
            _ = replacement;
            _ = runtime.Evaluate(Model.CompatibilityInvariant, allocation);
        }

        private void RunRepairDecision(ConsistencyPreview preview)
        {
            _ = preview.Evaluate(Model.CapacityInvariant, Supply);
            var allocation = preview.Related(Model.SupplyAllocations, Supply)
                .OrderBy(value => value.Id).First();
            var replacement = preview.Related(Model.CandidateSupplies, allocation.Demand)
                .Where(value => value.Id != allocation.SupplyId)
                .OrderBy(value => value.Id)
                .First(value => preview.Evaluate(Model.RemainingCapacity, value) >= allocation.Quantity);
            _ = replacement;
            _ = preview.Evaluate(Model.CompatibilityInvariant, allocation);
        }
    }
}
