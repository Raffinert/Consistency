namespace Raffinert.Consistency.Tests;

public sealed class MixedModelPreparedExecutionTests
{
    [Theory]
    [InlineData(MutationCase.ParentIncrease)]
    [InlineData(MutationCase.ParentDecrease)]
    [InlineData(MutationCase.ItemKeyChange)]
    [InlineData(MutationCase.ItemAdd)]
    [InlineData(MutationCase.ItemRemove)]
    [InlineData(MutationCase.CollectionAdd)]
    [InlineData(MutationCase.CollectionRemove)]
    [InlineData(MutationCase.CollectionReset)]
    [InlineData(MutationCase.LinkRetarget)]
    [InlineData(MutationCase.ParentAndLinkRemove)]
    [InlineData(MutationCase.ConservativeWave)]
    [InlineData(MutationCase.DirtyInvalidFanIn)]
    [InlineData(MutationCase.ScheduleRepair)]
    public void Mixed_model_has_equivalent_results_across_all_execution_modes(MutationCase mutationCase)
    {
        var direct = Execute(mutationCase, ExecutionMode.PreparedCommit);
        var preview = Execute(mutationCase, ExecutionMode.PreviewThenCommit);
        var plan = Execute(mutationCase, ExecutionMode.PlanThenCommit);
        var apply = Execute(mutationCase, ExecutionMode.Apply);

        RuntimeApplyResultAssert.Equivalent(direct.Result, preview.Result);
        RuntimeApplyResultAssert.Equivalent(direct.Result, plan.Result);
        RuntimeApplyResultAssert.Equivalent(direct.Result, apply.Result);
        Assert.Equal(direct.State, preview.State);
        Assert.Equal(direct.State, plan.State);
        Assert.Equal(direct.State, apply.State);
    }

    [Fact]
    public void Fixed_seed_randomized_mixed_model_executes_one_hundred_equivalent_valid_waves()
    {
        var random = new Random(731_993);
        for (var wave = 0; wave < 100; wave++)
        {
            var next = random.Next(0, 30);
            var direct = ExecuteParentValue(next, ExecutionMode.PreparedCommit);
            var preview = ExecuteParentValue(next, ExecutionMode.PreviewThenCommit);
            var plan = ExecuteParentValue(next, ExecutionMode.PlanThenCommit);
            var apply = ExecuteParentValue(next, ExecutionMode.Apply);
            RuntimeApplyResultAssert.Equivalent(direct.Result, preview.Result);
            RuntimeApplyResultAssert.Equivalent(direct.Result, plan.Result);
            RuntimeApplyResultAssert.Equivalent(direct.Result, apply.Result);
            Assert.Equal(direct.State, preview.State);
            Assert.Equal(direct.State, plan.State);
            Assert.Equal(direct.State, apply.State);
        }
    }

