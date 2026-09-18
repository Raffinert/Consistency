using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM13EfTracking
{
    public static async Task RunAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StorageDbContext>().UseSqlite(connection).Options;
        await using var db = new StorageDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = ModelDFixture.Create();
        fixture.Prime();
        db.Attach(fixture.Link);
        db.ChangeTracker.AcceptAllChanges();
        fixture.ChangeInvoicePrice(55m);
        ObjectMaterializeScenarioSupport.EvaluateAllLinkTargets(fixture);

        fixture.Link.PriceRate = 5.5m;
        var price = db.Entry(fixture.Link).Property(x => x.PriceRate);
        price.OriginalValue = 5.5m;
        price.IsModified = false;
        fixture.Runtime.Materialize(fixture.Link);
        db.ChangeTracker.DetectChanges();

        ModelDAssertions.Require(!price.IsModified,
            "OM13 an already-equal target is not marked modified by object Materialize.");
        ModelDAssertions.Require(db.Entry(fixture.Link).Property(x => x.UnitRate).IsModified,
            "OM13 a changed target is naturally marked modified.");
        ModelDAssertions.Require(!db.Entry(fixture.Link).Property(x => x.InvoiceLineId).IsModified,
            "OM13 unrelated scalar properties remain untouched.");
    }
}
