namespace Raffinert.Consistency;

public static class DerivedBuilderExtensions
{
    public static Derived<TSource, TValue> Select<TSource, TValue>(
        this DerivedBuilder<TSource> builder,
        Func<TSource, TValue> computation) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SelectOpaque(computation);
    }
}
