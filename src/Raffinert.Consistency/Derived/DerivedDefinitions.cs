using System.Linq.Expressions;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

internal interface IDerivedDefinition
{
    string? DefinitionKey { get; set; }
    IObjectSetDefinition SourceSet { get; }
    IReadOnlyList<DerivedInput> Inputs { get; }
    LambdaExpression ComputationExpression { get; }
    ExpressionDependencyAnalysis Analysis { get; }
    DerivedImpactPolicy ImpactPolicy { get; }
    string ComputationPlanName { get; }
    bool RequiresExactPropagation { get; }
    bool PrefersConservativePropagation { get; }
    bool AllowIncompleteDependencies { get; set; }
    IDerivedRuntimeState CreateState(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived);
    IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate);
}

internal abstract record DerivedInput;
internal sealed record RelationDerivedInput(IRelationDefinition Relation) : DerivedInput;
internal record UpstreamDerivedInput(IDerivedDefinition Upstream) : DerivedInput
{
    public virtual bool IsProjected => false;
    public virtual object? Project(object source) => source;
}

internal sealed record ProjectedUpstreamDerivedInput(
    IDerivedDefinition Upstream,
    LambdaExpression SelectorExpression,
    Func<object, object?> CompiledSelector,
    DependencyPath SelectorPath) : UpstreamDerivedInput(Upstream)
{
    public override bool IsProjected => true;
    public IObjectSetDefinition UpstreamSet => Upstream.SourceSet;
    public override object? Project(object source) => CompiledSelector(source);
}

internal sealed class DerivedDefinition<TSource, TItem, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    RelationDefinition<TSource, TItem> relation,
    LambdaExpression computationExpression,
    Func<TSource, IReadOnlyList<TItem>, TValue> computation,
    DerivedImpactPolicy impactPolicy,
    bool forceFullRecompute,
    bool preferConservativePropagation) : IDerivedDefinition
    where TSource : class
    where TItem : class
{
    public ObjectSetDefinition<TSource> SourceSetDefinition { get; } = sourceSet;
    public string? DefinitionKey { get; set; }
    public RelationDefinition<TSource, TItem> RelationDefinition { get; } = relation;
    public Func<TSource, IReadOnlyList<TItem>, TValue> Computation { get; } = computation;
    public IObjectSetDefinition SourceSet => SourceSetDefinition;
    public IReadOnlyList<DerivedInput> Inputs { get; } = [new RelationDerivedInput(relation)];
    public LambdaExpression ComputationExpression { get; } = computationExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeDerived(computationExpression);
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public IDerivedComputationPlan<TSource, TItem, TValue>? IncrementalPlan { get; } =
        DerivedComputationPlanner.Create(
            (Expression<Func<TSource, IReadOnlyList<TItem>, TValue>>)computationExpression,
            forceFullRecompute);
    public string ComputationPlanName => IncrementalPlan?.DisplayName ?? "FullRecompute";
    public bool RequiresExactPropagation => IncrementalPlan is not null ||
        ImpactPolicy.MembershipAdded != DependencySeverity.Dirty ||
        ImpactPolicy.MembershipRemoved != DependencySeverity.Dirty;
    public bool PrefersConservativePropagation { get; } = preferConservativePropagation;
    public bool AllowIncompleteDependencies { get; set; }

    public IDerivedRuntimeState CreateState(IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new DerivedRuntimeState<TSource, TItem, TValue>(this, (RelationRuntimeState<TSource, TItem>)relations[RelationDefinition]);

    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(
            this,
            predicate,
            (Func<TSource, TValue, bool>)compiledPredicate);
}

internal sealed class SourceDerivedDefinition<TSource, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    Expression<Func<TSource, TValue>> computationExpression,
    Func<TSource, TValue> computation,
    DerivedImpactPolicy impactPolicy,
    IReadOnlyList<TrackedExpressionDependency> declaredDependencies) : IDerivedDefinition
    where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs { get; } = [];
    public LambdaExpression ComputationExpression => computationExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeSourceDerived(computationExpression, declaredDependencies);
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public string ComputationPlanName => "SourceFullRecompute";
    public bool RequiresExactPropagation => false;
    public bool PrefersConservativePropagation => false;
    public bool AllowIncompleteDependencies { get; set; }

    public IDerivedRuntimeState CreateState(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this, computation);

    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(
            this,
            predicate,
            (Func<TSource, TValue, bool>)compiledPredicate);
}

