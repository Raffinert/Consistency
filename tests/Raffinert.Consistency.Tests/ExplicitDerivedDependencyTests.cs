using System.Linq.Expressions;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency.Tests;

public sealed class ExplicitDerivedDependencyTests
{
    [Fact]
    public void Opaque_source_computation_requires_explicit_dependency_contract()
    {
        var rejected = new ConsistencyModelBuilder();
        var rejectedRoots = rejected.Objects<Association>().Key(x => x.Id);
        _ = rejected.Derived(rejectedRoots).Select(x => Calculate(x.Source.Value, x.Target.Value));
        var error = Assert.Throws<InvalidOperationException>(() => rejected.Build());
        Assert.Contains("ContainsOpaqueCode", error.Message);
        Assert.Contains("AllowIncompleteDependencies", error.Message);

        var accepted = new ConsistencyModelBuilder();
        var roots = accepted.Objects<Association>().Key(x => x.Id);
        _ = accepted.Derived(roots)
            .DependsOn(x => x.Source.Value)
            .DependsOn(x => x.Target.Value)
            .Select(x => Calculate(x.Source.Value, x.Target.Value));
        _ = accepted.Build();
    }

    [Fact]
    public void Declared_nested_dependencies_fan_out_selectively_and_follow_retargeting()
    {
        var model = new ConsistencyModelBuilder();
        var associations = model.Objects<Association>().Key(x => x.Id);
        var rate = model.Derived(associations)
            .DependsOn(x => x.Source.Value)
            .DependsOn(x => x.Target.Value)
            .Select(x => Calculate(x.Source.Value, x.Target.Value));
        var shared = new Endpoint { Value = 12m };
        var other = new Endpoint { Value = 30m };
        var firstTarget = new Endpoint { Value = 4m };
        var secondTarget = new Endpoint { Value = 5m };
        var unrelatedSource = new Endpoint { Value = 8m };
        var first = new Association { Id = 1, Source = shared, Target = firstTarget };
        var second = new Association { Id = 2, Source = shared, Target = secondTarget };
        var unrelated = new Association { Id = 3, Source = unrelatedSource, Target = secondTarget };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(associations, [first, second, unrelated]));
        Assert.Equal(3m, runtime.Evaluate(rate, first));
        Assert.Equal(2.4m, runtime.Evaluate(rate, second));
        Assert.Equal(1.6m, runtime.Evaluate(rate, unrelated));

        shared.Value = 20m;
        runtime.Apply(Change.Property(shared, x => x.Value, 12m, 20m));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(rate, first));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(rate, second));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(rate, unrelated));
        Assert.Equal(5m, runtime.Evaluate(rate, first));
        Assert.Equal(4m, runtime.Evaluate(rate, second));

        firstTarget.Value = 10m;
        runtime.Apply(Change.Property(firstTarget, x => x.Value, 4m, 10m));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(rate, first));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(rate, second));
        Assert.Equal(2m, runtime.Evaluate(rate, first));

        second.Source = other;
        runtime.Apply(Change.Property(associations, second, x => x.Source, shared, other));
        Assert.Equal(6m, runtime.Evaluate(rate, second));
        shared.Value = 25m;
        runtime.Apply(Change.Property(shared, x => x.Value, 20m, 25m));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(rate, second));
        other.Value = 35m;
        runtime.Apply(Change.Property(other, x => x.Value, 30m, 35m));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(rate, second));
        Assert.Equal(7m, runtime.Evaluate(rate, second));
    }

    [Fact]
    public void Declared_dependencies_augment_inferred_dependencies_and_deduplicate()
    {
        var model = new ConsistencyModelBuilder();
        var associations = model.Objects<Association>().Key(x => x.Id);
        var value = model.Derived(associations)
            .DependsOn(x => x.Source.Value)
            .DependsOn(x => x.Source.Value)
            .Select(x => Calculate(x.Source.Value, 1m) + x.Adjustment);
        var source = new Endpoint { Value = 2m };
        var association = new Association { Id = 1, Source = source, Target = new(), Adjustment = 3m };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(associations, [association]));
        Assert.Equal(5m, runtime.Evaluate(value, association));
        Assert.Equal(2, value.Definition.Analysis.Dependencies.Count);

        association.Adjustment = 4m;
        runtime.Apply(Change.Property(associations, association, x => x.Adjustment, 3m, 4m));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(value, association));
        Assert.Equal(6m, runtime.Evaluate(value, association));
    }

    [Fact]
    public void DependsOn_rejects_malformed_and_collection_selectors_immediately()
    {
        var model = new ConsistencyModelBuilder();
        var roots = model.Objects<Association>().Key(x => x.Id);
        var builder = model.Derived(roots);
        Expression<Func<Association, decimal?>>? missing = null;
        Assert.Throws<ArgumentNullException>(() => builder.DependsOn(missing!));
        Assert.Contains("single non-collection member path",
            Assert.Throws<ArgumentException>(() => builder.DependsOn(x => Calculate(x.Source.Value, 1m))).Message);
        Assert.Throws<ArgumentException>(() => builder.DependsOn(x => x.Adjustment + x.Id));
        Assert.Throws<ArgumentException>(() => builder.DependsOn(x => x.Adjustment > 0 ? x.Id : 0));
        var external = new Endpoint();
        Assert.Throws<ArgumentException>(() => builder.DependsOn(_ => external.Value));
        Assert.Throws<ArgumentException>(() => builder.DependsOn(x => x.Items));
        Assert.Throws<ArgumentException>(() => builder.DependsOn(x => x));
    }

    [Fact]
    public void DependsOn_does_not_suppress_external_state()
    {
        var multiplier = 2m;
        var model = new ConsistencyModelBuilder();
        var roots = model.Objects<Association>().Key(x => x.Id);
        _ = model.Derived(roots).DependsOn(x => x.Source.Value)
            .Select(x => Calculate(x.Source.Value, 1m) * multiplier);
        var error = Assert.Throws<InvalidOperationException>(() => model.Build());
        Assert.Contains("ContainsExternalState", error.Message);
    }

    [Fact]
    public void Declared_paths_flow_to_scope_and_debug_metadata()
    {
        var model = new ConsistencyModelBuilder();
        var roots = model.Objects<Association>().Key(x => x.Id);
        var direct = model.Derived(roots).DependsOn(x => x.Adjustment)
            .Select(x => OpaqueScale(x.Adjustment));
        var nested = model.Derived(roots).DependsOn(x => x.Source.Value)
            .Select(x => OpaqueScale(x.Source.Value ?? 0m));
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();

        Assert.Empty(runtime.GetScopeRequirements(direct.Definition));
        var requirement = Assert.Single(runtime.GetScopeRequirements(nested.Definition));
        Assert.Equal(roots.Definition.Id, requirement.Set.Id);
        Assert.Equal(ScopeRequirementReason.NavigationConsumerCoverage, requirement.Reason);
        Assert.Contains("DerivedSource: Association.Source.Value", compiled.DebugView);
        Assert.DoesNotContain("Incomplete, explicitly allowed", compiled.DebugView);
    }

    private static decimal? Calculate(decimal? source, decimal? target) =>
        source is null || target is null || target == 0m ? null : source / target;
    private static decimal OpaqueScale(decimal value) => value * 2m;

    private sealed class Association
    {
        public int Id { get; set; }
        public Endpoint Source { get; set; } = null!;
        public Endpoint Target { get; set; } = null!;
        public decimal Adjustment { get; set; }
        public List<Endpoint> Items { get; set; } = [];
    }

    private sealed class Endpoint { public decimal? Value { get; set; } }
}
