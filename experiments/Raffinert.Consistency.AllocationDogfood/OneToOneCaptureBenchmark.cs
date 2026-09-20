using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.AllocationDogfood;

internal static class OneToOneCaptureBenchmark
{
    private static readonly int[] Sizes = [100, 1_000, 10_000];

    internal static void Run()
    {
        Console.WriteLine("EF FK-only one-to-one capture benchmark (single measured capture)");
        Console.WriteLine(
            "size | capture ms | bytes | refs | lookups/candidates | evidence | cleanup scans | emitted");
        foreach (var size in Sizes)
        {
            using var fixture = Fixture.Create(size);
            var measurement = fixture.Measure();
            Console.WriteLine(
                $"{size,5} | {measurement.ElapsedMs,10:0.000} | {measurement.Bytes,12:0} | " +
                $"{measurement.Diagnostics.ReferenceNavigationsVisited,5} | " +
                $"{measurement.Diagnostics.ReferenceIndexLookups,7}/" +
                $"{measurement.Diagnostics.ReferenceCandidateChecks,-10} | " +
                $"{measurement.Diagnostics.PrincipalReferenceEvidenceCaptured,8} | " +
                $"{measurement.Diagnostics.PrincipalReferenceCleanupScans,13} | " +
                $"{measurement.Diagnostics.PrincipalReferenceChangesEmitted,7}");
        }
    }

    private sealed record Measurement(
        double ElapsedMs,
        long Bytes,
        EfFingerprintDiagnostics Diagnostics);

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly BenchmarkContext _db;
        private readonly ConsistencyUnitOfWorkMappings _mappings;

        private Fixture(
            SqliteConnection connection,
            BenchmarkContext db,
            ConsistencyUnitOfWorkMappings mappings)
        {
            _connection = connection;
            _db = db;
            _mappings = mappings;
        }

        internal static Fixture Create(int size)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var db = new BenchmarkContext(
                new DbContextOptionsBuilder<BenchmarkContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var originals = Enumerable.Range(1, size)
                .Select(index => new Principal { Id = index * 2 - 1 }).ToArray();
            var replacements = Enumerable.Range(1, size)
                .Select(index => new Principal { Id = index * 2 }).ToArray();
            var details = Enumerable.Range(1, size)
                .Select(index => new Detail
                {
                    Id = index,
                    Principal = originals[index - 1]
                }).ToArray();
            for (var index = 0; index < size; index++)
                originals[index].Detail = details[index];
            db.AddRange(originals);
            db.AddRange(replacements);
            db.AddRange(details);
            db.SaveChanges();
            for (var index = 0; index < size; index++)
                details[index].PrincipalId = replacements[index].Id;
            var model = new ConsistencyModelBuilder();
            var mappings = new ConsistencyUnitOfWorkMappings()
                .Map(model.Objects<Principal>().Key(value => value.Id))
                .Map(model.Objects<Detail>().Key(value => value.Id));
            return new Fixture(connection, db, mappings);
        }

        internal Measurement Measure()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var diagnostics = new EfFingerprintDiagnostics();
            var start = Stopwatch.GetTimestamp();
            _ = ChangeTrackerAdapter.CaptureUnitOfWork(
                _db.ChangeTracker, _mappings, static (_, _) => true, diagnostics);
            var elapsed = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
            return new Measurement(
                elapsed,
                GC.GetAllocatedBytesForCurrentThread() - before,
                diagnostics);
        }

        public void Dispose()
        {
            _db.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Principal>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<Detail>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<Principal>().HasOne(value => value.Detail).WithOne(value => value.Principal)
                .HasForeignKey<Detail>(value => value.PrincipalId).IsRequired(false);
        }
    }

    private sealed class Principal
    {
        public int Id { get; set; }
        public Detail? Detail { get; set; }
    }

    private sealed class Detail
    {
        public int Id { get; set; }
        public int? PrincipalId { get; set; }
        public Principal? Principal { get; set; }
    }
}
