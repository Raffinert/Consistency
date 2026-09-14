using System.Runtime.CompilerServices;

namespace Raffinert.Relations.Tests;

internal static class RuntimeApplyResultAssert
{
    public static void Equivalent(RuntimeApplyResult expected, RuntimeApplyResult actual) =>
        Assert.Equal(Normalize(expected), Normalize(actual));

    private static string Normalize(RuntimeApplyResult result)
    {
        var lines = new List<string>
        {
            $"detail:{result.DetailLevel}",
            $"change:{result.ChangeImpact.Access.ReindexedRelations}:{result.ChangeImpact.Access.ReindexedRoots}:" +
            $"{result.ChangeImpact.Semantic.AffectedRelations}:{result.ChangeImpact.Semantic.AffectedRoots}"
        };
        lines.AddRange(result.RelationImpacts.Select(impact =>
            $"relation:{impact.RelationId}:{impact.DefinitionKey}:" +
            $"add={Join(impact.AddedPairs.Select(pair => Ref(pair.Left) + ">" + Ref(pair.Right)))}:" +
            $"remove={Join(impact.RemovedPairs.Select(pair => Ref(pair.Left) + ">" + Ref(pair.Right)))}:" +
            $"affected={Join(impact.AffectedSources.Select(Ref))}"));
        lines.AddRange(result.DerivedImpacts.SelectMany(impact => impact.Sources.Select(source =>
            $"derived:{impact.DerivedId}:{impact.DefinitionKey}:{Identity(source)}:{source.Severity}:" +
            $"causes={Join(source.Causes.Select(Cause))}")));
        lines.AddRange(result.InvariantImpacts.SelectMany(impact => impact.Sources.Select(source =>
            $"invariant:{impact.InvariantId}:{impact.DefinitionKey}:{Identity(source)}:{source.Severity}:" +
            $"causes={Join(source.Causes.Select(Cause))}")));
        lines.AddRange(result.RepairRequests.Select(request =>
            $"repair:{request.InvariantId}:{request.DefinitionKey}:{request.Reason}:{Identity(request.SourceIdentity, request.Source)}"));
        lines.AddRange(result.ImmediateEvaluationRequests.Select(request =>
            $"immediate:{request.InvariantId}:{request.DefinitionKey}:{Identity(request.SourceIdentity, request.Source)}"));
        lines.AddRange(result.MutationOrigins.Select(origin =>
            $"origin:{origin.OriginId}:{origin.Kind}:{origin.MemberName}:{origin.CollectionKind}:" +
            $"{Identity(origin.SourceIdentity, origin.Source)}:{Ref(origin.CollectionItem)}"));
        return string.Join('\n', lines.OrderBy(line => line, StringComparer.Ordinal));
    }

    private static string Cause(DependencyImpactCause cause) => cause switch
    {
        DirectSourceMemberCause direct =>
            $"direct:{direct.OriginId}:{direct.MemberName}:{direct.Policy}:{direct.ClassifiedSeverity}",
        RelationDependencyCause relation =>
            $"relation:{relation.RelationId}:{relation.DefinitionKey}:{relation.Kind}:{relation.Precision}:" +
            Join(relation.OriginIds.Select(id => id.ToString())),
        UpstreamDerivedCause upstream =>
            $"upstream:{upstream.DerivedId}:{upstream.DefinitionKey}:{upstream.UpstreamImpactId}:{upstream.Precision}",
        InvariantReactionCause reaction =>
            $"reaction:{reaction.Reaction}:{reaction.InputSeverity}:{reaction.OutputSeverity}",
        _ => cause.ToString() ?? cause.GetType().Name
    };

    private static string Identity(SourceDependencyImpact impact) => Identity(impact.SourceIdentity, impact.Source);

    private static string Identity(SourceIdentity? identity, object source) => identity?.DurableIdentity is { } durable
        ? $"{durable.ObjectSetKey}/{Join(durable.KeyParts.Select(part => part.Value))}"
        : identity is not null ? $"{identity.ObjectSetKey}/{identity.SourceKey}" : Ref(source);

    private static string Ref(object? value) => value is null
        ? "null"
        : $"{value.GetType().FullName}@{RuntimeHelpers.GetHashCode(value)}";

    private static string Join(IEnumerable<string> values) => string.Join(',', values.OrderBy(value => value, StringComparer.Ordinal));
}
