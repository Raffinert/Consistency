using System.Reflection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class EfLegacyApiSurfaceTests
{
    [Fact]
    public void Ef_mappings_do_not_expose_legacy_materialize_method()
    {
        Assert.DoesNotContain(
            typeof(ConsistencyEfCoreMappings).GetMethods(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.DeclaredOnly),
            method => method.Name == "Materialize");
    }
}
