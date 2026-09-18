using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class OM14PersistenceOrchestration
{
    public static async Task RunAsync()
    {
        var targeted = ModelDFixture.Create();
        targeted.Prime();
        targeted.ChangeInvoicePrice(55m);
        targeted.Runtime.ResetMaterializationDiagnostics();
        _ = targeted.Runtime.Materialize(targeted.Model.PriceRate, targeted.Link);
        ModelDAssertions.Require(
            targeted.Runtime.MaterializationDiagnostics.TargetedTargetsVisited == 1 &&
            targeted.Runtime.MaterializationDiagnostics.ObjectTargetsVisited == 0,
            "OM14 targeted orchestration visits exactly the affected definition.");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StorageDbContext>().UseSqlite(connection).Options;
        await using (var db = new StorageDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var fixture = ModelDFixture.Create();
            fixture.Prime();
            db.Add(fixture.Link);
            await db.SaveChangesAsync();
            fixture.ChangeInvoicePrice(55m);
            fixture.Runtime.ResetMaterializationDiagnostics();
            fixture.Runtime.Materialize(fixture.Link);
            await db.SaveChangesAsync();
            ModelDAssertions.Require(fixture.Runtime.MaterializationDiagnostics.ObjectTargetsVisited == 5,
                "OM14 source-wide persistence orchestration visits all k link targets.");
        }

        await using var verification = new StorageDbContext(options);
        var persisted = await verification.Links.AsNoTracking().SingleAsync();
        ModelDAssertions.Require(persisted.PriceRate == 5.5m && persisted.UnitRate == 11m,
            "OM14 adapter-owned source-wide boundary persists all link mirrors.");
    }
}
