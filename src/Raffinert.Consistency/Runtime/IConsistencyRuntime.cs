namespace Raffinert.Consistency;

/// <summary>
/// Application-facing logical evaluation and materialization operations for a consistency runtime.
/// </summary>
public interface IConsistencyRuntime
{
    /// <summary>
    /// Makes the logical derived value current and returns it without writing a materialized mirror.
    /// </summary>
    TValue Evaluate<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class;

    /// <summary>
    /// Evaluates and synchronizes exactly one configured materialized representation.
    /// </summary>
    TValue Materialize<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class;

    /// <summary>
    /// Synchronizes every configured materialized representation physically located on the source object.
    /// </summary>
    void Materialize<TSource>(TSource source) where TSource : class;
}
