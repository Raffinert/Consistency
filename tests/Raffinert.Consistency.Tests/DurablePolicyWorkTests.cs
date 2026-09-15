using System.Text.Json;

namespace Raffinert.Consistency.Tests;

public sealed class DurablePolicyWorkTests
{
    [Fact]
    public void Empty_result_returns_empty_durable_policy_work()
    {
        var work = Result().GetDurablePolicyWork();

        Assert.Empty(work.RepairRequests);
        Assert.Empty(work.ImmediateEvaluationRequests);
    }

    [Fact]
    public void Repair_request_projects_without_live_source_reference()
    {
        var source = new Source();
        var work = Result(repairs: [Repair(source)]).GetDurablePolicyWork();

        var request = Assert.Single(work.RepairRequests);
        Assert.Equal("repair", request.DefinitionKey);
        Assert.DoesNotContain(typeof(DurableRepairRequestInfo).GetProperties(),
            property => property.PropertyType == typeof(object) || property.PropertyType == typeof(Type));
        Assert.DoesNotContain(typeof(DurableRepairRequestInfo).GetProperties(),
            property => property.Name is "InvariantId" or "SourceReference" or "Callback");
    }

    [Fact]
    public void Immediate_evaluation_projects_without_live_source_reference()
    {
        var source = new Source();
        var work = Result(immediate: [Immediate(source)]).GetDurablePolicyWork();

        var request = Assert.Single(work.ImmediateEvaluationRequests);
        Assert.Equal("evaluate", request.DefinitionKey);
        Assert.DoesNotContain(typeof(DurableImmediateEvaluationRequestInfo).GetProperties(),
            property => property.PropertyType == typeof(object) || property.PropertyType == typeof(Type));
    }

    [Fact]
    public void Repair_reason_is_preserved()
    {
        var request = Repair(new Source(), reason: DependencySeverity.Invalid);

        Assert.Equal(DependencySeverity.Invalid,
            Assert.Single(Result(repairs: [request]).GetDurablePolicyWork().RepairRequests).Reason);
    }

    [Fact]
    public void Request_order_is_preserved()
    {
        var repairs = new[]
        {
            Repair(new Source(), definitionKey: "first", key: 1),
            Repair(new Source(), definitionKey: "second", key: 2)
        };
        var immediate = new[]
        {
            Immediate(new Source(), definitionKey: "third", key: 3),
            Immediate(new Source(), definitionKey: "fourth", key: 4)
        };

        var work = Result(repairs, immediate).GetDurablePolicyWork();

        Assert.Equal(["first", "second"], work.RepairRequests.Select(request => request.DefinitionKey));
        Assert.Equal(["third", "fourth"],
            work.ImmediateEvaluationRequests.Select(request => request.DefinitionKey));
    }

    [Fact]
    public void Composite_key_parts_are_preserved()
    {
        var key = new { OrganizationId = 42, Code = "A" };

        var source = Assert.Single(Result(repairs: [Repair(new Source(), key: key)])
            .GetDurablePolicyWork().RepairRequests).Source;

        Assert.Equal(["OrganizationId", "Code"], source.KeyParts.Select(part => part.Name));
        Assert.Equal(["42", "A"], source.KeyParts.Select(part => part.Value));
    }

    [Fact]
    public void Guid_key_is_preserved()
    {
        var key = Guid.Parse("d2719c8b-2231-4ed0-bcc4-34fa03ea9471");

        var source = Assert.Single(Result(repairs: [Repair(new Source(), key: key)])
            .GetDurablePolicyWork().RepairRequests).Source;

        Assert.Equal("d2719c8b-2231-4ed0-bcc4-34fa03ea9471", Assert.Single(source.KeyParts).Value);
    }

    [Fact]
    public void Date_and_numeric_canonical_key_parts_are_preserved()
    {
        var key = new { Date = new DateOnly(2026, 9, 15), Number = 1234567L };

        var parts = Assert.Single(Result(repairs: [Repair(new Source(), key: key)])
            .GetDurablePolicyWork().RepairRequests).Source.KeyParts;

        Assert.Equal(["date", "int64"], parts.Select(part => part.Type));
        Assert.Equal(["2026-09-15", "1234567"], parts.Select(part => part.Value));
    }

