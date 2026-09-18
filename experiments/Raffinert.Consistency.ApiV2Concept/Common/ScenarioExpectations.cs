using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.ApiV2Concept.Common;

internal sealed record ScenarioModel(
    string Candidate,
    CompiledConsistencyModel Compiled,
    ObjectSet<SimpleLine> SimpleLines,
    Derived<SimpleLine, decimal> SimpleRemaining,
    ObjectSet<Association> Associations,
    Derived<Association, decimal?> AssociationUnitRate,
    ObjectSet<OrderLine> Lines,
    ObjectSet<Fulfillment> Fulfillments,
    ObjectSet<Allocation> Allocations,
    Derived<OrderLine, decimal> FulfilledQuantity,
    Derived<OrderLine, decimal> RemainingQuantity,
    Derived<Allocation, bool> AllocationValidity,
    Invariant<Allocation> AllocationInvariant,
    List<Guid> Repairs)
{
    public ConsistencyEfCoreMappings CreateMappings() => new ConsistencyEfCoreMappings()
        .Map(SimpleLines)
        .Map(Associations)
        .Map(Lines)
        .Map(Fulfillments)
        .Map(Allocations)
        .Materialize(AssociationUnitRate, x => x.UnitRate)
        .Enforce(AllocationInvariant)
        .DiscoverConsumers(Associations, x => x.SourceItem, (db, sources) =>
        {
            var ids = sources.Select(x => x.Id).ToArray();
            return db.Set<Association>()
                .Where(x => ids.Contains(x.SourceItemId))
                .Include(x => x.SourceItem)
                .Include(x => x.TargetItem);
        })
        .DiscoverConsumers(Associations, x => x.TargetItem, (db, targets) =>
        {
            var ids = targets.Select(x => x.Id).ToArray();
            return db.Set<Association>()
                .Where(x => ids.Contains(x.TargetItemId))
                .Include(x => x.SourceItem)
                .Include(x => x.TargetItem);
        });
}

internal static class ScenarioExpectations
{
    public static async Task RunAllAsync(IEnumerable<ScenarioModel> candidates)
    {
        foreach (var candidate in candidates)
        {
            RunCoreScenarios(candidate);
            await RunEfScenariosAsync(candidate);
            Console.WriteLine($"  {candidate.Candidate}: S1-S9 passed");
        }
    }

    private static void RunCoreScenarios(ScenarioModel model)
    {
        var simple = new SimpleLine { Id = 1, OrderedQuantity = 10m, FulfilledQuantity = 3m };
        var line = new OrderLine { OrderedQuantity = 10m, UnitRate = 25m };
        var fulfillment = new Fulfillment { Quantity = 5m };
        var allocation = new Allocation
        {
            OrderLine = line,
            OrderLineId = line.Id,
            ReservedQuantity = 4m,
            CapturedRate = 25m
        };
        var runtime = model.Compiled.CreateRuntime(seed =>
        {
            seed.Add(model.SimpleLines, [simple]);
            seed.Add(model.Lines, [line]);
            seed.Add(model.Fulfillments, [fulfillment]);
            seed.Add(model.Allocations, [allocation]);
        });

        Require(runtime.Get(model.SimpleRemaining, simple) == 7m, model, "S1 direct derived value");
        Require(runtime.Get(model.FulfilledQuantity, line) == 5m, model, "S3 relation Sum");
        Require(runtime.Get(model.RemainingQuantity, line) == 5m, model, "S4 derived composition");
        Require(runtime.Get(model.AllocationValidity, allocation), model, "S5 projected dependency");
        Require(runtime.Evaluate(model.AllocationInvariant, allocation), model, "S7 initial invariant");

        line.OrderedQuantity = 12m;
        runtime.Apply(Change.Property(model.Lines, line, x => x.OrderedQuantity, 10m, 12m));
        Require(runtime.GetState(model.RemainingQuantity, line) == DerivedValueState.Dirty,
            model, "S6 increase is Dirty");
        _ = runtime.Get(model.AllocationValidity, allocation);
        _ = runtime.Evaluate(model.AllocationInvariant, allocation);
        model.Repairs.Clear();

        line.OrderedQuantity = 4m;
        var decrease = runtime.ApplyDetailed(
            MutationSet.Create(Change.Property(model.Lines, line, x => x.OrderedQuantity, 12m, 4m)),
            RuntimeImpactDetailLevel.Causal);
        Require(runtime.GetState(model.RemainingQuantity, line) == DerivedValueState.Invalid,
            model, "S6 decrease is Invalid");
        Require(decrease.Result.RepairRequests.Count == 1, model, "S7 repair requested once");
        decrease.Dispatch.Invoke();
        Require(model.Repairs.SequenceEqual([allocation.Id]), model,
            $"S7 deferred repair dispatch (callbacks: {string.Join(",", model.Repairs)})");

        model.Repairs.Clear();
        _ = runtime.Get(model.AllocationValidity, allocation);
        _ = runtime.Evaluate(model.AllocationInvariant, allocation);
        fulfillment.Cancelled = true;
        runtime.Apply(Change.Property(model.Fulfillments, fulfillment, x => x.Cancelled, false, true));
        Require(runtime.GetState(model.FulfilledQuantity, line) == DerivedValueState.Invalid,
            model, "S3/S6 cancellation invalidates aggregate");
        Require(model.Repairs.SequenceEqual([allocation.Id]), model, "S5 reverse routing reaches repair");

        Require(model.SimpleRemaining.DefinitionKey == "simple-remaining" &&
                model.AssociationUnitRate.DefinitionKey == "association-unit-rate" &&
                model.AllocationInvariant.DefinitionKey == "allocation-validity-invariant",
            model, "S9 stable names");
        Require(model.Compiled.DebugView.Contains("IncrementalSum(Fulfillment.Quantity)", StringComparison.Ordinal),
            model, "S3 recognized Sum selects the existing incremental plan");
    }

