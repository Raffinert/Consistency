namespace Raffinert.Consistency.Tests;

public sealed class CompiledDependencyGraphTests
{
    [Fact]
    public void Compiled_graph_assigns_upstream_before_downstream_topological_order()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var first = model.Derived(sources).Select(source => source.Value).Named("first");
        var second = model.Derived(sources).From(first).Select((_, value) => value + 1).Named("second");
        var third = model.Derived(sources).From(second).Select((_, value) => value + 1).Named("third");

        var graph = Compile([first, second, third]);

        Assert.True(Position(graph, first) < Position(graph, second));
        Assert.True(Position(graph, second) < Position(graph, third));
    }

    [Fact]
    public void Compiled_graph_places_invariant_after_all_of_its_upstream_derived_nodes()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var first = model.Derived(sources).Select(source => source.Value).Named("first");
        var second = model.Derived(sources).From(first).Select((_, value) => value + 1).Named("second");
        var invariant = model.Invariant(sources).From(first).From(second)
            .Must((_, left, right) => left < right).Named("ordered");

        var graph = Compile([first, second], [invariant]);

        Assert.True(Position(graph, first) < Position(graph, invariant));
        Assert.True(Position(graph, second) < Position(graph, invariant));
    }

    [Fact]
    public void Compiled_graph_deduplicates_duplicate_logical_edges()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var upstream = model.Derived(sources).Select(source => source.Value);
        var downstream = model.Derived(sources).From(upstream).From(upstream)
            .Select((_, left, right) => left + right);

        var graph = Compile([upstream, downstream]);
        var from = Node(graph, upstream).Id;
        var to = Node(graph, downstream).Id;

        Assert.Single(graph.Edges, edge => edge.FromNodeId == from && edge.ToNodeId == to);
    }

    [Fact]
    public void Compiled_graph_topological_order_is_deterministic_for_same_model()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value);
        var left = model.Derived(sources).From(root).Select((_, value) => value + 1);
        var right = model.Derived(sources).From(root).Select((_, value) => value + 2);
        var join = model.Derived(sources).From(left).From(right).Select((_, first, second) => first + second);

        var firstCompilation = Compile([root, left, right, join]);
        var secondCompilation = Compile([root, left, right, join]);

        Assert.Equal(
            firstCompilation.Nodes.Select(node => (node.Definition, node.TopologicalOrder)),
            secondCompilation.Nodes.Select(node => (node.Definition, node.TopologicalOrder)));
    }

    [Fact]
    public void Compiled_graph_preserves_diamond_structure_without_duplicate_downstream_edges()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value);
        var left = model.Derived(sources).From(root).Select((_, value) => value + 1);
        var right = model.Derived(sources).From(root).Select((_, value) => value + 2);
        var join = model.Derived(sources).From(left).From(right).Select((_, first, second) => first + second);

        var graph = Compile([root, left, right, join]);
        var rootId = Node(graph, root).Id;
        var leftId = Node(graph, left).Id;
        var rightId = Node(graph, right).Id;
        var joinId = Node(graph, join).Id;

        Assert.Equal(
            [leftId, rightId],
            graph.Edges.Where(edge => edge.FromNodeId == rootId).Select(edge => edge.ToNodeId).Order());
        Assert.Single(graph.Edges, edge => edge.FromNodeId == leftId && edge.ToNodeId == joinId);
        Assert.Single(graph.Edges, edge => edge.FromNodeId == rightId && edge.ToNodeId == joinId);
    }

    [Fact]
    public void Compiled_graph_outgoing_adjacency_matches_edges()
    {
        var graph = CreateDiamondGraph();

        Assert.All(graph.Nodes, node => Assert.Equal(
            graph.Edges.Where(edge => edge.FromNodeId == node.Id),
            graph.GetOutgoing(node.Id)));
    }

    [Fact]
    public void Compiled_graph_incoming_adjacency_matches_edges()
    {
        var graph = CreateDiamondGraph();

        Assert.All(graph.Nodes, node => Assert.Equal(
            graph.Edges.Where(edge => edge.ToNodeId == node.Id),
            graph.GetIncoming(node.Id)));
    }

    [Fact]
    public void Derived_to_derived_edge_retains_exact_upstream_input_metadata()
    {
        var model = new ConsistencyModelBuilder();
        var roots = model.Objects<Root>().Key(root => root.Id);
        var middles = model.Objects<Middle>().Key(middle => middle.Id);
        var rootValue = model.Derived(roots).Select(root => root.Value);
        var middleValue = model.Derived(middles).From(middle => middle.Root, rootValue)
            .Select((_, value) => value + 1);
        var input = Assert.Single(middleValue.Definition.Inputs.OfType<UpstreamDerivedInput>());

        var graph = CompiledDependencyGraph.Compile(
            [rootValue.Definition, middleValue.Definition], []);
        var edge = Assert.Single(graph.Edges);

        Assert.Equal(CompiledDependencyEdgeKind.DerivedToDerived, edge.Kind);
        Assert.Same(input, edge.DerivedInput);
    }

    [Fact]
    public void Derived_to_invariant_edge_has_correct_kind()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var value = model.Derived(sources).Select(source => source.Value);
        var invariant = model.Invariant(sources).From(value).Must((_, current) => current >= 0);

        var graph = Compile([value], [invariant]);
        var edge = Assert.Single(graph.Edges);

        Assert.Equal(CompiledDependencyEdgeKind.DerivedToInvariant, edge.Kind);
        Assert.Null(edge.DerivedInput);
    }

    [Fact]
    public void Compiled_graph_adjacency_is_duplicate_free()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var upstream = model.Derived(sources).Select(source => source.Value);
        var downstream = model.Derived(sources).From(upstream).From(upstream)
            .Select((_, left, right) => left + right);

        var graph = Compile([upstream, downstream]);
        var upstreamNode = Node(graph, upstream);
        var downstreamNode = Node(graph, downstream);

        Assert.Single(graph.GetOutgoing(upstreamNode.Id));
        Assert.Single(graph.GetIncoming(downstreamNode.Id));
    }

    [Fact]
    public void Deep_derived_chain_propagates_in_topological_order_without_registration_order_dependency()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var chain = new List<Derived<Source, int>>
        {
            model.Derived(sources).Select(source => source.Value).Named("a0")
        };
        for (var index = 1; index < 12; index++)
        {
            var upstream = chain[^1];
            chain.Add(model.Derived(sources).From(upstream)
                .Select((_, value) => value + 1).Named($"a{index}"));
        }
        var invariant = model.Invariant(sources).From(chain[^1])
            .Must((source, value) => value == source.Value + 11).Named("chain-invariant");
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        Assert.Equal(12, runtime.Evaluate(chain[^1], source));
        Assert.True(runtime.Evaluate(invariant, source));

        source.Value = 7;
        runtime.Apply(Change.Property(sources, source, value => value.Value, 1, 7));

        Assert.All(chain, derived => Assert.NotEqual(DerivedValueState.Fresh, runtime.GetState(derived, source)));
        Assert.Equal(18, runtime.Evaluate(chain[^1], source));
        Assert.All(chain, derived => Assert.Equal(DerivedValueState.Fresh, runtime.GetState(derived, source)));
        Assert.True(runtime.Evaluate(invariant, source));
    }

    [Fact]
    public void Projected_upstream_chain_maps_sources_across_sets_through_multiple_DAG_levels()
    {
        var model = new ConsistencyModelBuilder();
        var roots = model.Objects<Root>().Key(root => root.Id);
        var middles = model.Objects<Middle>().Key(middle => middle.Id);
        var leaves = model.Objects<Leaf>().Key(leaf => leaf.Id);
        var rootValue = model.Derived(roots)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(root => root.Value);
        var middleValue = model.Derived(middles).From(middle => middle.Root, rootValue)
            .Select((_, value) => value + 1);
        var leafValue = model.Derived(leaves).From(leaf => leaf.Middle, middleValue)
            .Select((_, value) => value + 1);
        var firstRoot = new Root { Value = 1 };
        var secondRoot = new Root { Value = 10 };
        var firstMiddle = new Middle { Root = firstRoot };
        var secondMiddle = new Middle { Root = secondRoot };
        var firstLeaf = new Leaf { Middle = firstMiddle };
        var secondLeaf = new Leaf { Middle = secondMiddle };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(leaves, [secondLeaf, firstLeaf]);
            seed.Add(middles, [secondMiddle, firstMiddle]);
            seed.Add(roots, [secondRoot, firstRoot]);
        });
        Assert.Equal(3, runtime.Evaluate(leafValue, firstLeaf));
        Assert.Equal(12, runtime.Evaluate(leafValue, secondLeaf));

        firstRoot.Value = 2;
        runtime.Apply(Change.Property(roots, firstRoot, root => root.Value, 1, 2));

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(middleValue, firstMiddle));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(leafValue, firstLeaf));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(middleValue, secondMiddle));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(leafValue, secondLeaf));
        Assert.Equal(4, runtime.Evaluate(leafValue, firstLeaf));
    }

    [Fact]
    public void Diamond_downstream_source_is_propagated_once_semantically()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value).Named("root");
        var left = model.Derived(sources).From(root).Select((_, value) => value + 1).Named("left");
        var right = model.Derived(sources).From(root).Select((_, value) => value + 2).Named("right");
        var join = model.Derived(sources).From(left).From(right)
            .Select((_, first, second) => first + second).Named("join");
        model.Invariant(sources).From(join).Must((_, value) => value < 100)
            .RepairWhenViolated().Named("limit");
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(join, source);
        source.Value = 2;

        var result = runtime.ApplyDetailed(
            MutationSet.Create(Change.Property(sources, source, value => value.Value, 1, 2)),
            RuntimeImpactDetailLevel.Causal).Result;

        var joinImpact = result.DerivedImpacts.Single(impact => impact.DefinitionKey == "join");
        var joinSource = Assert.Single(joinImpact.Sources);
        Assert.Equal(
            ["left", "right"],
            joinSource.Causes.OfType<UpstreamDerivedCause>()
                .Select(cause => cause.DefinitionKey).Order(StringComparer.Ordinal));
        Assert.Empty(result.RepairRequests);
    }

    [Fact]
    public void Runtime_DAG_topology_matches_compiled_graph_for_chain_diamond_and_invariant()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value).Named("root");
        var chain = model.Derived(sources).From(root).Select((_, value) => value + 1).Named("chain");
        var left = model.Derived(sources).From(chain).Select((_, value) => value + 2).Named("left");
        var right = model.Derived(sources).From(chain).Select((_, value) => value + 3).Named("right");
        var join = model.Derived(sources).From(left).From(right)
            .Select((_, first, second) => first + second).Named("join");
        model.Invariant(sources).From(join).Must((_, value) => value >= 0).Named("non-negative");
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(join, source);
        source.Value = 2;

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(sources, source, value => value.Value, 1, 2))).Result;

        Assert.Equal(
            ["chain", "join", "left", "right", "root"],
            result.DerivedImpacts.Select(impact => impact.DefinitionKey).Order(StringComparer.Ordinal));
        Assert.Equal("non-negative", Assert.Single(result.InvariantImpacts).DefinitionKey);
    }

    [Fact]
    public void Projected_runtime_edge_uses_compiled_input_metadata()
    {
        var model = new ConsistencyModelBuilder();
        var roots = model.Objects<Root>().Key(root => root.Id);
        var middles = model.Objects<Middle>().Key(middle => middle.Id);
        var rootValue = model.Derived(roots).Select(root => root.Value).Named("root");
        var projected = model.Derived(middles).From(middle => middle.Root, rootValue)
            .Select((_, value) => value + 1).Named("projected");
        var firstRoot = new Root { Value = 1 };
        var secondRoot = new Root { Value = 10 };
        var firstMiddle = new Middle { Root = firstRoot };
        var secondMiddle = new Middle { Root = secondRoot };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(roots, [firstRoot, secondRoot]);
            seed.Add(middles, [firstMiddle, secondMiddle]);
        });
        _ = runtime.Evaluate(projected, firstMiddle);
        _ = runtime.Evaluate(projected, secondMiddle);
        firstRoot.Value = 2;

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(roots, firstRoot, root => root.Value, 1, 2))).Result;

        var impact = result.DerivedImpacts.Single(value => value.DefinitionKey == "projected");
        Assert.Same(firstMiddle, Assert.Single(impact.Sources).Source);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(projected, secondMiddle));
    }

    [Fact]
    public void DebugView_preserves_compiled_dependency_DAG_nodes_order_and_edges()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value);
        var downstream = model.Derived(sources).From(root).Select((_, value) => value + 1);
        model.Invariant(sources).From(downstream).Must((_, value) => value >= 0);

        var debugView = model.Build().DebugView;
        var dagLines = debugView[(debugView.IndexOf("Dependency DAG:", StringComparison.Ordinal))..]
            .Split(Environment.NewLine);

        Assert.Equal("Dependency DAG:", dagLines[0]);
        Assert.Equal(3, dagLines.Count(line => line.TrimStart().StartsWith("[", StringComparison.Ordinal)));
        Assert.Contains(dagLines, line => line.Contains("[0] Derived:", StringComparison.Ordinal));
        Assert.Contains(dagLines, line => line.Contains("[1] Derived:", StringComparison.Ordinal));
        Assert.Contains(dagLines, line => line.Contains("[2] Invariant:", StringComparison.Ordinal));
        Assert.Equal(2, dagLines.Count(line => line.TrimStart().StartsWith("Edge:", StringComparison.Ordinal)));
    }

    private static CompiledDependencyGraph Compile(
        IReadOnlyList<Derived<Source, int>> derived,
        IReadOnlyList<Invariant<Source>>? invariants = null) => CompiledDependencyGraph.Compile(
        derived.Select(value => value.Definition).ToArray(),
        invariants?.Select(value => value.Definition).ToArray() ?? []);

    private static CompiledDependencyGraph CreateDiamondGraph()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value);
        var left = model.Derived(sources).From(root).Select((_, value) => value + 1);
        var right = model.Derived(sources).From(root).Select((_, value) => value + 2);
        var join = model.Derived(sources).From(left).From(right).Select((_, first, second) => first + second);
        return Compile([root, left, right, join]);
    }

    private static CompiledDependencyNode Node<T>(CompiledDependencyGraph graph, Derived<Source, T> derived) =>
        graph.Nodes.Single(node => ReferenceEquals(node.Definition, derived.Definition));

    private static int Position<T>(CompiledDependencyGraph graph, Derived<Source, T> derived) =>
        Node(graph, derived).TopologicalOrder;

    private static int Position(CompiledDependencyGraph graph, Invariant<Source> invariant) =>
        graph.Nodes.Single(node => ReferenceEquals(node.Definition, invariant.Definition)).TopologicalOrder;

    private sealed class Source
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public int Value { get; set; }
    }

    private sealed class Root
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public int Value { get; set; }
    }

    private sealed class Middle
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required Root Root { get; init; }
    }

    private sealed class Leaf
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required Middle Middle { get; init; }
    }
}
