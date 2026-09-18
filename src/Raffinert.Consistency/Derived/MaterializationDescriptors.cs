using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Consistency;

internal sealed class MaterializationDescriptor
{
    private MaterializationDescriptor(
        IDerivedDefinition definition,
        Type valueType,
        PropertyInfo target,
        Func<object, object?> read,
        Action<object, object?> write)
    {
        Definition = definition;
        ValueType = valueType;
        Target = target;
        Read = read;
        Write = write;
    }

    public IDerivedDefinition Definition { get; }
    public IObjectSetDefinition SourceSet => Definition.SourceSet;
    public Type SourceType => SourceSet.ObjectType;
    public Type ValueType { get; }
    public PropertyInfo Target { get; }
    public Func<object, object?> Read { get; }
    public Action<object, object?> Write { get; }
    public bool ValuesEqual(object? left, object? right) => Equals(left, right);

    public static MaterializationDescriptor Create<TSource, TValue>(
        IDerivedDefinition definition,
        Expression<Func<TSource, TValue>> target)
        where TSource : class => Create(definition, target, exactTargetType: true);

    public static MaterializationDescriptor CreateLegacy<TSource, TValue>(
        IDerivedDefinition definition,
        Expression<Func<TSource, TValue>> target)
        where TSource : class => Create(definition, target, exactTargetType: false);

    private static MaterializationDescriptor Create<TSource, TValue>(
        IDerivedDefinition definition,
        Expression<Func<TSource, TValue>> target,
        bool exactTargetType)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(target);
        Expression body = target.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;
        if (body is not MemberExpression
            {
                Expression: ParameterExpression parameter,
                Member: PropertyInfo property
            } ||
            !ReferenceEquals(parameter, target.Parameters[0]) ||
            property.GetMethod?.IsStatic == true ||
            property.SetMethod is not { } setter ||
            (exactTargetType && (!setter.IsPublic || setter.ReturnParameter.GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))) ||
            property.GetIndexParameters().Length != 0)
            throw new ArgumentException(
                "MaterializeTo must select one direct writable property on the derived source object.",
                nameof(target));
        if (exactTargetType
                ? property.PropertyType != typeof(TValue)
                : !property.PropertyType.IsAssignableFrom(typeof(TValue)))
            throw new ArgumentException(
                exactTargetType
                    ? "The materialization target property type must exactly match the derived value type."
                    : "The materialization property is incompatible with the derived value type.",
                nameof(target));
        if (!ReferenceEquals(definition.SourceSet.ObjectType, typeof(TSource)))
            throw new ArgumentException(
                "The materialization target source type does not match the derived source object set.",
                nameof(target));

        var source = Expression.Parameter(typeof(TSource), "source");
        var read = Expression.Lambda<Func<TSource, object?>>(
            Expression.Convert(Expression.Property(source, property), typeof(object)), source).Compile();
        var instanceParameter = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var write = Expression.Lambda<Action<object, object?>>(
            Expression.Assign(
                Expression.Property(Expression.Convert(instanceParameter, typeof(TSource)), property),
                Expression.Convert(value, property.PropertyType)),
            instanceParameter,
            value).Compile();
        return new MaterializationDescriptor(
            definition,
            typeof(TValue),
            property,
            sourceObject => read((TSource)sourceObject),
            write);
    }
}
