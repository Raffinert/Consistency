using System.Linq.Expressions;
using System.Reflection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.FinalApiDogfood;

internal interface IMaterializationDefinition
{
    object SourceIdentity { get; }
    object ValueIdentity { get; }
    PropertyInfo TargetMember { get; }
    FinalNode ValueNode { get; }
    PreparedMirror Prepare(ConsistencyRuntime runtime, object source);
    void AddTo(ConsistencyEfCoreMappings mappings);
}

internal sealed class MaterializationDefinition<TSource, TValue> : IMaterializationDefinition
    where TSource : class
{
    private readonly FinalValue<TSource, TValue> _value;
    private readonly Expression<Func<TSource, TValue>> _target;
    private readonly Func<TSource, TValue> _read;
    private readonly Action<TSource, TValue> _write;

    public MaterializationDefinition(
        FinalValue<TSource, TValue> value,
        Expression<Func<TSource, TValue>> target,
        PropertyInfo property)
    {
        _value = value;
        _target = target;
        TargetMember = property;
        _read = target.Compile();
        var source = Expression.Parameter(typeof(TSource), "source");
        var materialized = Expression.Parameter(typeof(TValue), "value");
        _write = Expression.Lambda<Action<TSource, TValue>>(
            Expression.Assign(Expression.Property(source, property), materialized), source, materialized).Compile();
    }

    public object SourceIdentity => _value.Source;
    public object ValueIdentity => _value;
    public PropertyInfo TargetMember { get; }
    public FinalNode ValueNode => _value.Node;

    public PreparedMirror Prepare(ConsistencyRuntime runtime, object source)
    {
        if (source is not TSource typed)
            throw new ArgumentException("Materialization source has the wrong CLR type.", nameof(source));
        var value = runtime.Get(_value.Raw, typed);
        return new PreparedMirror<TSource, TValue>(typed, value, _read(typed), _read, _write, TargetMember.Name);
    }

    public void AddTo(ConsistencyEfCoreMappings mappings) => mappings.Materialize(_value.Raw, _target);
}

internal abstract class PreparedMirror
{
    public abstract object? Value { get; }
    public abstract void Apply();
    public abstract void Restore();
}

internal sealed class PreparedMirror<TSource, TValue>(
    TSource source,
    TValue value,
    TValue previous,
    Func<TSource, TValue> read,
    Action<TSource, TValue> write,
    string targetName) : PreparedMirror
    where TSource : class
{
    public override object? Value => value;

    public override void Apply()
    {
        if (EqualityComparer<TValue>.Default.Equals(previous, value)) return;
        write(source, value);
        if (!EqualityComparer<TValue>.Default.Equals(read(source), value))
            throw new InvalidOperationException(
                $"Materialization target '{targetName}' did not retain the evaluated value.");
    }

    public override void Restore()
    {
        if (!EqualityComparer<TValue>.Default.Equals(read(source), previous))
            write(source, previous);
    }
}

internal sealed class FinalCompiledModel(
    CompiledConsistencyModel raw,
    IReadOnlyList<IMaterializationDefinition> materializations,
    string logicalDebugView)
{
    public CompiledConsistencyModel Raw { get; } = raw;
    public string LogicalDebugView { get; } = logicalDebugView;

    public FinalRuntime CreateRuntime(Action<FinalSeedBuilder> seed)
    {
        var memberships = new List<SourceMembership>();
        var runtime = Raw.CreateRuntime(rawSeed => seed(new FinalSeedBuilder(rawSeed, memberships)));
        return new FinalRuntime(runtime, materializations, memberships);
    }

    public void AddMaterializationsTo(ConsistencyEfCoreMappings mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        foreach (var materialization in materializations)
            materialization.AddTo(mappings);
    }
}

internal sealed class FinalSeedBuilder(RuntimeSeedBuilder raw, List<SourceMembership> memberships)
{
    public FinalSeedBuilder Add<TSource>(FinalSet<TSource> set, IEnumerable<TSource> sources)
        where TSource : class
    {
        var values = sources.ToArray();
        raw.Add(set.Raw, values);
        memberships.AddRange(values.Select(value => new SourceMembership(set, value)));
        return this;
    }
}

internal sealed record SourceMembership(object SetIdentity, object Source);

internal sealed class FinalRuntime
{
    private readonly ConsistencyRuntime _raw;
    private readonly Dictionary<object, IMaterializationDefinition> _byValue =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, IReadOnlyList<IMaterializationDefinition>> _bySet =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, IReadOnlyList<object>> _memberships =
        new(ReferenceEqualityComparer.Instance);

    public FinalRuntime(
        ConsistencyRuntime raw,
        IReadOnlyList<IMaterializationDefinition> materializations,
        IReadOnlyList<SourceMembership> memberships)
    {
        _raw = raw;
        foreach (var materialization in materializations)
            _byValue.Add(materialization.ValueIdentity, materialization);
        _bySet = materializations.GroupBy(x => x.SourceIdentity, ReferenceEqualityComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IMaterializationDefinition>)group.ToArray(),
                ReferenceEqualityComparer.Instance);
        _memberships = memberships.GroupBy(x => x.Source, ReferenceEqualityComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<object>)group.Select(x => x.SetIdentity).ToArray(),
                ReferenceEqualityComparer.Instance);
    }

    public ConsistencyRuntime Raw => _raw;

    public TValue Evaluate<TSource, TValue>(FinalValue<TSource, TValue> value, TSource source)
        where TSource : class => _raw.Get(value.Raw, source);

    public TValue Materialize<TSource, TValue>(FinalValue<TSource, TValue> value, TSource source)
        where TSource : class
    {
        if (!_byValue.TryGetValue(value, out var definition))
            throw new InvalidOperationException(
                "This derived definition has no materialization target. Use Evaluate instead.");
        var prepared = definition.Prepare(_raw, source);
        Apply([prepared]);
        return (TValue)prepared.Value!;
    }

    public void Materialize<TSource>(TSource source) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_memberships.TryGetValue(source, out var sets))
            throw new InvalidOperationException("The source is not registered in this runtime.");
        var definitions = sets.SelectMany(set =>
                _bySet.TryGetValue(set, out var targets) ? targets : [])
            .ToArray();
        var prepared = definitions.Select(definition => definition.Prepare(_raw, source)).ToArray();
        Apply(prepared);
    }

    private static void Apply(IReadOnlyList<PreparedMirror> prepared)
    {
        var attempted = new Stack<PreparedMirror>();
        try
        {
            foreach (var mirror in prepared)
            {
                attempted.Push(mirror);
                mirror.Apply();
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            while (attempted.TryPop(out var mirror))
            {
                try { mirror.Restore(); }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException(
                    "Materialization failed and rollback was incomplete.", [error, .. rollbackErrors]);
            throw;
        }
    }
}
