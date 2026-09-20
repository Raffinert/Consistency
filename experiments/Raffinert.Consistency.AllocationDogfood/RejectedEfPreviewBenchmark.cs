using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.AllocationDogfood;

internal static class RejectedEfPreviewBenchmark
{
    private static readonly int[] Sizes = [100, 1_000, 10_000];

    public static void Run()
    {
        Console.WriteLine("EF rejected-preview repair query benchmark (5 measured iterations; lower is better)");
        Console.WriteLine("size | rejection+preview/query ms | allocated bytes | reads | fingerprint validations");
        foreach (var size in Sizes)
        {
            using var fixture = BenchmarkFixture.Create(size);
            _ = fixture.Measure();
            var values = Enumerable.Range(0, 5).Select(_ => fixture.Measure()).ToArray();
            Console.WriteLine(
                $"{size,5} | {Median(values, value => value.SetupMs),9:0.000}/" +
                $"{Median(values, value => value.QueryMs),8:0.000} | " +
                $"{Median(values, value => value.Bytes),15:0} | " +
                $"{Median(values, value => value.Reads),5:0} | " +
                $"{Median(values, value => value.Validations),23:0}");
        }
    }

    private static double Median<T>(IReadOnlyList<T> values, Func<T, double> selector) =>
        values.Select(selector).OrderBy(value => value).ElementAt(values.Count / 2);

    private sealed record Measurement(
        double SetupMs,
        double QueryMs,
        long Bytes,
        int Reads,
        long Validations);

    private sealed class BenchmarkFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly AllocationDbContext _db;
        private readonly ConsistencyEfCoreSession<AllocationDbContext> _session;
        private readonly AllocationConsistencyModel _model;
        private readonly Supply _supply;

        private BenchmarkFixture(
            SqliteConnection connection,
            ServiceProvider provider,
            IServiceScope scope,
            AllocationDbContext db,
            ConsistencyEfCoreSession<AllocationDbContext> session,
            AllocationConsistencyModel model,
            Supply supply)
        {
            _connection = connection;
            _provider = provider;
            _scope = scope;
            _db = db;
            _session = session;
            _model = model;
            _supply = supply;
        }

        public static BenchmarkFixture Create(int size)
        {
            var model = new AllocationConsistencyModel();
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<AllocationDbContext>()
                .UseSqlite(connection).Options;
            using (var seed = new AllocationDbContext(options))
            {
                seed.Database.EnsureCreated();
                var day = new DateOnly(2026, 1, 15);
                var demand = new Demand
                {
                    Id = 1,
                    ResourceCode = "A",
                    Date = day,
                    RequestedQuantity = size
                };
                var first = new Supply
                {
                    Id = 1,
                    ResourceCode = "A",
                    Date = day,
                    Capacity = size + 1m,
                    AllocatedQuantity = size,
                    RemainingCapacity = 1m
                };
                var second = new Supply
                {
                    Id = 2,
                    ResourceCode = "A",
                    Date = day,
                    Capacity = size + 1m,
                    RemainingCapacity = size + 1m
                };
                seed.AddRange(demand, first, second);
                seed.AddRange(Enumerable.Range(1, size).Select(id => new Allocation
                {
                    Id = id,
                    Demand = demand,
                    Supply = first,
                    Quantity = 1m
                }));
                seed.SaveChanges();
            }

            var services = new ServiceCollection();
            services.AddDbContext<AllocationDbContext>(builder => builder.UseSqlite(connection));
            services.AddRaffinertConsistency<AllocationDbContext>(
                model.Compiled,
                model.Mappings,
                new ConsistencySaveOptions { Scope = model.CompleteScope() });
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
            var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();
            db.Demands.Load();
            db.Supplies.Load();
            db.Allocations.Include(value => value.Demand).Include(value => value.Supply).Load();
            db.Fulfillments.Include(value => value.Supply).Load();
            var supply = db.Supplies.Single(value => value.Id == 1);
            supply.Capacity = size - 1m;
            _ = runtime;
            return new BenchmarkFixture(
                connection,
                provider,
                scope,
                db,
                scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<AllocationDbContext>>(),
                model,
                supply);
        }

        public Measurement Measure()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var validations = _session.RejectedPreviewValidationCount;
            var setupStart = Stopwatch.GetTimestamp();
            ConsistencyInvariantViolationException error;
            try
            {
                _db.SaveChanges();
                throw new InvalidOperationException("The benchmark mutation unexpectedly satisfied the invariant.");
            }
            catch (ConsistencyInvariantViolationException rejected)
            {
                error = rejected;
            }
            using var preview = _session.CreateRejectedPreview(error);
            var setupMs = ElapsedMilliseconds(setupStart);
            var queryStart = Stopwatch.GetTimestamp();
            var reads = RunRepairDecision(preview);
            var queryMs = ElapsedMilliseconds(queryStart);
            return new Measurement(
                setupMs,
                queryMs,
                GC.GetAllocatedBytesForCurrentThread() - before,
                reads,
                _session.RejectedPreviewValidationCount - validations);
        }

        private int RunRepairDecision(ConsistencyPreview preview)
        {
            var reads = 0;
            _ = preview.Evaluate(_model.CapacityInvariant, _supply);
            reads++;
            var allocation = preview.Related(_model.SupplyAllocations, _supply)
                .OrderBy(value => value.Id).First();
            reads++;
            var candidates = preview.Related(_model.CandidateSupplies, allocation.Demand)
                .Where(value => value.Id != allocation.SupplyId)
                .OrderBy(value => value.Id);
            reads++;
            foreach (var candidate in candidates)
            {
                reads++;
                if (preview.Evaluate(_model.RemainingCapacity, candidate) >= allocation.Quantity)
                    break;
            }
            _ = preview.Evaluate(_model.CompatibilityInvariant, allocation);
            reads++;
            return reads;
        }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
            _connection.Dispose();
        }

        private static double ElapsedMilliseconds(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
    }
}
