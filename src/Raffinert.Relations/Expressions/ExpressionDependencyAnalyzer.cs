using System.Linq.Expressions;

namespace Raffinert.Relations.Expressions;

internal enum ExpressionParameterRole
{
    RelationLeft,
    RelationRight,
    DerivedSource,
    RelationItem,
    RelationMembership,
    InvariantSource,
    DerivedValue
}

internal sealed record TrackedExpressionDependency(ExpressionParameterRole Role, DependencyPath Path);

internal sealed record ExpressionDependencyAnalysis(
    IReadOnlyList<TrackedExpressionDependency> Dependencies,
    DependencyAnalysisFlags Flags,
    bool HasRelationMembershipDependency);

internal static class ExpressionDependencyAnalyzer
{
    private static readonly HashSet<string> SupportedLinqOperators =
    [
        nameof(Enumerable.Count),
        nameof(Enumerable.Any),
        nameof(Enumerable.All),
        nameof(Enumerable.Sum),
        nameof(Enumerable.Min),
        nameof(Enumerable.Max),
        nameof(Enumerable.Average),
        nameof(Enumerable.Select),
        nameof(Enumerable.Where),
        nameof(Enumerable.OrderBy),
        nameof(Enumerable.ThenBy)
    ];

    public static ExpressionDependencyAnalysis AnalyzeRelation(LambdaExpression expression) =>
        Analyze(
            expression.Body,
            new Dictionary<ParameterExpression, ExpressionParameterRole>
            {
                [expression.Parameters[0]] = ExpressionParameterRole.RelationLeft,
                [expression.Parameters[1]] = ExpressionParameterRole.RelationRight
            });

    public static ExpressionDependencyAnalysis AnalyzeDerived(LambdaExpression expression) =>
        Analyze(
            expression.Body,
            new Dictionary<ParameterExpression, ExpressionParameterRole>
            {
                [expression.Parameters[0]] = ExpressionParameterRole.DerivedSource,
                [expression.Parameters[1]] = ExpressionParameterRole.RelationMembership
            });

    public static ExpressionDependencyAnalysis AnalyzeInvariant(LambdaExpression expression) =>
        Analyze(
            expression.Body,
            new Dictionary<ParameterExpression, ExpressionParameterRole>
            {
                [expression.Parameters[0]] = ExpressionParameterRole.InvariantSource,
                [expression.Parameters[1]] = ExpressionParameterRole.DerivedValue
            });

    private static ExpressionDependencyAnalysis Analyze(
        Expression body,
        Dictionary<ParameterExpression, ExpressionParameterRole> parameters)
    {
        var visitor = new DependencyVisitor(parameters);
        visitor.Visit(body);
        return visitor.CreateResult();
    }

