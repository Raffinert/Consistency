using System.Linq.Expressions;

namespace Raffinert.Consistency.Expressions;

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
    bool HasRelationMembershipDependency,
    LinqDependencySemantics LinqSemantics);

[Flags]
internal enum LinqDependencySemantics
{
    None = 0,
    Membership = 1,
    Item = 2,
    Ordering = 4
}

internal static class ExpressionDependencyAnalyzer
{
    private static readonly HashSet<string> SupportedLinqOperators =
    [
        nameof(Enumerable.Count),
        nameof(Enumerable.LongCount),
        nameof(Enumerable.Any),
        nameof(Enumerable.All),
        nameof(Enumerable.Sum),
        nameof(Enumerable.Min),
        nameof(Enumerable.Max),
        nameof(Enumerable.Average),
        nameof(Enumerable.Select),
        nameof(Enumerable.Where),
        nameof(Enumerable.OrderBy),
        nameof(Enumerable.ThenBy),
        nameof(Enumerable.First),
        nameof(Enumerable.FirstOrDefault),
        nameof(Enumerable.Single),
        nameof(Enumerable.SingleOrDefault),
        nameof(Enumerable.Last),
        nameof(Enumerable.LastOrDefault),
        nameof(Enumerable.Distinct),
        nameof(Enumerable.Take),
        nameof(Enumerable.Skip),
        nameof(Enumerable.Contains)
    ];

    public static ExpressionDependencyAnalysis AnalyzeRelation(LambdaExpression expression) =>
        Analyze(
            expression.Body,
            new Dictionary<ParameterExpression, ExpressionParameterRole>
            {
                [expression.Parameters[0]] = ExpressionParameterRole.RelationLeft,
                [expression.Parameters[1]] = ExpressionParameterRole.RelationRight
            });

    public static ExpressionDependencyAnalysis AnalyzeDerived(LambdaExpression expression)
    {
        var parameters = new Dictionary<ParameterExpression, ExpressionParameterRole>
        {
            [expression.Parameters[0]] = ExpressionParameterRole.DerivedSource
        };
        if (expression.Parameters.Count > 1)
            parameters[expression.Parameters[1]] = ExpressionParameterRole.RelationMembership;
        return Analyze(expression.Body, parameters);
    }

    public static ExpressionDependencyAnalysis AnalyzeSourceDerived(LambdaExpression expression) =>
        Analyze(expression.Body, new Dictionary<ParameterExpression, ExpressionParameterRole>
        {
            [expression.Parameters[0]] = ExpressionParameterRole.DerivedSource
        });

