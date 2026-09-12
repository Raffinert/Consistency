using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Relations.Expressions;

internal sealed class MemberPath
{
    public MemberPath(Type rootType, IReadOnlyList<MemberInfo> members)
    {
        RootType = rootType;
        Members = members;
    }

    public Type RootType { get; }
    public IReadOnlyList<MemberInfo> Members { get; }
    public string DisplayName => $"{RootType.Name}.{string.Join('.', Members.Select(member => member.Name))}";

    public object? Read(object root)
    {
        object? current = root;
        foreach (var member in Members)
        {
            if (current is null)
                return null;
            current = member switch
            {
                PropertyInfo property => property.GetValue(current),
                FieldInfo field => field.GetValue(current),
                _ => throw new NotSupportedException($"Member '{member.Name}' is not a property or field.")
            };
        }
        return current;
    }

    internal static bool TryCreate(Expression expression, ParameterExpression root, out MemberPath path)
    {
        expression = StripConvert(expression);
        var members = new Stack<MemberInfo>();
        while (expression is MemberExpression member)
        {
            members.Push(member.Member);
            expression = StripConvert(member.Expression!);
        }

        if (expression == root && members.Count > 0)
        {
            path = new MemberPath(root.Type, members.ToArray());
            return true;
        }

        path = null!;
        return false;
    }

    internal static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            expression = convert.Operand;
        return expression;
    }
}

internal sealed record JoinKeyPart(
    MemberPath Left,
    MemberPath Right,
    IEqualityComparer<object?> Comparer,
    string EqualitySemantics);

internal sealed class DefaultObjectEqualityComparer : IEqualityComparer<object?>
{
    public static DefaultObjectEqualityComparer Instance { get; } = new();
    public new bool Equals(object? x, object? y) => object.Equals(x, y);
    public int GetHashCode(object? obj) => obj?.GetHashCode() ?? 0;
}

internal sealed class StringObjectEqualityComparer(StringComparer comparer) : IEqualityComparer<object?>
{
    public new bool Equals(object? x, object? y) => comparer.Equals((string?)x, (string?)y);
    public int GetHashCode(object? obj) => obj is string value ? comparer.GetHashCode(value) : 0;
}

internal sealed record DependencyPathSegment(
    Type DeclaringType,
    MemberInfo Member,
    Type ValueType);

internal sealed class DependencyPath
{
    public DependencyPath(int rootParameterIndex, Type rootType, IReadOnlyList<MemberInfo> members)
    {
        RootParameterIndex = rootParameterIndex;
        RootType = rootType;
        Segments = members.Select((member, index) =>
        {
            var memberType = GetMemberType(member);
            var valueType = index < members.Count - 1 && TryGetEnumerableElementType(memberType, out var elementType)
                ? elementType
                : memberType;
            return new DependencyPathSegment(member.DeclaringType ?? rootType, member, valueType);
        }).ToArray();
    }

    public int RootParameterIndex { get; }
    public Type RootType { get; }
    public IReadOnlyList<DependencyPathSegment> Segments { get; }
    public IReadOnlyList<MemberInfo> Members => Segments.Select(segment => segment.Member).ToArray();
    public string DisplayName => $"{RootType.Name}.{string.Join('.', Segments.Select(segment => segment.Member.Name))}";

    private static Type GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new NotSupportedException($"Member '{member.Name}' is not a property or field.")
    };

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }
        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        elementType = enumerable?.GetGenericArguments()[0]!;
        return enumerable is not null;
    }
}

[Flags]
internal enum DependencyAnalysisFlags
{
    Complete = 0,
    ContainsExternalState = 1,
    ContainsOpaqueCode = 2
}

