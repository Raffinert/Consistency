namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed class EfTouchedSources
{
    private readonly HashSet<object> _sources = new(ReferenceEqualityComparer.Instance);

    private EfTouchedSources(IEnumerable<object> sources) => _sources.UnionWith(sources);

    public static EfTouchedSources Create(IReadOnlyList<RuntimeMutation> mutations) =>
        new(mutations.Select(mutation => mutation switch
        {
            PropertyChange change => change.Instance,
            CollectionChange change => change.Owner,
            ObjectAdded change => change.Instance,
            ObjectRemoved change => change.Instance,
            CoverageAdmission change => change.Instance,
            _ => throw new InvalidOperationException(
                $"Unsupported EF touched-source mutation type '{mutation.GetType().Name}'.")
        }));

    public bool Contains(object source) => _sources.Contains(source);
}
