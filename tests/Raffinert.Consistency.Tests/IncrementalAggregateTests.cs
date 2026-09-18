namespace Raffinert.Consistency.Tests;

public sealed partial class DerivedStateTests
{
    [Fact]
    public void Recognized_cardinality_plans_keep_fresh_values_updated_from_membership_deltas()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).From(relation).Count();
        var longCount = model.Derived(sources).From(relation).LongCount();
        var any = model.Derived(sources).From(relation).Any();
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Evaluate(count, source));
        Assert.Equal(0L, runtime.Evaluate(longCount, source));
        Assert.False(runtime.Evaluate(any, source));

        runtime.Add(items, item);

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, source));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(longCount, source));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(any, source));
        Assert.Equal(1, runtime.Evaluate(count, source));
        Assert.Equal(1L, runtime.Evaluate(longCount, source));
        Assert.True(runtime.Evaluate(any, source));
        Assert.Contains("Computation plan: IncrementalCount", compiled.DebugView);
        Assert.Contains("Computation plan: IncrementalLongCount", compiled.DebugView);
        Assert.Contains("Computation plan: IncrementalAny", compiled.DebugView);
    }

    [Fact]
    public void Incremental_sum_updates_from_item_and_membership_deltas()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var total = model.Derived(sources).From(relation).Sum(item => item.Quantity);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = Source("A");
        var first = Item("A", 1m);
        var second = Item("A", 3m);
        runtime.Add(sources, source);
        runtime.Add(items, first);
        Assert.Equal(1m, runtime.Evaluate(total, source));

        first.Quantity = 2m;
        runtime.Apply(Change.Property(items, first, x => x.Quantity, 1m, 2m));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(total, source));
        Assert.Equal(2m, runtime.Evaluate(total, source));

        runtime.Add(items, second);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(total, source));
        Assert.Equal(5m, runtime.Evaluate(total, source));

        runtime.Remove(items, first);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(total, source));
        Assert.Equal(3m, runtime.Evaluate(total, source));
        Assert.Contains("Computation plan: IncrementalSum(DerivedItemRecord.Quantity)", compiled.DebugView);
    }

    [Fact]
    public void Forced_full_recompute_plan_remains_the_semantic_fallback()
    {
        var model = new ConsistencyModelBuilder().UseFullRecomputePlansForTesting();
        var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var total = model.Derived(sources).From(relation).Sum(item => item.Quantity);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = Source("A");
        var item = Item("A", 1m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(1m, runtime.Evaluate(total, source));

        item.Quantity = 2m;
        runtime.Apply(Change.Property(items, item, x => x.Quantity, 1m, 2m));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(total, source));
        Assert.Equal(2m, runtime.Evaluate(total, source));
        Assert.Contains("Computation plan: FullRecompute", compiled.DebugView);
    }

    [Fact]
    public void Incremental_sum_matches_forced_full_recompute_across_random_mutations()
    {
        var optimized = CreateScenario(forceFullRecompute: false);
        var reference = CreateScenario(forceFullRecompute: true);
        var random = new Random(31991);
        var optimizedItems = new List<DerivedItemRecord>();
        var referenceItems = new List<DerivedItemRecord>();

        for (var operation = 0; operation < 150; operation++)
        {
            var choice = optimizedItems.Count == 0 ? 0 : random.Next(4);
            if (choice == 0)
            {
                var id = Guid.NewGuid();
                var code = random.Next(2) == 0 ? "A" : "B";
                var quantity = random.Next(1, 20);
                var optimizedItem = new DerivedItemRecord { Id = id, Code = code, Quantity = quantity };
                var referenceItem = new DerivedItemRecord { Id = id, Code = code, Quantity = quantity };
                optimizedItems.Add(optimizedItem);
                referenceItems.Add(referenceItem);
                optimized.Runtime.Add(optimized.Items, optimizedItem);
                reference.Runtime.Add(reference.Items, referenceItem);
            }
            else
            {
                var index = random.Next(optimizedItems.Count);
                var optimizedItem = optimizedItems[index];
                var referenceItem = referenceItems[index];
                if (choice == 1)
                {
                    optimized.Runtime.Remove(optimized.Items, optimizedItem);
                    reference.Runtime.Remove(reference.Items, referenceItem);
                    optimizedItems.RemoveAt(index);
                    referenceItems.RemoveAt(index);
                }
                else if (choice == 2)
                {
                    var oldQuantity = optimizedItem.Quantity;
                    var newQuantity = random.Next(1, 20);
                    optimizedItem.Quantity = newQuantity;
                    referenceItem.Quantity = newQuantity;
                    optimized.Runtime.Apply(Change.Property(
                        optimized.Items, optimizedItem, x => x.Quantity, oldQuantity, newQuantity));
                    reference.Runtime.Apply(Change.Property(
                        reference.Items, referenceItem, x => x.Quantity, oldQuantity, newQuantity));
                }
                else
                {
                    var oldCode = optimizedItem.Code;
                    var newCode = oldCode == "A" ? "B" : "A";
                    optimizedItem.Code = newCode;
                    referenceItem.Code = newCode;
                    optimized.Runtime.Apply(Change.Property(
                        optimized.Items, optimizedItem, x => x.Code, oldCode, newCode));
                    reference.Runtime.Apply(Change.Property(
                        reference.Items, referenceItem, x => x.Code, oldCode, newCode));
                }
            }

            Assert.Equal(
                reference.Runtime.Evaluate(reference.Total, reference.Source),
                optimized.Runtime.Evaluate(optimized.Total, optimized.Source));
        }

        static IncrementalScenario CreateScenario(bool forceFullRecompute)
        {
            var model = new ConsistencyModelBuilder();
            if (forceFullRecompute)
                model.UseFullRecomputePlansForTesting();
            var sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
            var items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
            var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
            var total = model.Derived(sources).From(relation).Sum(item => item.Quantity);
            var runtime = model.Build().CreateRuntime();
            var source = Source("A");
            runtime.Add(sources, source);
            Assert.Equal(0m, runtime.Evaluate(total, source));
            return new IncrementalScenario(runtime, sources, items, total, source);
        }
    }

}
