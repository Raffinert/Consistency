namespace Raffinert.Relations.Tests;

public sealed class NavigationTouchedStateTests
{
    [Fact]
    public void Preview_retarget_restores_old_reverse_mapping_and_plan_commit_installs_new_mapping()
    {
        var scenario = CreateScalarScenario();
        var oldChild = scenario.Root.Child;
        var newChild = new Child { Value = 2 };
        scenario.Root.Child = newChild;
        var prepared = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Roots, scenario.Root, root => root.Child, oldChild, newChild)));
        _ = scenario.Runtime.Get(scenario.Value, scenario.Root);

        _ = scenario.Runtime.PreviewDetailed(prepared);
        var navigation = typeof(Root).GetProperty(nameof(Root.Child))!;
        Assert.Contains(scenario.Root, scenario.Runtime.GetNavigationOwners(navigation, oldChild));
        Assert.DoesNotContain(scenario.Root, scenario.Runtime.GetNavigationOwners(navigation, newChild));

        var replanned = scenario.Runtime.Prepare(MutationSet.Create(Change.Property(
            scenario.Roots, scenario.Root, root => root.Child, oldChild, newChild)));
        var plan = scenario.Runtime.PlanDetailed(replanned);
        scenario.Runtime.Commit(plan);
        Assert.DoesNotContain(scenario.Root, scenario.Runtime.GetNavigationOwners(navigation, oldChild));
        Assert.Contains(scenario.Root, scenario.Runtime.GetNavigationOwners(navigation, newChild));
    }

    [Fact]
    public void Shared_navigation_owner_reference_count_restores_exactly()
    {
        var model = new RelationModelBuilder();
        var roots = model.Objects<NestedRoot>().Key(root => root.Id);
        var value = model.Derived(roots).Compute(root => root.Container.Child.Value);
        var child = new Child { Value = 1 };
        var container = new Container { Child = child };
        var first = new NestedRoot { Container = container };
        var second = new NestedRoot { Container = container };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(roots, [first, second]));
        _ = runtime.Get(value, first);
        _ = runtime.Get(value, second);

        var removal = runtime.Prepare(MutationSet.Create(Change.Remove(roots, first)));
        _ = runtime.PreviewDetailed(removal);
        runtime.Commit(removal);
        child.Value = 2;

        runtime.Apply(Change.Property(child, value => value.Value, 1, 2));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(value, second));
    }

    [Fact]
    public void Collection_navigation_preview_restores_items_and_reverse_owners()
    {
        var model = new RelationModelBuilder();
        var roots = model.Objects<CollectionRoot>().Key(root => root.Id);
        var total = model.Derived(roots).Compute(root => root.Children.Sum(child => child.Value));
        var existing = new Child { Value = 1 };
        var added = new Child { Value = 2 };
        var root = new CollectionRoot { Children = [existing] };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(roots, [root]));
        _ = runtime.Get(total, root);
        root.Children.Add(added);
        var prepared = runtime.Prepare(MutationSet.Create(Change.CollectionAdd(
            roots, root, value => value.Children, added)));

        _ = runtime.PreviewDetailed(prepared);
        var navigation = typeof(CollectionRoot).GetProperty(nameof(CollectionRoot.Children))!;
        Assert.Contains(root, runtime.GetNavigationOwners(navigation, existing));
        Assert.DoesNotContain(root, runtime.GetNavigationOwners(navigation, added));
    }

    [Fact]
    public void Navigation_patch_size_does_not_scale_with_unrelated_runtime_population()
    {
        var small = CreateNavigationPopulation(10_000);
        var large = CreateNavigationPopulation(100_000);

        var smallCount = small.Runtime.CaptureNavigationPatchEntryCount(small.Roots, small.Touched);
        var largeCount = large.Runtime.CaptureNavigationPatchEntryCount(large.Roots, large.Touched);

        Assert.True(largeCount < smallCount * 2,
            $"Touched navigation patch grew from {smallCount} to {largeCount} entries.");
    }

    private static ScalarScenario CreateScalarScenario()
    {
        var model = new RelationModelBuilder();
        var roots = model.Objects<Root>().Key(root => root.Id);
        var value = model.Derived(roots).Compute(root => root.Child.Value);
        var root = new Root { Child = new Child { Value = 1 } };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(roots, [root]));
        return new ScalarScenario(runtime, roots, root, value);
    }

    private static NavigationPopulation CreateNavigationPopulation(int count)
    {
        var model = new RelationModelBuilder();
        var roots = model.Objects<Root>().Key(root => root.Id);
        model.Derived(roots).Compute(root => root.Child.Value);
        var values = Enumerable.Range(0, count)
            .Select(index => new Root { Child = new Child { Value = index } }).ToArray();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(roots, values));
        return new NavigationPopulation(runtime, roots, values[0]);
    }

    private sealed record ScalarScenario(
        RelationRuntime Runtime,
        ObjectSet<Root> Roots,
        Root Root,
        Derived<Root, int> Value);
    private sealed record NavigationPopulation(RelationRuntime Runtime, ObjectSet<Root> Roots, Root Touched);

    private sealed class Root
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Child Child { get; set; } = null!;
    }

    private sealed class NestedRoot
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Container Container { get; set; } = null!;
    }

    private sealed class CollectionRoot
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<Child> Children { get; set; } = [];
    }

    private sealed class Container
    {
        public Child Child { get; set; } = null!;
    }

    private sealed class Child
    {
        public int Value { get; set; }
    }
}