internal sealed class ComposedDerivedDefinition<TSource, TUpstream, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    IDerivedDefinition upstream,
    Expression<Func<TSource, TUpstream, TValue>> expression,
    Func<TSource, TUpstream, TValue> computation,
    DerivedImpactPolicy impactPolicy,
    IReadOnlyList<TrackedExpressionDependency>? declaredDependencies = null) : IDerivedDefinition where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs { get; } = [new UpstreamDerivedInput(upstream)];
    public LambdaExpression ComputationExpression => expression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeComposedDerived(expression, declaredDependencies ?? []);
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public string ComputationPlanName => "DependencyFullRecompute";
    public bool RequiresExactPropagation => false;
    public bool PrefersConservativePropagation => false;
    public bool AllowIncompleteDependencies { get; set; }
    public IDerivedRuntimeState CreateState(IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this,
            source => computation(source, (TUpstream)resolveDerived(upstream).GetValue(source)!));
    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(this, predicate, (Func<TSource, TValue, bool>)compiledPredicate);
}

internal sealed class ProjectedComposedDerivedDefinition<TSource, TUpstreamSource, TUpstream, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    IDerivedDefinition upstream,
    Expression<Func<TSource, TUpstreamSource>> selectorExpression,
    Func<TSource, TUpstreamSource> selector,
    Expression<Func<TSource, TUpstream, TValue>> expression,
    Func<TSource, TUpstream, TValue> computation,
    DerivedImpactPolicy impactPolicy,
    IReadOnlyList<TrackedExpressionDependency>? declaredDependencies = null) : IDerivedDefinition
    where TSource : class
    where TUpstreamSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs { get; } =
        [CreateInput(upstream, selectorExpression, selector)];
    public LambdaExpression ComputationExpression => expression;
    public ExpressionDependencyAnalysis Analysis { get; } = Combine(
        ExpressionDependencyAnalyzer.AnalyzeComposedDerived(expression, declaredDependencies ?? []),
        ExpressionDependencyAnalyzer.AnalyzeSourceDerived(selectorExpression));
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public string ComputationPlanName => "ProjectedDependencyFullRecompute";
    public bool RequiresExactPropagation => false;
    public bool PrefersConservativePropagation => false;
    public bool AllowIncompleteDependencies { get; set; }

    public IDerivedRuntimeState CreateState(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this, source => computation(
            source,
            (TUpstream)resolveDerived(upstream).GetValue(selector(source))!));

    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(this, predicate, (Func<TSource, TValue, bool>)compiledPredicate);

    private static ExpressionDependencyAnalysis Combine(
        ExpressionDependencyAnalysis first,
        ExpressionDependencyAnalysis second) => new(
        first.Dependencies.Concat(second.Dependencies).Distinct().ToArray(),
        first.Flags | second.Flags,
        first.HasRelationMembershipDependency || second.HasRelationMembershipDependency,
        first.LinqSemantics | second.LinqSemantics);

    private static ProjectedUpstreamDerivedInput CreateInput(
        IDerivedDefinition definition,
        Expression<Func<TSource, TUpstreamSource>> expression,
        Func<TSource, TUpstreamSource> compiled)
    {
        var analysis = ExpressionDependencyAnalyzer.AnalyzeSourceDerived(expression);
        var dependencies = analysis.Dependencies
            .Where(value => value.Role == ExpressionParameterRole.DerivedSource)
            .ToArray();
        if (expression.Body is not MemberExpression || analysis.Flags != 0 || dependencies.Length != 1 ||
            dependencies[0].Path.Segments.Count != 1)
            throw new ArgumentException(
                "A projected selector must be a direct non-null tracked reference member.", nameof(expression));
        return new ProjectedUpstreamDerivedInput(
            definition, expression, source => compiled((TSource)source), dependencies[0].Path);
    }
}

