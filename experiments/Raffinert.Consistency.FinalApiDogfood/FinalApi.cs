using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.FinalApiDogfood;

internal sealed class FinalModel
{
    private readonly List<FinalNode> _nodes = [];
    private readonly List<TrackedDependency> _dependencies = [];
    private readonly List<IMaterializationDefinition> _materializations = [];
    private int _hiddenNodeId;

    internal ConsistencyModelBuilder Raw { get; } = new();

    public FinalSet<T> Objects<T>() where T : class => new(this, Raw.Objects<T>());

    public FinalDerivedStart<T> Derived<T>(FinalSet<T> source) where T : class => new(this, source);

    public FinalRelationDraft<TLeft, TRight> Relation<TLeft, TRight>(
        FinalSet<TLeft> left,
        FinalSet<TRight> right)
        where TLeft : class where TRight : class => new(this, left, right);

    public FinalInvariantStart<TSource> Invariant<TSource>(FinalSet<TSource> source)
        where TSource : class => new(source);

    public FinalCompiledModel Build()
    {
        ValidateMirrorDependencies();
        var compiled = Raw.Build();
        return new FinalCompiledModel(compiled, _materializations.ToArray(), RenderDebugView());
    }

    internal FinalValue<TSource, TValue> AddValue<TSource, TValue>(
        FinalSet<TSource> source,
        Derived<TSource, TValue> raw,
        IReadOnlyList<FinalNode> upstream,
        IReadOnlyList<TrackedDependency> dependencies,
        string? aggregate = null,
        string? projection = null)
        where TSource : class
    {
        var node = new FinalNode(source, typeof(TValue), upstream, dependencies, aggregate, projection);
        _nodes.Add(node);
        _dependencies.AddRange(dependencies);
        return new FinalValue<TSource, TValue>(this, source, raw, node);
    }

    internal Derived<TSource, int> BuildDependencyToken<TSource>(
        FinalSet<TSource> source,
        IReadOnlyList<IDependencyRegistration<TSource>> dependencies)
        where TSource : class
    {
        var builder = Raw.Derived(source.Raw);
        foreach (var dependency in dependencies)
            dependency.Apply(builder);
        return builder.Compute(_ => 0).Named($"$opaque-dependencies-{_hiddenNodeId++}");
    }

    internal Derived<TSource, TValue> BuildProjectedBridge<TSource, TTarget, TValue>(
        FinalSet<TSource> source,
        Expression<Func<TSource, TTarget>> selector,
        FinalValue<TTarget, TValue> upstream)
        where TSource : class where TTarget : class => Raw.Derived(source.Raw)
        .Using(selector, upstream.Raw)
        .Compute((_, value) => value)
        .Named($"$projected-bridge-{_hiddenNodeId++}");

    internal void RegisterMaterialization<TSource, TValue>(
        FinalValue<TSource, TValue> value,
        Expression<Func<TSource, TValue>> target)
        where TSource : class
    {
        if (value.Node.Materialization is not null)
            throw new InvalidOperationException("A derived definition can have only one materialization target.");
        var property = DirectWritableProperty(target);
        if (_materializations.Any(existing => ReferenceEquals(existing.SourceIdentity, value.Source) &&
                                              existing.TargetMember == property))
            throw new InvalidOperationException(
                $"Materialization target '{property.Name}' is already registered for this object set.");
        var definition = new MaterializationDefinition<TSource, TValue>(value, target, property);
        _materializations.Add(definition);
        value.Node.Materialization = property;
    }

    internal TrackedDependency TrackDependency<TSource, TValue>(
        FinalSet<TSource> source,
        Expression<Func<TSource, TValue>> expression)
        where TSource : class
    {
        var members = MemberPath(expression);
        return new TrackedDependency(source, members[^1], string.Join('.', members.Select(x => x.Name)), expression);
    }

    private void ValidateMirrorDependencies()
    {
        foreach (var dependency in _dependencies)
        {
            var target = _materializations.FirstOrDefault(materialization =>
                ReferenceEquals(materialization.SourceIdentity, dependency.SourceIdentity) &&
                materialization.TargetMember == dependency.LeafMember);
            if (target is null) continue;
            var name = target.ValueNode.Name ?? "<unnamed>";
            throw new InvalidOperationException(
                $"'{dependency.Path}' is a materialization target of derived definition '{name}'. " +
                $"Depend on the derived definition with From({name}) instead of treating the mirror property " +
                "as an independent source dependency.");
        }
    }

