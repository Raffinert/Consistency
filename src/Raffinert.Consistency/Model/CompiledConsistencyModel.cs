using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

public sealed class CompiledConsistencyModel
{
    private readonly IReadOnlyList<IObjectSetDefinition> _sets;
    private readonly IReadOnlyList<IRelationDefinition> _relations;
    private readonly IReadOnlyList<IDerivedDefinition> _derivedStates;
    private readonly IReadOnlyList<IInvariantDefinition> _invariants;
    private readonly CompiledDependencyGraph _dependencyGraph;

    internal CompiledConsistencyModel(
        IReadOnlyList<IObjectSetDefinition> sets,
        IReadOnlyList<IRelationDefinition> relations,
        IReadOnlyList<IDerivedDefinition> derivedStates,
        IReadOnlyList<IInvariantDefinition> invariants,
        CompiledDependencyGraph dependencyGraph)
    {
        _sets = sets;
        _relations = relations;
        _derivedStates = derivedStates;
        _invariants = invariants;
        _dependencyGraph = dependencyGraph;
        Diagnostics = CreateDiagnostics();
        DebugView = CreateDebugView();
    }

    public string DebugView { get; }
    public CompiledModelDiagnostics Diagnostics { get; }
    public ConsistencyRuntime CreateRuntime() => new(_sets, _relations, _derivedStates, _invariants, _dependencyGraph);
    public ConsistencyRuntime CreateRuntime(Action<RuntimeSeedBuilder> configureSeed)
    {
        ArgumentNullException.ThrowIfNull(configureSeed);
        var seed = new RuntimeSeedBuilder();
        configureSeed(seed);
        var runtime = CreateRuntime();
        runtime.Bootstrap(seed.Entries);
        return runtime;
    }
    public ConsistencyRuntime CreateRuntime(RuntimeDiagnosticOptions diagnosticOptions)
    {
        ArgumentNullException.ThrowIfNull(diagnosticOptions);
        return new ConsistencyRuntime(_sets, _relations, _derivedStates, _invariants, _dependencyGraph, null, diagnosticOptions);
    }
    public ConsistencyRuntime CreateRuntime(
        RuntimeDiagnosticOptions diagnosticOptions,
        Action<RuntimeSeedBuilder> configureSeed)
    {
        ArgumentNullException.ThrowIfNull(diagnosticOptions);
        ArgumentNullException.ThrowIfNull(configureSeed);
        var seed = new RuntimeSeedBuilder();
        configureSeed(seed);
        var runtime = CreateRuntime(diagnosticOptions);
        runtime.Bootstrap(seed.Entries);
        return runtime;
    }
    internal ConsistencyRuntime CreateRuntime(IDependencyImpactPolicy dependencyImpactPolicy) =>
        new(_sets, _relations, _derivedStates, _invariants, _dependencyGraph, dependencyImpactPolicy);

