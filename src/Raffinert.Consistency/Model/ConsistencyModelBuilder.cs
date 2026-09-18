using System.Linq.Expressions;
using System.Reflection;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

/// <summary>Builds an immutable description of object sets and their relations.</summary>
public sealed class ConsistencyModelBuilder
{
    private readonly object _identity = new();
    private readonly List<IObjectSetDefinition> _objectSets = [];
    private readonly List<IRelationDefinition> _relations = [];
    private readonly List<IDerivedDefinition> _derivedStates = [];
    private readonly List<IInvariantDefinition> _invariants = [];
    private readonly List<MaterializationDescriptor> _materializations = [];
    private bool _built;

    internal bool ForceScanPlansForTesting { get; private set; }
    internal bool ForceFullRecomputePlansForTesting { get; private set; }

    internal ConsistencyModelBuilder UseScanPlansForTesting()
    {
        ThrowIfBuilt();
        ForceScanPlansForTesting = true;
        return this;
    }

    internal ConsistencyModelBuilder UseFullRecomputePlansForTesting()
    {
        ThrowIfBuilt();
        ForceFullRecomputePlansForTesting = true;
        return this;
    }

    public ObjectSetBuilder<T> Objects<T>() where T : class
    {
        ThrowIfBuilt();
        var set = new ObjectSetBuilder<T>(_identity, _objectSets.Count, ThrowIfBuilt);
        _objectSets.Add(set.Definition);
        return set;
    }