    private string RenderDebugView()
    {
        var text = new StringBuilder();
        foreach (var node in _nodes)
        {
            text.AppendLine($"{node.Name ?? "<unnamed>"} [Derived<{node.Source.SourceType.Name}, {Friendly(node.ValueType)}>]");
            foreach (var upstream in node.Upstream)
                text.AppendLine($"  from {upstream.Name ?? "<unnamed>"}");
            if (node.Projection is not null)
                text.AppendLine($"  projection {node.Projection}");
            foreach (var dependency in node.Dependencies)
                text.AppendLine($"  depends-on {dependency.Path}");
            if (node.Materialization is not null)
                text.AppendLine($"  materializes-to {node.Materialization.Name}");
            if (node.Aggregate is not null)
                text.AppendLine($"  operator {node.Aggregate}");
        }
        return text.ToString();
    }

    private static string Friendly(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        return nullable is null ? type.Name : $"{nullable.Name}?";
    }

    private static PropertyInfo DirectWritableProperty<TSource, TValue>(Expression<Func<TSource, TValue>> expression)
    {
        var body = expression.Body is UnaryExpression unary ? unary.Operand : expression.Body;
        if (body is not MemberExpression { Expression: ParameterExpression, Member: PropertyInfo property } ||
            property.SetMethod is null || property.GetMethod?.IsStatic == true || property.GetIndexParameters().Length != 0)
            throw new ArgumentException(
                "MaterializeTo must select one direct writable property on the derived source object.",
                nameof(expression));
        return property;
    }

    private static IReadOnlyList<MemberInfo> MemberPath(LambdaExpression expression)
    {
        var members = new Stack<MemberInfo>();
        Expression? current = expression.Body is UnaryExpression unary ? unary.Operand : expression.Body;
        while (current is MemberExpression member)
        {
            members.Push(member.Member);
            current = member.Expression;
        }
        if (current != expression.Parameters[0] || members.Count == 0)
            throw new ArgumentException("DependsOn must select a member path rooted at the source parameter.");
        return members.ToArray();
    }

}

internal interface IFinalSet
{
    Type SourceType { get; }
}

internal sealed class FinalSet<T> : IFinalSet where T : class
{
    private readonly ObjectSetBuilder<T> _builder;
    private string? _name;

    internal FinalSet(FinalModel model, ObjectSetBuilder<T> builder) => (Model, _builder) = (model, builder);
    internal FinalModel Model { get; }
    internal ObjectSet<T> Raw { get; private set; } = null!;
    public Type SourceType => typeof(T);

    public FinalSet<T> Named(string name)
    {
        _name = name;
        return this;
    }

    public FinalSet<T> Key<TKey>(Expression<Func<T, TKey>> key)
    {
        if (_name is not null) _builder.Named(_name);
        Raw = _builder.Key(key);
        return this;
    }
}

internal sealed class FinalDerivedStart<TSource>(FinalModel model, FinalSet<TSource> source)
    where TSource : class
{
    public FinalValue<TSource, TValue> Select<TValue>(Expression<Func<TSource, TValue>> computation)
    {
        var raw = model.Raw.Derived(source.Raw).Compute(computation);
        return model.AddValue(source, raw, [], []);
    }

    public FinalDirectStage<TSource> DependsOn<TValue>(
        Expression<Func<TSource, TValue>> first,
        params Expression<Func<TSource, TValue>>[] additional)
    {
        var builder = model.Raw.Derived(source.Raw).DependsOn(first);
        var dependencies = new List<TrackedDependency> { model.TrackDependency(source, first) };
        foreach (var dependency in additional)
        {
            builder.DependsOn(dependency);
            dependencies.Add(model.TrackDependency(source, dependency));
        }
        return new FinalDirectStage<TSource>(model, source, builder, dependencies);
    }

    public FinalFromOne<TSource, TUpstream> From<TUpstream>(FinalValue<TSource, TUpstream> upstream)
    {
        upstream.RequireOwner(model, source);
        return new FinalFromOne<TSource, TUpstream>(model, source, upstream);
    }

    public FinalPair<TSource, TFirst, TSecond> From<TFirst, TSecond>(
        FinalValue<TSource, TFirst> first,
        FinalValue<TSource, TSecond> second)
    {
        first.RequireOwner(model, source);
        second.RequireOwner(model, source);
        return new FinalPair<TSource, TFirst, TSecond>(model, source, first, second);
    }

    public FinalProjectedOne<TSource, TTarget, TUpstream> From<TTarget, TUpstream>(
        Expression<Func<TSource, TTarget>> selector,
        FinalValue<TTarget, TUpstream> upstream)
        where TTarget : class
    {
        upstream.RequireModel(model);
        return new FinalProjectedOne<TSource, TTarget, TUpstream>(model, source, selector, upstream);
    }

    public FinalRelationAggregate<TSource, TItem> From<TItem>(FinalRelation<TSource, TItem> relation)
        where TItem : class
    {
        relation.RequireOwner(model, source);
        return new FinalRelationAggregate<TSource, TItem>(model, source, relation);
    }
}

