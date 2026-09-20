using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.AllocationDogfood;

internal static class EfMutationCaptureBenchmark
{
    private static readonly int[] Sizes = [100, 1_000, 10_000];

    public static void Run()
    {
        Console.WriteLine("EF one-fingerprint capture benchmark (5 measured iterations; no external discovery)");
        Console.WriteLine("size | capture ms | bytes | entries | refs/colls | ref candidates/lookups | coll candidates/lookups | mutations");
        foreach (var size in Sizes)
        {
            using var fixture = BenchmarkFixture.Create(size);
            _ = fixture.Measure();
            var values = Enumerable.Range(0, 5).Select(_ => fixture.Measure()).ToArray();
            var median = values.OrderBy(value => value.ElapsedMs).ElementAt(values.Length / 2);
            Console.WriteLine(
                $"{size,5} | {median.ElapsedMs,10:0.000} | {median.Bytes,12:0} | " +
                $"{median.Diagnostics.TrackedEntries,7} | " +
                $"{median.Diagnostics.ReferenceNavigationsVisited,5}/" +
                $"{median.Diagnostics.CollectionNavigationsVisited,-5} | " +
                $"{median.Diagnostics.ReferenceCandidateChecks,14}/" +
                $"{median.Diagnostics.ReferenceIndexLookups,-7} | " +
                $"{median.Diagnostics.CollectionCandidateChecks,15}/" +
                $"{median.Diagnostics.CollectionIndexLookups,-7} | " +
                $"{median.MutationCount,9}");
            Console.WriteLine(
                $"      phases ms detect={median.Diagnostics.DetectChangesMilliseconds:0.000}, " +
                $"policy={median.Diagnostics.PolicyValidationMilliseconds:0.000}, " +
                $"navigation={median.Diagnostics.NavigationCaptureMilliseconds:0.000}, " +
                $"scalar={median.Diagnostics.ScalarCaptureMilliseconds:0.000}, " +
                $"discovery={median.Diagnostics.ExternalDiscoveryMilliseconds:0.000}, " +
                $"fingerprint={median.Diagnostics.FingerprintConstructionMilliseconds:0.000}");
        }
    }

    private sealed record Measurement(
        double ElapsedMs,
        long Bytes,
        int MutationCount,
        EfFingerprintDiagnostics Diagnostics);

    private sealed class BenchmarkFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AllocationDbContext _db;
        private readonly AllocationConsistencyModel _model;
        private readonly ConsistencyRuntime _runtime;
        private readonly ConsistencySaveOptions _options;

        private BenchmarkFixture(
            SqliteConnection connection,
            AllocationDbContext db,
            AllocationConsistencyModel model,
            ConsistencyRuntime runtime,
            ConsistencySaveOptions options)
        {
            _connection = connection;
            _db = db;
            _model = model;
            _runtime = runtime;
            _options = options;
        }

        public static BenchmarkFixture Create(int size)
        {
            var model = new AllocationConsistencyModel();
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var db = new AllocationDbContext(
                new DbContextOptionsBuilder<AllocationDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var day = new DateOnly(2026, 1, 15);
            var demand = new Demand { Id = 1, ResourceCode = "A", Date = day, RequestedQuantity = size };
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
            db.AddRange(demand, first, second);
            db.AddRange(Enumerable.Range(1, size).Select(id => new Allocation
            {
                Id = id,
                Demand = demand,
                Supply = first,
                Quantity = 1m
            }));
            db.SaveChanges();
            var runtime = model.Compiled.CreateRuntime(seed =>
            {
                seed.Add(model.Demands, db.Demands.Local);
                seed.Add(model.Supplies, db.Supplies.Local);
                seed.Add(model.Allocations, db.Allocations.Local);
                seed.Add(model.Fulfillments, db.Fulfillments.Local);
            });
            first.Capacity = size - 1m;
            return new BenchmarkFixture(
                connection,
                db,
                model,
                runtime,
                new ConsistencySaveOptions { Scope = model.CompleteScope() });
        }

        public Measurement Measure()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var diagnostics = new EfFingerprintDiagnostics();
            var start = Stopwatch.GetTimestamp();
            var fingerprint = ConsistencyCoordinator.CaptureFingerprint(
                _db,
                _runtime,
                _model.Mappings,
                _options,
                IncludeSemanticProperty,
                diagnostics);
            var elapsed = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
            return new Measurement(
                elapsed,
                GC.GetAllocatedBytesForCurrentThread() - before,
                fingerprint.Count,
                diagnostics);
        }

        private bool IncludeSemanticProperty(
            Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
            Microsoft.EntityFrameworkCore.Metadata.IProperty property)
        {
            var member = property.PropertyInfo;
            if (member is null)
                return true;
            var mapping = _model.Mappings.UnitOfWorkMappings.Resolve(entry);
            return _runtime.GetTrackedMemberUsage(mapping?.SetDefinition, member) !=
                ConsistencyRuntime.ModelMemberUsageKind.None;
        }

        public void Dispose()
        {
            _db.Dispose();
            _connection.Dispose();
        }
    }
}
