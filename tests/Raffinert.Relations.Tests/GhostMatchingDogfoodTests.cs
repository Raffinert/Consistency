namespace Raffinert.Relations.Tests;

public sealed class GhostMatchingDogfoodTests
{
    [Fact]
    public void Poil_lgr_sum_reports_precommit_violation_and_discard_preserves_runtime()
    {
        var model = new RelationModelBuilder();
        var poils = model.Objects<Poil>().Named("poils").Key(x => x.Id);
        var lgrs = model.Objects<Lgr>().Named("lgrs").Key(x => x.Id);
        var links = model.Relation(poils, lgrs).Where((poil, lgr) => poil.Id == lgr.PoilId).Named("poil-lgrs");
        var sum = model.Derived(poils).Using(links).Incrementally()
            .Compute((_, rows) => rows.Sum(x => x.Quantity)).Named("lgr-sum");
        var balanced = model.Derived(poils).Using(sum)
            .Compute((poil, total) => poil.Unknown || poil.LinkedQuantity == total).Named("poil-balanced");
        var invariant = model.Invariant(poils).Using(balanced)
            .Must((_, valid) => valid).Named("poil-balance-invariant");
        var poil = new Poil { LinkedQuantity = 5 };
        var lgr = new Lgr { PoilId = poil.Id, Quantity = 5 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(poils, [poil]); seed.Add(lgrs, [lgr]); });
        Assert.True(runtime.Evaluate(invariant, poil));
        var diagnostics = runtime.Diagnostics;
        lgr.Quantity = 4;

        var rejected = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(lgrs, lgr, x => x.Quantity, 5m, 4m))), RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.Affected);

        Assert.True(rejected.HasInvariantViolations);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(diagnostics.PredicateEvaluations, runtime.Diagnostics.PredicateEvaluations);
        Assert.Equal(diagnostics.DerivedFullRecomputations, runtime.Diagnostics.DerivedFullRecomputations);
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, poil));

        poil.LinkedQuantity = 4;
        var accepted = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(lgrs, lgr, x => x.Quantity, 5m, 4m),
            Change.Property(poils, poil, x => x.LinkedQuantity, 5m, 4m))),
            RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);
        Assert.False(accepted.HasInvariantViolations);
        runtime.Commit(accepted);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Composite_bookkeeping_relation_retargets_in_planned_final_state()
    {
        var model = new RelationModelBuilder();
        var lgrs = model.Objects<Lgr>().Named("retarget-lgrs").Key(x => x.Id);
        var rows = model.Objects<Polgr>().Named("polgrs").Key(x => x.Id);
        var matches = model.Relation(lgrs, rows).Where((lgr, row) =>
            lgr.PurchaseOrderLineId == row.PurchaseOrderLineId && lgr.GoodsReceiptId == row.GoodsReceiptId)
            .Named("matching-polgr");
        var count = model.Derived(lgrs).Using(matches).Incrementally()
            .Compute((_, values) => values.Count()).Named("matching-polgr-count");
        var invariant = model.Invariant(lgrs).Using(count)
            .Must((_, value) => value > 0).Named("lgr-existence-invariant");
        var lineId = Guid.NewGuid();
        var lgr = new Lgr { PoilId = Guid.NewGuid(), PurchaseOrderLineId = lineId, GoodsReceiptId = 10 };
        var oldRow = new Polgr { PurchaseOrderLineId = lineId, GoodsReceiptId = 10 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lgrs, [lgr]); seed.Add(rows, [oldRow]); });
        Assert.True(runtime.Evaluate(invariant, lgr));
        lgr.GoodsReceiptId = 11;

        var rejected = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(lgrs, lgr, x => x.GoodsReceiptId, 10L, 11L))),
            RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);
        Assert.True(rejected.HasInvariantViolations);

        var replacement = new Polgr { PurchaseOrderLineId = lineId, GoodsReceiptId = 11 };
        var accepted = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(lgrs, lgr, x => x.GoodsReceiptId, 10L, 11L), Change.Add(rows, replacement))),
            RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);
        Assert.False(accepted.HasInvariantViolations);
    }

    private sealed class Poil { public Guid Id { get; } = Guid.NewGuid(); public decimal LinkedQuantity { get; set; } public bool Unknown { get; set; } }
    private sealed class Lgr { public Guid Id { get; } = Guid.NewGuid(); public Guid PoilId { get; init; } public Guid PurchaseOrderLineId { get; init; } public long GoodsReceiptId { get; set; } public decimal Quantity { get; set; } }
    private sealed class Polgr { public Guid Id { get; } = Guid.NewGuid(); public Guid PurchaseOrderLineId { get; init; } public long GoodsReceiptId { get; init; } }
}
