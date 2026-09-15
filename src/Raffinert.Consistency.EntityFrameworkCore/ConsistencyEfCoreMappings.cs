using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

public sealed class ConsistencyEfCoreMappings
{
    private readonly ConsistencyUnitOfWorkMappings _sets = new();
    private readonly List<IInvariantDefinition> _enforced = [];
    private readonly List<Materialization> _materializations = [];

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

    internal ConsistencyUnitOfWorkMappings UnitOfWorkMappings => _sets;
    internal bool HasEnforced => _enforced.Count > 0;
    internal bool HasMaterializations => _materializations.Count > 0;
    internal IReadOnlyList<Materialization> Materializations => _materializations;
    internal HashSet<int> Validate(DbContext context, ConsistencyRuntime runtime)
    {
        var ids = _enforced.Select(runtime.GetInvariantId).ToHashSet();
        foreach (var mapping in _materializations)
        {
            _ = runtime.GetDerivedId(mapping.Definition);
            var usage = runtime.GetMemberUsage(mapping.Definition.SourceSet, mapping.Property);
            if (usage != ConsistencyRuntime.ModelMemberUsageKind.None)
                throw new InvalidOperationException($"Materialized mirrors are sink-only and cannot feed the Relations graph ({usage}).");
            var entity = context.Model.FindEntityType(mapping.Definition.SourceSet.ObjectType)
                ?? throw new InvalidOperationException("The materialized source type is not mapped by EF Core.");
            var property = entity.FindProperty(mapping.Property)
                ?? throw new InvalidOperationException("The materialization property is not mapped by EF Core.");
            if (property.IsPrimaryKey() || entity.GetKeys().Any(key => key.Properties.Contains(property)))
                throw new InvalidOperationException("A key property cannot be a materialized mirror.");
            if (property.ValueGenerated != ValueGenerated.Never)
                throw new InvalidOperationException("A store-generated property cannot be a materialized mirror.");
        }
        return ids;
    }

    internal sealed record Materialization(IDerivedDefinition Definition, PropertyInfo Property);
}