    [Fact]
    public void Unnamed_invariant_throws()
    {
        var request = Repair(new Source()) with { DefinitionKey = null };

        var error = Assert.Throws<InvalidOperationException>(() =>
            Result(repairs: [request]).GetDurablePolicyWork());

        Assert.Contains("invariant", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unnamed_object_set_throws()
    {
        var request = Repair(new Source(), objectSetKey: null);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Result(repairs: [request]).GetDurablePolicyWork());

        Assert.Contains("object set", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_key_shape_throws()
    {
        var request = Repair(new Source(), key: 1.5m);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Result(repairs: [request]).GetDurablePolicyWork());

        Assert.Contains("supported source key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_invalid_request_rejects_the_entire_batch()
    {
        var requests = new[]
        {
            Repair(new Source(), definitionKey: "valid", key: 1),
            Repair(new Source(), definitionKey: "invalid", key: 1.5m)
        };

        Assert.Throws<InvalidOperationException>(() => Result(repairs: requests).GetDurablePolicyWork());
    }

    [Fact]
    public void Returned_collections_are_not_mutable_through_the_public_contract()
    {
        var work = Result(
            repairs: [Repair(new Source())],
            immediate: [Immediate(new Source())]).GetDurablePolicyWork();

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<DurableRepairRequestInfo>)work.RepairRequests).Add(work.RepairRequests[0]));
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<DurableImmediateEvaluationRequestInfo>)work.ImmediateEvaluationRequests)
            .Add(work.ImmediateEvaluationRequests[0]));
    }

    [Fact]
    public void Durable_policy_work_round_trips_through_system_text_json()
    {
        var expected = Result(
            repairs: [Repair(new Source(), key: new { OrganizationId = 42, Code = "A" })],
            immediate: [Immediate(new Source(), key: Guid.Parse("d2719c8b-2231-4ed0-bcc4-34fa03ea9471"))])
            .GetDurablePolicyWork();

        var actual = JsonSerializer.Deserialize<DurablePolicyWork>(JsonSerializer.Serialize(expected));

        Assert.NotNull(actual);
        var expectedRepair = Assert.Single(expected.RepairRequests);
        var actualRepair = Assert.Single(actual.RepairRequests);
        Assert.Equal(expectedRepair.DefinitionKey, actualRepair.DefinitionKey);
        Assert.Equal(expectedRepair.Reason, actualRepair.Reason);
        AssertIdentity(expectedRepair.Source, actualRepair.Source);
        var expectedImmediate = Assert.Single(expected.ImmediateEvaluationRequests);
        var actualImmediate = Assert.Single(actual.ImmediateEvaluationRequests);
        Assert.Equal(expectedImmediate.DefinitionKey, actualImmediate.DefinitionKey);
        AssertIdentity(expectedImmediate.Source, actualImmediate.Source);
    }

    private static RepairRequestInfo Repair(
        object source,
        string? definitionKey = "repair",
        string? objectSetKey = "sources",
        object? key = null,
        DependencySeverity reason = DependencySeverity.Dirty) =>
        new(1, source, reason)
        {
            DefinitionKey = definitionKey,
            SourceIdentity = Identity(source, objectSetKey, key ?? 1)
        };

    private static ImmediateEvaluationRequestInfo Immediate(
        object source,
        string? definitionKey = "evaluate",
        string? objectSetKey = "sources",
        object? key = null) =>
        new(2, source)
        {
            DefinitionKey = definitionKey,
            SourceIdentity = Identity(source, objectSetKey, key ?? 1)
        };

    private static SourceIdentity Identity(object source, string? objectSetKey, object key) =>
        new(objectSetKey, source.GetType(), key)
        {
            DurableIdentity = DurableSourceIdentityFactory.Create(objectSetKey, source.GetType(), key)
        };

    private static RuntimeApplyResult Result(
        IReadOnlyList<RepairRequestInfo>? repairs = null,
        IReadOnlyList<ImmediateEvaluationRequestInfo>? immediate = null) =>
        new(
            new ChangeImpact(new AccessImpact(0, 0), new SemanticImpact(0, 0)),
            [], [], [], repairs ?? [], immediate ?? [], RuntimeImpactDetailLevel.Summary, []);

    private static void AssertIdentity(DurableSourceIdentity expected, DurableSourceIdentity actual)
    {
        Assert.Equal(expected.ObjectSetKey, actual.ObjectSetKey);
        Assert.Equal(expected.SourceType, actual.SourceType);
        Assert.Equal(expected.KeyParts, actual.KeyParts);
    }

    private sealed class Source;
}