    public RelationBuilder<TLeft, TRight> Relation<TLeft, TRight>(
        ObjectSet<TLeft> left,
        ObjectSet<TRight> right)
        where TLeft : class
        where TRight : class
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        EnsureOwned(left.ModelIdentity);
        EnsureOwned(right.ModelIdentity);
        return new RelationBuilder<TLeft, TRight>(this, left, right);
    }

    public DerivedBuilder<TSource> Derived<TSource>(ObjectSet<TSource> source) where TSource : class
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(source);
        EnsureOwned(source.ModelIdentity);
        return new DerivedBuilder<TSource>(this, source);
    }

    public InvariantBuilder<TSource> Invariant<TSource>(ObjectSet<TSource> source) where TSource : class
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(source);
        EnsureOwned(source.ModelIdentity);
        return new InvariantBuilder<TSource>(this, source);
    }

    public CompiledConsistencyModel Build()
    {
        ThrowIfBuilt();
        ValidateDefinitionKeys();
        ValidateMaterializationDependencies();
        foreach (var set in _objectSets)
        {
            if (!set.HasKey)
                throw new InvalidOperationException($"Object set '{set.ObjectType.Name}' has no key. Call Key(...) before Build().");
        }
        foreach (var derived in _derivedStates)
        {
            ValidateComplete(
                "Derived computation",
                derived.ComputationExpression.Body.ToString(),
                derived.Analysis.Flags,
                derived.AllowIncompleteDependencies);
        }
        foreach (var invariant in _invariants)
        {
            ValidateComplete(
                "Invariant predicate",
                invariant.PredicateExpression.Body.ToString(),
                invariant.Analysis.Flags,
                invariant.AllowIncompleteDependencies);
        }
        foreach (var relation in _derivedStates.SelectMany(derived => derived.Inputs)
                     .OfType<RelationDerivedInput>().Select(input => input.Relation).Distinct())
        {
            ValidateComplete(
                "Materialized relation",
                relation.PredicateExpression.Body.ToString(),
                relation.Analysis.DependencyAnalysis,
                relation.AllowIncompleteDependencies);
            var consumers = _derivedStates.Where(derived =>
                derived.Inputs.OfType<RelationDerivedInput>()
                    .Any(input => ReferenceEquals(input.Relation, relation)));
            if (consumers.All(derived => derived.PrefersConservativePropagation) &&
                !consumers.Any(derived => derived.RequiresExactPropagation))
                relation.UseConservativePropagation();
            else
                relation.RequireExactPropagation();
        }

        var dependencyGraph = CompiledDependencyGraph.Compile(_derivedStates, _invariants);

        _built = true;
        return new CompiledConsistencyModel(
            _objectSets.ToArray(),
            _relations.ToArray(),
            _derivedStates.ToArray(),
            _invariants.ToArray(),
            _materializations.ToArray(),
            dependencyGraph);
    }

    private void ValidateDefinitionKeys()
    {
        var definitions = _objectSets.Select(set => (Kind: "object set", set.DefinitionKey))
            .Concat(_relations.Select(relation => ("relation", relation.DefinitionKey)))
            .Concat(_derivedStates.Select(derived => ("derived value", derived.DefinitionKey)))
            .Concat(_invariants.Select(invariant => ("invariant", invariant.DefinitionKey)))
            .Where(value => value.DefinitionKey is not null)
            .ToArray();
        var duplicate = definitions.GroupBy(value => value.DefinitionKey!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Definition key '{duplicate.Key}' is used more than once. Explicit definition keys must be unique within a compiled model.");
    }

    internal void AddRelation(IRelationDefinition relation)
    {
        ThrowIfBuilt();
        _relations.Add(relation);
    }

    internal void AddDerived(IDerivedDefinition derived)
    {
        ThrowIfBuilt();
        _derivedStates.Add(derived);
    }

    internal void AddInvariant(IInvariantDefinition invariant)
    {
        ThrowIfBuilt();
        _invariants.Add(invariant);
    }

    internal void AddMaterialization<TSource, TValue>(
        IDerivedDefinition definition,
        Expression<Func<TSource, TValue>> target)
        where TSource : class
    {
        ThrowIfBuilt();
        EnsureDerived(definition);
        if (_materializations.Any(value => ReferenceEquals(value.Definition, definition)))
            throw new InvalidOperationException("A derived definition can have only one materialization target.");
        var descriptor = MaterializationDescriptor.Create(definition, target);
        if (_materializations.Any(value =>
                ReferenceEquals(value.SourceSet, descriptor.SourceSet) && value.Target == descriptor.Target))
            throw new InvalidOperationException(
                $"Materialization target '{descriptor.Target.Name}' is already registered for this object set.");
        _materializations.Add(descriptor);
    }

    internal void EnsureRelation(IRelationDefinition relation)
    {
        ThrowIfBuilt();
        if (!_relations.Contains(relation))
            throw new ArgumentException("The relation does not belong to this model builder.");
    }

    internal void EnsureDerived(IDerivedDefinition derived)
    {
        ThrowIfBuilt();
        if (!_derivedStates.Contains(derived))
            throw new ArgumentException("The derived state does not belong to this model builder.");
    }

    internal void EnsureMutable() => ThrowIfBuilt();

    private void ValidateMaterializationDependencies()
    {
        foreach (var derived in _derivedStates)
        {
            foreach (var dependency in derived.Analysis.Dependencies.Where(value =>
                         value.Role == ExpressionParameterRole.DerivedSource &&
                         value.Path.Segments.Count == 1))
            {
                var target = _materializations.FirstOrDefault(value =>
                    ReferenceEquals(value.SourceSet, derived.SourceSet) &&
                    value.Target == dependency.Path.Segments[0].Member);
                if (target is null)
                    continue;
                var name = target.Definition.DefinitionKey ?? "<unnamed>";
                throw new InvalidOperationException(
                    $"'{target.Target.Name}' is a materialization target of derived definition '{name}'. " +
                    $"Depend on the logical definition with From({name}) instead of treating the mirror property " +
                    "as an independent source dependency.");
            }
        }
    }

    private static void ValidateComplete(
        string consumerKind,
        string description,
        DependencyAnalysisFlags flags,
        bool allowIncomplete)
    {
        if (flags == DependencyAnalysisFlags.Complete || allowIncomplete)
            return;
        throw new InvalidOperationException(
            $"{consumerKind} '{description}' has incomplete dependency tracking ({flags}). " +
            "Call AllowIncompleteDependencies() on that definition to explicitly accept weaker " +
            "cached-freshness guarantees.");
    }

    private void EnsureOwned(object identity)
    {
        if (!ReferenceEquals(identity, _identity))
            throw new ArgumentException("The object set belongs to a different model builder.");
    }

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("This model builder has already been built.");
    }
}

/// <summary>Declares a logical set of runtime objects and its stable key.</summary>
public sealed class ObjectSetBuilder<T> where T : class
{
    private readonly Action _ensureMutable;

    internal ObjectSetBuilder(object modelIdentity, int id, Action ensureMutable)
    {
        ModelIdentity = modelIdentity;
        Definition = new ObjectSetDefinition<T>(id);
        Set = new ObjectSet<T>(modelIdentity, Definition);
        _ensureMutable = ensureMutable;
    }

    internal object ModelIdentity { get; }
    internal ObjectSetDefinition<T> Definition { get; }
    public ObjectSet<T> Set { get; }

