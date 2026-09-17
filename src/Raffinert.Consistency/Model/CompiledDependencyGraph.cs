namespace Raffinert.Consistency;

internal enum DependencyNodeKind
{
    Derived,
    Invariant
}

internal sealed record CompiledDependencyNode(
    int Id,
    DependencyNodeKind Kind,
    object Definition,
    int TopologicalOrder);

internal enum CompiledDependencyEdgeKind
{
    DerivedToDerived,
    DerivedToInvariant
}

internal sealed record CompiledDependencyEdge(
    int FromNodeId,
    int ToNodeId,
    CompiledDependencyEdgeKind Kind,
    UpstreamDerivedInput? DerivedInput);

internal sealed class CompiledDependencyGraph
{
    private readonly IReadOnlyList<CompiledDependencyEdge>[] _incoming;
    private readonly IReadOnlyList<CompiledDependencyEdge>[] _outgoing;

    private CompiledDependencyGraph(
        IReadOnlyList<CompiledDependencyNode> nodes,
        IReadOnlyList<CompiledDependencyEdge> edges,
        IReadOnlyList<CompiledDependencyEdge>[] incoming,
        IReadOnlyList<CompiledDependencyEdge>[] outgoing)
    {
        Nodes = nodes;
        Edges = edges;
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public IReadOnlyList<CompiledDependencyNode> Nodes { get; }
    public IReadOnlyList<CompiledDependencyEdge> Edges { get; }

    public IReadOnlyList<CompiledDependencyEdge> GetIncoming(int nodeId) => _incoming[nodeId];
    public IReadOnlyList<CompiledDependencyEdge> GetOutgoing(int nodeId) => _outgoing[nodeId];

    public static CompiledDependencyGraph Compile(
        IReadOnlyList<IDerivedDefinition> derived,
        IReadOnlyList<IInvariantDefinition> invariants)
    {
        var definitions = derived.Cast<object>().Concat(invariants).ToArray();
        var ids = definitions.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id, ReferenceEqualityComparer.Instance);
        var edgeCandidates = new List<CompiledDependencyEdge>();
        foreach (var downstream in derived)
            foreach (var input in downstream.Inputs.OfType<UpstreamDerivedInput>())
                edgeCandidates.Add(new CompiledDependencyEdge(
                    ids[input.Upstream],
                    ids[downstream],
                    CompiledDependencyEdgeKind.DerivedToDerived,
                    input));
        foreach (var invariant in invariants)
            foreach (var upstream in invariant.UpstreamDerived)
                edgeCandidates.Add(new CompiledDependencyEdge(
                    ids[upstream],
                    ids[invariant],
                    CompiledDependencyEdgeKind.DerivedToInvariant,
                    null));

        var edges = edgeCandidates
            .DistinctBy(edge => (edge.FromNodeId, edge.ToNodeId, edge.Kind))
            .OrderBy(edge => edge.FromNodeId)
            .ThenBy(edge => edge.ToNodeId)
            .ThenBy(edge => edge.Kind)
            .ToArray();
        ValidateEdges(definitions, edges);
        var incoming = CreateAdjacency(definitions.Length, edges, incoming: true);
        var outgoing = CreateAdjacency(definitions.Length, edges, incoming: false);

        var indegree = new int[definitions.Length];
        foreach (var edge in edges)
            indegree[edge.ToNodeId]++;

        var ready = new SortedSet<int>(Enumerable.Range(0, definitions.Length).Where(id => indegree[id] == 0));
        var ordered = new List<int>();
        while (ready.Count > 0)
        {
            var id = ready.Min;
            ready.Remove(id);
            ordered.Add(id);
            foreach (var edge in outgoing[id])
                if (--indegree[edge.ToNodeId] == 0)
                    ready.Add(edge.ToNodeId);
        }

        if (ordered.Count != definitions.Length)
        {
            var cycle = Enumerable.Range(0, definitions.Length).Where(id => indegree[id] > 0)
                .Select(id => Describe(definitions[id]));
            throw new InvalidOperationException(
                $"Dependency cycle detected among: {string.Join(" -> ", cycle)}.");
        }

        var positions = ordered.Select((id, position) => (id, position))
            .ToDictionary(pair => pair.id, pair => pair.position);
        var nodes = definitions.Select((definition, id) => new CompiledDependencyNode(
            id,
            definition is IDerivedDefinition ? DependencyNodeKind.Derived : DependencyNodeKind.Invariant,
            definition,
            positions[id])).OrderBy(node => node.TopologicalOrder).ToArray();
        return new CompiledDependencyGraph(
            Array.AsReadOnly(nodes),
            Array.AsReadOnly(edges),
            incoming,
            outgoing);
    }

    private static IReadOnlyList<CompiledDependencyEdge>[] CreateAdjacency(
        int nodeCount,
        IReadOnlyList<CompiledDependencyEdge> edges,
        bool incoming)
    {
        var adjacency = Enumerable.Range(0, nodeCount)
            .Select(_ => new List<CompiledDependencyEdge>())
            .ToArray();
        foreach (var edge in edges)
            adjacency[incoming ? edge.ToNodeId : edge.FromNodeId].Add(edge);
        return adjacency.Select(values =>
                (IReadOnlyList<CompiledDependencyEdge>)Array.AsReadOnly(values.ToArray()))
            .ToArray();
    }

    private static void ValidateEdges(
        IReadOnlyList<object> definitions,
        IReadOnlyList<CompiledDependencyEdge> edges)
    {
        foreach (var edge in edges)
        {
            if (edge.FromNodeId < 0 || edge.FromNodeId >= definitions.Count ||
                edge.ToNodeId < 0 || edge.ToNodeId >= definitions.Count)
                throw new InvalidOperationException("A compiled dependency edge references an unknown node.");
            if (definitions[edge.FromNodeId] is not IDerivedDefinition)
                throw new InvalidOperationException("A compiled dependency edge must originate at a derived node.");
            if (edge.Kind == CompiledDependencyEdgeKind.DerivedToDerived &&
                (definitions[edge.ToNodeId] is not IDerivedDefinition || edge.DerivedInput is null))
                throw new InvalidOperationException(
                    "A derived-to-derived edge must target a derived node and retain its semantic input.");
            if (edge.Kind == CompiledDependencyEdgeKind.DerivedToInvariant &&
                (definitions[edge.ToNodeId] is not IInvariantDefinition || edge.DerivedInput is not null))
                throw new InvalidOperationException(
                    "A derived-to-invariant edge must target an invariant node without a derived input.");
        }
    }

    private static string Describe(object definition) => definition switch
    {
        IDerivedDefinition derived => derived.DefinitionKey ?? derived.ComputationExpression.Body.ToString(),
        IInvariantDefinition invariant => invariant.DefinitionKey ?? invariant.PredicateExpression.Body.ToString(),
        _ => definition.GetType().Name
    };
}