    private string CreateDebugView()
    {
        var lines = new List<string>();
        foreach (var set in _sets)
        {
            lines.Add($"ObjectSet {set.ObjectType.Name}");
            lines.Add($"  Key: {GetMemberName(set.KeyExpression?.Body)}");
        }
        foreach (var relation in _relations)
        {
            lines.Add($"Relation {relation.LeftSet.ObjectType.Name} -> {relation.RightSet.ObjectType.Name}");
            lines.Add($"  Predicate: {relation.PredicateExpression.Body}");
            lines.Add("  Dependencies:");
            foreach (var dependency in relation.Analysis.DependencyPaths)
                lines.Add($"    {dependency.DisplayName}");
            lines.Add("  Join key:");
            foreach (var key in relation.Analysis.JoinKeyParts)
                lines.Add($"    {key.Left.DisplayName} <-> {key.Right.DisplayName} ({key.EqualitySemantics})");
            lines.Add($"  Access plan: {relation.AccessPlan.DisplayName}");
            lines.Add($"  Reverse access plan: {relation.ReverseAccessPlan?.DisplayName ?? "Disabled"}");
            lines.Add($"  Propagation plan: {relation.PropagationPlan}");
            lines.Add($"  Pair membership retained: {relation.PropagationPlan == RelationPropagationPlan.ExactMaterialized}");
            lines.Add($"  Relation materialization: {(relation.PropagationPlan == RelationPropagationPlan.ExactMaterialized ? "ExactPropagation" : "None")}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(relation.Analysis.DependencyAnalysis)}");
            var materialized = _derivedStates.Any(derived =>
                derived.Inputs.OfType<RelationDerivedInput>()
                    .Any(input => ReferenceEquals(input.Relation, relation)));
            lines.Add($"  Dependency tracking: {FormatDependencyTracking(
                relation.Analysis.DependencyAnalysis,
                relation.AllowIncompleteDependencies,
                materialized)}");
            lines.Add($"  Residual predicate: {(relation.Analysis.HasResidualPredicate ? "Yes" : "No")}");
            foreach (var residual in relation.Analysis.RecognizedResiduals)
                lines.Add($"    {residual}");
            var navigationEdges = relation.Analysis.DependencyPaths
                .SelectMany(path => path.Segments.Take(Math.Max(0, path.Segments.Count - 1)))
                .Where(segment => !segment.ValueType.IsValueType && segment.ValueType != typeof(string))
                .Select(segment => $"{segment.DeclaringType.Name}.{segment.Member.Name}")
                .Distinct()
                .ToArray();
            if (navigationEdges.Length > 0)
            {
                lines.Add("  Navigation indexes:");
                foreach (var edge in navigationEdges)
                    lines.Add($"    {edge} (shared reverse tracked)");
            }
        }
        foreach (var derived in _derivedStates)
        {
            var inputs = derived.Inputs.Select(input => input switch
            {
                RelationDerivedInput relation => $"relation:{relation.Relation.RightSet.ObjectType.Name}",
                UpstreamDerivedInput upstream =>
                    $"derived:{upstream.Upstream.DefinitionKey ?? upstream.Upstream.SourceSet.ObjectType.Name}",
                _ => throw new NotSupportedException($"Unknown derived input '{input.GetType().Name}'.")
            });
            lines.Add($"Derived {derived.SourceSet.ObjectType.Name} using [{string.Join(", ", inputs)}]: {derived.ComputationExpression.Body}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(derived.Analysis.Flags)}");
            lines.Add($"  Dependency tracking: {FormatDependencyTracking(
                derived.Analysis.Flags,
                derived.AllowIncompleteDependencies,
                cached: true)}");
            lines.Add($"  Relation membership: {(derived.Analysis.HasRelationMembershipDependency ? "Yes" : "No")}");
            lines.Add($"  Impact policy: membership added={derived.ImpactPolicy.MembershipAdded}, " +
                $"removed={derived.ImpactPolicy.MembershipRemoved}, item changed={derived.ImpactPolicy.ItemChanged}, " +
                $"source changed={derived.ImpactPolicy.SourceChanged}");
            lines.Add($"  Computation plan: {derived.ComputationPlanName}");
            lines.Add($"  LINQ semantics: {derived.Analysis.LinqSemantics}");
            foreach (var dependency in derived.Analysis.Dependencies)
                lines.Add($"  {dependency.Role}: {dependency.Path.DisplayName}");
        }
        foreach (var invariant in _invariants)
        {
            lines.Add($"Invariant {invariant.SourceSet.ObjectType.Name}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(invariant.Analysis.Flags)}");
            lines.Add($"  Dependency tracking: {FormatDependencyTracking(
                invariant.Analysis.Flags,
                invariant.AllowIncompleteDependencies,
                cached: true)}");
            foreach (var dependency in invariant.Analysis.Dependencies)
                lines.Add($"  {dependency.Role}: {dependency.Path.DisplayName}");
        }
        lines.Add("Dependency DAG:");
        foreach (var node in _dependencyGraph.Nodes)
            lines.Add($"  [{node.TopologicalOrder}] {node.Kind}: {node.Id}");
        foreach (var edge in _dependencyGraph.Edges)
            lines.Add($"  Edge: {edge.FromNodeId} -> {edge.ToNodeId}");
        return string.Join(Environment.NewLine, lines);
    }

    private CompiledModelDiagnostics CreateDiagnostics()
    {
        var relationIds = _relations.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id);
        var derivedIds = _derivedStates.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id);
        return new CompiledModelDiagnostics(
            _sets.Select(set => new ObjectSetModelDiagnostics(
                set.Id,
                set.ObjectType,
                set.KeyExpression?.Body.ToString() ?? "<missing>")
            { DefinitionKey = set.DefinitionKey })
                .ToArray(),
            _relations.Select((relation, id) => new RelationModelDiagnostics(
                id,
                relation.LeftSet.Id,
                relation.RightSet.Id,
                relation.LeftSet.ObjectType,
                relation.RightSet.ObjectType,
                relation.PredicateExpression.Body.ToString(),
                relation.Analysis.DependencyPaths.Select(path => path.DisplayName).ToArray(),
                relation.Analysis.JoinKeyParts.Select(key =>
                    $"{key.Left.DisplayName} <-> {key.Right.DisplayName} ({key.EqualitySemantics})").ToArray(),
                ToPublicAccessPlan(relation.AccessPlan),
                relation.ReverseAccessPlan is null ? null : ToPublicAccessPlan(relation.ReverseAccessPlan),
                relation.PropagationPlan != RelationPropagationPlan.ExactMaterialized
                    ? RelationMaterializationMode.None
                    : RelationMaterializationMode.ExactPropagation,
                (RelationPropagationPlanKind)relation.PropagationPlan,
                relation.PropagationPlan == RelationPropagationPlan.ExactMaterialized,
                CreatePropagationReason(relation),
                ToPublicCompleteness(relation.Analysis.DependencyAnalysis),
                relation.AllowIncompleteDependencies)
            { DefinitionKey = relation.DefinitionKey })
                .ToArray(),
            _derivedStates.Select((derived, id) => new DerivedModelDiagnostics(
                id,
                derived.SourceSet.Id,
                derived.Inputs.OfType<RelationDerivedInput>().Select(input => input.Relation)
                    .Select(relation => relationIds[relation]).ToArray(),
                derived.Inputs.OfType<UpstreamDerivedInput>().Select(input => input.Upstream)
                    .Select(upstream => derivedIds[upstream]).ToArray(),
                derived.ComputationExpression.Body.ToString(),
                derived.Analysis.Dependencies.Select(dependency =>
                    $"{dependency.Role}: {dependency.Path.DisplayName}").ToArray(),
                ToPublicCompleteness(derived.Analysis.Flags),
                derived.AllowIncompleteDependencies,
                derived.Analysis.HasRelationMembershipDependency,
                (DerivedLinqSemantics)(int)derived.Analysis.LinqSemantics,
                derived.ComputationPlanName,
                derived.ImpactPolicy.MembershipAdded,
                derived.ImpactPolicy.MembershipRemoved,
                derived.ImpactPolicy.ItemChanged,
                derived.ImpactPolicy.SourceChanged)
            {
                DefinitionKey = derived.DefinitionKey,
                HasConditionalSourcePolicy = derived.ImpactPolicy.SourceMemberRules.Count > 0,
                SourceMemberRuleCount = derived.ImpactPolicy.SourceMemberRules.Count,
                SourceMemberRuleNames = derived.ImpactPolicy.SourceMemberRules
                    .Select(rule => rule.Member.Name).ToArray()
            })
                .ToArray(),
            _invariants.Select((invariant, id) => new InvariantModelDiagnostics(
                id,
                invariant.SourceSet.Id,
                invariant.UpstreamDerived.Select(upstream => derivedIds[upstream]).ToArray(),
                invariant.PredicateExpression.Body.ToString(),
                invariant.Analysis.Dependencies.Select(dependency =>
                    $"{dependency.Role}: {dependency.Path.DisplayName}").ToArray(),
                ToPublicCompleteness(invariant.Analysis.Flags),
                invariant.AllowIncompleteDependencies,
                invariant.Reaction)
            { DefinitionKey = invariant.DefinitionKey })
                .ToArray());
    }

    private static RelationAccessPlanKind ToPublicAccessPlan(RelationAccessPlan plan) => plan switch
    {
        HashJoinAccessPlan => RelationAccessPlanKind.HashJoin,
        _ => RelationAccessPlanKind.Scan
    };

    private string CreatePropagationReason(IRelationDefinition relation)
    {
        var consumers = _derivedStates.Where(derived =>
            derived.Inputs.OfType<RelationDerivedInput>()
                .Any(input => ReferenceEquals(input.Relation, relation))).ToArray();
        if (relation.PropagationPlan == RelationPropagationPlan.ConservativeInvalidation)
            return "Explicit full-recompute consumer preference";
        if (consumers.Any(derived => derived.RequiresExactPropagation))
            return "Incremental computation or exact membership severity";
        return relation.PropagationPlan == RelationPropagationPlan.ExactMaterialized
            ? "Exact propagation default"
            : "No derived consumer";
    }

    private static DependencyCompletenessIssue ToPublicCompleteness(DependencyAnalysisFlags flags)
    {
        var result = DependencyCompletenessIssue.None;
        if (flags.HasFlag(DependencyAnalysisFlags.ContainsOpaqueCode))
            result |= DependencyCompletenessIssue.OpaqueCode;
        if (flags.HasFlag(DependencyAnalysisFlags.ContainsExternalState))
            result |= DependencyCompletenessIssue.ExternalState;
        return result;
    }

    private static string FormatDependencyAnalysis(DependencyAnalysisFlags flags) =>
        flags == DependencyAnalysisFlags.Complete
            ? nameof(DependencyAnalysisFlags.Complete)
            : string.Join(", ", Enum.GetValues<DependencyAnalysisFlags>()
                .Where(flag => flag != DependencyAnalysisFlags.Complete && flags.HasFlag(flag)));

    private static string FormatDependencyTracking(
        DependencyAnalysisFlags flags,
        bool explicitlyAllowed,
        bool cached)
    {
        if (flags == DependencyAnalysisFlags.Complete)
            return "Fully tracked";
        if (explicitlyAllowed)
            return $"Incomplete, explicitly allowed ({flags}); cached freshness is not guaranteed";
        return cached
            ? $"Incomplete, rejected at build ({flags})"
            : $"Incomplete direct-query evaluation ({flags}); no cached freshness claim";
    }

    private static string GetMemberName(Expression? expression)
    {
        while (expression is UnaryExpression unary) expression = unary.Operand;
        return expression is MemberExpression member ? member.Member.Name : expression?.ToString() ?? "<missing>";
    }
}