internal sealed class RelationAnalysis
{
    public RelationAnalysis(
        IReadOnlyList<DependencyPath> dependencyPaths,
        IReadOnlyList<JoinKeyPart> joinKeyParts,
        DependencyAnalysisFlags dependencyAnalysis,
        bool hasResidualPredicate,
        IReadOnlyList<string>? recognizedResiduals = null)
    {
        DependencyPaths = dependencyPaths;
        JoinKeyParts = joinKeyParts;
        DependencyAnalysis = dependencyAnalysis;
        HasResidualPredicate = hasResidualPredicate;
        RecognizedResiduals = recognizedResiduals ?? [];
        Dependencies = dependencyPaths
            .SelectMany(ExpandDependencyPath)
            .Distinct(MemberPathComparer.Instance)
            .ToArray();
    }

    public IReadOnlyList<DependencyPath> DependencyPaths { get; }

    // Transitional segment-level view used by the current change router. Complete paths remain
    // the source of truth and this view can be removed when impact routing consumes them directly.
    public IReadOnlyList<MemberPath> Dependencies { get; }
    public IReadOnlyList<JoinKeyPart> JoinKeyParts { get; }
    public DependencyAnalysisFlags DependencyAnalysis { get; }
    public bool HasResidualPredicate { get; }
    public IReadOnlyList<string> RecognizedResiduals { get; }

    private static IEnumerable<MemberPath> ExpandDependencyPath(DependencyPath path)
    {
        foreach (var segment in path.Segments)
            yield return new MemberPath(segment.DeclaringType, [segment.Member]);
    }

    private sealed class MemberPathComparer : IEqualityComparer<MemberPath>
    {
        public static MemberPathComparer Instance { get; } = new();

        public bool Equals(MemberPath? x, MemberPath? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null && x.RootType == y.RootType && x.Members.SequenceEqual(y.Members));

        public int GetHashCode(MemberPath obj)
        {
            var hash = new HashCode();
            hash.Add(obj.RootType);
            foreach (var member in obj.Members)
                hash.Add(member);
            return hash.ToHashCode();
        }
    }
}

internal static class RelationExpressionAnalyzer
{
    public static RelationAnalysis Analyze(LambdaExpression predicate)
    {
        var dependencyResult = ExpressionDependencyAnalyzer.AnalyzeRelation(predicate);

        var joins = new List<JoinKeyPart>();
        var recognizedResiduals = new List<string>();
        var hasResidual = false;
        foreach (var term in FlattenConjunction(predicate.Body))
        {
            if (!TryExtractJoin(term, predicate.Parameters[0], predicate.Parameters[1], out var join))
            {
                hasResidual = true;
                if (TryDescribeResidual(term, predicate.Parameters, out var description))
                    recognizedResiduals.Add(description);
            }
            else
                joins.Add(join);
        }

        return new RelationAnalysis(
            dependencyResult.Dependencies.Select(dependency => dependency.Path).ToArray(),
            joins,
            dependencyResult.Flags,
            hasResidual,
            recognizedResiduals);
    }

    private static bool TryDescribeResidual(
        Expression expression,
        IReadOnlyList<ParameterExpression> parameters,
        out string description)
    {
        if (TryGetDependencyPath(expression, parameters, out var booleanPath) && expression.Type == typeof(bool))
        {
            description = $"Boolean filter: {booleanPath.DisplayName}";
            return true;
        }

        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual &&
                TryNormalizeJoinPaths(binary.Left, binary.Right, parameters[0], parameters[1], out var left, out var right))
            {
                description = $"Range: {left.DisplayName} {GetOperator(binary.NodeType)} {right.DisplayName}";
                return true;
            }

