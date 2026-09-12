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
        var declaringType = rootType;
        Segments = members.Select(member =>
        {
            var valueType = GetMemberType(member);
            var segment = new DependencyPathSegment(declaringType, member, valueType);
            declaringType = valueType;
            return segment;
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
        var dependencies = new List<DependencyPath>();
        var dependencyAnalysis = DependencyAnalysisFlags.Complete;
        CollectDependencies(predicate.Body, predicate.Parameters, dependencies, ref dependencyAnalysis);

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
            dependencies.Distinct(DependencyPathComparer.Instance).ToArray(),
            joins,
            dependencyAnalysis,
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

        if (TryGetPath(equal.Left, leftParameter, out var left) &&
            TryGetPath(equal.Right, rightParameter, out var right))
        {
            join = new JoinKeyPart(left, right, DefaultObjectEqualityComparer.Instance, "Default equality");
            return true;
        }

        if (TryGetPath(equal.Right, leftParameter, out left) &&
            TryGetPath(equal.Left, rightParameter, out right))
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
        if (TryGetPath(first, leftParameter, out left) && TryGetPath(second, rightParameter, out right))
            return true;
        if (TryGetPath(second, leftParameter, out left) && TryGetPath(first, rightParameter, out right))
            return true;
        left = null!;
        right = null!;
        return false;
    }

    private static bool HasSafeIndexEquality(BinaryExpression equality)
    {
        var leftType = StripConvert(equality.Left).Type;
        var rightType = StripConvert(equality.Right).Type;
        if (leftType != rightType)
            return false;

        var type = Nullable.GetUnderlyingType(leftType) ?? leftType;
        if (type == typeof(string))
            return equality.Method is null || equality.Method == typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)]);

        return equality.Method is null && (type.IsValueType || type.IsEnum);
    }

    private static void CollectDependencies(
        Expression expression,
        IReadOnlyList<ParameterExpression> parameters,
        List<DependencyPath> paths,
        ref DependencyAnalysisFlags dependencyAnalysis)
    {
        if (TryGetDependencyPath(expression, parameters, out var dependencyPath))
        {
            paths.Add(dependencyPath);
            return;
        }

        switch (expression)
        {
            case BinaryExpression binary:
                CollectDependencies(binary.Left, parameters, paths, ref dependencyAnalysis);
                CollectDependencies(binary.Right, parameters, paths, ref dependencyAnalysis);
                break;
            case UnaryExpression unary:
                CollectDependencies(unary.Operand, parameters, paths, ref dependencyAnalysis);
                break;
            case MethodCallExpression call:
                if (!IsRecognizedStringEquality(call))
                    dependencyAnalysis |= DependencyAnalysisFlags.ContainsOpaqueCode;
                if (call.Object is not null) CollectDependencies(call.Object, parameters, paths, ref dependencyAnalysis);
                foreach (var argument in call.Arguments) CollectDependencies(argument, parameters, paths, ref dependencyAnalysis);
                break;
            case ConditionalExpression conditional:
                CollectDependencies(conditional.Test, parameters, paths, ref dependencyAnalysis);
                CollectDependencies(conditional.IfTrue, parameters, paths, ref dependencyAnalysis);
                CollectDependencies(conditional.IfFalse, parameters, paths, ref dependencyAnalysis);
                break;
            case MemberExpression member:
                if (member.Expression is null || IsExternalMemberAccess(member, parameters))
                    dependencyAnalysis |= DependencyAnalysisFlags.ContainsExternalState;
                if (member.Expression is not null)
                    CollectDependencies(member.Expression, parameters, paths, ref dependencyAnalysis);
                break;
            case ConstantExpression or ParameterExpression:
                break;
            default:
                dependencyAnalysis |= DependencyAnalysisFlags.ContainsOpaqueCode;
                break;
        }
    }

    private static bool IsRecognizedStringEquality(MethodCallExpression call) =>
        call.Method.DeclaringType == typeof(string) &&
        call.Method.Name == nameof(string.Equals) &&
        call.Object is null &&
        call.Arguments.Count == 3 &&
        call.Arguments[2] is ConstantExpression { Value: StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase };

    private static bool TryGetDependencyPath(
        Expression expression,
        IReadOnlyList<ParameterExpression> parameters,
        out DependencyPath path)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            if (!TryGetPath(expression, parameters[index], out var memberPath))
                continue;
            path = new DependencyPath(index, memberPath.RootType, memberPath.Members);
            return true;
        }

        path = null!;
        return false;
    }

    private static bool IsExternalMemberAccess(MemberExpression member, IReadOnlyList<ParameterExpression> parameters)
    {
        Expression? root = member.Expression;
        while (root is MemberExpression nested)
            root = nested.Expression;
        while (root is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            root = unary.Operand;
        return root is ConstantExpression || (root is ParameterExpression parameter && !parameters.Contains(parameter));
    }

    internal static bool TryGetPath(Expression expression, ParameterExpression root, out MemberPath path)
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

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            expression = convert.Operand;
        return expression;
    }

    private sealed class DependencyPathComparer : IEqualityComparer<DependencyPath>
    {
        public static DependencyPathComparer Instance { get; } = new();

        public bool Equals(DependencyPath? x, DependencyPath? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             x.RootParameterIndex == y.RootParameterIndex &&
             x.RootType == y.RootType &&
             x.Segments.Select(segment => segment.Member).SequenceEqual(y.Segments.Select(segment => segment.Member)));

        public int GetHashCode(DependencyPath obj)
        {
            var hash = new HashCode();
            hash.Add(obj.RootParameterIndex);
            hash.Add(obj.RootType);
            foreach (var segment in obj.Segments)
                hash.Add(segment.Member);
            return hash.ToHashCode();
        }
    }
}
