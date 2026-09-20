using System.Linq.Expressions;

namespace Raffinert.Consistency;

internal enum RelationPropagationPlan
{
    None,
    ExactMaterialized,
    ConservativeInvalidation
}

public sealed class RelationBuilder<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly ConsistencyModelBuilder _model;
    private readonly ObjectSet<TLeft> _left;
    private readonly ObjectSet<TRight> _right;
    private bool _defined;

    internal RelationBuilder(ConsistencyModelBuilder model, ObjectSet<TLeft> left, ObjectSet<TRight> right)
    {
        _model = model;
        _left = left;
        _right = right;
    }

    public Relation<TLeft, TRight> Where(Expression<Func<TLeft, TRight, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_defined)
            throw new InvalidOperationException("This relation builder already has a predicate.");

        _defined = true;
        var definition = new RelationDefinition<TLeft, TRight>(
            _left.Definition,
            _right.Definition,
            predicate,
            Expressions.RelationExpressionAnalyzer.Analyze(predicate),
            _model);
        _model.AddRelation(definition);
        return new Relation<TLeft, TRight>(definition, _model.EnsureMutable);
    }
}

/// <summary>A typed handle to a relation in a compiled model.</summary>
public sealed class Relation<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly Action _ensureMutable;

    internal Relation(RelationDefinition<TLeft, TRight> definition, Action ensureMutable)
    {
        Definition = definition;
        _ensureMutable = ensureMutable;
    }
    internal RelationDefinition<TLeft, TRight> Definition { get; }

    /// <summary>The optional stable logical key assigned to this relation.</summary>
    public string? DefinitionKey => Definition.DefinitionKey;

    /// <summary>Assigns a stable logical key for diagnostics and durable integration messages.</summary>
    public Relation<TLeft, TRight> Named(string definitionKey)
    {
        _ensureMutable();
        Definition.DefinitionKey = ObjectSetBuilder<TLeft>.ValidateDefinitionKey(definitionKey);
        return this;
    }

    /// <summary>
    /// Explicitly permits incomplete dependency tracking. If this relation is materialized for a
    /// derived value, cached freshness is not guaranteed for dependencies hidden by opaque code or
    /// mutable external state.
    /// </summary>
    public Relation<TLeft, TRight> AllowIncompleteDependencies()
    {
        _ensureMutable();
        Definition.AllowIncompleteDependencies = true;
        return this;
    }
}

internal interface IRelationDefinition
{
    string? DefinitionKey { get; }
    IObjectSetDefinition LeftSet { get; }
    IObjectSetDefinition RightSet { get; }
    LambdaExpression PredicateExpression { get; }
    Expressions.RelationAnalysis Analysis { get; }
    Expressions.RelationAccessPlan AccessPlan { get; }
    Expressions.RelationAccessPlan? ReverseAccessPlan { get; }
    RelationPropagationPlan PropagationPlan { get; }
    bool AllowIncompleteDependencies { get; }
    void RequireExactPropagation();
    void UseConservativePropagation();
    IRelationRuntimeState CreateState(IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets);
    IRelationQueryState CreatePreviewState(
        Func<IObjectSetDefinition, IEnumerable<object>> instances,
        Func<IObjectSetDefinition, object, bool> contains);
}

internal sealed class RelationDefinition<TLeft, TRight> : IRelationDefinition
    where TLeft : class
    where TRight : class
{
    public RelationDefinition(
        ObjectSetDefinition<TLeft> leftSet,
        ObjectSetDefinition<TRight> rightSet,
        Expression<Func<TLeft, TRight, bool>> predicate,
        Expressions.RelationAnalysis analysis,
        ConsistencyModelBuilder model)
    {
        Left = leftSet;
        Right = rightSet;
        Predicate = predicate.Compile();
        PredicateExpression = predicate;
        Analysis = analysis;
        AccessPlan = Expressions.RelationPlanner.Plan(analysis, model.ForceScanPlansForTesting);
        _forceScanPlans = model.ForceScanPlansForTesting;
    }

    private readonly bool _forceScanPlans;

    public ObjectSetDefinition<TLeft> Left { get; }
    public string? DefinitionKey { get; set; }
    public ObjectSetDefinition<TRight> Right { get; }
    public Func<TLeft, TRight, bool> Predicate { get; }
    public IObjectSetDefinition LeftSet => Left;
    public IObjectSetDefinition RightSet => Right;
    public LambdaExpression PredicateExpression { get; }
    public Expressions.RelationAnalysis Analysis { get; }
    public Expressions.RelationAccessPlan AccessPlan { get; }
    public Expressions.RelationAccessPlan? ReverseAccessPlan { get; private set; }
    public RelationPropagationPlan PropagationPlan { get; private set; }
    public bool AllowIncompleteDependencies { get; set; }

    public void RequireExactPropagation()
    {
        PropagationPlan = RelationPropagationPlan.ExactMaterialized;
        ReverseAccessPlan ??= Expressions.RelationPlanner.PlanReverse(Analysis, _forceScanPlans);
    }

    public void UseConservativePropagation()
    {
        if (PropagationPlan == RelationPropagationPlan.ExactMaterialized)
            return;
        PropagationPlan = RelationPropagationPlan.ConservativeInvalidation;
        ReverseAccessPlan ??= Expressions.RelationPlanner.PlanReverse(Analysis, _forceScanPlans);
    }

    public IRelationRuntimeState CreateState(IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets) =>
        new RelationRuntimeState<TLeft, TRight>(this, sets[Left], sets[Right]);

    public IRelationQueryState CreatePreviewState(
        Func<IObjectSetDefinition, IEnumerable<object>> instances,
        Func<IObjectSetDefinition, object, bool> contains) =>
        new PreviewRelationState<TLeft, TRight>(this, instances, contains);
}
