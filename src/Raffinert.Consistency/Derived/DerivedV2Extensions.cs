namespace Raffinert.Consistency;

public static class DerivedV2Extensions
{
    /// <summary>
    /// Selects an opaque calculator. Declare every source/member read with DependsOn so Build can
    /// enforce the cached-freshness completeness contract.
    /// </summary>
    public static Derived<TSource, TValue> Select<TSource, TValue>(
        this DerivedBuilder<TSource> builder,
        Func<TSource, TValue> computation)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SelectOpaque(computation);
    }
}