internal sealed class FinalPair<TSource, TFirst, TSecond>(
    FinalModel model,
    FinalSet<TSource> source,
    FinalValue<TSource, TFirst> first,
    FinalValue<TSource, TSecond> second)
    where TSource : class
{
    private Action<DerivedImpactPolicyBuilder<TSource>>? _impact;

    public FinalPair<TSource, TFirst, TSecond> Impact(Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        _impact = configure;
        return this;
    }

    public FinalValue<TSource, TValue> Select<TValue>(
        Expression<Func<TSource, TFirst, TSecond, TValue>> computation)
    {
        var builder = model.Raw.Derived(source.Raw).Using(first.Raw, second.Raw);
        if (_impact is not null) builder.Impact(_impact);
        var raw = builder.Compute(computation);
        return model.AddValue(source, raw, [first.Node, second.Node], []);
    }
}

internal sealed class FinalDirectStage<TSource>(
    FinalModel model,
    FinalSet<TSource> source,
    DerivedBuilder<TSource> builder,
    List<TrackedDependency> dependencies)
    where TSource : class
{
    public FinalDirectStage<TSource> Impact(Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        builder.Impact(configure);
        return this;
    }

    public FinalValue<TSource, TValue> Select<TValue>(Func<TSource, TValue> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var sourceParameter = Expression.Parameter(typeof(TSource), "source");
        var opaqueCall = Expression.Invoke(Expression.Constant(computation), sourceParameter);
        var expression = Expression.Lambda<Func<TSource, TValue>>(opaqueCall, sourceParameter);
        var raw = builder.Compute(expression);
        return model.AddValue(source, raw, [], dependencies);
    }
}

internal sealed class FinalFromOne<TSource, TUpstream>(
    FinalModel model,
    FinalSet<TSource> source,
    FinalValue<TSource, TUpstream> upstream)
    where TSource : class
{
    private readonly List<IDependencyRegistration<TSource>> _dependencyExpressions = [];
    private readonly List<TrackedDependency> _dependencies = [];
    private Action<DerivedImpactPolicyBuilder<TSource>>? _impact;

    public FinalFromOne<TSource, TUpstream> DependsOn<TValue>(
        Expression<Func<TSource, TValue>> first,
        params Expression<Func<TSource, TValue>>[] additional)
    {
        _dependencyExpressions.Add(new DependencyRegistration<TSource, TValue>(first));
        _dependencies.Add(model.TrackDependency(source, first));
        foreach (var dependency in additional)
        {
            _dependencyExpressions.Add(new DependencyRegistration<TSource, TValue>(dependency));
            _dependencies.Add(model.TrackDependency(source, dependency));
        }
        return this;
    }

    public FinalFromOne<TSource, TUpstream> Impact(Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        _impact = configure;
        return this;
    }

    public FinalValue<TSource, TValue> Select<TValue>(
        Expression<Func<TSource, TUpstream, TValue>> computation)
    {
        Derived<TSource, TValue> raw;
        if (_dependencyExpressions.Count == 0)
        {
            var builder = model.Raw.Derived(source.Raw).Using(upstream.Raw);
            if (_impact is not null) builder.Impact(_impact);
            raw = builder.Compute(computation);
        }
        else
        {
            var token = model.BuildDependencyToken(source, _dependencyExpressions);
            var builder = model.Raw.Derived(source.Raw).Using(upstream.Raw, token);
            if (_impact is not null) builder.Impact(_impact);
            var compiled = computation.Compile();
            raw = builder.Compute((item, value, _) => compiled(item, value)).AllowIncompleteDependencies();
        }
        return model.AddValue(source, raw, [upstream.Node], _dependencies);
    }
}

