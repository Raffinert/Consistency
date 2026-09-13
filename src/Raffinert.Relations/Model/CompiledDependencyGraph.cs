namespace Raffinert.Relations;

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

internal sealed record CompiledDependencyEdge(int FromNodeId, int ToNodeId);

internal sealed class CompiledDependencyGraph
{
    private CompiledDependencyGraph(
        IReadOnlyList<CompiledDependencyNode> nodes,
        IReadOnlyList<CompiledDependencyEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public IReadOnlyList<CompiledDependencyNode> Nodes { get; }
    public IReadOnlyList<CompiledDependencyEdge> Edges { get; }

    public static CompiledDependencyGraph Compile(
        IReadOnlyList<IDerivedDefinition> derived,
        IReadOnlyList<IInvariantDefinition> invariants)
    {
        var definitions = derived.Cast<object>().Concat(invariants).ToArray();
        var ids = definitions.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id, ReferenceEqualityComparer.Instance);
        var edges = new List<CompiledDependencyEdge>();
        foreach (var downstream in derived)
            foreach (var upstream in downstream.Inputs.OfType<UpstreamDerivedInput>().Select(input => input.Upstream))
                edges.Add(new CompiledDependencyEdge(ids[upstream], ids[downstream]));
        foreach (var invariant in invariants)
            foreach (var upstream in invariant.UpstreamDerived)
                edges.Add(new CompiledDependencyEdge(ids[upstream], ids[invariant]));

        var outgoing = Enumerable.Range(0, definitions.Length).ToDictionary(id => id, _ => new List<int>());
        var indegree = new int[definitions.Length];
        foreach (var edge in edges.Distinct())
        {
            outgoing[edge.FromNodeId].Add(edge.ToNodeId);
            indegree[edge.ToNodeId]++;
        }

        var ready = new SortedSet<int>(Enumerable.Range(0, definitions.Length).Where(id => indegree[id] == 0));
        var ordered = new List<int>();
        while (ready.Count > 0)
        {
            var id = ready.Min;
            ready.Remove(id);
            ordered.Add(id);
            foreach (var downstream in outgoing[id].Order())
                if (--indegree[downstream] == 0)
                    ready.Add(downstream);
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
        return new CompiledDependencyGraph(nodes, edges.Distinct().ToArray());
    }

    private static string Describe(object definition) => definition switch
    {
        IDerivedDefinition derived => derived.DefinitionKey ?? derived.ComputationExpression.Body.ToString(),
        IInvariantDefinition invariant => invariant.DefinitionKey ?? invariant.PredicateExpression.Body.ToString(),
        _ => definition.GetType().Name
    };
}
