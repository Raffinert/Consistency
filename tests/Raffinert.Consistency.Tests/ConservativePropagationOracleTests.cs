namespace Raffinert.Consistency.Tests;

public sealed class ConservativePropagationOracleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lifecycle_key_residual_item_and_mixed_changes_converge_to_oracle(bool forceScan)
    {
        var exact = World.Create(conservative: false, forceScan);
        var conservative = World.Create(conservative: true, forceScan);
        var sourceSpecs = new[]
        {
            (Guid.NewGuid(), "A", true),
            (Guid.NewGuid(), "B", true),
            (Guid.NewGuid(), "C", true)
        };
        foreach (var spec in sourceSpecs)
        {
            exact.AddSource(spec.Item1, spec.Item2, spec.Item3);
            conservative.AddSource(spec.Item1, spec.Item2, spec.Item3);
        }
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        exact.AddItem(firstId, "A", true, 2);
        conservative.AddItem(firstId, "A", true, 2);
        exact.AddItem(secondId, "B", false, 5);
        conservative.AddItem(secondId, "B", false, 5);
        AssertWorlds(exact, conservative);

        // Item-only aggregate dependency.
        exact.ChangeAmount(firstId, 2, 3);
        conservative.ChangeAmount(firstId, 2, 3);
        AssertNotFresh(exact, conservative, sourceSpecs[0].Item1);
        AssertWorlds(exact, conservative);

        // Residual predicate dependency.
        exact.ChangeItemActive(secondId, false, true);
        conservative.ChangeItemActive(secondId, false, true);
        AssertNotFresh(exact, conservative, sourceSpecs[1].Item1);
        AssertWorlds(exact, conservative);

        // Both old and new key buckets are required.
        exact.ChangeItemCode(firstId, "A", "B");
        conservative.ChangeItemCode(firstId, "A", "B");
        AssertNotFresh(exact, conservative, sourceSpecs[0].Item1, sourceSpecs[1].Item1);
        AssertWorlds(exact, conservative);

        // A left change is source-scoped.
        exact.ChangeSourceCode(sourceSpecs[2].Item1, "C", "B");
        conservative.ChangeSourceCode(sourceSpecs[2].Item1, "C", "B");
        AssertNotFresh(exact, conservative, sourceSpecs[2].Item1);
        AssertWorlds(exact, conservative);

        var addedId = Guid.NewGuid();
        exact.AddItem(addedId, "B", true, 7);
        conservative.AddItem(addedId, "B", true, 7);
        AssertNotFresh(exact, conservative, sourceSpecs[1].Item1, sourceSpecs[2].Item1);
        AssertWorlds(exact, conservative);

        exact.RemoveItem(secondId);
        conservative.RemoveItem(secondId);
        AssertNotFresh(exact, conservative, sourceSpecs[1].Item1, sourceSpecs[2].Item1);
        AssertWorlds(exact, conservative);

        // Multiple right-side operations in one wave.
        var batchId = Guid.NewGuid();
        exact.ApplyMixedBatch(addedId, batchId);
        conservative.ApplyMixedBatch(addedId, batchId);
        AssertNotFresh(exact, conservative, sourceSpecs[0].Item1, sourceSpecs[1].Item1, sourceSpecs[2].Item1);
        AssertWorlds(exact, conservative);

        exact.RemoveSource(sourceSpecs[2].Item1);
        conservative.RemoveSource(sourceSpecs[2].Item1);
        AssertWorlds(exact, conservative);
        Assert.Equal(2, exact.Runtime.DerivedStateEntryCount);
        Assert.Equal(2, conservative.Runtime.DerivedStateEntryCount);
        Assert.Equal(0, conservative.Runtime.MaterializedRelationPairCount);
    }

    [Fact]
    public void Composite_and_ordinal_ignore_case_keys_route_old_and_new_candidates()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<Entity>().Key(value => value.Id);
        var items = model.Objects<Entity>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            source.Region == item.Region &&
            string.Equals(source.Code, item.Code, StringComparison.OrdinalIgnoreCase));
        var count = model.Derived(sources).Using(relation).PreferConservativePropagation()
            .Compute((_, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime();
        var oldCandidate = new Entity { Id = Guid.NewGuid(), Region = 1, Code = "alpha" };
        var newCandidate = new Entity { Id = Guid.NewGuid(), Region = 2, Code = "BETA" };
        var unrelated = new Entity { Id = Guid.NewGuid(), Region = 3, Code = "gamma" };
        var item = new Entity { Id = Guid.NewGuid(), Region = 1, Code = "ALPHA" };
        runtime.Apply(MutationSet.Create(
            Change.Add(sources, oldCandidate), Change.Add(sources, newCandidate),
            Change.Add(sources, unrelated), Change.Add(items, item)));
        foreach (var source in new[] { oldCandidate, newCandidate, unrelated })
            _ = runtime.Get(count, source);
        item.Region = 2;
        item.Code = "beta";

        runtime.Apply(MutationSet.Create(
            Change.Property(items, item, value => value.Code, "ALPHA", "beta"),
            Change.Property(items, item, value => value.Region, 1, 2)));

        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, oldCandidate));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(count, newCandidate));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(count, unrelated));
        Assert.Equal(0, runtime.Get(count, oldCandidate));
        Assert.Equal(1, runtime.Get(count, newCandidate));
        Assert.Equal(0, runtime.MaterializedRelationPairCount);
    }

    private static void AssertNotFresh(World exact, World conservative, params Guid[] sourceIds)
    {
        foreach (var id in sourceIds)
        {
            Assert.NotEqual(DerivedValueState.Fresh, exact.State(id));
            Assert.NotEqual(DerivedValueState.Fresh, conservative.State(id));
        }
    }

    private static void AssertWorlds(World exact, World conservative)
    {
        var expected = exact.Oracle();
        Assert.Equal(expected, exact.Values());
        Assert.Equal(expected, conservative.Values());
        Assert.Equal(0, conservative.Runtime.MaterializedRelationPairCount);
    }

    private sealed class World
    {
        private readonly ObjectSet<Entity> _sources;
        private readonly ObjectSet<Entity> _items;
        private readonly Derived<Entity, int> _total;
        private readonly Dictionary<Guid, Entity> _sourceById = [];
        private readonly Dictionary<Guid, Entity> _itemById = [];

        private World(RelationRuntime runtime, ObjectSet<Entity> sources, ObjectSet<Entity> items,
            Derived<Entity, int> total)
        {
            Runtime = runtime;
            _sources = sources;
            _items = items;
            _total = total;
        }

        public RelationRuntime Runtime { get; }

        public static World Create(bool conservative, bool forceScan)
        {
            var model = new RelationModelBuilder();
            if (forceScan)
                model.UseScanPlansForTesting();
            var sources = model.Objects<Entity>().Key(value => value.Id);
            var items = model.Objects<Entity>().Key(value => value.Id);
            var relation = model.Relation(sources, items).Where((source, item) =>
                source.Code == item.Code && source.Active && item.Active);
            var builder = model.Derived(sources).Using(relation);
            if (conservative)
                builder.PreferConservativePropagation();
            var total = builder.Compute((_, matches) => matches.Sum(item => item.Amount));
            return new World(model.Build().CreateRuntime(), sources, items, total);
        }

        public void AddSource(Guid id, string code, bool active)
        {
            var source = new Entity { Id = id, Code = code, Active = active };
            _sourceById.Add(id, source);
            Runtime.Add(_sources, source);
        }

        public void RemoveSource(Guid id)
        {
            Runtime.Remove(_sources, _sourceById[id]);
            _sourceById.Remove(id);
        }

        public void AddItem(Guid id, string code, bool active, int amount)
        {
            var item = new Entity { Id = id, Code = code, Active = active, Amount = amount };
            _itemById.Add(id, item);
            Runtime.Add(_items, item);
        }

        public void RemoveItem(Guid id)
        {
            Runtime.Remove(_items, _itemById[id]);
            _itemById.Remove(id);
        }

        public void ChangeAmount(Guid id, int oldValue, int newValue)
        {
            var item = _itemById[id];
            item.Amount = newValue;
            Runtime.Apply(Change.Property(_items, item, value => value.Amount, oldValue, newValue));
        }

        public void ChangeItemActive(Guid id, bool oldValue, bool newValue)
        {
            var item = _itemById[id];
            item.Active = newValue;
            Runtime.Apply(Change.Property(_items, item, value => value.Active, oldValue, newValue));
        }

        public void ChangeItemCode(Guid id, string oldValue, string newValue)
        {
            var item = _itemById[id];
            item.Code = newValue;
            Runtime.Apply(Change.Property(_items, item, value => value.Code, oldValue, newValue));
        }

        public void ChangeSourceCode(Guid id, string oldValue, string newValue)
        {
            var source = _sourceById[id];
            source.Code = newValue;
            Runtime.Apply(Change.Property(_sources, source, value => value.Code, oldValue, newValue));
        }

        public void ApplyMixedBatch(Guid removedId, Guid addedId)
        {
            var removed = _itemById[removedId];
            var added = new Entity { Id = addedId, Code = "A", Active = true, Amount = 11 };
            var changed = _itemById.Values.Single(item => item.Id != removedId);
            var oldAmount = changed.Amount;
            changed.Amount++;
            _itemById.Remove(removedId);
            _itemById.Add(addedId, added);
            Runtime.Apply(MutationSet.Create(
                Change.Remove(_items, removed),
                Change.Add(_items, added),
                Change.Property(_items, changed, value => value.Amount, oldAmount, changed.Amount)));
        }

        public DerivedValueState State(Guid id) => Runtime.GetState(_total, _sourceById[id]);

        public int[] Values() => _sourceById.Values.OrderBy(value => value.Id)
            .Select(source => Runtime.Get(_total, source)).ToArray();

        public int[] Oracle() => _sourceById.Values.OrderBy(value => value.Id)
            .Select(source => _itemById.Values
                .Where(item => source.Code == item.Code && source.Active && item.Active)
                .Sum(item => item.Amount))
            .ToArray();
    }

    private sealed class Entity
    {
        public Guid Id { get; init; }
        public int Region { get; set; }
        public string Code { get; set; } = "";
        public bool Active { get; set; }
        public int Amount { get; set; }
    }
}