internal sealed class FinalProjectedOne<TSource, TTarget, TUpstream>(
    FinalModel model,
    FinalSet<TSource> source,
    Expression<Func<TSource, TTarget>> selector,
    FinalValue<TTarget, TUpstream> projected)
    where TSource : class where TTarget : class
{
    public FinalProjectedAndLocal<TSource, TTarget, TUpstream, TLocal> From<TLocal>(
        FinalValue<TSource, TLocal> local)
    {
        local.RequireOwner(model, source);
        return new FinalProjectedAndLocal<TSource, TTarget, TUpstream, TLocal>(
            model, source, selector, projected, local);
    }
}

internal sealed class FinalProjectedAndLocal<TSource, TTarget, TProjected, TLocal>(
    FinalModel model,
    FinalSet<TSource> source,
    Expression<Func<TSource, TTarget>> selector,
    FinalValue<TTarget, TProjected> projected,
    FinalValue<TSource, TLocal> local)
    where TSource : class where TTarget : class
{
    private Action<DerivedImpactPolicyBuilder<TSource>>? _impact;

    public FinalProjectedAndLocal<TSource, TTarget, TProjected, TLocal> Impact(
        Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        _impact = configure;
        return this;
    }

    public FinalValue<TSource, TValue> Select<TValue>(
        Expression<Func<TSource, TProjected, TLocal, TValue>> computation)
    {
        var bridge = model.BuildProjectedBridge(source, selector, projected);
        var builder = model.Raw.Derived(source.Raw).Using(bridge, local.Raw);
        if (_impact is not null) builder.Impact(_impact);
        var raw = builder.Compute(computation);
        return model.AddValue(
            source,
            raw,
            [projected.Node, local.Node],
            [],
            projection: $"{selector} -> {projected.Node.Name ?? "<unnamed>"}");
    }
}

internal sealed class FinalValue<TSource, TValue> where TSource : class
{
    internal FinalValue(FinalModel model, FinalSet<TSource> source, Derived<TSource, TValue> raw, FinalNode node) =>
        (Model, Source, Raw, Node) = (model, source, raw, node);

    internal FinalModel Model { get; }
    internal FinalSet<TSource> Source { get; }
    internal Derived<TSource, TValue> Raw { get; }
    internal FinalNode Node { get; }

    public FinalValue<TSource, TValue> MaterializeTo(Expression<Func<TSource, TValue>> target)
    {
        Model.RegisterMaterialization(this, target);
        return this;
    }

    public FinalValue<TSource, TValue> Named(string name)
    {
        Raw.Named(name);
        Node.Name = name;
        return this;
    }

    internal void RequireModel(FinalModel model)
    {
        if (!ReferenceEquals(Model, model))
            throw new ArgumentException("The derived handle belongs to a different model.");
    }

    internal void RequireOwner(FinalModel model, FinalSet<TSource> source)
    {
        RequireModel(model);
        if (!ReferenceEquals(Source, source))
            throw new ArgumentException("The derived handle belongs to a different ObjectSet identity.");
    }
}

internal sealed class FinalRelationDraft<TLeft, TRight>(
    FinalModel model,
    FinalSet<TLeft> left,
    FinalSet<TRight> right)
    where TLeft : class where TRight : class
{
    public FinalRelation<TLeft, TRight> Where(Expression<Func<TLeft, TRight, bool>> predicate) =>
        new(model, left, right, model.Raw.Relation(left.Raw, right.Raw).Where(predicate));
}

internal sealed class FinalRelation<TLeft, TRight>(
    FinalModel model,
    FinalSet<TLeft> left,
    FinalSet<TRight> right,
    Relation<TLeft, TRight> raw)
    where TLeft : class where TRight : class
{
    internal FinalModel Model { get; } = model;
    internal FinalSet<TLeft> Left { get; } = left;
    internal FinalSet<TRight> Right { get; } = right;
    internal Relation<TLeft, TRight> Raw { get; } = raw;
    internal string? Name { get; private set; }

    public FinalRelation<TLeft, TRight> Named(string name)
    {
        Raw.Named(name);
        Name = name;
        return this;
    }

    internal void RequireOwner(FinalModel model, FinalSet<TLeft> source)
    {
        if (!ReferenceEquals(Model, model) || !ReferenceEquals(Left, source))
            throw new ArgumentException("The relation belongs to a different model or left ObjectSet identity.");
    }
}