internal sealed class ProjectedComposedDerivedDefinition<TSource, TUpstreamSource, TFirst, TSecond, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    IDerivedDefinition first,
    IDerivedDefinition second,
    Expression<Func<TSource, TUpstreamSource>> selectorExpression,
    Func<TSource, TUpstreamSource> selector,
    Expression<Func<TSource, TFirst, TSecond, TValue>> expression,
    Func<TSource, TFirst, TSecond, TValue> computation,
    DerivedImpactPolicy impactPolicy) : IDerivedDefinition
    where TSource : class
    where TUpstreamSource : class
{
    private readonly ProjectedUpstreamDerivedInput _firstInput = CreateInput(first, selectorExpression, selector);
    private readonly ProjectedUpstreamDerivedInput _secondInput = CreateInput(second, selectorExpression, selector);
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs => [_firstInput, _secondInput];
    public LambdaExpression ComputationExpression => expression;
    public ExpressionDependencyAnalysis Analysis { get; } = Combine(
        ExpressionDependencyAnalyzer.AnalyzeComposedDerived(expression),
        ExpressionDependencyAnalyzer.AnalyzeSourceDerived(selectorExpression));
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public string ComputationPlanName => "ProjectedDependencyFullRecompute";
    public bool RequiresExactPropagation => false;
    public bool PrefersConservativePropagation => false;
    public bool AllowIncompleteDependencies { get; set; }

    public IDerivedRuntimeState CreateState(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this, source =>
        {
            var target = selector(source);
            return computation(
                source,
                (TFirst)resolveDerived(first).GetValue(target)!,
                (TSecond)resolveDerived(second).GetValue(target)!);
        });

    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(this, predicate, (Func<TSource, TValue, bool>)compiledPredicate);

    private static ExpressionDependencyAnalysis Combine(
        ExpressionDependencyAnalysis firstAnalysis,
        ExpressionDependencyAnalysis secondAnalysis) => new(
        firstAnalysis.Dependencies.Concat(secondAnalysis.Dependencies).Distinct().ToArray(),
        firstAnalysis.Flags | secondAnalysis.Flags,
        firstAnalysis.HasRelationMembershipDependency || secondAnalysis.HasRelationMembershipDependency,
        firstAnalysis.LinqSemantics | secondAnalysis.LinqSemantics);

    private static ProjectedUpstreamDerivedInput CreateInput(
        IDerivedDefinition definition,
        Expression<Func<TSource, TUpstreamSource>> selectorExpressionValue,
        Func<TSource, TUpstreamSource> compiled)
    {
        var analysis = ExpressionDependencyAnalyzer.AnalyzeSourceDerived(selectorExpressionValue);
        var dependencies = analysis.Dependencies
            .Where(value => value.Role == ExpressionParameterRole.DerivedSource).ToArray();
        if (selectorExpressionValue.Body is not MemberExpression || analysis.Flags != 0 ||
            dependencies.Length != 1 || dependencies[0].Path.Segments.Count != 1)
            throw new ArgumentException("A projected selector must be a direct non-null tracked reference member.");
        return new ProjectedUpstreamDerivedInput(
            definition, selectorExpressionValue, source => compiled((TSource)source), dependencies[0].Path);
    }
}

