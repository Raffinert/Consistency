using System.Linq.Expressions;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

internal interface IInvariantDefinition
{
    string? DefinitionKey { get; set; }
    IObjectSetDefinition SourceSet { get; }
    IReadOnlyList<IDerivedDefinition> UpstreamDerived { get; }
    InvariantReaction Reaction { get; set; }
    LambdaExpression PredicateExpression { get; }
    ExpressionDependencyAnalysis Analysis { get; }
    bool AllowIncompleteDependencies { get; set; }
    IInvariantRuntimeState CreateState(
        IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> derivedStates);
    void DispatchRepair(object source);
    void SetRepairScheduler(Delegate scheduler);
}

internal sealed class InvariantDefinition<TSource, TValue>(
    IDerivedDefinition derived,
    LambdaExpression predicateExpression,
    Func<TSource, TValue, bool> predicate) : IInvariantDefinition
    where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IDerivedDefinition DerivedDefinition { get; } = derived;
    public Func<TSource, TValue, bool> Predicate { get; } = predicate;
    public LambdaExpression PredicateExpression { get; } = predicateExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeInvariant(predicateExpression);
    public bool AllowIncompleteDependencies { get; set; }
    public IObjectSetDefinition SourceSet => DerivedDefinition.SourceSet;
    public IReadOnlyList<IDerivedDefinition> UpstreamDerived { get; } = [derived];
    public InvariantReaction Reaction { get; set; } = InvariantReaction.MarkDirty;
    public Action<TSource>? RepairScheduler { get; set; }

    public IInvariantRuntimeState CreateState(
        IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> derivedStates) =>
        new InvariantRuntimeState<TSource>(this, source => Predicate(
            source,
            (TValue)derivedStates[DerivedDefinition].GetValue(source)!));

    public void DispatchRepair(object source) => RepairScheduler!((TSource)source);
    public void SetRepairScheduler(Delegate scheduler) => RepairScheduler = (Action<TSource>)scheduler;
}

internal sealed class MultiInvariantDefinition<TSource, TFirst, TSecond>(
    IDerivedDefinition first,
    IDerivedDefinition second,
    LambdaExpression predicateExpression,
    Func<TSource, TFirst, TSecond, bool> predicate) : IInvariantDefinition where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => first.SourceSet;
    public IReadOnlyList<IDerivedDefinition> UpstreamDerived { get; } = [first, second];
    public InvariantReaction Reaction { get; set; } = InvariantReaction.MarkDirty;
    public LambdaExpression PredicateExpression => predicateExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeSourceInvariant(predicateExpression);
    public bool AllowIncompleteDependencies { get; set; }
    public Action<TSource>? RepairScheduler { get; private set; }

    public IInvariantRuntimeState CreateState(
        IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> derivedStates) =>
        new InvariantRuntimeState<TSource>(this, source => predicate(
            source,
            (TFirst)derivedStates[first].GetValue(source)!,
            (TSecond)derivedStates[second].GetValue(source)!));

    public void DispatchRepair(object source) => RepairScheduler!((TSource)source);
    public void SetRepairScheduler(Delegate scheduler) => RepairScheduler = (Action<TSource>)scheduler;
}

internal interface IInvariantRuntimeState : ISourceLifecycleParticipant
{
    IInvariantDefinition Definition { get; }
    int SourceStateEntryCount { get; }
    object CaptureState();
    void RestoreState(object snapshot);
    void ApplyImpact(
        IEnumerable<object> sources,
        DependencyImpactKind impact,
        RuntimePolicyActions policyActions);
    void EvaluatePolicy(object source);
    bool EvaluateValue(object source);
    InvariantEvaluationState GetValueState(object source);
}

internal sealed class InvariantRuntimeState<TSource>(
    IInvariantDefinition definition,
    Func<TSource, bool> evaluate) : IInvariantRuntimeState
    where TSource : class
{
    private readonly Dictionary<TSource, InvariantEvaluationState> _states = new(ReferenceEqualityComparer<TSource>.Instance);

    public IInvariantDefinition Definition => definition;
    public IObjectSetDefinition SourceSet => definition.SourceSet;
    public int SourceStateEntryCount => _states.Count;

    public bool Evaluate(TSource source)
    {
        var valid = evaluate(source);
        _states[source] = valid ? InvariantEvaluationState.Valid : InvariantEvaluationState.Violated;
        return valid;
    }

    public InvariantEvaluationState GetState(TSource source) =>
        _states.TryGetValue(source, out var state) ? state : InvariantEvaluationState.Unknown;

    public object CaptureState() => new Dictionary<TSource, InvariantEvaluationState>(
        _states,
        ReferenceEqualityComparer<TSource>.Instance);

    public void RestoreState(object snapshot)
    {
        _states.Clear();
        foreach (var pair in (Dictionary<TSource, InvariantEvaluationState>)snapshot)
            _states.Add(pair.Key, pair.Value);
    }

    public void ApplyImpact(
        IEnumerable<object> sources,
        DependencyImpactKind impact,
        RuntimePolicyActions policyActions)
    {
        var typedSources = sources.Cast<TSource>().Distinct(ReferenceEqualityComparer<TSource>.Instance).ToArray();
        switch (definition.Reaction)
        {
            case InvariantReaction.EvaluateImmediately:
                Mark(typedSources, impact);
                foreach (var source in typedSources)
                    policyActions.AddImmediateEvaluation(this, source);
                break;
            case InvariantReaction.MarkInvalid:
                Mark(typedSources, DependencyImpactKind.Invalid);
                break;
            case InvariantReaction.ScheduleRepair:
                Mark(typedSources, DependencyImpactKind.Invalid);
                foreach (var source in typedSources)
                    policyActions.AddRepairRequest(
                        definition,
                        source,
                        DependencyImpactKind.Invalid);
                break;
            default:
                Mark(typedSources, impact);
                break;
        }
    }

    public void EvaluatePolicy(object source) => Evaluate((TSource)source);
    public bool EvaluateValue(object source) => Evaluate((TSource)source);
    public InvariantEvaluationState GetValueState(object source) => GetState((TSource)source);

    public void OnSourceAdded(object source) => _states.Remove((TSource)source);

    public void OnSourceRemoved(object source) => _states.Remove((TSource)source);

    private void Mark(IEnumerable<TSource> sources, DependencyImpactKind impact)
    {
        foreach (var source in sources)
        {
            if (!_states.TryGetValue(source, out var current))
                continue;
            _states[source] = DependencyStateTransitions.Apply(current, impact);
        }
    }
}