    [Fact]
    public void Classifier_failure_restores_prepared_execution_state()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<FailureSource>().Key(source => source.Id);
        var derived = model.Derived(sources)
            .Impact(policy => policy.SourceMemberChanged(
                source => source.Value,
                (_, _) => throw new InjectedFailureException()))
            .Select(source => source.Value);
        var source = new FailureSource { Value = 1 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(derived, source);
        source.Value = 2;
        var prepared = runtime.Prepare(MutationSet.Create(Change.Property(
            sources, source, value => value.Value, 1, 2)));
        var diagnostics = runtime.Diagnostics;

        Assert.Throws<InjectedFailureException>(() => runtime.PlanDetailed(prepared));

        Assert.Equal(0, runtime.Version);
        Assert.Equal(diagnostics, runtime.Diagnostics);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(derived, source));
        Assert.False(prepared.IsCommitted);
        Assert.False(prepared.IsDispatched);
    }

    [Fact]
    public void Incremental_derived_failure_restores_set_relation_cache_and_version()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<FailureSource>().Key(source => source.Id);
        var items = model.Objects<FailureItem>().Key(item => item.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var total = model.Derived(sources).From(relation).Sum(item => item.Quantity);
        var source = new FailureSource { Code = "A" };
        var existing = new FailureItem { Code = "A", StoredQuantity = 1 };
        var failing = new FailureItem { Code = "A", StoredQuantity = 2, ThrowOnRead = true };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(sources, [source]);
            seed.Add(items, [existing]);
        });
        Assert.Equal(1, runtime.Evaluate(total, source));
        var prepared = runtime.Prepare(MutationSet.Create(Change.Add(items, failing)));

        Assert.Throws<InjectedFailureException>(() => runtime.PreviewDetailed(prepared));

        Assert.Equal(0, runtime.Version);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(total, source));
        Assert.True(runtime.HasMaterializedPair(relation, source, existing));
        Assert.False(runtime.HasMaterializedPair(relation, source, failing));
        Assert.False(prepared.IsCommitted);
    }

    [Fact]
    public void Invariant_evaluation_dispatch_failure_keeps_commit_and_allows_retry()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<FailureSource>().Key(source => source.Id);
        var value = model.Derived(sources).Select(source => source.Value);
        model.Invariant(sources).From(value).Must((source, current) => EvaluateInvariant(source, current))
            .ReactWith(InvariantReaction.EvaluateImmediately)
            .AllowIncompleteDependencies();
        var source = new FailureSource { Value = 1, FailEvaluation = true };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, [source]));
        _ = runtime.Evaluate(value, source);
        source.Value = 2;
        var prepared = runtime.Prepare(MutationSet.Create(Change.Property(
            sources, source, item => item.Value, 1, 2)));
        runtime.Commit(prepared);
        Assert.Throws<InjectedFailureException>(() => runtime.Dispatch(prepared));
        Assert.Equal(1, runtime.Version);
        Assert.True(prepared.IsCommitted);
        Assert.False(prepared.IsDispatched);

        source.FailEvaluation = false;
        runtime.Dispatch(prepared);
        Assert.True(prepared.IsDispatched);
    }

    private static Outcome Execute(MutationCase mutationCase, ExecutionMode mode)
    {
        var scenario = CreateScenario();
        var mutations = CreateMutation(scenario, mutationCase);
        return Execute(scenario, mutations, mode);
    }

    private static Outcome ExecuteParentValue(int next, ExecutionMode mode)
    {
        var scenario = CreateScenario();
        var old = scenario.First.Amount;
        scenario.First.Amount = next;
        return Execute(scenario, MutationSet.Create(Change.Property(
            scenario.Parents, scenario.First, parent => parent.Amount, old, next)), mode);
    }

    private static Outcome Execute(Scenario scenario, MutationSet mutations, ExecutionMode mode)
    {
        var result = mode switch
        {
            ExecutionMode.PreparedCommit => scenario.Runtime.CommitDetailed(
                scenario.Runtime.Prepare(mutations), RuntimeImpactDetailLevel.Causal),
            ExecutionMode.PreviewThenCommit => PreviewThenCommit(),
            ExecutionMode.PlanThenCommit => PlanThenCommit(),
            ExecutionMode.Apply => scenario.Runtime.ApplyDetailed(
                mutations, RuntimeImpactDetailLevel.Causal).Result,
            _ => throw new InvalidOperationException()
        };
        var state = string.Join('|',
            scenario.Runtime.Version,
            ReadState(() => scenario.Runtime.GetState(scenario.ExactCount, scenario.First)),
            ReadState(() => scenario.Runtime.GetState(scenario.FinalChain, scenario.First)),
            ReadState(() => scenario.Runtime.GetState(scenario.Diamond, scenario.First)),
            ReadState(() => scenario.Runtime.GetState(scenario.Invariant, scenario.First)),
            scenario.Runtime.Diagnostics.RelationPairsAdded,
            scenario.Runtime.Diagnostics.RelationPairsRemoved);
        return new Outcome(result, state);

        static string ReadState<T>(Func<T> read)
        {
            try
            {
                var value = read();
                return value is null ? "null" : value.ToString()!;
            }
            catch (InvalidOperationException) { return "removed"; }
        }

        RuntimeApplyResult PreviewThenCommit()
        {
            var prepared = scenario.Runtime.Prepare(mutations);
            _ = scenario.Runtime.PreviewDetailed(prepared, RuntimeImpactDetailLevel.Causal);
            return scenario.Runtime.CommitDetailed(prepared, RuntimeImpactDetailLevel.Causal);
        }

        RuntimeApplyResult PlanThenCommit()
        {
            var prepared = scenario.Runtime.Prepare(mutations);
            var plan = scenario.Runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal);
            return scenario.Runtime.Commit(plan);
        }
    }

    private static MutationSet CreateMutation(Scenario scenario, MutationCase mutationCase)
    {
        switch (mutationCase)
        {
            case MutationCase.ParentIncrease:
            case MutationCase.ScheduleRepair:
                scenario.First.Amount = mutationCase == MutationCase.ScheduleRepair ? 30 : 12;
                return MutationSet.Create(Change.Property(
                    scenario.Parents, scenario.First, parent => parent.Amount, 10, scenario.First.Amount));
            case MutationCase.ParentDecrease:
                scenario.First.Amount = 8;
                return MutationSet.Create(Change.Property(
                    scenario.Parents, scenario.First, parent => parent.Amount, 10, 8));
            case MutationCase.ItemKeyChange:
                scenario.FirstItem.Code = "B";
                return MutationSet.Create(Change.Property(
                    scenario.Items, scenario.FirstItem, item => item.Code, "A", "B"));
            case MutationCase.ItemAdd:
                return MutationSet.Create(Change.Add(scenario.Items,
                    new Item { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000099"), Code = "A", Value = 3 }));
            case MutationCase.ItemRemove:
                return MutationSet.Create(Change.Remove(scenario.Items, scenario.FirstItem));
            case MutationCase.CollectionAdd:
                var added = new Tag
                {
                    Id = Guid.Parse("50000000-0000-0000-0000-000000000003"),
                    Value = 3
                };
                scenario.First.Tags.Add(added);
                return MutationSet.Create(Change.CollectionAdd(
                    scenario.Parents, scenario.First, parent => parent.Tags, added));
            case MutationCase.CollectionRemove:
                var removed = scenario.First.Tags[0];
                scenario.First.Tags.Remove(removed);
                return MutationSet.Create(Change.CollectionRemove(
                    scenario.Parents, scenario.First, parent => parent.Tags, removed));
            case MutationCase.CollectionReset:
                scenario.First.Tags.Reverse();
                return MutationSet.Create(Change.CollectionReset(
                    scenario.Parents, scenario.First, parent => parent.Tags));
            case MutationCase.LinkRetarget:
                scenario.Link.Parent = scenario.Second;
                return MutationSet.Create(Change.Property(
                    scenario.Links, scenario.Link, link => link.Parent, scenario.First, scenario.Second));
            case MutationCase.ParentAndLinkRemove:
                return MutationSet.Create(
                    Change.Remove(scenario.Links, scenario.Link),
                    Change.Remove(scenario.Parents, scenario.First));
            case MutationCase.ConservativeWave:
                scenario.FirstConservative.Code = "B";
                scenario.SecondConservative.Code = "A";
                return MutationSet.Create(
                    Change.Property(scenario.ConservativeItems, scenario.FirstConservative,
                        item => item.Code, "A", "B"),
                    Change.Property(scenario.ConservativeItems, scenario.SecondConservative,
                        item => item.Code, "B", "A"));
            case MutationCase.DirtyInvalidFanIn:
                scenario.First.Amount = 8;
                scenario.First.Reserved = 4;
                return MutationSet.Create(
                    Change.Property(scenario.Parents, scenario.First, parent => parent.Amount, 10, 8),
                    Change.Property(scenario.Parents, scenario.First, parent => parent.Reserved, 2, 4));
            default:
                throw new InvalidOperationException();
        }
    }

    private static Scenario CreateScenario()
    {
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<Parent>().Named("parents").Key(parent => parent.Id);
        var items = model.Objects<Item>().Named("items").Key(item => item.Id);
        var conservativeItems = model.Objects<ConservativeItem>().Named("conservative-items")
            .Key(item => item.Id);
        var links = model.Objects<Link>().Named("links").Key(link => link.Id);
        var exact = model.Relation(parents, items).Where((parent, item) => parent.Code == item.Code)
            .Named("exact");
        var conservative = model.Relation(parents, conservativeItems)
            .Where((parent, item) => parent.Code == item.Code).Named("conservative");
        var amount = model.Derived(parents)
            .Impact(policy => policy.SourceMemberChanged(parent => parent.Amount, (oldValue, newValue) =>
                newValue < oldValue ? DependencySeverity.Invalid : DependencySeverity.Dirty))
            .Select(parent => parent.Amount).Named("amount");
        var reserved = model.Derived(parents).Select(parent => parent.Reserved).Named("reserved");
        var exactCount = model.Derived(parents).From(exact).Count().Named("exact-count");
        model.Derived(parents).From(conservative).PreferConservativePropagation()
            .Select((_, matches) => matches.Count).Named("conservative-count");
        model.Derived(parents).Select(parent => parent.Tags.Sum(tag => tag.Value)).Named("tag-total");
        var chainB = model.Derived(parents).From(amount).Select((_, value) => value + 1).Named("chain-b");
        var finalChain = model.Derived(parents).From(chainB).Select((_, value) => value * 2).Named("chain-c");
        var diamondLeft = model.Derived(parents).From(amount).Select((_, value) => value + 2).Named("diamond-b");
        var diamondRight = model.Derived(parents).From(amount).Select((_, value) => value + 3).Named("diamond-c");
        var diamond = model.Derived(parents).From(diamondLeft).From(diamondRight)
            .Select((_, left, right) => left + right).Named("diamond-d");
        model.Derived(links).From(link => link.Parent, amount)
            .Select((_, value) => value).Named("projected-one");
        model.Derived(links).From(link => link.Parent, amount, reserved)
            .Select((_, available, used) => available - used).Named("projected-two");
        var invariant = model.Invariant(parents).From(finalChain).From(exactCount)
            .Must((_, chain, count) => chain + count < 25).Named("alpha-invariant")
            .ScheduleRepairWith(_ => { });

        var first = new Parent
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Code = "A",
            Amount = 10,
            Reserved = 2,
            Tags =
            [
                new Tag { Id = Guid.Parse("50000000-0000-0000-0000-000000000001"), Value = 1 },
                new Tag { Id = Guid.Parse("50000000-0000-0000-0000-000000000002"), Value = 2 }
            ]
        };
        var second = new Parent
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
            Code = "B",
            Amount = 6,
            Reserved = 1
        };
        var firstItem = new Item
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Code = "A",
            Value = 2
        };
        var firstConservative = new ConservativeItem
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Code = "A"
        };
        var secondConservative = new ConservativeItem
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000002"),
            Code = "B"
        };
        var link = new Link { Id = Guid.Parse("40000000-0000-0000-0000-000000000001"), Parent = first };
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(parents, [first, second]);
            seed.Add(items, [firstItem]);
            seed.Add(conservativeItems, [firstConservative, secondConservative]);
            seed.Add(links, [link]);
        });
        _ = runtime.Evaluate(exactCount, first);
        _ = runtime.Evaluate(finalChain, first);
        _ = runtime.Evaluate(diamond, first);
        _ = runtime.Evaluate(invariant, first);
        return new Scenario(runtime, parents, items, conservativeItems, links, first, second, firstItem,
            firstConservative, secondConservative, link, exactCount, finalChain, diamond, invariant);
    }

    public enum MutationCase
    {
        ParentIncrease, ParentDecrease, ItemKeyChange, ItemAdd, ItemRemove, CollectionAdd,
        CollectionRemove, CollectionReset, LinkRetarget, ParentAndLinkRemove, ConservativeWave,
        DirtyInvalidFanIn, ScheduleRepair
    }

    private enum ExecutionMode { PreparedCommit, PreviewThenCommit, PlanThenCommit, Apply }
    private sealed record Outcome(RuntimeApplyResult Result, string State);
    private sealed record Scenario(
        ConsistencyRuntime Runtime, ObjectSet<Parent> Parents, ObjectSet<Item> Items,
        ObjectSet<ConservativeItem> ConservativeItems, ObjectSet<Link> Links,
        Parent First, Parent Second, Item FirstItem, ConservativeItem FirstConservative,
        ConservativeItem SecondConservative, Link Link, Derived<Parent, int> ExactCount,
        Derived<Parent, int> FinalChain, Derived<Parent, int> Diamond, Invariant<Parent> Invariant);

    private sealed class Parent
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public int Amount { get; set; }
        public int Reserved { get; set; }
        public List<Tag> Tags { get; set; } = [];
    }

    private sealed class Item
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public int Value { get; set; }
    }

    private sealed class ConservativeItem
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
    }

    private sealed class Link
    {
        public Guid Id { get; set; }
        public Parent Parent { get; set; } = null!;
    }

    private sealed class Tag
    {
        public Guid Id { get; set; }
        public int Value { get; set; }
    }

    private sealed class FailureSource
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
        public int Value { get; set; }
        public bool FailEvaluation { get; set; }
    }

    private sealed class FailureItem
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Code { get; set; } = "";
        public int StoredQuantity { get; set; }
        public bool ThrowOnRead { get; set; }
        public int Quantity => ThrowOnRead ? throw new InjectedFailureException() : StoredQuantity;
    }

    private sealed class InjectedFailureException : Exception;

    private static bool EvaluateInvariant(FailureSource source, int value) => source.FailEvaluation
        ? throw new InjectedFailureException()
        : value >= 0;
}