    public static TrackedExpressionDependency AnalyzeDeclaredSourceDependency(LambdaExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Parameters.Count != 1 ||
            !MemberPath.TryCreate(expression.Body, expression.Parameters[0], out var path) ||
            path.Members.Any(DependencyPathNavigation.IsCollection))
            throw new ArgumentException(
                "A declared derived dependency must be a single non-collection member path rooted in the derived source.",
                nameof(expression));
        return new TrackedExpressionDependency(
            ExpressionParameterRole.DerivedSource,
            new DependencyPath(0, path.RootType, path.Members));
    }

    public static ExpressionDependencyAnalysis AnalyzeSourceDerived(
        LambdaExpression expression,
        IReadOnlyList<TrackedExpressionDependency> declaredDependencies)
    {
        var inferred = AnalyzeSourceDerived(expression);
        return new ExpressionDependencyAnalysis(
            inferred.Dependencies.Concat(declaredDependencies)
                .Distinct(TrackedDependencyComparer.Instance).ToArray(),
            declaredDependencies.Count == 0
                ? inferred.Flags
                : inferred.Flags & ~DependencyAnalysisFlags.ContainsOpaqueCode,
            inferred.HasRelationMembershipDependency,
            inferred.LinqSemantics);
    }

    public static ExpressionDependencyAnalysis AnalyzeComposedDerived(LambdaExpression expression)
    {
        var parameters = new Dictionary<ParameterExpression, ExpressionParameterRole>
        {
            [expression.Parameters[0]] = ExpressionParameterRole.DerivedSource
        };
        foreach (var parameter in expression.Parameters.Skip(1))
            parameters[parameter] = ExpressionParameterRole.DerivedValue;
        return Analyze(expression.Body, parameters);
    }

    public static ExpressionDependencyAnalysis AnalyzeComposedDerived(
        LambdaExpression expression,
        IReadOnlyList<TrackedExpressionDependency> declaredDependencies)
    {
        var inferred = AnalyzeComposedDerived(expression);
        return new ExpressionDependencyAnalysis(
            inferred.Dependencies.Concat(declaredDependencies)
                .Distinct(TrackedDependencyComparer.Instance).ToArray(),
            declaredDependencies.Count == 0
                ? inferred.Flags
                : inferred.Flags & ~DependencyAnalysisFlags.ContainsOpaqueCode,
            inferred.HasRelationMembershipDependency,
            inferred.LinqSemantics);
    }

    public static ExpressionDependencyAnalysis AnalyzeInvariant(LambdaExpression expression) =>
        Analyze(
            expression.Body,
            new Dictionary<ParameterExpression, ExpressionParameterRole>
            {
                [expression.Parameters[0]] = ExpressionParameterRole.InvariantSource,
                [expression.Parameters[1]] = ExpressionParameterRole.DerivedValue
            });

    public static ExpressionDependencyAnalysis AnalyzeSourceInvariant(LambdaExpression expression) =>
        Analyze(expression.Body, new Dictionary<ParameterExpression, ExpressionParameterRole>
        {
            [expression.Parameters[0]] = ExpressionParameterRole.InvariantSource
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
        private readonly Dictionary<ParameterExpression, CollectionParameterBinding> _collectionBindings = [];
        private readonly List<TrackedExpressionDependency> _dependencies = [];
        private DependencyAnalysisFlags _flags;
        private bool _hasMembershipDependency;
        private LinqDependencySemantics _linqSemantics;

        public ExpressionDependencyAnalysis CreateResult() => new(
            _dependencies.Distinct(TrackedDependencyComparer.Instance).ToArray(),
            _flags,
            _hasMembershipDependency,
            _linqSemantics);

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
                    var rootType = memberPath.RootType;
                    IReadOnlyList<System.Reflection.MemberInfo> members = memberPath.Members;
                    if (_collectionBindings.TryGetValue(parameter, out var binding))
                    {
                        rootType = binding.RootType;
                        members = binding.Prefix.Concat(memberPath.Members).ToArray();
                    }
                    _dependencies.Add(new TrackedExpressionDependency(
                        role,
                        new DependencyPath(GetParameterIndex(role), rootType, members)));
                }
                return node;
            }

            if (node.Expression is MethodCallExpression call &&
                IsSupportedLinq(call) &&
                IsRelationCollection(call.Arguments[0]))
            {
                _dependencies.Add(new TrackedExpressionDependency(
                    ExpressionParameterRole.RelationItem,
                    new DependencyPath(1, node.Member.DeclaringType!, [node.Member])));
                _linqSemantics |= LinqDependencySemantics.Item;
                Visit(call);
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
                _linqSemantics |= GetLinqSemantics(node);
                var collectionBinding = TryGetCollectionBinding(node.Arguments[0]);
                if (collectionBinding is null)
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
                        var role = collectionBinding?.Role ?? ExpressionParameterRole.RelationItem;
                        if (_parameters.TryAdd(parameter, role))
                        {
                            added.Add(parameter);
                            if (collectionBinding is not null)
                                _collectionBindings.Add(parameter, collectionBinding);
                        }
                    }
                    Visit(lambda.Body);
                    foreach (var parameter in added)
                    {
                        _collectionBindings.Remove(parameter);
                        _parameters.Remove(parameter);
                    }
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
            (IsRelationCollection(call.Arguments[0]) || TryGetCollectionBinding(call.Arguments[0]) is not null);

        private static LinqDependencySemantics GetLinqSemantics(MethodCallExpression call)
        {
            var semantics = LinqDependencySemantics.Membership;
            if (call.Arguments.Skip(1).Any(argument => UnwrapLambda(argument) is not null))
                semantics |= LinqDependencySemantics.Item;
            if (call.Method.Name is nameof(Enumerable.OrderBy) or nameof(Enumerable.ThenBy) or
                nameof(Enumerable.First) or nameof(Enumerable.FirstOrDefault) or
                nameof(Enumerable.Last) or nameof(Enumerable.LastOrDefault) or
                nameof(Enumerable.Take) or nameof(Enumerable.Skip))
                semantics |= LinqDependencySemantics.Ordering;
            return semantics;
        }

        private CollectionParameterBinding? TryGetCollectionBinding(Expression expression)
        {
            foreach (var parameter in _parameters.Keys)
            {
                if (!MemberPath.TryCreate(expression, parameter, out var path))
                    continue;
                var role = _parameters[parameter];
                var rootType = path.RootType;
                IReadOnlyList<System.Reflection.MemberInfo> prefix = path.Members;
                if (_collectionBindings.TryGetValue(parameter, out var parent))
                {
                    rootType = parent.RootType;
                    prefix = parent.Prefix.Concat(path.Members).ToArray();
                }
                return new CollectionParameterBinding(role, rootType, prefix);
            }
            return null;
        }

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

        private sealed record CollectionParameterBinding(
            ExpressionParameterRole Role,
            Type RootType,
            IReadOnlyList<System.Reflection.MemberInfo> Prefix);
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
