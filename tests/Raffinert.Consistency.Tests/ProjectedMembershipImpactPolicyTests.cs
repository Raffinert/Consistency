namespace Raffinert.Consistency.Tests;

public sealed class ProjectedMembershipImpactPolicyTests
{
    [Fact]
    public void Source_change_uses_configured_projected_membership_impact()
    {
        var model = new ConsistencyModelBuilder();
        var demands = model.Objects<MembershipDemand>().Key(value => value.Id);
        var supplies = model.Objects<MembershipSupply>().Key(value => value.Id);
        var allocations = model.Objects<MembershipAllocation>().Key(value => value.Id);
        var candidates = model.Relation(demands, supplies)
            .Where((demand, supply) => demand.Code == supply.Code);
        var compatible = model.Derived(allocations)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .FromMembership(
                candidates,
                allocation => allocation.Demand,
                allocation => allocation.Supply);
        var runtime = model.Build().CreateRuntime();
        var firstDemand = new MembershipDemand { Id = Guid.NewGuid(), Code = "A" };
        var secondDemand = new MembershipDemand { Id = Guid.NewGuid(), Code = "B" };
        var supply = new MembershipSupply { Id = Guid.NewGuid(), Code = "A" };
        var allocation = new MembershipAllocation
        {
            Id = Guid.NewGuid(),
            Demand = firstDemand,
            Supply = supply
        };
        runtime.Add(demands, firstDemand);
        runtime.Add(demands, secondDemand);
        runtime.Add(supplies, supply);
        runtime.Add(allocations, allocation);
        Assert.True(runtime.Evaluate(compatible, allocation));

        allocation.Demand = secondDemand;
        runtime.Apply(Change.Property(
            allocations,
            allocation,
            value => value.Demand,
            firstDemand,
            secondDemand));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(compatible, allocation));
        Assert.False(runtime.Evaluate(compatible, allocation));
    }

    [Fact]
    public void Membership_removal_uses_configured_projected_membership_impact()
    {
        var model = new ConsistencyModelBuilder();
        var demands = model.Objects<MembershipDemand>().Key(value => value.Id);
        var supplies = model.Objects<MembershipSupply>().Key(value => value.Id);
        var allocations = model.Objects<MembershipAllocation>().Key(value => value.Id);
        var candidates = model.Relation(demands, supplies)
            .Where((demand, supply) => demand.Code == supply.Code);
        var compatible = model.Derived(allocations)
            .Impact(policy => policy.MembershipRemoved(DependencySeverity.Invalid))
            .FromMembership(
                candidates,
                allocation => allocation.Demand,
                allocation => allocation.Supply);
        var runtime = model.Build().CreateRuntime();
        var demand = new MembershipDemand { Id = Guid.NewGuid(), Code = "A" };
        var supply = new MembershipSupply { Id = Guid.NewGuid(), Code = "A" };
        var allocation = new MembershipAllocation
        {
            Id = Guid.NewGuid(),
            Demand = demand,
            Supply = supply
        };
        runtime.Add(demands, demand);
        runtime.Add(supplies, supply);
        runtime.Add(allocations, allocation);
        Assert.True(runtime.Evaluate(compatible, allocation));

        supply.Code = "B";
        runtime.Apply(Change.Property(supplies, supply, value => value.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(compatible, allocation));
        Assert.False(runtime.Evaluate(compatible, allocation));
    }

    [Fact]
    public void Projected_membership_impact_defaults_to_dirty()
    {
        var model = new ConsistencyModelBuilder();
        var demands = model.Objects<MembershipDemand>().Key(value => value.Id);
        var supplies = model.Objects<MembershipSupply>().Key(value => value.Id);
        var allocations = model.Objects<MembershipAllocation>().Key(value => value.Id);
        var candidates = model.Relation(demands, supplies)
            .Where((demand, supply) => demand.Code == supply.Code);
        var compatible = model.Derived(allocations)
            .FromMembership(
                candidates,
                allocation => allocation.Demand,
                allocation => allocation.Supply);
        var runtime = model.Build().CreateRuntime();
        var demand = new MembershipDemand { Id = Guid.NewGuid(), Code = "A" };
        var supply = new MembershipSupply { Id = Guid.NewGuid(), Code = "A" };
        var allocation = new MembershipAllocation
        {
            Id = Guid.NewGuid(),
            Demand = demand,
            Supply = supply
        };
        runtime.Add(demands, demand);
        runtime.Add(supplies, supply);
        runtime.Add(allocations, allocation);
        Assert.True(runtime.Evaluate(compatible, allocation));

        supply.Code = "B";
        runtime.Apply(Change.Property(supplies, supply, value => value.Code, "A", "B"));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(compatible, allocation));
        Assert.False(runtime.Evaluate(compatible, allocation));
    }

    private sealed class MembershipDemand
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = string.Empty;
    }

    private sealed class MembershipSupply
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = string.Empty;
    }

    private sealed class MembershipAllocation
    {
        public Guid Id { get; set; }
        public MembershipDemand Demand { get; set; } = null!;
        public MembershipSupply Supply { get; set; } = null!;
    }
}
