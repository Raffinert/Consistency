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
    private readonly List<MaterializationDescriptor> _materializations = [];
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
    internal IReadOnlyList<MaterializationDescriptor> Materializations => _materializations;
    internal IReadOnlyList<ConsumerResolver> ConsumerResolvers => _consumerResolvers;
    internal IReadOnlyList<(object Definition, IObjectSetDefinition Set, ExternalConsumerAnalysis Analysis)> ActivePolicyDefinitions(
        ConsistencyRuntime runtime,
        ConsistencySaveBehavior saveBehavior)
    {
        var values = new List<(object Definition, IObjectSetDefinition Set, ExternalConsumerAnalysis Analysis)>();
        foreach (var definition in _enforced)
            values.Add((definition, definition.SourceSet, runtime.GetExternalConsumerAnalysis(definition)));
        if (saveBehavior == ConsistencySaveBehavior.RecalculateAndValidate)
            foreach (var mapping in runtime.Materializations)
                values.Add((mapping.Definition, mapping.Definition.SourceSet,
                    runtime.GetExternalConsumerAnalysis(mapping.Definition)));
        return values;
    }

    internal IEnumerable<ExternalConsumerDescriptor> ActiveConsumerDescriptors(
        ConsistencyRuntime runtime,
        ConsistencySaveBehavior saveBehavior) =>
        ActivePolicyDefinitions(runtime, saveBehavior)
            .SelectMany(value => value.Analysis.Descriptors)
            .DistinctBy(value => (value.RootSet, value.Navigation, value.TargetMember));

    internal IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
        ConsistencyRuntime runtime,
        ConsistencyScope? scope,
        ConsistencySaveBehavior saveBehavior)
    {
        runtime.ValidateScopeOwnership(scope);
        var activeDefinitions = ActivePolicyDefinitions(runtime, saveBehavior);
        var gaps = activeDefinitions.SelectMany(value => value.Definition switch
        {
            IInvariantDefinition invariant => runtime.GetScopeGaps(invariant, scope),
            IDerivedDefinition derived => runtime.GetScopeGaps(derived, scope),
            _ => []
        });
        var obligations = new Dictionary<IObjectSetDefinition, (List<ExternalConsumerDescriptor> Descriptors, bool Unsupported)>(
            ReferenceEqualityComparer<IObjectSetDefinition>.Instance);
        foreach (var value in activeDefinitions)
        {
            foreach (var descriptor in value.Analysis.Descriptors)
            {
                if (!obligations.TryGetValue(descriptor.RootSet, out var state))
                    state = ([], false);
                state.Descriptors.Add(descriptor);
                obligations[descriptor.RootSet] = state;
            }
            foreach (var unsupportedSet in value.Analysis.UnsupportedRootSets)
            {
                if (!obligations.TryGetValue(unsupportedSet, out var state))
                    state = ([], false);
                state.Unsupported = true;
                obligations[unsupportedSet] = state;
            }
        }
        var capabilityBySet = obligations.ToDictionary(pair => pair.Key.Id, pair =>
            !pair.Value.Unsupported && pair.Value.Descriptors.Count > 0 &&
            pair.Value.Descriptors.All(descriptor =>
                _consumerResolvers.Any(resolver => resolver.Matches(descriptor))));
        return gaps.Distinct()
            .Where(gap => gap.RequirementKind != ConsistencyScopeRequirementKind.NavigationConsumerCoverage ||
                !capabilityBySet.TryGetValue(gap.ObjectSetId, out var capable) || !capable)
            .OrderBy(gap => gap.ObjectSetId)
            .ThenBy(gap => gap.RequirementKind)
            .ToArray();
    }

    internal HashSet<int> Validate(DbContext context, ConsistencyRuntime runtime)
    {
        _materializations.Clear();
        _materializations.AddRange(runtime.Materializations);
        var ids = _enforced.Select(runtime.GetInvariantId).ToHashSet();
        foreach (var mapping in _materializations)
        {
            _ = runtime.GetDerivedId(mapping.Definition);
            var usage = runtime.GetMemberUsage(mapping.Definition.SourceSet, mapping.Target);
            if (usage != ConsistencyRuntime.ModelMemberUsageKind.None)
                throw new InvalidOperationException($"Materialized mirrors are sink-only and cannot feed the consistency graph ({usage}).");
            var entity = context.Model.FindEntityType(mapping.Definition.SourceSet.ObjectType)
                ?? throw new InvalidOperationException("The materialized source type is not mapped by EF Core.");
            var property = entity.FindProperty(mapping.Target)
                ?? throw new InvalidOperationException("The materialization property is not mapped by EF Core.");
            if (property.IsPrimaryKey() || entity.GetKeys().Any(key => key.Properties.Contains(property)))
                throw new InvalidOperationException("A key property cannot be a materialized mirror.");
            if (property.ValueGenerated != ValueGenerated.Never)
                throw new InvalidOperationException("A store-generated property cannot be a materialized mirror.");
        }
        foreach (var resolver in _consumerResolvers)
        {
            if (!runtime.OwnsObjectSet(resolver.RootSet))
                throw new ArgumentException("A consumer resolver object set belongs to another compiled model.");
            var entity = context.Model.FindEntityType(resolver.RootSet.ObjectType)
                ?? throw new InvalidOperationException("The consumer resolver root type is not mapped by EF Core.");
            var navigations = entity.GetNavigations().Where(navigation =>
                navigation.PropertyInfo == resolver.Navigation || navigation.FieldInfo == resolver.Navigation).ToArray();
            if (navigations.Length != 1)
                throw new InvalidOperationException(
                    "A consumer resolver must target exactly one mapped EF reference navigation.");
            var navigation = navigations[0];
            if (navigation.IsCollection)
                throw new InvalidOperationException("A consumer resolver cannot target a collection navigation.");
            if (navigation.TargetEntityType.ClrType != resolver.TargetType)
                throw new InvalidOperationException(
                    "A consumer resolver target type must exactly match the mapped navigation target type.");
            resolver.EfNavigation = navigation;
        }
        return ids;
    }

    internal sealed record ConsumerResolver(
        IObjectSetDefinition RootSet,
        MemberInfo Navigation,
        Type TargetType,
        Func<DbContext, IReadOnlyCollection<object>, IQueryable<object>> Query)
    {
        public INavigation? EfNavigation { get; set; }

        public bool Matches(ExternalConsumerDescriptor descriptor) =>
            ReferenceEquals(RootSet, descriptor.RootSet) && Navigation == descriptor.Navigation &&
            TargetType == descriptor.TargetType;
    }
}
