namespace Raffinert.Consistency.Tests;

public sealed class DependencyDagCaptureStateTests
{
    [Fact]
    public void Capture_state_propagates_deep_chain_in_one_topological_pass()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var chain = CreateChain(model, sources, depth: 12);
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(chain[^1], source);
        var prepared = ChangeValue(runtime, sources, source, 2);

        var count = runtime.CaptureDependencyPatchEntryCount(prepared);

        Assert.Equal(12, count);
    }

    [Fact]
    public void Capture_state_propagates_diamond_sources_without_duplicate_semantics()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value);
        var left = model.Derived(sources).From(root).Select((_, value) => value + 1);
        var right = model.Derived(sources).From(root).Select((_, value) => value + 2);
        var join = model.Derived(sources).From(left).From(right).Select((_, first, second) => first + second);
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(join, source);
        var prepared = ChangeValue(runtime, sources, source, 2);

        var count = runtime.CaptureDependencyPatchEntryCount(prepared);

        Assert.Equal(4, count);
    }

    [Fact]
    public void Capture_state_propagates_projected_sources_across_sets()
    {
        var model = new ConsistencyModelBuilder();
        var roots = model.Objects<Root>().Key(root => root.Id);
        var middles = model.Objects<Middle>().Key(middle => middle.Id);
        var leaves = model.Objects<Leaf>().Key(leaf => leaf.Id);
        var rootValue = model.Derived(roots).Select(root => root.Value);
        var middleValue = model.Derived(middles).From(middle => middle.Root, rootValue)
            .Select((_, value) => value + 1);
        var leafValue = model.Derived(leaves).From(leaf => leaf.Middle, middleValue)
            .Select((_, value) => value + 1);
        var root = new Root { Value = 1 };
        var middle = new Middle { Root = root };
        var leaf = new Leaf { Middle = middle };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(roots, [root]);
            seed.Add(middles, [middle]);
            seed.Add(leaves, [leaf]);
        });
        _ = runtime.Evaluate(leafValue, leaf);
        root.Value = 2;
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(roots, root, value => value.Value, 1, 2)));

        var count = runtime.CaptureDependencyPatchEntryCount(prepared);

        Assert.Equal(3, count);
    }

    [Fact]
    public void Capture_state_merges_direct_and_inherited_sources_for_same_downstream_node()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var root = model.Derived(sources).Select(source => source.Value);
        var downstream = model.Derived(sources).From(root)
            .Select((source, value) => value + source.Offset);
        var source = new Source { Value = 1, Offset = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(downstream, source);
        source.Value = 2;
        source.Offset = 2;
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(sources, source, value => value.Value, 1, 2),
            Change.Property(sources, source, value => value.Offset, 1, 2)));

        var count = runtime.CaptureDependencyPatchEntryCount(prepared);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Capture_state_preserves_previous_snapshot_sources_through_downstream_chain()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var chain = CreateChain(model, sources, depth: 6);
        var source = new Source { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(chain[^1], source);
        var prepared = ChangeValue(runtime, sources, source, 2);

        var plan = runtime.PlanDetailed(prepared);
        var scope = runtime.GetForwardPatchScopeCounts(plan);
        runtime.Commit(plan);

        Assert.Equal(6, scope.DependencyEntries);
        Assert.All(chain, derived =>
            Assert.Equal(DerivedValueState.Dirty, runtime.GetState(derived, source)));
    }

    private static List<Derived<Source, int>> CreateChain(
        ConsistencyModelBuilder model,
        ObjectSet<Source> sources,
        int depth)
    {
        var chain = new List<Derived<Source, int>>
        {
            model.Derived(sources).Select(source => source.Value)
        };
        for (var index = 1; index < depth; index++)
        {
            var upstream = chain[^1];
            chain.Add(model.Derived(sources).From(upstream).Select((_, value) => value + 1));
        }
        return chain;
    }

    private static PreparedMutation ChangeValue(
        ConsistencyRuntime runtime,
        ObjectSet<Source> sources,
        Source source,
        int value)
    {
        var previous = source.Value;
        source.Value = value;
        return runtime.Prepare(MutationSet.Create(
            Change.Property(sources, source, current => current.Value, previous, value)));
    }

    private sealed class Source
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
        public int Offset { get; set; }
    }

    private sealed class Root
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Value { get; set; }
    }

    private sealed class Middle
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required Root Root { get; init; }
    }

    private sealed class Leaf
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required Middle Middle { get; init; }
    }
}
