using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class D11EfTracking
{
    public static async Task RunAsync()
    {
        foreach (var policy in Harness.Policies)
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<StorageDbContext>().UseSqlite(connection).Options;
            await using var db = new StorageDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var fixture = Fixture.Create(policy);
            fixture.Storage.Materialize(fixture.Model.PriceRateTarget, fixture.Link);
            db.Attach(fixture.Link);
            db.ChangeTracker.AcceptAllChanges();
            fixture.ChangeInvoicePrice(55m);
            db.ChangeTracker.DetectChanges();
            db.Entry(fixture.Link).Property(x => x.PriceRate).IsModified = false;

            _ = fixture.Storage.Get(fixture.Model.PriceRateTarget, fixture.Link);
            db.ChangeTracker.DetectChanges();
            var modifiedByGet = db.Entry(fixture.Link).Property(x => x.PriceRate).IsModified;
            var expected = policy is not ModelARuntimeAuthoritative;
            Harness.Require(modifiedByGet == expected, fixture,
                "D11 EF modification state must reflect whether Get assigns PriceRate.");

            var noRead = Fixture.Create(policy);
            noRead.Storage.Materialize(noRead.Model.PriceRateTarget, noRead.Link);
            await using var secondDb = new StorageDbContext(options);
            secondDb.Attach(noRead.Link);
            secondDb.ChangeTracker.AcceptAllChanges();
            noRead.ChangeInvoicePrice(55m);
            noRead.Storage.Materialize(noRead.Model.PriceRateTarget, noRead.Link);
            secondDb.ChangeTracker.DetectChanges();
            Harness.Require(secondDb.Entry(noRead.Link).Property(x => x.PriceRate).IsModified,
                noRead, "D11 no-read persistence boundary must produce a tracked mirror update.");
        }
    }
}