            if (binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual &&
                TryDescribeConstantFilter(binary, parameters, out description))
                return true;
        }

        description = "";
        return false;
    }

    private static bool TryDescribeConstantFilter(
        BinaryExpression binary,
        IReadOnlyList<ParameterExpression> parameters,
        out string description)
    {
        DependencyPath path;
        ConstantExpression constant;
        if (TryGetDependencyPath(binary.Left, parameters, out path) && binary.Right is ConstantExpression rightConstant)
        {
            constant = rightConstant;
        }
        else if (TryGetDependencyPath(binary.Right, parameters, out path) && binary.Left is ConstantExpression leftConstant)
        {
            constant = leftConstant;
        }
        else
        {
            description = "";
            return false;
        }

        description = $"Constant filter: {path.DisplayName} {GetOperator(binary.NodeType)} {constant.Value ?? "null"}";
        return true;
    }

    private static string GetOperator(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.Equal => "==",
        ExpressionType.NotEqual => "!=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        _ => nodeType.ToString()
    };

    private static IEnumerable<Expression> FlattenConjunction(Expression expression)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } and)
        {
            foreach (var item in FlattenConjunction(and.Left)) yield return item;
            foreach (var item in FlattenConjunction(and.Right)) yield return item;
            yield break;
        }
        yield return expression;
    }

    private static bool TryExtractJoin(
        Expression expression,
        ParameterExpression leftParameter,
        ParameterExpression rightParameter,
        out JoinKeyPart join)
    {
        join = null!;
        if (expression is MethodCallExpression call &&
            TryExtractStringEquality(call, leftParameter, rightParameter, out join))
            return true;
        if (expression is not BinaryExpression { NodeType: ExpressionType.Equal } equal)
            return false;

        if (!HasSafeIndexEquality(equal))
            return false;

        if (MemberPath.TryCreate(equal.Left, leftParameter, out var left) &&
            MemberPath.TryCreate(equal.Right, rightParameter, out var right))
        {
            join = new JoinKeyPart(left, right, DefaultObjectEqualityComparer.Instance, "Default equality");
            return true;
        }

        if (MemberPath.TryCreate(equal.Right, leftParameter, out left) &&
            MemberPath.TryCreate(equal.Left, rightParameter, out right))
        {
            join = new JoinKeyPart(left, right, DefaultObjectEqualityComparer.Instance, "Default equality");
            return true;
        }

        return false;
    }

    private static bool TryExtractStringEquality(
        MethodCallExpression call,
        ParameterExpression leftParameter,
        ParameterExpression rightParameter,
        out JoinKeyPart join)
    {
        join = null!;
        if (call.Method.DeclaringType != typeof(string) || call.Method.Name != nameof(string.Equals) ||
            call.Object is not null || call.Arguments.Count != 3 ||
            call.Arguments[2] is not ConstantExpression { Value: StringComparison comparison } ||
            comparison is not StringComparison.Ordinal and not StringComparison.OrdinalIgnoreCase)
            return false;

        if (!TryNormalizeJoinPaths(call.Arguments[0], call.Arguments[1], leftParameter, rightParameter, out var left, out var right))
            return false;

        var comparer = comparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        join = new JoinKeyPart(left, right, new StringObjectEqualityComparer(comparer), comparison.ToString());
        return true;
    }

    private static bool TryNormalizeJoinPaths(
        Expression first,
        Expression second,
        ParameterExpression leftParameter,
        ParameterExpression rightParameter,
        out MemberPath left,
        out MemberPath right)
    {
        if (MemberPath.TryCreate(first, leftParameter, out left) &&
            MemberPath.TryCreate(second, rightParameter, out right))
            return true;
        if (MemberPath.TryCreate(second, leftParameter, out left) &&
            MemberPath.TryCreate(first, rightParameter, out right))
            return true;
        left = null!;
        right = null!;
        return false;
    }

    private static bool HasSafeIndexEquality(BinaryExpression equality)
    {
        var leftType = MemberPath.StripConvert(equality.Left).Type;
        var rightType = MemberPath.StripConvert(equality.Right).Type;
        if (leftType != rightType)
            return false;

        var type = Nullable.GetUnderlyingType(leftType) ?? leftType;
        if (type == typeof(string))
            return equality.Method is null || equality.Method == typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)]);

        return equality.Method is null && (type.IsValueType || type.IsEnum);
    }

    private static bool TryGetDependencyPath(
        Expression expression,
        IReadOnlyList<ParameterExpression> parameters,
        out DependencyPath path)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            if (!MemberPath.TryCreate(expression, parameters[index], out var memberPath))
                continue;
            path = new DependencyPath(index, memberPath.RootType, memberPath.Members);
            return true;
        }

        path = null!;
        return false;
    }

}
