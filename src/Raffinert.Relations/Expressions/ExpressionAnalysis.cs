using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Relations.Expressions;

internal sealed class MemberPath
{
    private readonly Func<object, object?> _accessor;

    public MemberPath(Type rootType, IReadOnlyList<MemberInfo> members)
    {
        RootType = rootType;
        Members = members;
        _accessor = CompileAccessor();
    }

    public Type RootType { get; }
    public IReadOnlyList<MemberInfo> Members { get; }
    public string DisplayName => $"{RootType.Name}.{string.Join('.', Members.Select(member => member.Name))}";

    public object? Read(object root) => _accessor(root);

    private Func<object, object?> CompileAccessor()
    {
        var root = Expression.Parameter(typeof(object), "root");
        Expression current = Expression.Convert(root, RootType);
        foreach (var member in Members)
            current = Expression.MakeMemberAccess(current, member);
        return Expression.Lambda<Func<object, object?>>(Expression.Convert(current, typeof(object)), root).Compile();
    }
}

internal sealed record JoinKeyPart(MemberPath Left, MemberPath Right);

internal sealed record RelationAnalysis(
    IReadOnlyList<MemberPath> Dependencies,
    IReadOnlyList<JoinKeyPart> JoinKeyParts,
    bool IsDependencyAnalysisComplete,
    bool HasResidualPredicate);

internal static class RelationExpressionAnalyzer
{
    public static RelationAnalysis Analyze(LambdaExpression predicate)
    {
        var dependencies = new List<MemberPath>();
        var complete = true;
        CollectDependencies(predicate.Body, predicate.Parameters, dependencies, ref complete);

        var joins = new List<JoinKeyPart>();
        var hasResidual = false;
        foreach (var term in FlattenConjunction(predicate.Body))
        {
            if (!TryExtractJoin(term, predicate.Parameters[0], predicate.Parameters[1], out var join))
                hasResidual = true;
            else
                joins.Add(join);
        }

        return new RelationAnalysis(
            dependencies.SelectMany(ExpandDependencyPath).Distinct(MemberPathComparer.Instance).ToArray(),
            joins,
            complete,
            hasResidual);
    }

    private static IEnumerable<MemberPath> ExpandDependencyPath(MemberPath path)
    {
        var rootType = path.RootType;
        foreach (var member in path.Members)
        {
            yield return new MemberPath(rootType, [member]);
            rootType = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => rootType
            };
        }
    }

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
        if (expression is not BinaryExpression { NodeType: ExpressionType.Equal } equal)
            return false;

        if (!HasSafeIndexEquality(equal))
            return false;

        if (TryGetPath(equal.Left, leftParameter, out var left) &&
            TryGetPath(equal.Right, rightParameter, out var right))
        {
            join = new JoinKeyPart(left, right);
            return true;
        }

        if (TryGetPath(equal.Right, leftParameter, out left) &&
            TryGetPath(equal.Left, rightParameter, out right))
        {
            join = new JoinKeyPart(left, right);
            return true;
        }

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
        List<MemberPath> paths,
        ref bool complete)
    {
        if (TryGetPath(expression, parameters[0], out var leftPath) ||
            TryGetPath(expression, parameters[1], out leftPath))
        {
            paths.Add(leftPath);
            return;
        }

        switch (expression)
        {
            case BinaryExpression binary:
                CollectDependencies(binary.Left, parameters, paths, ref complete);
                CollectDependencies(binary.Right, parameters, paths, ref complete);
                break;
            case UnaryExpression unary:
                CollectDependencies(unary.Operand, parameters, paths, ref complete);
                break;
            case MethodCallExpression call:
                complete = false;
                if (call.Object is not null) CollectDependencies(call.Object, parameters, paths, ref complete);
                foreach (var argument in call.Arguments) CollectDependencies(argument, parameters, paths, ref complete);
                break;
            case ConditionalExpression conditional:
                CollectDependencies(conditional.Test, parameters, paths, ref complete);
                CollectDependencies(conditional.IfTrue, parameters, paths, ref complete);
                CollectDependencies(conditional.IfFalse, parameters, paths, ref complete);
                break;
            case MemberExpression member:
                if (member.Expression is not null)
                    CollectDependencies(member.Expression, parameters, paths, ref complete);
                break;
            case ConstantExpression or ParameterExpression:
                break;
            default:
                complete = false;
                break;
        }
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
            foreach (var member in obj.Members) hash.Add(member);
            return hash.ToHashCode();
        }
    }
}
