namespace Raffinert.Consistency.Tests;

public sealed class ProposedStatePreviewTests
{
    [Fact]
    public void Preview_evaluates_scalar_derived_value_without_changing_committed_cache()
    {
        var fixture = Fixture.Create();
        Assert.Equal(2m, fixture.Runtime.Evaluate(fixture.Remaining, fixture.FirstSupply));
        Assert.True(fixture.Runtime.Evaluate(fixture.CapacityValid, fixture.FirstSupply));
        var version = fixture.Runtime.Version;
        fixture.FirstSupply.Capacity = 6m;
        var plan = fixture.Plan(Change.Property(
            fixture.Supplies, fixture.FirstSupply, value => value.Capacity, 10m, 6m));

        using (var preview = fixture.Runtime.CreatePreview(plan))
        {
            Assert.Equal(-2m, preview.Evaluate(fixture.Remaining, fixture.FirstSupply));
            Assert.Equal(-2m, preview.Evaluate(fixture.Remaining, fixture.FirstSupply));
            Assert.False(preview.Evaluate(fixture.CapacityValid, fixture.FirstSupply));
            Assert.Equal(DerivedValueState.Fresh,
                preview.GetState(fixture.Remaining, fixture.FirstSupply));
        }

        Assert.Equal(version, fixture.Runtime.Version);
        Assert.Equal(2m, fixture.Runtime.Evaluate(fixture.Remaining, fixture.FirstSupply));
        Assert.Equal(InvariantEvaluationState.Valid,
            fixture.Runtime.GetState(fixture.CapacityValid, fixture.FirstSupply));
    }

    [Fact]
    public void Preview_relation_uses_final_membership_without_rebasing_committed_index()
    {
        var fixture = Fixture.Create();
        Assert.Equal([fixture.FirstSupply, fixture.SecondSupply],
            fixture.Runtime.Related(fixture.Candidates, fixture.Demand));
        fixture.SecondSupply.Code = "B";
        var plan = fixture.Plan(Change.Property(
            fixture.Supplies, fixture.SecondSupply, value => value.Code, "A", "B"));

        using var preview = fixture.Runtime.CreatePreview(plan);

        Assert.Equal([fixture.FirstSupply], preview.Related(fixture.Candidates, fixture.Demand));
        Assert.True(fixture.Runtime.HasMaterializedPair(
            fixture.Candidates, fixture.Demand, fixture.SecondSupply));
    }

    [Fact]
    public void Preview_projected_membership_evaluates_final_compatibility()
    {
        var fixture = Fixture.Create();
        Assert.True(fixture.Runtime.Evaluate(fixture.CompatibilityValid, fixture.Allocation));
        fixture.Demand.Code = "B";
        var plan = fixture.Plan(Change.Property(
            fixture.Demands, fixture.Demand, value => value.Code, "A", "B"));

        using var preview = fixture.Runtime.CreatePreview(plan);

        Assert.False(preview.Evaluate(fixture.CompatibilityValid, fixture.Allocation));
        Assert.Equal(InvariantEvaluationState.Valid,
            fixture.Runtime.GetState(fixture.CompatibilityValid, fixture.Allocation));
    }

    [Fact]
    public void Preview_retarget_updates_both_aggregate_owners_in_final_state()
    {
        var fixture = Fixture.Create();
        Assert.Equal(8m, fixture.Runtime.Evaluate(fixture.Allocated, fixture.FirstSupply));
        Assert.Equal(0m, fixture.Runtime.Evaluate(fixture.Allocated, fixture.SecondSupply));
        fixture.Allocation.SupplyId = fixture.SecondSupply.Id;
        fixture.Allocation.Supply = fixture.SecondSupply;
        var plan = fixture.Plan(
            Change.Property(fixture.Allocations, fixture.Allocation,
                value => value.SupplyId, fixture.FirstSupply.Id, fixture.SecondSupply.Id),
            Change.Property(fixture.Allocations, fixture.Allocation,
                value => value.Supply, fixture.FirstSupply, fixture.SecondSupply));

        using var preview = fixture.Runtime.CreatePreview(plan);

        Assert.Equal(0m, preview.Evaluate(fixture.Allocated, fixture.FirstSupply));
        Assert.Equal(8m, preview.Evaluate(fixture.Allocated, fixture.SecondSupply));
        Assert.True(preview.Evaluate(fixture.CapacityValid, fixture.FirstSupply));
        Assert.True(preview.Evaluate(fixture.CapacityValid, fixture.SecondSupply));
        Assert.Equal(8m, fixture.Runtime.Evaluate(fixture.Allocated, fixture.FirstSupply));
        Assert.Equal(0m, fixture.Runtime.Evaluate(fixture.Allocated, fixture.SecondSupply));
    }

    [Fact]
    public void Preview_applies_add_and_remove_lifecycle_to_relation_queries()
    {
        var fixture = Fixture.Create();
        var added = new Supply { Id = 3, Code = "A", Capacity = 20m };
        var plan = fixture.Plan(
            Change.Remove(fixture.Supplies, fixture.SecondSupply),
            Change.Add(fixture.Supplies, added));

        using var preview = fixture.Runtime.CreatePreview(plan);

        Assert.Equal([fixture.FirstSupply, added],
            preview.Related(fixture.Candidates, fixture.Demand));
        Assert.DoesNotContain(added, fixture.Runtime.Related(fixture.Candidates, fixture.Demand));
    }

