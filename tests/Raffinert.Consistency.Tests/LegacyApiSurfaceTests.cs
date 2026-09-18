using System.Reflection;

namespace Raffinert.Consistency.Tests;

public sealed class LegacyApiSurfaceTests
{
    [Fact]
    public void Public_surface_contains_v2_types_and_excludes_legacy_vocabulary()
    {
        var exported = typeof(ConsistencyModelBuilder).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported,
            type => type.Name.StartsWith("DerivedUsingBuilder", StringComparison.Ordinal));
        Assert.DoesNotContain(exported,
            type => type.Name.StartsWith("InvariantUsingBuilder", StringComparison.Ordinal));
        Assert.Contains(exported, type => type == typeof(DerivedRelationBuilder<,>));
        Assert.Contains(exported, type => type == typeof(InvariantValueBuilder<,>));
        Assert.Contains(exported, type => type == typeof(InvariantValueBuilder<,,>));

        AssertNoPublicMethod(typeof(DerivedBuilder<>), "Using", "Compute");
        AssertNoPublicMethod(typeof(DerivedRelationBuilder<,>), "Incrementally", "Compute");
        AssertNoPublicMethod(typeof(DerivedUpstreamBuilder<,>), "Compute");
        AssertNoPublicMethod(typeof(DerivedUpstreamBuilder<,,>), "Compute");
        AssertNoPublicMethod(typeof(ProjectedDerivedUpstreamBuilder<,,>), "Compute");
        AssertNoPublicMethod(typeof(ProjectedDerivedUpstreamBuilder<,,,>), "Compute");
        AssertNoPublicMethod(typeof(InvariantBuilder<>), "Using");
        AssertNoPublicMethod(typeof(ConsistencyRuntime), "Get");

        AssertPublicMethod(typeof(DerivedBuilder<>), "From");
        AssertPublicMethod(typeof(DerivedBuilder<>), "Select");
        AssertPublicMethod(typeof(DerivedRelationBuilder<,>), "Sum");
        AssertPublicMethod(typeof(DerivedRelationBuilder<,>), "Count");
        AssertPublicMethod(typeof(DerivedRelationBuilder<,>), "LongCount");
        AssertPublicMethod(typeof(DerivedRelationBuilder<,>), "Any");
        AssertPublicMethod(typeof(InvariantBuilder<>), "From");
        AssertPublicMethod(typeof(ConsistencyRuntime), "Evaluate");
        AssertPublicMethod(typeof(ConsistencyRuntime), "Materialize");
    }

    [Fact]
    public void Fluent_from_stages_have_the_renamed_public_types()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<Line>().Key(line => line.Id);
        var receipts = model.Objects<Receipt>().Key(receipt => receipt.Id);
        var relation = model.Relation(lines, receipts).Where((line, receipt) => line.Id == receipt.LineId);
        var relationStage = model.Derived(lines).From(relation);
        var first = model.Derived(lines).Select(line => line.First);
        var second = model.Derived(lines).Select(line => line.Second);
        var invariantStage = model.Invariant(lines).From(first);
        var invariantStage2 = invariantStage.From(second);

        Assert.Equal(typeof(DerivedRelationBuilder<,>), relationStage.GetType().GetGenericTypeDefinition());
        Assert.Equal(typeof(InvariantValueBuilder<,>), invariantStage.GetType().GetGenericTypeDefinition());
        Assert.Equal(typeof(InvariantValueBuilder<,,>), invariantStage2.GetType().GetGenericTypeDefinition());

        relationStage.Count();
        invariantStage.Must((_, value) => value >= 0);
        invariantStage2.Must((_, firstValue, secondValue) => firstValue + secondValue >= 0);
    }

    private static void AssertNoPublicMethod(Type type, params string[] names)
    {
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        foreach (var name in names)
            Assert.DoesNotContain(methods, method => method.Name == name);
    }

    private static void AssertPublicMethod(Type type, string name) =>
        Assert.Contains(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => method.Name == name);

    private sealed class Line
    {
        public int Id { get; set; }
        public int First { get; set; }
        public int Second { get; set; }
    }

    private sealed class Receipt
    {
        public int Id { get; set; }
        public int LineId { get; set; }
    }
}
