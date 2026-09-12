using System.Linq.Expressions;

namespace Raffinert.Relations;

public sealed class RelationBuilder<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSetBuilder<TLeft> _left;
    private readonly ObjectSetBuilder<TRight> _right;
    private bool _defined;

    internal RelationBuilder(RelationModelBuilder model, ObjectSetBuilder<TLeft> left, ObjectSetBuilder<TRight> right)
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
        return new Relation<TLeft, TRight>(definition);
    }
}

/// <summary>A typed handle to a relation in a compiled model.</summary>
public sealed class Relation<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    internal Relation(RelationDefinition<TLeft, TRight> definition) => Definition = definition;
    internal RelationDefinition<TLeft, TRight> Definition { get; }
}

internal interface IRelationDefinition
{
    IObjectSetDefinition LeftSet { get; }
    IObjectSetDefinition RightSet { get; }
    LambdaExpression PredicateExpression { get; }
    Expressions.RelationAnalysis Analysis { get; }
    Expressions.RelationAccessPlan AccessPlan { get; }
    Expressions.RelationAccessPlan? ReverseAccessPlan { get; }
    void RequireExactPropagation();
    IRelationRuntimeState CreateState(IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets);
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
        RelationModelBuilder model)
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
    public ObjectSetDefinition<TRight> Right { get; }
    public Func<TLeft, TRight, bool> Predicate { get; }
    public IObjectSetDefinition LeftSet => Left;
    public IObjectSetDefinition RightSet => Right;
    public LambdaExpression PredicateExpression { get; }
    public Expressions.RelationAnalysis Analysis { get; }
    public Expressions.RelationAccessPlan AccessPlan { get; }
    public Expressions.RelationAccessPlan? ReverseAccessPlan { get; private set; }

    public void RequireExactPropagation() =>
        ReverseAccessPlan ??= Expressions.RelationPlanner.PlanReverse(Analysis, _forceScanPlans);

    public IRelationRuntimeState CreateState(IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets) =>
        new RelationRuntimeState<TLeft, TRight>(this, sets[Left], sets[Right]);
}
