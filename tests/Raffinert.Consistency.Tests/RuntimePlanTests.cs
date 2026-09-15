namespace Raffinert.Consistency.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public void Randomized_hash_results_equal_forced_scan_after_mutations()
    {
        var hashModel = new RelationModelBuilder();
        var hashLeft = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRight = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRelation = hashModel.Relation(hashLeft, hashRight).Where((a, b) => a.Code == b.Code && b.Enabled);
        var hashRuntime = hashModel.Build().CreateRuntime();

        var scanModel = new RelationModelBuilder().UseScanPlansForTesting();
        var scanLeft = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRight = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRelation = scanModel.Relation(scanLeft, scanRight).Where((a, b) => a.Code == b.Code && b.Enabled);
        var scanRuntime = scanModel.Build().CreateRuntime();

        var random = new Random(7319);
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var active = new List<CodeHolder>();
        hashRuntime.Add(hashLeft, source);
        scanRuntime.Add(scanLeft, source);

        for (var operation = 0; operation < 250; operation++)
        {
            switch (random.Next(active.Count == 0 ? 1 : 4))
            {
                case 0:
                    {
                        var item = new CodeHolder
                        {
                            Id = Guid.NewGuid(),
                            Code = RandomCode(),
                            Enabled = random.Next(2) == 0
                        };
                        active.Add(item);
                        hashRuntime.Add(hashRight, item);
                        scanRuntime.Add(scanRight, item);
                        break;
                    }
                case 1:
                    {
                        var item = active[random.Next(active.Count)];
                        var oldCode = item.Code;
                        item.Code = RandomCode();
                        hashRuntime.Apply(Change.Property(hashRight, item, x => x.Code, oldCode, item.Code));
                        scanRuntime.Apply(Change.Property(scanRight, item, x => x.Code, oldCode, item.Code));
                        break;
                    }
                case 2:
                    {
                        var item = active[random.Next(active.Count)];
                        item.Enabled = !item.Enabled;
                        hashRuntime.Apply(Change.Property(hashRight, item, x => x.Enabled, !item.Enabled, item.Enabled));
                        scanRuntime.Apply(Change.Property(scanRight, item, x => x.Enabled, !item.Enabled, item.Enabled));
                        break;
                    }
                default:
                    {
                        var index = random.Next(active.Count);
                        var item = active[index];
                        active.RemoveAt(index);
                        hashRuntime.Remove(hashRight, item);
                        scanRuntime.Remove(scanRight, item);
                        break;
                    }
            }

            Assert.Equal(
                scanRuntime.Related(scanRelation, source).Select(item => item.Id).Order(),
                hashRuntime.Related(hashRelation, source).Select(item => item.Id).Order());
        }

        string RandomCode() => ((char)('A' + random.Next(4))).ToString();
    }

    [Fact]
    public void Ordinal_ignore_case_string_equality_uses_matching_hash_semantics()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<CodeHolder>().Key(x => x.Id);
        var right = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(left, right).Where((a, b) =>
            string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "AbC" };
        var match = new CodeHolder { Id = Guid.NewGuid(), Code = "aBc" };
        runtime.Add(left, source);
        runtime.Add(right, match);

        Assert.Equal([match], runtime.Related(relation, source));
    }

    [Fact]
    public void Relation_can_be_queried_from_the_right_without_a_reverse_hash_index()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<CodeHolder>().Key(x => x.Id);
        var right = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(left, right).Where((a, b) => a.Code == b.Code);
        var runtime = model.Build().CreateRuntime();
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        var target = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(left, first);
        runtime.Add(left, second);
        runtime.Add(right, target);

        Assert.Equal([first], runtime.RelatedFromRight(relation, target));
    }

    [Fact]
    public void Runtime_diagnostics_report_local_predicate_work_and_affected_sources()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count + 1);
        var runtime = model.Build().CreateRuntime();
        runtime.Add(sources, new CodeHolder { Id = Guid.NewGuid(), Code = "A" });
        runtime.Add(sources, new CodeHolder { Id = Guid.NewGuid(), Code = "B" });
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(items, item);
        runtime.ResetDiagnostics();

        item.Code = "B";
        runtime.Apply(Change.Property(items, item, value => value.Code, "A", "B"));

        Assert.Equal(1, runtime.Diagnostics.PredicateEvaluations);
        Assert.Equal(2, runtime.Diagnostics.AffectedSources);
    }

}
