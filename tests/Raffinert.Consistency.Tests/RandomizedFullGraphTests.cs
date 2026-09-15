using Xunit.Sdk;

namespace Raffinert.Consistency.Tests;

public sealed class RandomizedFullGraphTests
{
    [Fact]
    public void Optimized_full_graph_matches_scan_and_reference_oracles()
    {
        const int seed = 18473;
        var random = new Random(seed);
        var trace = new List<string>();
        var hash = CreateScenario(forceScan: false);
        var scan = CreateScenario(forceScan: true);
        var sources = Enumerable.Range(0, 4).Select(index => NewSource(index)).ToList();
        var items = Enumerable.Range(0, 6).Select(index => NewItem(index)).ToList();
        foreach (var source in sources)
        {
            hash.Runtime.Add(hash.Sources, source);
            scan.Runtime.Add(scan.Sources, source);
        }
        foreach (var item in items)
        {
            hash.Runtime.Add(hash.Items, item);
            scan.Runtime.Add(scan.Items, item);
        }
        Verify();

        for (var operation = 0; operation < 300; operation++)
        {
            hash.Repairs.Clear();
            scan.Repairs.Clear();
            try
            {
                ApplyRandomOperation();
                Verify();
            }
            catch (Exception exception)
            {
                throw new XunitException(
                    $"Randomized full-graph failure. Seed={seed}, operation={operation}." +
                    Environment.NewLine + string.Join(Environment.NewLine, trace) +
                    Environment.NewLine + exception);
            }
        }

        void ApplyRandomOperation()
        {
            var choice = random.Next(10);
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
                        trace.Add($"add source {source.Id} {source.Code}");
                        hash.Runtime.Add(hash.Sources, source);
                        scan.Runtime.Add(scan.Sources, source);
                        break;
                    }
                case 1:
                    {
                        var index = random.Next(sources.Count);
                        var source = sources[index];
                        sources.RemoveAt(index);
                        trace.Add($"remove source {source.Id}");
                        hash.Runtime.Remove(hash.Sources, source);
                        scan.Runtime.Remove(scan.Sources, source);
                        break;
                    }
                case 2:
                    {
                        var source = sources[random.Next(sources.Count)];
                        var old = source.Code;
                        source.Code = RandomCode();
                        trace.Add($"source code {source.Id} {old}->{source.Code}");
                        ApplyBoth(Change.Property(hash.Sources, source, value => value.Code, old, source.Code),
                            Change.Property(scan.Sources, source, value => value.Code, old, source.Code));
                        break;
                    }
                case 3:
                    {
                        var source = sources[random.Next(sources.Count)];
                        var old = source.Adjustment;
                        source.Adjustment = random.Next(0, 5);
                        trace.Add($"source adjustment {source.Id} {old}->{source.Adjustment}");
                        ApplyBoth(
                            Change.Property(hash.Sources, source, value => value.Adjustment, old, source.Adjustment),
                            Change.Property(scan.Sources, source, value => value.Adjustment, old, source.Adjustment));
                        break;
                    }
                case 4:
                    {
                        var source = sources[random.Next(sources.Count)];
                        var old = source.Policy!.Maximum;
                        source.Policy.Maximum = random.Next(5, 25);
                        trace.Add($"policy maximum {source.Id} {old}->{source.Policy.Maximum}");
                        ApplyBoth(
                            Change.Property(source.Policy, value => value.Maximum, old, source.Policy.Maximum),
                            Change.Property(source.Policy, value => value.Maximum, old, source.Policy.Maximum));
                        break;
                    }
                case 5:
                    {
                        var item = NewItem(random.Next());
                        items.Add(item);
                        trace.Add($"add item {item.Id}");
                        hash.Runtime.Add(hash.Items, item);
                        scan.Runtime.Add(scan.Items, item);
                        break;
                    }
                case 6:
                    {
                        var index = random.Next(items.Count);
                        var item = items[index];
                        items.RemoveAt(index);
                        trace.Add($"remove item {item.Id}");
                        hash.Runtime.Remove(hash.Items, item);
                        scan.Runtime.Remove(scan.Items, item);
                        break;
                    }
                case 7:
                    {
                        var item = items[random.Next(items.Count)];
                        if (item.Details is null)
                        {
                            item.Details = new DerivedItemDetails { Code = RandomCode(), Quantity = 1m };
                            trace.Add($"assign item details {item.Id}");
                            ApplyBoth(
                                Change.Property(hash.Items, item, value => value.Details, null, item.Details),
                                Change.Property(scan.Items, item, value => value.Details, null, item.Details));
                            break;
                        }
                        var old = item.Details!.Code;
                        item.Details.Code = RandomCode();
                        trace.Add($"item nested code {item.Id} {old}->{item.Details.Code}");
                        ApplyBoth(
                            Change.Property(item.Details, value => value.Code, old, item.Details.Code),
                            Change.Property(item.Details, value => value.Code, old, item.Details.Code));
                        break;
                    }
                case 8:
                    {
                        var item = items[random.Next(items.Count)];
                        var old = item.Details;
                        item.Details = random.Next(4) == 0
                            ? null
                            : new DerivedItemDetails
                            {
                                Code = RandomCode(),
                                Quantity = random.Next(1, 10)
                            };
                        trace.Add($"replace item details {item.Id}");
                        ApplyBoth(
                            Change.Property(hash.Items, item, value => value.Details, old, item.Details),
                            Change.Property(scan.Items, item, value => value.Details, old, item.Details));
                        break;
                    }
                default:
                    {
                        var item = items[random.Next(items.Count)];
                        var old = item.Enabled;
                        item.Enabled = !old;
                        trace.Add($"toggle item {item.Id} {old}->{item.Enabled}");
                        ApplyBoth(
                            Change.Property(hash.Items, item, value => value.Enabled, old, item.Enabled),
                            Change.Property(scan.Items, item, value => value.Enabled, old, item.Enabled));
                        break;
                    }
            }
        }

        void ApplyBoth(PropertyChange hashChange, PropertyChange scanChange)
        {
            var hashImpact = hash.Runtime.Apply(hashChange, ChangeValidationMode.StrictNewValue);
            var scanImpact = scan.Runtime.Apply(scanChange, ChangeValidationMode.StrictNewValue);
            Assert.Equal(scanImpact.Semantic, hashImpact.Semantic);
            AssertRelationImpactsEqual(hash, scan);
        }

        void Verify()
        {
            foreach (var source in sources)
            {
                var expected = items
                    .Where(item => item.Enabled && item.Details?.Code == source.Code)
                    .OrderBy(item => item.Id)
                    .ToArray();
                Assert.Equal(expected.Select(item => item.Id),
                    hash.Runtime.Related(hash.Relation, source).Select(item => item.Id).Order());
                Assert.Equal(expected.Select(item => item.Id),
                    scan.Runtime.Related(scan.Relation, source).Select(item => item.Id).Order());
                Assert.Equal(scan.Runtime.GetState(scan.Derived, source), hash.Runtime.GetState(hash.Derived, source));
                var expectedValue = expected.Sum(item => item.Details!.Quantity) + source.Adjustment;
                Assert.Equal(expectedValue, hash.Runtime.Get(hash.Derived, source));
                Assert.Equal(expectedValue, scan.Runtime.Get(scan.Derived, source));
                var expectedInvariant = expectedValue <= source.Policy!.Maximum;
                Assert.Equal(expectedInvariant, hash.Runtime.Evaluate(hash.Invariant, source));
                Assert.Equal(expectedInvariant, scan.Runtime.Evaluate(scan.Invariant, source));
                Assert.Equal(scan.Runtime.GetState(scan.Invariant, source), hash.Runtime.GetState(hash.Invariant, source));
            }
            Assert.Equal(scan.Repairs.Order(), hash.Repairs.Order());
            Assert.All(hash.Repairs, repaired => Assert.Contains(sources, source => source.Id == repaired));
        }

        string RandomCode() => ((char)('A' + random.Next(4))).ToString();
        DerivedSourceRecord NewSource(int index) => new()
        {
            Id = Guid.NewGuid(),
            Code = ((char)('A' + Math.Abs(index % 4))).ToString(),
            Adjustment = Math.Abs(index % 3),
            Policy = new ReceiptPolicy { Maximum = 18m }
        };
        DerivedItemRecord NewItem(int index) => new()
        {
            Id = Guid.NewGuid(),
            Enabled = index % 3 != 0,
            Details = new DerivedItemDetails
            {
                Code = ((char)('A' + Math.Abs(index % 4))).ToString(),
                Quantity = Math.Abs(index % 7) + 1
            }
        };
    }

    private static Scenario CreateScenario(bool forceScan)
    {
        var model = new RelationModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        var sources = model.Objects<DerivedSourceRecord>().Key(value => value.Id);
        var items = model.Objects<DerivedItemRecord>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) =>
            item.Details != null && source.Code == item.Details.Code && item.Enabled);
        var derived = model.Derived(sources).Using(relation)
            .Compute((source, matches) => matches.Sum(item => item.Details!.Quantity) + source.Adjustment);
        var repairs = new List<Guid>();
        var invariant = model.Invariant(sources).Using(derived)
            .Must((source, value) => value <= source.Policy!.Maximum)
            .ScheduleRepairWith(source => repairs.Add(source.Id));
        return new Scenario(model.Build().CreateRuntime(), sources, items, relation, derived, invariant, repairs);
    }

    private static void AssertRelationImpactsEqual(Scenario hash, Scenario scan)
    {
        var hashImpact = hash.Runtime.LastRelationImpacts.GetValueOrDefault(hash.Relation.Definition);
        var scanImpact = scan.Runtime.LastRelationImpacts.GetValueOrDefault(scan.Relation.Definition);
        Assert.Equal(scanImpact is null, hashImpact is null);
        if (hashImpact is null || scanImpact is null)
            return;
        Assert.Equal(
            scanImpact.AddedPairs.Select(PairKey).Order(),
            hashImpact.AddedPairs.Select(PairKey).Order());
        Assert.Equal(
            scanImpact.RemovedPairs.Select(PairKey).Order(),
            hashImpact.RemovedPairs.Select(PairKey).Order());
        Assert.Equal(
            scanImpact.AffectedLefts.Cast<DerivedSourceRecord>().Select(value => value.Id).Order(),
            hashImpact.AffectedLefts.Cast<DerivedSourceRecord>().Select(value => value.Id).Order());

        static string PairKey(RelationPair pair) =>
            $"{((DerivedSourceRecord)pair.Left).Id}:{((DerivedItemRecord)pair.Right).Id}";
    }

    private sealed record Scenario(
        RelationRuntime Runtime,
        ObjectSet<DerivedSourceRecord> Sources,
        ObjectSet<DerivedItemRecord> Items,
        Relation<DerivedSourceRecord, DerivedItemRecord> Relation,
        Derived<DerivedSourceRecord, decimal> Derived,
        Invariant<DerivedSourceRecord> Invariant,
        List<Guid> Repairs);
}