    public ObjectSet<T> Key<TKey>(Expression<Func<T, TKey>> key)
    {
        _ensureMutable();
        ArgumentNullException.ThrowIfNull(key);
        if (Definition.HasKey)
            throw new InvalidOperationException($"A key has already been declared for '{typeof(T).Name}'.");

        KeyExpressionValidator.Validate(key);
        var compiled = key.Compile();
        Definition.SetKey(key, value => compiled(value));
        return Set;
    }

    /// <summary>Assigns a stable logical key for diagnostics and durable integration messages.</summary>
    public ObjectSetBuilder<T> Named(string definitionKey)
    {
        _ensureMutable();
        Definition.DefinitionKey = ValidateDefinitionKey(definitionKey);
        return this;
    }

    internal static string ValidateDefinitionKey(string definitionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionKey);
        return definitionKey.Trim();
    }

    public override string ToString() => $"ObjectSet {typeof(T).Name}";
}

/// <summary>A stable typed identity handle for an object set.</summary>
public sealed class ObjectSet<T> where T : class
{
    internal ObjectSet(object modelIdentity, ObjectSetDefinition<T> definition)
    {
        ModelIdentity = modelIdentity;
        Definition = definition;
    }

    internal object ModelIdentity { get; }
    internal ObjectSetDefinition<T> Definition { get; }

    /// <summary>The optional stable logical key assigned while building the model.</summary>
    public string? DefinitionKey => Definition.DefinitionKey;

    public override string ToString() => $"ObjectSet {typeof(T).Name}";
}

internal interface IObjectSetDefinition
{
    int Id { get; }
    string? DefinitionKey { get; }
    Type ObjectType { get; }
    bool HasKey { get; }
    LambdaExpression? KeyExpression { get; }
    IReadOnlySet<MemberInfo> KeyMembers { get; }
    object? ReadKey(object instance);
}

internal sealed class ObjectSetDefinition<T>(int id) : IObjectSetDefinition where T : class
{
    private Func<T, object?>? _keyAccessor;

    public int Id { get; } = id;
    public string? DefinitionKey { get; set; }
    public Type ObjectType => typeof(T);
    public bool HasKey => _keyAccessor is not null;
    public LambdaExpression? KeyExpression { get; private set; }
    public IReadOnlySet<MemberInfo> KeyMembers { get; private set; } = new HashSet<MemberInfo>();

    public void SetKey(LambdaExpression expression, Func<T, object?> accessor)
    {
        KeyExpression = expression;
        KeyMembers = KeyMemberCollector.Collect(expression);
        _keyAccessor = accessor;
    }

    public object? ReadKey(object instance) =>
        (_keyAccessor ?? throw new InvalidOperationException("The object set has no key."))((T)instance);
}

internal sealed class KeyMemberCollector : ExpressionVisitor
{
    private readonly ParameterExpression _parameter;
    private readonly HashSet<MemberInfo> _members = [];

    private KeyMemberCollector(ParameterExpression parameter) => _parameter = parameter;

    public static IReadOnlySet<MemberInfo> Collect(LambdaExpression expression)
    {
        var collector = new KeyMemberCollector(expression.Parameters[0]);
        collector.Visit(expression.Body);
        return collector._members;
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        Expression? root = node.Expression;
        while (root is MemberExpression member)
            root = member.Expression;
        while (root is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            root = unary.Operand;

        if (root == _parameter)
            _members.Add(node.Member);

        return base.VisitMember(node);
    }
}

internal static class KeyExpressionValidator
{
    public static void Validate(LambdaExpression expression)
    {
        if (!IsKeyShape(Unwrap(expression.Body), expression.Parameters[0], allowComposite: true))
            throw new ArgumentException(
                "A key must be a direct scalar/value member or a tuple/anonymous composite of direct scalar/value members. " +
                "Nested navigation, method calls, captured/static state, and collection-derived keys are not supported.",
                nameof(expression));
    }

    private static bool IsKeyShape(Expression expression, ParameterExpression parameter, bool allowComposite)
    {
        expression = Unwrap(expression);
        if (expression is MemberExpression { Expression: var owner } member &&
            ReferenceEquals(Unwrap(owner!), parameter))
            return IsStableValueType(member.Type);

        return allowComposite && expression is NewExpression { Arguments.Count: > 0 } composite &&
               composite.Arguments.All(argument => IsKeyShape(argument, parameter, allowComposite: false));
    }

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            expression = unary.Operand;
        return expression;
    }

    private static bool IsStableValueType(Type type) =>
        type == typeof(string) || type.IsValueType;
}
