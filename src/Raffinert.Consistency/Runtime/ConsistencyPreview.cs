namespace Raffinert.Consistency;

/// <summary>
/// Experimental read-only query view over one prepared plan's final proposed state.
/// It owns isolated derived and invariant caches and never installs its state into the committed runtime.
/// </summary>
internal sealed class ConsistencyPreview : IDisposable
{
    private readonly Action _validate;
    private readonly Func<IObjectSetDefinition, object, bool> _contains;
    private readonly IReadOnlyDictionary<IRelationDefinition, IRelationQueryState> _relations;
    private readonly IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> _derived;
    private readonly IReadOnlyDictionary<IInvariantDefinition, IInvariantRuntimeState> _invariants;
    private bool _disposed;

    internal ConsistencyPreview(
        Action validate,
        Func<IObjectSetDefinition, object, bool> contains,
        IReadOnlyDictionary<IRelationDefinition, IRelationQueryState> relations,
        IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> derived,
        IReadOnlyDictionary<IInvariantDefinition, IInvariantRuntimeState> invariants)
    {
        _validate = validate;
        _contains = contains;
        _relations = relations;
        _derived = derived;
        _invariants = invariants;
    }

    public IReadOnlyList<TRight> Related<TLeft, TRight>(
        Relation<TLeft, TRight> relation,
        TLeft left)
        where TLeft : class where TRight : class
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(left);
        Validate();
        if (!_relations.TryGetValue(relation.Definition, out var state))
            throw new ArgumentException("The relation does not belong to this proposed-state view.", nameof(relation));
        EnsureRegistered(relation.Definition.LeftSet, left, nameof(left));
        return ((IRelationQueryState<TLeft, TRight>)state).Related(left);
    }

    public TValue Evaluate<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        Validate();
        if (!_derived.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived definition does not belong to this proposed-state view.",
                nameof(derived));
        EnsureRegistered(derived.Definition.SourceSet, source, nameof(source));
        ValidateProjectedTargets(derived.Definition, source);
        return (TValue)state.GetValue(source)!;
    }

    public bool Evaluate<TSource>(Invariant<TSource> invariant, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        Validate();
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this proposed-state view.",
                nameof(invariant));
        EnsureRegistered(invariant.Definition.SourceSet, source, nameof(source));
        foreach (var upstream in invariant.Definition.UpstreamDerived)
            ValidateProjectedTargets(upstream, source);
        return state.EvaluateValue(source);
    }

    public DerivedValueState GetState<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        Validate();
        if (!_derived.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived definition does not belong to this proposed-state view.",
                nameof(derived));
        EnsureRegistered(derived.Definition.SourceSet, source, nameof(source));
        return state.GetValueState(source);
    }

    public InvariantEvaluationState GetState<TSource>(Invariant<TSource> invariant, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        Validate();
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this proposed-state view.",
                nameof(invariant));
        EnsureRegistered(invariant.Definition.SourceSet, source, nameof(source));
        return state.GetValueState(source);
    }

    public void Dispose() => _disposed = true;

    private void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _validate();
    }

    private void EnsureRegistered(IObjectSetDefinition set, object source, string parameter)
    {
        if (!_contains(set, source))
            throw new ArgumentException(
                "The source is not registered in the proposed object set.", parameter);
    }

    private void ValidateProjectedTargets(IDerivedDefinition definition, object source)
    {
        foreach (var input in definition.Inputs.OfType<ProjectedUpstreamDerivedInput>())
        {
            var target = input.Project(source) ?? throw new InvalidOperationException(
                "A projected dependency target cannot be null.");
            if (!_contains(input.UpstreamSet, target))
                throw new InvalidOperationException(
                    "The projected dependency target is not registered in the proposed upstream object set.");
        }
    }
}
