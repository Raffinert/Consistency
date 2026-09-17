using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal static class ExternalConsumerDiscovery
{
    public static IReadOnlyList<RuntimeMutation> Discover(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ConsistencyUnitOfWork unit,
        ConsistencyScope? scope = null)
    {
        var requests = BuildRequests(runtime, mappings, unit.Mutations, scope);
        if (requests.Count == 0) return [];
        var admissions = new List<RuntimeMutation>();
        foreach (var request in requests)
        {
            var registration = mappings.ConsumerResolvers.SingleOrDefault(value =>
                ReferenceEquals(value.RootSet, request.RootSet) && value.Navigation == request.Navigation);
            if (registration is null)
                throw new IncompleteConsistencyScopeException([
                    new ConsistencyScopeGap(request.RootSet.Id, request.RootSet.DefinitionKey,
                        request.RootSet.ObjectType, ConsistencyScopeRequirementKind.NavigationConsumerCoverage)]);
            var roots = registration.Query(context, request.Targets).ToArray();
            Collect(context, runtime, mappings, request, roots, admissions);
        }
        return admissions;
    }

    public static async Task<IReadOnlyList<RuntimeMutation>> DiscoverAsync(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ConsistencyUnitOfWork unit,
        CancellationToken cancellationToken,
        ConsistencyScope? scope = null)
    {
        var requests = BuildRequests(runtime, mappings, unit.Mutations, scope);
        if (requests.Count == 0) return [];
        var admissions = new List<RuntimeMutation>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registration = mappings.ConsumerResolvers.SingleOrDefault(value =>
                ReferenceEquals(value.RootSet, request.RootSet) && value.Navigation == request.Navigation);
            if (registration is null)
                throw new IncompleteConsistencyScopeException([
                    new ConsistencyScopeGap(request.RootSet.Id, request.RootSet.DefinitionKey,
                        request.RootSet.ObjectType, ConsistencyScopeRequirementKind.NavigationConsumerCoverage)]);
            var roots = await registration.Query(context, request.Targets).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            Collect(context, runtime, mappings, request, roots, admissions);
        }
        return admissions;
    }

    private static IReadOnlyList<ExternalConsumerRequest> BuildRequests(
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        IReadOnlyList<RuntimeMutation> mutations,
        ConsistencyScope? scope)
    {
        var descriptors = mappings.ActiveConsumerDescriptors(runtime).ToArray();
        var groups = new Dictionary<(IObjectSetDefinition Set, MemberInfo Navigation), List<object>>(
            new RequestKeyComparer());
        foreach (var change in mutations.OfType<PropertyChange>())
        {
            foreach (var descriptor in descriptors.Where(value => (scope is null || !scope.Contains(value.RootSet)) &&
                         value.TargetMember == change.Member &&
                         value.TargetType.IsInstanceOfType(change.Instance)))
            {
                var key = (descriptor.RootSet, descriptor.Navigation);
                if (!groups.TryGetValue(key, out var targets))
                    groups.Add(key, targets = []);
                if (!targets.Contains(change.Instance, ReferenceEqualityComparer.Instance))
                    targets.Add(change.Instance);
            }
        }
        return groups
            .OrderBy(group => group.Key.Set.Id)
            .ThenBy(group => group.Key.Navigation.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Navigation.Name, StringComparer.Ordinal)
            .Select(group => new ExternalConsumerRequest(
                group.Key.Set, group.Key.Navigation,
                descriptors.First(value => ReferenceEquals(value.RootSet, group.Key.Set) &&
                    value.Navigation == group.Key.Navigation).TargetType,
                group.Value.ToArray()))
            .ToArray();
    }

    private static void Collect(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ExternalConsumerRequest request,
        IEnumerable<object> resolvedRoots,
        List<RuntimeMutation> admissions)
    {
        var targets = request.Targets.ToHashSet(ReferenceEqualityComparer.Instance);
        var candidates = resolvedRoots
            .Concat(context.ChangeTracker.Entries()
                .Where(entry => request.RootSet.ObjectType.IsInstanceOfType(entry.Entity))
                .Select(entry => entry.Entity))
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToArray();
        var keys = new Dictionary<object, object>();
        foreach (var root in candidates)
        {
            if (!request.RootSet.ObjectType.IsInstanceOfType(root))
                throw new InvalidOperationException("A consumer resolver returned an incompatible root type.");
            var entry = context.Entry(root);
            if (entry.State == EntityState.Detached)
                throw new InvalidOperationException("A consumer resolver returned an untracked root.");
            var mapping = mappings.UnitOfWorkMappings.Resolve(entry);
            if (mapping is null || !ReferenceEquals(mapping.SetDefinition, request.RootSet))
                throw new InvalidOperationException(
                    "A consumer resolver returned a root that is not mapped to the requested object set.");
            var currentTarget = MemberReader.Read(request.Navigation, root);
            if (currentTarget is null)
            {
                if (request.Navigation is PropertyInfo && entry.Metadata.FindNavigation(request.Navigation.Name) is not null &&
                    !entry.Navigation(request.Navigation.Name).IsLoaded)
                    throw new InvalidOperationException(
                        "A discovered consumer is missing a tracked evaluation reference.");
                continue;
            }
            if (!targets.Contains(currentTarget))
                continue;
            if ((entry.State == EntityState.Deleted || entry.State == EntityState.Modified) &&
                !runtime.IsRegistered(request.RootSet, root))
                throw new InvalidOperationException(
                    "An existing modified or deleted consumer root is not registered in the consistency runtime.");
            if (entry.State == EntityState.Deleted)
                continue;
            if (entry.State == EntityState.Added || runtime.IsRegistered(request.RootSet, root))
                continue;
            if (admissions.Any(value => value is IAddedMutation added &&
                    ReferenceEquals(added.Set, request.RootSet) &&
                    ReferenceEquals(added.Instance, root)))
                continue;
            var key = request.RootSet.ReadKey(root) ?? throw new InvalidOperationException("Object keys cannot be null.");
            if (runtime.HasRegisteredKey(request.RootSet, root) ||
                keys.Values.Any(value => Equals(value, key)) ||
                admissions.OfType<IAddedMutation>().Any(value =>
                    ReferenceEquals(value.Set, request.RootSet) &&
                    Equals(request.RootSet.ReadKey(value.Instance), key)))
                throw new InvalidOperationException("A consumer resolver returned duplicate runtime keys.");
            keys.Add(root, key);
            ValidateEvaluationClosure(context, mappings, runtime, request.RootSet, root);
            admissions.Add(new CoverageAdmission(request.RootSet, root));
        }
    }

    private static void ValidateEvaluationClosure(
        DbContext context,
        ConsistencyEfCoreMappings mappings,
        ConsistencyRuntime runtime,
        IObjectSetDefinition rootSet,
        object root)
    {
        foreach (var descriptor in mappings.ActiveConsumerDescriptors(runtime)
                     .Where(value => ReferenceEquals(value.RootSet, rootSet)))
        {
            var target = MemberReader.Read(descriptor.Navigation, root);
            if (target is null) continue;
            if (context.Entry(target).State == EntityState.Detached)
                throw new InvalidOperationException("A discovered consumer is missing a tracked evaluation reference.");
        }
    }

    internal sealed record ExternalConsumerRequest(
        IObjectSetDefinition RootSet,
        MemberInfo Navigation,
        Type TargetType,
        IReadOnlyList<object> Targets);

    private sealed class RequestKeyComparer : IEqualityComparer<(IObjectSetDefinition Set, MemberInfo Navigation)>
    {
        public bool Equals((IObjectSetDefinition Set, MemberInfo Navigation) x,
            (IObjectSetDefinition Set, MemberInfo Navigation) y) =>
            ReferenceEquals(x.Set, y.Set) && x.Navigation == y.Navigation;
        public int GetHashCode((IObjectSetDefinition Set, MemberInfo Navigation) value) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(value.Set), value.Navigation);
    }
}
