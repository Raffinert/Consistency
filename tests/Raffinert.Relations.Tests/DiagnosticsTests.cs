namespace Raffinert.Relations.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Compiled_diagnostics_expose_public_model_structure_and_semantics()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Named("Source").Key(value => value.Id);
        var items = model.Objects<DerivedItemRecord>().Named("Item").Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code)
            .Named("Source.Items");
        var total = model.Derived(sources).Using(relation)
            .Impact(policy => policy.MembershipRemoved(DependencySeverity.Invalid))
            .Incrementally()
            .Compute((source, matches) => matches.Sum(item => item.Quantity))
            .Named("Source.Total");
        model.Invariant(sources).Using(total).Must((source, value) => value >= 0m)
            .Named("Source.Total.NonNegative")
            .ReactWith(InvariantReaction.MarkInvalid);

        var compiled = model.Build();
        var diagnostics = compiled.Diagnostics;

        Assert.Equal(2, diagnostics.ObjectSets.Count);
        Assert.Equal(0, diagnostics.ObjectSets[0].ObjectSetId);
        Assert.Equal(typeof(DerivedSourceRecord), diagnostics.ObjectSets[0].ObjectType);
        Assert.Equal("Source", diagnostics.ObjectSets[0].DefinitionKey);
        Assert.Contains("Id", diagnostics.ObjectSets[0].KeyExpression);
        var relationDiagnostic = Assert.Single(diagnostics.Relations);
        Assert.Equal(0, relationDiagnostic.RelationId);
        Assert.Equal("Source.Items", relationDiagnostic.DefinitionKey);
        Assert.Equal(RelationAccessPlanKind.HashJoin, relationDiagnostic.AccessPlan);
        Assert.Equal(RelationAccessPlanKind.HashJoin, relationDiagnostic.ReverseAccessPlan);
        Assert.Equal(RelationMaterializationMode.ExactPropagation, relationDiagnostic.Materialization);
        Assert.Equal(DependencyCompletenessIssue.None, relationDiagnostic.CompletenessIssues);
        Assert.NotEmpty(relationDiagnostic.Dependencies);
        Assert.NotEmpty(relationDiagnostic.JoinKeys);
        var derivedDiagnostic = Assert.Single(diagnostics.DerivedValues);
        Assert.Equal(0, derivedDiagnostic.DerivedId);
        Assert.Equal("Source.Total", derivedDiagnostic.DefinitionKey);
        Assert.Equal(0, derivedDiagnostic.RelationId);
        Assert.True(derivedDiagnostic.UsesRelationMembership);
        Assert.True(derivedDiagnostic.LinqSemantics.HasFlag(DerivedLinqSemantics.Membership));
        Assert.True(derivedDiagnostic.LinqSemantics.HasFlag(DerivedLinqSemantics.Item));
        Assert.Equal("IncrementalSum(DerivedItemRecord.Quantity)", derivedDiagnostic.ComputationPlan);
        Assert.Equal(DependencySeverity.Invalid, derivedDiagnostic.MembershipRemovedSeverity);
        var invariantDiagnostic = Assert.Single(diagnostics.Invariants);
        Assert.Equal(0, invariantDiagnostic.InvariantId);
        Assert.Equal("Source.Total.NonNegative", invariantDiagnostic.DefinitionKey);
        Assert.Equal(0, invariantDiagnostic.DerivedId);
        Assert.Equal(InvariantReaction.MarkInvalid, invariantDiagnostic.Reaction);
    }

    [Fact]
    public void Runtime_diagnostics_count_incremental_work_and_policy_requests_since_reset()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Named("Source").Key(value => value.Id);
        var items = model.Objects<DerivedItemRecord>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var total = model.Derived(sources).Using(relation).Incrementally()
            .Compute((source, matches) => matches.Sum(item => item.Quantity));
        var repairs = new List<DerivedSourceRecord>();
        var invariant = model.Invariant(sources).Using(total).Must((source, value) => value == 0m)
            .Named("Source.ZeroTotal")
            .ScheduleRepairWith(repairs.Add);
        var runtime = model.Build().CreateRuntime();
        var source = new DerivedSourceRecord { Id = Guid.NewGuid(), Code = "A" };
        var item = new DerivedItemRecord { Id = Guid.NewGuid(), Code = "B", Quantity = 2m };
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(0m, runtime.Get(total, source));
        Assert.True(runtime.Evaluate(invariant, source));
        runtime.ResetDiagnostics();
        item.Code = "A";

        var result = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(items, item, value => value.Code, "B", "A")));
        var diagnostics = runtime.Diagnostics;

        Assert.True(diagnostics.PredicateEvaluations > 0);
        Assert.Equal(1, diagnostics.ReindexedRoots);
        Assert.Equal(1, diagnostics.AffectedSources);
        Assert.Equal(1, diagnostics.RelationPairsAdded);
        Assert.Equal(0, diagnostics.RelationPairsRemoved);
        Assert.Equal(0, diagnostics.DerivedFullRecomputations);
        Assert.Equal(1, diagnostics.IncrementalDerivedUpdates);
        Assert.Equal(1, diagnostics.PolicyRequestsEmitted);
        var request = Assert.Single(result.RepairRequests);
        Assert.Equal("Source.ZeroTotal", request.DefinitionKey);
        Assert.Equal("Source", request.SourceIdentity!.ObjectSetKey);
        Assert.Equal(source.Id, request.SourceIdentity.SourceKey);
        Assert.True(request.SourceIdentity.IsDurable);
        Assert.Empty(repairs);

        result.DispatchPolicies();
        Assert.Equal([source], repairs);
        runtime.ResetDiagnostics();
        Assert.Equal(0, runtime.Diagnostics.PredicateEvaluations);
        Assert.Equal(0, runtime.Diagnostics.IncrementalDerivedUpdates);
        Assert.Equal(0, runtime.Diagnostics.PolicyRequestsEmitted);
    }

    [Fact]
    public void Definition_keys_survive_declaration_reordering_while_ordinals_may_change()
    {
        var first = BuildWithOrder(reverse: false).Diagnostics.Relations;
        var second = BuildWithOrder(reverse: true).Diagnostics.Relations;

        Assert.NotEqual(
            first.Single(value => value.DefinitionKey == "ByCode").RelationId,
            second.Single(value => value.DefinitionKey == "ByCode").RelationId);
        Assert.Equal(
            first.Select(value => value.DefinitionKey).Order().ToArray(),
            second.Select(value => value.DefinitionKey).Order().ToArray());
    }

    [Fact]
    public void Duplicate_definition_keys_are_rejected()
    {
        var model = new RelationModelBuilder();
        _ = model.Objects<DerivedSourceRecord>().Named("Duplicate").Key(value => value.Id);
        _ = model.Objects<DerivedItemRecord>().Named("Duplicate").Key(value => value.Id);

        var error = Assert.Throws<InvalidOperationException>(model.Build);

        Assert.Contains("unique", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledRelationModel BuildWithOrder(bool reverse)
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<DerivedSourceRecord>().Key(value => value.Id);
        var items = model.Objects<DerivedItemRecord>().Key(value => value.Id);
        Action byCode = () => model.Relation(sources, items)
            .Where((source, item) => source.Code == item.Code).Named("ByCode");
        Action byQuantity = () => model.Relation(sources, items)
            .Where((_, item) => item.Quantity > 0m).Named("ByQuantity");
        if (reverse)
        {
            byQuantity();
            byCode();
        }
        else
        {
            byCode();
            byQuantity();
        }
        return model.Build();
    }
}