    private static async Task RunEfScenariosAsync(ScenarioModel model)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DogfoodDbContext>().UseSqlite(connection).Options;
        await using var db = new DogfoodDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var sourceA = new SourceItem { Id = 1, UnitValue = 12m };
        var sourceB = new SourceItem { Id = 2, UnitValue = 30m };
        var targetA = new TargetItem { Id = 10, UnitValue = 4m };
        var targetB = new TargetItem { Id = 11, UnitValue = 5m };
        var first = new Association { Id = 100, SourceItem = sourceA, TargetItem = targetA };
        var second = new Association { Id = 101, SourceItem = sourceA, TargetItem = targetB };
        first.UnitRate = UnitRateCalculator.Calculate(sourceA.UnitValue, targetA.UnitValue);
        second.UnitRate = UnitRateCalculator.Calculate(sourceA.UnitValue, targetB.UnitValue);
        db.AddRange(sourceA, sourceB, targetA, targetB, first, second);
        await db.SaveChangesAsync();

        var runtime = model.Compiled.CreateRuntime(seed => seed.Add(model.Associations, [first, second]));
        var mappings = model.CreateMappings();
        var optionsForSave = new ConsistencySaveOptions
        {
            Scope = new ConsistencyScope()
                .Complete(model.SimpleLines)
                .Complete(model.Associations)
                .Complete(model.Lines)
                .Complete(model.Fulfillments)
                .Complete(model.Allocations)
        };

        sourceA.UnitValue = 20m;
        await db.SaveChangesConsistentlyAsync(runtime, mappings, optionsForSave);
        Require(first.UnitRate == 5m && second.UnitRate == 4m, model,
            "S2 shared nested source fans out and S8 materializes");

        second.SourceItem = sourceB;
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesConsistentlyAsync(runtime, mappings, optionsForSave);
        Require(second.UnitRate == 6m, model, "S2 retargeting updates reverse routing");

        sourceA.UnitValue = 24m;
        await db.SaveChangesConsistentlyAsync(runtime, mappings, optionsForSave);
        Require(first.UnitRate == 6m && second.UnitRate == 6m, model,
            "S2 old target stops routing after retarget");

        var line = new OrderLine { OrderedQuantity = 10m, UnitRate = 25m };
        var fulfillment = new Fulfillment { Quantity = 5m };
        var allocation = new Allocation
        {
            OrderLine = line,
            OrderLineId = line.Id,
            ReservedQuantity = 4m,
            CapturedRate = 25m
        };
        db.AddRange(line, fulfillment, allocation);
        await db.SaveChangesConsistentlyAsync(runtime, mappings, optionsForSave);

        allocation.ReservedQuantity = 6m;
        var enforcementBlockedSave = false;
        try
        {
            await db.SaveChangesConsistentlyAsync(runtime, mappings, optionsForSave);
        }
        catch (ConsistencyInvariantViolationException)
        {
            enforcementBlockedSave = true;
        }
        Require(enforcementBlockedSave, model, "S8 EF enforcement rejects a violated invariant");
    }

    private static void Require(bool condition, ScenarioModel model, string expectation)
    {
        if (!condition)
            throw new InvalidOperationException($"{model.Candidate}: {expectation} failed.");
    }
}