    private sealed class DependencyVisitor(
        Dictionary<ParameterExpression, ExpressionParameterRole> parameters) : ExpressionVisitor
    {
        private readonly Dictionary<ParameterExpression, ExpressionParameterRole> _parameters = parameters;
        private readonly List<TrackedExpressionDependency> _dependencies = [];
        private DependencyAnalysisFlags _flags;
        private bool _hasMembershipDependency;

        public ExpressionDependencyAnalysis CreateResult() => new(
            _dependencies.Distinct(TrackedDependencyComparer.Instance).ToArray(),
            _flags,
            _hasMembershipDependency);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (TryGetBoundPath(node, out var parameter, out var memberPath))
            {
                var role = _parameters[parameter];
                if (role == ExpressionParameterRole.RelationMembership)
                {
                    _hasMembershipDependency = true;
                }
                else if (role != ExpressionParameterRole.DerivedValue)
                {
                    _dependencies.Add(new TrackedExpressionDependency(
                        role,
                        new DependencyPath(GetParameterIndex(role), memberPath.RootType, memberPath.Members)));
                }
                return node;
            }

            if (node.Expression is null || IsExternalMemberAccess(node))
                _flags |= DependencyAnalysisFlags.ContainsExternalState;
            return base.VisitMember(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (_parameters.TryGetValue(node, out var role) && role == ExpressionParameterRole.RelationMembership)
                _hasMembershipDependency = true;
            return node;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (IsSupportedLinq(node))
            {
                _hasMembershipDependency = true;
                Visit(node.Arguments[0]);
                foreach (var argument in node.Arguments.Skip(1))
                {
                    var lambda = UnwrapLambda(argument);
                    if (lambda is null)
                    {
                        Visit(argument);
                        continue;
                    }

                    var added = new List<ParameterExpression>();
                    foreach (var parameter in lambda.Parameters)
                    {
                        if (_parameters.TryAdd(parameter, ExpressionParameterRole.RelationItem))
                            added.Add(parameter);
                    }
                    Visit(lambda.Body);
                    foreach (var parameter in added)
                        _parameters.Remove(parameter);
                }
                return node;
            }

            if (!IsKnownStringEquality(node))
                _flags |= DependencyAnalysisFlags.ContainsOpaqueCode;
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            _flags |= DependencyAnalysisFlags.ContainsOpaqueCode;
            return base.VisitInvocation(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            _flags |= DependencyAnalysisFlags.ContainsOpaqueCode;
            foreach (var argument in node.Arguments)
                Visit(argument);
            return node;
        }

        private bool TryGetBoundPath(
            Expression expression,
            out ParameterExpression parameter,
            out MemberPath path)
        {
            foreach (var candidate in _parameters.Keys)
            {
                if (!MemberPath.TryCreate(expression, candidate, out path))
                    continue;
                parameter = candidate;
                return true;
            }
            parameter = null!;
            path = null!;
            return false;
        }

        private bool IsExternalMemberAccess(MemberExpression member)
        {
            Expression? root = member.Expression;
            while (root is MemberExpression nested)
                root = nested.Expression;
            while (root is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
                root = unary.Operand;
            return root is ConstantExpression ||
                (root is ParameterExpression parameter && !_parameters.ContainsKey(parameter));
        }

        private bool IsSupportedLinq(MethodCallExpression call) =>
            call.Method.DeclaringType is { } declaringType &&
            (declaringType == typeof(Enumerable) || declaringType == typeof(Queryable)) &&
            SupportedLinqOperators.Contains(call.Method.Name) &&
            call.Arguments.Count > 0 &&
            IsRelationCollection(call.Arguments[0]);

        private bool IsRelationCollection(Expression expression)
        {
            expression = MemberPath.StripConvert(expression);
            if (expression is ParameterExpression parameter &&
                _parameters.TryGetValue(parameter, out var role) &&
                role == ExpressionParameterRole.RelationMembership)
                return true;
            return expression is MethodCallExpression call && IsSupportedLinq(call);
        }

        private static LambdaExpression? UnwrapLambda(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
                expression = quote.Operand;
            return expression as LambdaExpression;
        }

        private static bool IsKnownStringEquality(MethodCallExpression call) =>
            call.Method.DeclaringType == typeof(string) &&
            call.Method.Name == nameof(string.Equals) &&
            call.Object is null &&
            call.Arguments.Count == 3 &&
            call.Arguments[2] is ConstantExpression { Value: StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase };

        private static int GetParameterIndex(ExpressionParameterRole role) => role switch
        {
            ExpressionParameterRole.RelationLeft or
            ExpressionParameterRole.DerivedSource or
            ExpressionParameterRole.InvariantSource => 0,
            _ => 1
        };
    }

    private sealed class TrackedDependencyComparer : IEqualityComparer<TrackedExpressionDependency>
    {
        public static TrackedDependencyComparer Instance { get; } = new();

        public bool Equals(TrackedExpressionDependency? x, TrackedExpressionDependency? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null && x.Role == y.Role &&
             x.Path.RootType == y.Path.RootType &&
             x.Path.Segments.Select(segment => segment.Member)
                 .SequenceEqual(y.Path.Segments.Select(segment => segment.Member)));

        public int GetHashCode(TrackedExpressionDependency dependency)
        {
            var hash = new HashCode();
            hash.Add(dependency.Role);
            hash.Add(dependency.Path.RootType);
            foreach (var segment in dependency.Path.Segments)
                hash.Add(segment.Member);
            return hash.ToHashCode();
        }
    }
}
