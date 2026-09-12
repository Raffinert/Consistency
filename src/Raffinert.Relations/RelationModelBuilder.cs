using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Relations;

/// <summary>Builds an immutable description of object sets and their relations.</summary>
public sealed class RelationModelBuilder
{
    private readonly object _identity = new();
    private readonly List<IObjectSetDefinition> _objectSets = [];
    private readonly List<IRelationDefinition> _relations = [];
    private readonly List<IDerivedDefinition> _derivedStates = [];
    private readonly List<IInvariantDefinition> _invariants = [];
    private bool _built;

    internal bool ForceScanPlansForTesting { get; private set; }

    internal RelationModelBuilder UseScanPlansForTesting()
    {
        ThrowIfBuilt();
        ForceScanPlansForTesting = true;
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
        ObjectSetBuilder<TLeft> left,
        ObjectSetBuilder<TRight> right)
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

    public DerivedBuilder<TSource> Derived<TSource>(ObjectSetBuilder<TSource> source) where TSource : class
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(source);
        EnsureOwned(source.ModelIdentity);
        return new DerivedBuilder<TSource>(this, source);
    }

    public InvariantBuilder<TSource> Invariant<TSource>(ObjectSetBuilder<TSource> source) where TSource : class
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(source);
        EnsureOwned(source.ModelIdentity);
        return new InvariantBuilder<TSource>(this, source);
    }

    public CompiledRelationModel Build()
    {
        ThrowIfBuilt();
        foreach (var set in _objectSets)
        {
            if (!set.HasKey)
                throw new InvalidOperationException($"Object set '{set.ObjectType.Name}' has no key. Call Key(...) before Build().");
        }

        _built = true;
        return new CompiledRelationModel(
            _objectSets.ToArray(),
            _relations.ToArray(),
            _derivedStates.ToArray(),
            _invariants.ToArray());
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
        _ensureMutable = ensureMutable;
    }

    internal object ModelIdentity { get; }
    internal ObjectSetDefinition<T> Definition { get; }

    public ObjectSetBuilder<T> Key<TKey>(Expression<Func<T, TKey>> key)
    {
        _ensureMutable();
        ArgumentNullException.ThrowIfNull(key);
        if (Definition.HasKey)
            throw new InvalidOperationException($"A key has already been declared for '{typeof(T).Name}'.");

        var compiled = key.Compile();
        Definition.SetKey(key, value => compiled(value));
        return this;
    }

    public override string ToString() => $"ObjectSet {typeof(T).Name}";
}

internal interface IObjectSetDefinition
{
    int Id { get; }
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
