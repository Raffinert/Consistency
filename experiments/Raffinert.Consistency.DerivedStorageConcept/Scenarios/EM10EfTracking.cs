using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM10EfTracking
{
    public static async Task RunAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StorageDbContext>().UseSqlite(connection).Options;
        await using var db = new StorageDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var fixture = ModelDFixture.Create();
        _ = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        db.Attach(fixture.Link);
        db.ChangeTracker.AcceptAllChanges();
        fixture.ChangeInvoicePrice(55m);
        db.ChangeTracker.DetectChanges();
        db.Entry(fixture.Link).Property(x => x.PriceRate).IsModified = false;

        var evaluated = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        db.ChangeTracker.DetectChanges();
        ModelDAssertions.Require(evaluated == 5.5m && fixture.Link.PriceRate == 6m,
            "EM10 Evaluate leaves the tracked mirror unchanged.");
        ModelDAssertions.Require(!db.Entry(fixture.Link).Property(x => x.PriceRate).IsModified,
            "EM10 Evaluate does not mark PriceRate modified.");

        var materialized = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        db.ChangeTracker.DetectChanges();
        ModelDAssertions.Require(materialized == 5.5m && fixture.Link.PriceRate == 5.5m,
            "EM10 Materialize explicitly changes the tracked mirror.");
        ModelDAssertions.Require(db.Entry(fixture.Link).Property(x => x.PriceRate).IsModified,
            "EM10 Materialize naturally marks PriceRate modified.");
    }
}
