using Xunit.Sdk;

namespace Raffinert.Relations.Tests;

public sealed class RandomizedDagPropagationTests
{
    [Theory]
    [InlineData(1729)]
    [InlineData(18473)]
    [InlineData(8675309)]
    public void Exact_conservative_hash_and_scan_DAGs_match_plain_oracle(int seed)
    {
        var random = new Random(seed);
        var trace = new List<string>();
        var scenarios = new[]
        {
            Scenario.Create(conservative: false, forceScan: false),
            Scenario.Create(conservative: false, forceScan: true),
            Scenario.Create(conservative: true, forceScan: false),
            Scenario.Create(conservative: true, forceScan: true)
        };
        var sources = Enumerable.Range(0, 5).Select(NewSource).ToList();
        var items = Enumerable.Range(0, 7).Select(NewItem).ToList();
        foreach (var scenario in scenarios)
        {
            scenario.Runtime.Apply(MutationSet.Create(sources.Select(source => Change.Add(scenario.Sources, source))
                .Concat(items.Select(item => Change.Add(scenario.Items, item))).ToArray()));
        }
        Verify();

        for (var operation = 0; operation < 180; operation++)
        {
            var before = Oracle();
            try
            {
                ApplyRandomOperation();
                var after = Oracle();
                AssertChangedValuesAreNotFresh(before, after);
                Verify();
            }
            catch (Exception exception)
            {
                throw new XunitException(
                    $"Randomized DAG propagation failure. Seed={seed}, operation={operation}." +
                    Environment.NewLine + string.Join(Environment.NewLine, trace.TakeLast(40)) +
                    Environment.NewLine + exception);
            }
        }

        void ApplyRandomOperation()
        {
            var choice = random.Next(11);
            if (sources.Count == 0)
                choice = 0;
            if (items.Count == 0 && choice >= 5)
                choice = 5;
            switch (choice)
            {
                case 0:
                    {
                        var source = NewSource(random.Next());
                        sources.Add(source);
                        trace.Add($"add source {source.Id}");
                        Apply(scenario => [Change.Add(scenario.Sources, source)]);
                        break;
                    }
                case 1:
                    {
                        var index = random.Next(sources.Count);
                        var source = sources[index];
                        sources.RemoveAt(index);
                        trace.Add($"remove source {source.Id}");
                        Apply(scenario => [Change.Remove(scenario.Sources, source)]);
                        break;
                    }
                case 2:
                    {
                        var source = Pick(sources);
                        var old = source.Adjustment;
                        source.Adjustment = random.Next(0, 8);
                        trace.Add($"source adjustment {source.Id} {old}->{source.Adjustment}");
                        Apply(scenario =>
                            [Change.Property(scenario.Sources, source, value => value.Adjustment, old, source.Adjustment)]);
                        break;
                    }
                case 3:
                    {
                        var source = Pick(sources);
                        var old = source.Code;
                        source.Code = RandomCode();
                        trace.Add($"source code {source.Id} {old}->{source.Code}");
                        Apply(scenario => [Change.Property(scenario.Sources, source, value => value.Code, old, source.Code)]);
                        break;
                    }
                case 4:
                    {
                        var source = Pick(sources);
                        var old = source.Policy!.Maximum;
                        source.Policy.Maximum = random.Next(4, 24);
                        trace.Add($"policy maximum {source.Id} {old}->{source.Policy.Maximum}");
                        Apply(_ => [Change.Property(source.Policy, value => value.Maximum, old, source.Policy.Maximum)]);
                        break;
                    }
                case 5:
                    {
                        var item = NewItem(random.Next());
                        items.Add(item);
                        trace.Add($"add item {item.Id}");
                        Apply(scenario => [Change.Add(scenario.Items, item)]);
                        break;
                    }
                case 6:
                    {
                        var index = random.Next(items.Count);
                        var item = items[index];
                        items.RemoveAt(index);
                        trace.Add($"remove item {item.Id}");
                        Apply(scenario => [Change.Remove(scenario.Items, item)]);
                        break;
                    }
                case 7:
                    {
                        var item = Pick(items);
                        var old = item.Details!.Code;
                        item.Details.Code = RandomCode();
                        trace.Add($"item code {item.Id} {old}->{item.Details.Code}");
                        Apply(_ => [Change.Property(item.Details, value => value.Code, old, item.Details.Code)]);
                        break;
                    }
                case 8:
                    {
                        var item = Pick(items);
                        var old = item.Details!.Quantity;
                        item.Details.Quantity = random.Next(1, 12);
                        trace.Add($"item quantity {item.Id} {old}->{item.Details.Quantity}");
                        Apply(_ => [Change.Property(item.Details, value => value.Quantity, old, item.Details.Quantity)]);
                        break;
                    }
                case 9:
                    {
                        var item = Pick(items);
                        var old = item.Enabled;
                        item.Enabled = !old;
                        trace.Add($"item enabled {item.Id} {old}->{item.Enabled}");
                        Apply(scenario => [Change.Property(scenario.Items, item, value => value.Enabled, old, item.Enabled)]);
                        break;
                    }
                default:
                    {
                        var source = Pick(sources);
                        var item = Pick(items);
                        var oldAdjustment = source.Adjustment;
                        var oldQuantity = item.Details!.Quantity;
                        source.Adjustment = random.Next(0, 8);
                        item.Details.Quantity = random.Next(1, 12);
                        trace.Add($"batch source {source.Id} adjustment and item {item.Id} quantity");
                        Apply(scenario =>
                        [
                            Change.Property(scenario.Sources, source, value => value.Adjustment,
                            oldAdjustment, source.Adjustment),
                        Change.Property(item.Details, value => value.Quantity, oldQuantity, item.Details.Quantity)
                        ]);
                        break;
                    }
            }
        }

        void Apply(Func<Scenario, RuntimeMutation[]> createMutations)
        {
            foreach (var scenario in scenarios)
            {
                var application = scenario.Runtime.ApplyDetailed(MutationSet.Create(createMutations(scenario)));
                Assert.Equal(
                    application.Result.RepairRequests.Count,
                    application.Result.RepairRequests
                        .Select(request => (request.InvariantId, ((DagSource)request.Source).Id))
                        .Distinct().Count());
            }
        }

        void AssertChangedValuesAreNotFresh(
            IReadOnlyDictionary<Guid, OracleValues> before,
            IReadOnlyDictionary<Guid, OracleValues> after)
        {
            foreach (var source in sources)
            {
                if (!before.TryGetValue(source.Id, out var oldValues))
                    continue;
                var newValues = after[source.Id];
                foreach (var scenario in scenarios)
                {
                    if (oldValues.A != newValues.A)
                    {
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.A, source));
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.C, source));
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.E, source));
                    }
                    if (oldValues.B != newValues.B)
                    {
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.B, source));
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.C, source));
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.E, source));
                    }
                    if (oldValues.D != newValues.D)
                    {
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.D, source));
                        Assert.NotEqual(DerivedValueState.Fresh, scenario.Runtime.GetState(scenario.E, source));
                    }
                }
            }
        }

        void Verify()
        {
            var oracle = Oracle();
            foreach (var scenario in scenarios)
            {
                foreach (var source in sources)
                {
                    var expected = oracle[source.Id];
                    Assert.Equal(expected.A, scenario.Runtime.Get(scenario.A, source));
                    Assert.Equal(expected.B, scenario.Runtime.Get(scenario.B, source));
                    Assert.Equal(expected.C, scenario.Runtime.Get(scenario.C, source));
                    Assert.Equal(expected.D, scenario.Runtime.Get(scenario.D, source));
                    Assert.Equal(expected.E, scenario.Runtime.Get(scenario.E, source));
                    Assert.Equal(expected.Invariant, scenario.Runtime.Evaluate(scenario.Invariant, source));
                }
                Assert.Equal(sources.Count * 5, scenario.Runtime.DerivedStateEntryCount);
                if (scenario.Conservative)
                    Assert.Equal(0, scenario.Runtime.MaterializedRelationPairCount);
            }
        }

        Dictionary<Guid, OracleValues> Oracle() => sources.ToDictionary(source => source.Id, source =>
        {
            var a = source.Adjustment;
            var b = items.Where(item => item.Enabled && item.Details!.Code == source.Code)
                .Sum(item => item.Details!.Quantity);
            var c = a + b;
            var d = source.Policy!.Maximum;
            var e = c - d;
            return new OracleValues(a, b, c, d, e, e <= 0 && c >= 0);
        });

        string RandomCode() => ((char)('A' + random.Next(4))).ToString();
        DagSource NewSource(int index) => new()
        {
            Id = Guid.NewGuid(),
            Code = ((char)('A' + (index & int.MaxValue) % 4)).ToString(),
            Adjustment = (index & int.MaxValue) % 5,
            Policy = new DagPolicy { Maximum = 14 }
        };
        DagItem NewItem(int index) => new()
        {
            Id = Guid.NewGuid(),
            Enabled = index % 3 != 0,
            Details = new DagItemDetails
            {
                Code = ((char)('A' + (index & int.MaxValue) % 4)).ToString(),
                Quantity = (index & int.MaxValue) % 7 + 1
            }
        };
        T Pick<T>(IReadOnlyList<T> values) => values[random.Next(values.Count)];
    }

    private sealed record OracleValues(int A, int B, int C, int D, int E, bool Invariant);

    private sealed record Scenario(
        RelationRuntime Runtime,
        ObjectSet<DagSource> Sources,
        ObjectSet<DagItem> Items,
        Derived<DagSource, int> A,
        Derived<DagSource, int> B,
        Derived<DagSource, int> C,
        Derived<DagSource, int> D,
        Derived<DagSource, int> E,
        Invariant<DagSource> Invariant,
        bool Conservative)
    {
        public static Scenario Create(bool conservative, bool forceScan)
        {
            var model = new RelationModelBuilder();
            if (forceScan)
                model.UseScanPlansForTesting();
            var sources = model.Objects<DagSource>().Key(value => value.Id);
            var items = model.Objects<DagItem>().Key(value => value.Id);
            var relation = model.Relation(sources, items).Where((source, item) =>
                item.Details != null && source.Code == item.Details.Code && item.Enabled);
            var a = model.Derived(sources).Compute(source => source.Adjustment);
            var bBuilder = model.Derived(sources).Using(relation);
            if (conservative)
                bBuilder.PreferConservativePropagation();
            var b = bBuilder.Compute((_, matches) => matches.Sum(item => item.Details!.Quantity));
            var c = model.Derived(sources).Using(a, b).Compute((_, left, right) => left + right);
            var d = model.Derived(sources).Compute(source => source.Policy!.Maximum);
            var e = model.Derived(sources).Using(c, d).Compute((_, left, right) => left - right);
            var invariant = model.Invariant(sources).Using(e, c)
                .Must((_, balance, total) => balance <= 0 && total >= 0)
                .ScheduleRepairWith(_ => { });
            return new Scenario(model.Build().CreateRuntime(), sources, items, a, b, c, d, e,
                invariant, conservative);
        }
    }

    private sealed class DagSource
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
        public int Adjustment { get; set; }
        public DagPolicy? Policy { get; set; }
    }

    private sealed class DagPolicy
    {
        public int Maximum { get; set; }
    }

    private sealed class DagItem
    {
        public Guid Id { get; init; }
        public bool Enabled { get; set; }
        public DagItemDetails? Details { get; set; }
    }

    private sealed class DagItemDetails
    {
        public string Code { get; set; } = "";
        public int Quantity { get; set; }
    }
}