internal sealed class FinalRelationAggregate<TLeft, TRight>(
    FinalModel model,
    FinalSet<TLeft> source,
    FinalRelation<TLeft, TRight> relation)
    where TLeft : class where TRight : class
{
    private Action<DerivedImpactPolicyBuilder<TLeft>>? _impact;

    public FinalRelationAggregate<TLeft, TRight> Impact(Action<DerivedImpactPolicyBuilder<TLeft>> configure)
    {
        _impact = configure;
        return this;
    }

    public FinalValue<TLeft, decimal> Sum(Expression<Func<TRight, decimal>> selector) =>
        Build(ExpressionFactories.Sum<TLeft, TRight>(selector), $"IncrementalSum({typeof(TRight).Name}.{MemberName(selector)})");

    public FinalValue<TLeft, int> Count() =>
        Build(ExpressionFactories.Count<TLeft, TRight>(), "IncrementalCount");

    public FinalValue<TLeft, long> LongCount() =>
        Build(ExpressionFactories.LongCount<TLeft, TRight>(), "IncrementalLongCount");

    public FinalValue<TLeft, bool> Any() =>
        Build(ExpressionFactories.Any<TLeft, TRight>(), "IncrementalAny");

    private FinalValue<TLeft, TValue> Build<TValue>(
        Expression<Func<TLeft, IReadOnlyList<TRight>, TValue>> computation,
        string plan)
    {
        var builder = model.Raw.Derived(source.Raw).Using(relation.Raw);
        if (_impact is not null) builder.Impact(_impact);
        var raw = builder.Incrementally().Compute(computation);
        return model.AddValue(source, raw, [], [], plan);
    }

    private static string MemberName<TValue>(Expression<Func<TRight, TValue>> selector) =>
        selector.Body is MemberExpression member ? member.Member.Name : "<selector>";
}

internal sealed class FinalInvariantStart<TSource>(FinalSet<TSource> source) where TSource : class
{
    public FinalInvariantFrom<TSource> From(FinalValue<TSource, bool> value)
    {
        value.RequireOwner(source.Model, source);
        return new FinalInvariantFrom<TSource>(source.Model.Raw.Invariant(source.Raw).Using(value.Raw));
    }
}

internal sealed class FinalInvariantFrom<TSource>(InvariantUsingBuilder<TSource, bool> builder)
    where TSource : class
{
    public FinalInvariant<TSource> Must(Expression<Func<TSource, bool, bool>> predicate) =>
        new(builder.Must(predicate));
}

internal sealed class FinalInvariant<TSource>(Invariant<TSource> raw) where TSource : class
{
    internal Invariant<TSource> Raw { get; } = raw;

    public FinalInvariant<TSource> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public FinalInvariant<TSource> RepairWhenViolated()
    {
        Raw.RepairWhenViolated();
        return this;
    }
}

internal sealed class FinalNode(
    IFinalSet source,
    Type valueType,
    IReadOnlyList<FinalNode> upstream,
    IReadOnlyList<TrackedDependency> dependencies,
    string? aggregate,
    string? projection)
{
    public IFinalSet Source { get; } = source;
    public Type ValueType { get; } = valueType;
    public IReadOnlyList<FinalNode> Upstream { get; } = upstream;
    public IReadOnlyList<TrackedDependency> Dependencies { get; } = dependencies;
    public string? Aggregate { get; } = aggregate;
    public string? Projection { get; } = projection;
    public string? Name { get; set; }
    public PropertyInfo? Materialization { get; set; }
}

internal sealed record TrackedDependency(
    object SourceIdentity,
    MemberInfo LeafMember,
    string Path,
    LambdaExpression Expression);

internal interface IDependencyRegistration<TSource> where TSource : class
{
    void Apply(DerivedBuilder<TSource> builder);
}

internal sealed class DependencyRegistration<TSource, TValue>(Expression<Func<TSource, TValue>> expression)
    : IDependencyRegistration<TSource>
    where TSource : class
{
    public void Apply(DerivedBuilder<TSource> builder) => builder.DependsOn(expression);
}
