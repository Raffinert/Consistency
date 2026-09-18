using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM11PersistenceWithoutExplicitMaterialize
{
    public static async Task RunAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StorageDbContext>().UseSqlite(connection).Options;
        await using (var db = new StorageDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var fixture = ModelDFixture.Create();
            _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
            db.Attach(fixture.Link);
            db.Entry(fixture.Link).State = EntityState.Added;
            db.Entry(fixture.Link.InvoiceLine).State = EntityState.Added;
            db.Entry(fixture.Link.PurchaseOrderLine).State = EntityState.Added;
            await db.SaveChangesAsync();

            fixture.ChangeInvoicePrice(55m);
            await PersistenceBoundary.SaveChangesAsync(db, fixture);
        }

        await using var verification = new StorageDbContext(options);
        var persisted = await verification.Links.AsNoTracking().SingleAsync();
        ModelDAssertions.Require(persisted.PriceRate == 5.5m,
            "EM11 adapter-owned persistence boundary materializes without an application ritual.");
    }

    private static class PersistenceBoundary
    {
        public static Task<int> SaveChangesAsync(StorageDbContext db, ModelDFixture fixture)
        {
            _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
            return db.SaveChangesAsync();
        }
    }
}
