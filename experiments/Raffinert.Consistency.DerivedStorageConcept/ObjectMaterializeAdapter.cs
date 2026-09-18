using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept;

internal sealed record ObjectMaterializationDiagnostics(
    int ObjectLookups,
    int ObjectTargetsVisited,
    int TargetedTargetsVisited);

internal sealed class ObjectMaterializeAdapter
{
    private readonly ExplicitEvaluateMaterializeRuntime _runtime;
    private readonly Dictionary<object, SetRegistration> _sets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, List<object>> _memberships = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Type> _knownSourceTypes = [];
    private long _registrationOrder;

    public ObjectMaterializeAdapter(ExplicitEvaluateMaterializeRuntime runtime) => _runtime = runtime;

    public int ObjectLookups { get; private set; }
    public int ObjectTargetsVisited { get; private set; }

    public void RegisterSet<TSource>(ObjectSet<TSource> set) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!_sets.ContainsKey(set))
            _sets.Add(set, new SetRegistration());
        _knownSourceTypes.Add(typeof(TSource));
    }

    public void RegisterSource<TSource>(ObjectSet<TSource> set, TSource source) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(source);
        RegisterSet(set);
        if (!_memberships.TryGetValue(source, out var memberships))
        {
            memberships = [];
            _memberships.Add(source, memberships);
        }
        if (!memberships.Contains(set, ReferenceEqualityComparer.Instance))
            memberships.Add(set);
    }

    public ObjectMaterializationTarget<TSource, TValue> RegisterTarget<TSource, TValue>(
        ObjectSet<TSource> set,
        MaterializedValue<TSource, TValue> target,
        int topologicalOrder)
        where TSource : class
    {
        RegisterSet(set);
        var registered = new ObjectMaterializationTarget<TSource, TValue>(
            set, target, topologicalOrder, _registrationOrder++);
        _sets[set].Targets.Add(registered);
        return registered;
    }

    public void Materialize<TSource>(TSource source) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectLookups++;
        if (!_memberships.TryGetValue(source, out var memberships))
        {
            if (_knownSourceTypes.Any(type => type.IsInstanceOfType(source)))
                throw new InvalidOperationException(
                    "The source instance is not registered in an object set known to this materialization adapter.");
            throw new ArgumentException(
                "The source CLR type does not belong to this materialization adapter's compiled model.",
                nameof(source));
        }

        var targets = memberships
            .SelectMany(set => _sets[set].Targets)
            .OrderBy(target => target.TopologicalOrder)
            .ThenBy(target => target.RegistrationOrder)
            .ToArray();
        ObjectTargetsVisited += targets.Length;

        // O2: complete all logical evaluation and snapshot capture before any physical write.
        var prepared = targets.Select(target => target.Prepare(_runtime, source)).ToArray();
        MaterializationTransaction.Apply(prepared);
    }

    public void ResetDiagnostics()
    {
        ObjectLookups = 0;
        ObjectTargetsVisited = 0;
    }

    private sealed class SetRegistration
    {
        public List<IObjectMaterializationTarget> Targets { get; } = [];
    }
}

internal interface IObjectMaterializationTarget
{
    object DerivedIdentity { get; }
    int TopologicalOrder { get; }
    long RegistrationOrder { get; }
    PreparedMaterialization Prepare(ExplicitEvaluateMaterializeRuntime runtime, object source);
}

internal sealed class ObjectMaterializationTarget<TSource, TValue>(
    ObjectSet<TSource> set,
    MaterializedValue<TSource, TValue> target,
    int topologicalOrder,
    long registrationOrder) : IObjectMaterializationTarget
    where TSource : class
{
    public ObjectSet<TSource> Set { get; } = set;
    public object DerivedIdentity => target.Derived;
    public int TopologicalOrder { get; } = topologicalOrder;
    public long RegistrationOrder { get; } = registrationOrder;

    public PreparedMaterialization Prepare(ExplicitEvaluateMaterializeRuntime runtime, object source)
    {
        if (source is not TSource typedSource)
            throw new ArgumentException("The materialization source has the wrong CLR type.", nameof(source));
        var value = runtime.Evaluate(target.Derived, typedSource);
        return new PreparedMaterialization<TSource, TValue>(target, typedSource, value, target.Read(typedSource));
    }
}

internal abstract class PreparedMaterialization
{
    public abstract object? Value { get; }
    public abstract bool Apply();
    public abstract void Restore();
}

internal sealed class PreparedMaterialization<TSource, TValue>(
    MaterializedValue<TSource, TValue> target,
    TSource source,
    TValue value,
    TValue previous) : PreparedMaterialization
    where TSource : class
{
    public override object? Value => value;

    public override bool Apply()
    {
        if (EqualityComparer<TValue>.Default.Equals(previous, value))
            return false;
        target.Write(source, value);
        if (!EqualityComparer<TValue>.Default.Equals(target.Read(source), value))
            throw new InvalidOperationException(
                $"Materialization target '{target.Name}' did not retain the evaluated value.");
        return true;
    }

    public override void Restore()
    {
        if (!EqualityComparer<TValue>.Default.Equals(target.Read(source), previous))
            target.Write(source, previous);
    }
}

internal static class MaterializationTransaction
{
    public static void Apply(IReadOnlyList<PreparedMaterialization> prepared)
    {
        var attempted = new Stack<PreparedMaterialization>();
        try
        {
            foreach (var target in prepared)
            {
                attempted.Push(target);
                _ = target.Apply();
            }
        }
        catch (Exception materializationError)
        {
            var rollbackErrors = new List<Exception>();
            while (attempted.TryPop(out var target))
            {
                try
                {
                    target.Restore();
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
                throw new AggregateException(
                    "Materialization failed and one or more previous target values could not be restored.",
                    [materializationError, .. rollbackErrors]);
            throw;
        }
    }
}