    [Fact]
    public void Disposing_preview_requires_no_runtime_rollback()
    {
        var fixture = Fixture.Create();
        var version = fixture.Runtime.Version;
        var derivedState = fixture.Runtime.GetState(fixture.Remaining, fixture.FirstSupply);
        fixture.FirstSupply.Capacity = 9m;
        var plan = fixture.Plan(Change.Property(
            fixture.Supplies, fixture.FirstSupply, value => value.Capacity, 10m, 9m));
        var preview = fixture.Runtime.CreatePreview(plan);

        _ = preview.Evaluate(fixture.Remaining, fixture.FirstSupply);
        preview.Dispose();

        Assert.Equal(version, fixture.Runtime.Version);
        Assert.Equal(derivedState, fixture.Runtime.GetState(fixture.Remaining, fixture.FirstSupply));
        Assert.Throws<ObjectDisposedException>(() =>
            preview.Evaluate(fixture.Remaining, fixture.FirstSupply));
    }

    [Fact]
    public void Preview_rejects_use_after_runtime_advances()
    {
        var fixture = Fixture.Create();
        fixture.FirstSupply.Capacity = 9m;
        var plan = fixture.Plan(Change.Property(
            fixture.Supplies, fixture.FirstSupply, value => value.Capacity, 10m, 9m));
        using var preview = fixture.Runtime.CreatePreview(plan);
        fixture.Runtime.Add(fixture.Demands, new Demand { Id = 2, Code = "B" });

        var error = Assert.Throws<InvalidOperationException>(() =>
            preview.Evaluate(fixture.Remaining, fixture.FirstSupply));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_DoesNotGuaranteeDetectionOfUnreportedCorePocoMutation()
    {
        var fixture = Fixture.Create();
        fixture.FirstSupply.Capacity = 9m;
        var plan = fixture.Plan(Change.Property(
            fixture.Supplies, fixture.FirstSupply, value => value.Capacity, 10m, 9m));
        using var preview = fixture.Runtime.CreatePreview(plan);

        fixture.SecondSupply.Capacity = 17m;

        Assert.Equal(17m, preview.Evaluate(fixture.Remaining, fixture.SecondSupply));
        Assert.Equal(0L, fixture.Runtime.Version);
    }

    private sealed class Fixture
    {
        private Fixture()
        {
            var model = new ConsistencyModelBuilder();
            Demands = model.Objects<Demand>().Key(value => value.Id);
            Supplies = model.Objects<Supply>().Key(value => value.Id);
            Allocations = model.Objects<Allocation>().Key(value => value.Id);
            Candidates = model.Relation(Demands, Supplies)
                .Where((demand, supply) => demand.Code == supply.Code);
            SupplyAllocations = model.Relation(Supplies, Allocations)
                .Where((supply, allocation) => supply.Id == allocation.SupplyId);
            Allocated = model.Derived(Supplies).From(SupplyAllocations)
                .Sum(value => value.Quantity);
            Remaining = model.Derived(Supplies).From(Allocated)
                .Select((supply, allocated) => supply.Capacity - allocated);
            CapacityValid = model.Invariant(Supplies).From(Remaining)
                .Must((_, remaining) => remaining >= 0m)
                .RepairWhenViolated();
            Compatible = model.Derived(Allocations).FromMembership(
                Candidates, allocation => allocation.Demand, allocation => allocation.Supply);
            CompatibilityValid = model.Invariant(Allocations).From(Compatible)
                .Must((_, compatible) => compatible)
                .RepairWhenViolated();
            Demand = new Demand { Id = 1, Code = "A" };
            FirstSupply = new Supply { Id = 1, Code = "A", Capacity = 10m };
            SecondSupply = new Supply { Id = 2, Code = "A", Capacity = 20m };
            Allocation = new Allocation
            {
                Id = 1,
                Demand = Demand,
                Supply = FirstSupply,
                SupplyId = FirstSupply.Id,
                Quantity = 8m
            };
            Runtime = model.Build().CreateRuntime(seed =>
            {
                seed.Add(Demands, [Demand]);
                seed.Add(Supplies, [FirstSupply, SecondSupply]);
                seed.Add(Allocations, [Allocation]);
            });
        }

        public ObjectSet<Demand> Demands { get; }
        public ObjectSet<Supply> Supplies { get; }
        public ObjectSet<Allocation> Allocations { get; }
        public Relation<Demand, Supply> Candidates { get; }
        public Relation<Supply, Allocation> SupplyAllocations { get; }
        public Derived<Supply, decimal> Allocated { get; }
        public Derived<Supply, decimal> Remaining { get; }
        public Derived<Allocation, bool> Compatible { get; }
        public Invariant<Supply> CapacityValid { get; }
        public Invariant<Allocation> CompatibilityValid { get; }
        public Demand Demand { get; }
        public Supply FirstSupply { get; }
        public Supply SecondSupply { get; }
        public Allocation Allocation { get; }
        public ConsistencyRuntime Runtime { get; }

        public static Fixture Create() => new();

        public PreparedImpactPlan Plan(params RuntimeMutation[] mutations)
        {
            var prepared = Runtime.Prepare(MutationSet.Create(mutations));
            return Runtime.PlanDetailed(
                prepared,
                RuntimeImpactDetailLevel.Summary,
                PlannedInvariantEvaluationMode.Affected,
                PlannedDerivedEvaluationMode.Affected);
        }
    }

    private sealed class Demand
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
    }

    private sealed class Supply
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public decimal Capacity { get; set; }
    }

    private sealed class Allocation
    {
        public int Id { get; set; }
        public int SupplyId { get; set; }
        public decimal Quantity { get; set; }
        public Demand Demand { get; set; } = null!;
        public Supply Supply { get; set; } = null!;
    }
}