internal sealed class ComposedDerivedDefinition<TSource, TFirst, TSecond, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    IDerivedDefinition first,
    IDerivedDefinition second,
    Expression<Func<TSource, TFirst, TSecond, TValue>> expression,
    Func<TSource, TFirst, TSecond, TValue> computation,
    DerivedImpactPolicy impactPolicy,
    IReadOnlyList<TrackedExpressionDependency>? declaredDependencies = null) : IDerivedDefinition where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs { get; } =
        [new UpstreamDerivedInput(first), new UpstreamDerivedInput(second)];
    public LambdaExpression ComputationExpression => expression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeComposedDerived(expression, declaredDependencies ?? []);
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public string ComputationPlanName => "DependencyFullRecompute";
    public bool RequiresExactPropagation => false;
    public bool PrefersConservativePropagation => false;
    public bool AllowIncompleteDependencies { get; set; }
    public IDerivedRuntimeState CreateState(IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this, source => computation(
            source,
            (TFirst)resolveDerived(first).GetValue(source)!,
            (TSecond)resolveDerived(second).GetValue(source)!));
    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(this, predicate, (Func<TSource, TValue, bool>)compiledPredicate);
}

internal sealed class MixedProjectedComposedDerivedDefinition<
    TSource, TUpstreamSource, TProjected, TLocal, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    IDerivedDefinition projected,
    IDerivedDefinition local,
    Expression<Func<TSource, TUpstreamSource>> selectorExpression,
    Func<TSource, TUpstreamSource> selector,
    Expression<Func<TSource, TProjected, TLocal, TValue>> expression,
    Func<TSource, TProjected, TLocal, TValue> computation,
    DerivedImpactPolicy impactPolicy,
    IReadOnlyList<TrackedExpressionDependency>? declaredDependencies = null) : IDerivedDefinition
    where TSource : class
    where TUpstreamSource : class
{
    private readonly ProjectedUpstreamDerivedInput _projectedInput =
        CreateInput(projected, selectorExpression, selector);

    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs => [_projectedInput, new UpstreamDerivedInput(local)];
    public LambdaExpression ComputationExpression => expression;
    public ExpressionDependencyAnalysis Analysis { get; } = Combine(
        ExpressionDependencyAnalyzer.AnalyzeComposedDerived(expression, declaredDependencies ?? []),
        ExpressionDependencyAnalyzer.AnalyzeSourceDerived(selectorExpression));
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public string ComputationPlanName => "ProjectedDependencyFullRecompute";
    public bool RequiresExactPropagation => false;
    public bool PrefersConservativePropagation => false;
    public bool AllowIncompleteDependencies { get; set; }

    public IDerivedRuntimeState CreateState(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this, source => computation(
            source,
            (TProjected)resolveDerived(projected).GetValue(selector(source))!,
            (TLocal)resolveDerived(local).GetValue(source)!));

    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(
            this, predicate, (Func<TSource, TValue, bool>)compiledPredicate);

    private static ExpressionDependencyAnalysis Combine(
        ExpressionDependencyAnalysis first,
        ExpressionDependencyAnalysis second) => new(
        first.Dependencies.Concat(second.Dependencies).Distinct().ToArray(),
        first.Flags | second.Flags,
        first.HasRelationMembershipDependency || second.HasRelationMembershipDependency,
        first.LinqSemantics | second.LinqSemantics);

    private static ProjectedUpstreamDerivedInput CreateInput(
        IDerivedDefinition definition,
        Expression<Func<TSource, TUpstreamSource>> expression,
        Func<TSource, TUpstreamSource> compiled)
    {
        var analysis = ExpressionDependencyAnalyzer.AnalyzeSourceDerived(expression);
        var dependencies = analysis.Dependencies
            .Where(value => value.Role == ExpressionParameterRole.DerivedSource)
            .ToArray();
        if (expression.Body is not MemberExpression || analysis.Flags != 0 || dependencies.Length != 1 ||
            dependencies[0].Path.Segments.Count != 1)
            throw new ArgumentException(
                "A projected selector must be a direct non-null tracked reference member.", nameof(expression));
        return new ProjectedUpstreamDerivedInput(
            definition, expression, source => compiled((TSource)source), dependencies[0].Path);
    }
}

