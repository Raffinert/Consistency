using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency.EntityFrameworkCore;

public sealed class ConsistencyEfCoreMappings
{
    private readonly ConsistencyUnitOfWorkMappings _sets = new();
    private readonly List<IInvariantDefinition> _enforced = [];
    private readonly List<Materialization> _materializations = [];
    private readonly List<ConsumerResolver> _consumerResolvers = [];

    public ConsistencyEfCoreMappings Map<TEntity>(ObjectSet<TEntity> set,
        Func<EntityEntry<TEntity>, bool>? selector = null) where TEntity : class
    {
        _sets.Map(set, selector); return this;
    }

    public ConsistencyEfCoreMappings Enforce<TSource>(Invariant<TSource> invariant) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        if (!_enforced.Contains(invariant.Definition)) _enforced.Add(invariant.Definition);
        return this;
    }

    public ConsistencyEfCoreMappings Materialize<TSource, TValue>(Derived<TSource, TValue> derived,
        Expression<Func<TSource, TValue>> property) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived); ArgumentNullException.ThrowIfNull(property);
        var body = property.Body is UnaryExpression unary ? unary.Operand : property.Body;
        if (body is not MemberExpression { Expression: ParameterExpression, Member: PropertyInfo info } ||
            info.GetMethod?.IsStatic == true || info.SetMethod is null || info.GetIndexParameters().Length != 0)
            throw new ArgumentException("Materialization target must be a direct writable instance property.", nameof(property));
        if (info.DeclaringType is null || !info.DeclaringType.IsAssignableFrom(typeof(TSource)) ||
            !info.PropertyType.IsAssignableFrom(typeof(TValue)))
            throw new ArgumentException("Materialization property is incompatible with the derived source/value type.", nameof(property));
        if (_materializations.Any(x => x.Property == info || ReferenceEquals(x.Definition, derived.Definition)))
            throw new InvalidOperationException("A materialized property or derived definition cannot be mapped twice.");
        _materializations.Add(new Materialization(derived.Definition, info)); return this;
    }

    /// <summary>Registers an authoritative reverse-consumer query for a direct navigation.</summary>
    public ConsistencyEfCoreMappings DiscoverConsumers<TRoot, TTarget>(
        ObjectSet<TRoot> roots,
        Expression<Func<TRoot, TTarget?>> navigation,
        Func<DbContext, IReadOnlyCollection<TTarget>, IQueryable<TRoot>> query)
        where TRoot : class where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(query);
        var body = navigation.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;
        if (body is not MemberExpression member ||
            member.Expression != navigation.Parameters[0] ||
            member.Member is not PropertyInfo && member.Member is not FieldInfo ||
            DependencyPathNavigation.IsCollection(member.Member) ||
            member.Member.DeclaringType is null ||
            !member.Member.DeclaringType.IsAssignableFrom(typeof(TRoot)))
            throw new ArgumentException(
                "Consumer navigation must be a direct non-collection reference rooted at the object-set parameter.",
                nameof(navigation));
        if (_consumerResolvers.Any(resolver =>
                ReferenceEquals(resolver.RootSet, roots.Definition) && resolver.Navigation == member.Member))
            throw new InvalidOperationException("A consumer resolver is already registered for this object-set navigation.");
        _consumerResolvers.Add(new ConsumerResolver(
            roots.Definition,
            member.Member,
            typeof(TTarget),
            (context, targets) => query(context, targets.Cast<TTarget>().ToArray()).Cast<object>()));
        return this;
    }

    internal ConsistencyUnitOfWorkMappings UnitOfWorkMappings => _sets;
    internal bool HasEnforced => _enforced.Count > 0;
    internal bool HasMaterializations => _materializations.Count > 0;
    internal IReadOnlyList<Materialization> Materializations => _materializations;
    internal IReadOnlyList<ConsumerResolver> ConsumerResolvers => _consumerResolvers;
    internal IEnumerable<ExternalConsumerDescriptor> ActiveConsumerDescriptors(ConsistencyRuntime runtime)
    {
        foreach (var definition in _enforced)
            foreach (var descriptor in runtime.GetExternalConsumerAnalysis(definition).Descriptors)
                yield return descriptor;
        foreach (var mapping in _materializations)
            foreach (var descriptor in runtime.GetExternalConsumerAnalysis(mapping.Definition).Descriptors)
                yield return descriptor;
    }
    internal IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
        ConsistencyRuntime runtime,
        ConsistencyScope? scope,
        ConsistencySaveBehavior saveBehavior)
    {
        runtime.ValidateScopeOwnership(scope);
        var activeDefinitions = new List<(IObjectSetDefinition Set, ExternalConsumerAnalysis Analysis)>();
        foreach (var definition in _enforced)
        {
            var analysis = runtime.GetExternalConsumerAnalysis(definition);
            activeDefinitions.Add((definition.SourceSet, analysis));
        }
        if (saveBehavior == ConsistencySaveBehavior.RecalculateAndValidate)
            foreach (var mapping in _materializations)
            {
                var analysis = runtime.GetExternalConsumerAnalysis(mapping.Definition);
                activeDefinitions.Add((mapping.Definition.SourceSet, analysis));
            }
        var gaps = _enforced.SelectMany(definition => runtime.GetScopeGaps(definition, scope));
        if (saveBehavior == ConsistencySaveBehavior.RecalculateAndValidate)
            gaps = gaps.Concat(_materializations.SelectMany(mapping =>
                runtime.GetScopeGaps(mapping.Definition, scope)));
        var capabilityBySet = activeDefinitions
            .GroupBy(value => value.Set, ReferenceEqualityComparer<IObjectSetDefinition>.Instance)
            .ToDictionary(group => group.Key.Id, group =>
            {
                var analyses = group.Select(value => value.Analysis).ToArray();
                var descriptors = analyses.SelectMany(value => value.Descriptors).ToArray();
                return !analyses.Any(value => value.HasUnsupportedNavigationDependency) &&
                    descriptors.Length > 0 && descriptors.All(descriptor =>
                        _consumerResolvers.Any(resolver => resolver.Matches(descriptor)));
            });
        return gaps.Distinct()
            .Where(gap => gap.RequirementKind != ConsistencyScopeRequirementKind.NavigationConsumerCoverage ||
                !capabilityBySet.TryGetValue(gap.ObjectSetId, out var capable) || !capable)
            .OrderBy(gap => gap.ObjectSetId)
            .ThenBy(gap => gap.RequirementKind)
            .ToArray();
    }

    internal HashSet<int> Validate(DbContext context, ConsistencyRuntime runtime)
    {
        var ids = _enforced.Select(runtime.GetInvariantId).ToHashSet();
        foreach (var mapping in _materializations)
        {
            _ = runtime.GetDerivedId(mapping.Definition);
            var usage = runtime.GetMemberUsage(mapping.Definition.SourceSet, mapping.Property);
            if (usage != ConsistencyRuntime.ModelMemberUsageKind.None)
                throw new InvalidOperationException($"Materialized mirrors are sink-only and cannot feed the consistency graph ({usage}).");
            var entity = context.Model.FindEntityType(mapping.Definition.SourceSet.ObjectType)
                ?? throw new InvalidOperationException("The materialized source type is not mapped by EF Core.");
            var property = entity.FindProperty(mapping.Property)
                ?? throw new InvalidOperationException("The materialization property is not mapped by EF Core.");
            if (property.IsPrimaryKey() || entity.GetKeys().Any(key => key.Properties.Contains(property)))
                throw new InvalidOperationException("A key property cannot be a materialized mirror.");
            if (property.ValueGenerated != ValueGenerated.Never)
                throw new InvalidOperationException("A store-generated property cannot be a materialized mirror.");
        }
        foreach (var resolver in _consumerResolvers)
            if (!runtime.OwnsObjectSet(resolver.RootSet))
                throw new ArgumentException("A consumer resolver object set belongs to another compiled model.");
        return ids;
    }

    internal sealed record Materialization(IDerivedDefinition Definition, PropertyInfo Property);

    internal sealed record ConsumerResolver(
        IObjectSetDefinition RootSet,
        MemberInfo Navigation,
        Type TargetType,
        Func<DbContext, IReadOnlyCollection<object>, IQueryable<object>> Query)
    {
        public bool Matches(ExternalConsumerDescriptor descriptor) =>
            ReferenceEquals(RootSet, descriptor.RootSet) && Navigation == descriptor.Navigation &&
            TargetType == descriptor.TargetType;
    }
}
