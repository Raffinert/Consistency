using System.Reflection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class LegacyApiSurfaceTests
{
    [Fact]
    public void Public_surface_exposes_only_v2_declaration_and_evaluation_vocabulary()
    {
        AssertNoPublicMethod(typeof(DerivedBuilder<>), "Using", "Compute");
        AssertNoPublicMethod(typeof(DerivedUsingBuilder<,>), "Incrementally", "Compute");
        AssertNoPublicMethod(typeof(DerivedUpstreamBuilder<,>), "Compute");
        AssertNoPublicMethod(typeof(DerivedUpstreamBuilder<,,>), "Compute");
        AssertNoPublicMethod(typeof(ProjectedDerivedUpstreamBuilder<,,>), "Compute");
        AssertNoPublicMethod(typeof(ProjectedDerivedUpstreamBuilder<,,,>), "Compute");
        AssertNoPublicMethod(typeof(InvariantBuilder<>), "Using");
        AssertNoPublicMethod(typeof(ConsistencyRuntime), "Get");
        AssertNoPublicMethod(typeof(ConsistencyEfCoreMappings), "Materialize");

        AssertPublicMethod(typeof(DerivedBuilder<>), "From");
        AssertPublicMethod(typeof(DerivedBuilder<>), "Select");
        AssertPublicMethod(typeof(DerivedUsingBuilder<,>), "Sum");
        AssertPublicMethod(typeof(DerivedUsingBuilder<,>), "Count");
        AssertPublicMethod(typeof(DerivedUsingBuilder<,>), "LongCount");
        AssertPublicMethod(typeof(DerivedUsingBuilder<,>), "Any");
        AssertPublicMethod(typeof(InvariantBuilder<>), "From");
        AssertPublicMethod(typeof(ConsistencyRuntime), "Evaluate");
        AssertPublicMethod(typeof(ConsistencyRuntime), "Materialize");
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
}
