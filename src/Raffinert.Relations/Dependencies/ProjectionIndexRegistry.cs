namespace Raffinert.Relations;

internal sealed class ProjectionIndexRegistry
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly IReadOnlyList<Entry> _entries;

    public ProjectionIndexRegistry(
        IReadOnlyList<IDerivedDefinition> definitions,
        IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets)
    {
        _sets = sets;
        _entries = definitions.SelectMany(definition => definition.Inputs
                .OfType<ProjectedUpstreamDerivedInput>()
                .Select(input => new Entry(definition.SourceSet, input)))
            .ToArray();
    }

    public int EdgeCount => _entries.Count;
    public int ReverseEntryCount => _entries.Sum(entry => entry.DownstreamToTarget.Count);
    public int TargetCount => _entries.Sum(entry => entry.TargetToDownstreams.Count);

    public void AddRoot(IObjectSetDefinition set, object source)
    {
        foreach (var entry in _entries.Where(value => ReferenceEquals(value.DownstreamSet, set)))
            entry.Add(source);
    }

    public void RemoveRoot(IObjectSetDefinition set, object source)
    {
        foreach (var entry in _entries.Where(value => ReferenceEquals(value.DownstreamSet, set)))
            entry.Remove(source);
    }

    public void RefreshRoot(IObjectSetDefinition set, object source)
    {
        foreach (var entry in _entries.Where(value => ReferenceEquals(value.DownstreamSet, set)))
        {
            entry.Remove(source);
            entry.Add(source);
        }
    }

    public IReadOnlyCollection<object> Resolve(
        ProjectedUpstreamDerivedInput input,
        IEnumerable<object> upstreamSources)
    {
        var entry = _entries.Single(value => ReferenceEquals(value.Input, input));
        var result = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var upstream in upstreamSources)
            if (entry.TargetToDownstreams.TryGetValue(upstream, out var downstream))
                result.UnionWith(downstream);
        return result;
    }

    public void ValidateAll()
    {
        foreach (var entry in _entries)
            foreach (var (source, target) in entry.DownstreamToTarget)
            {
                if (target is null)
                    throw new InvalidOperationException("A projected dependency target cannot be null.");
                if (!_sets[entry.Input.UpstreamSet].Contains(target))
                    throw new InvalidOperationException(
                        $"The projected target selected by '{entry.Input.SelectorExpression}' is not registered " +
                        "in the exact upstream object set.");
                if (!_sets[entry.DownstreamSet].Contains(source))
                    throw new InvalidOperationException("A projection index contains an unregistered downstream source.");
            }
    }

    public object CaptureState() => _entries.Select(entry => entry.CaptureState()).ToArray();

    public void RestoreState(object snapshot)
    {
        var states = (EntryState[])snapshot;
        for (var index = 0; index < _entries.Count; index++)
            _entries[index].RestoreState(states[index]);
    }

    private sealed class Entry(
        IObjectSetDefinition downstreamSet,
        ProjectedUpstreamDerivedInput input)
    {
        public IObjectSetDefinition DownstreamSet => downstreamSet;
        public ProjectedUpstreamDerivedInput Input => input;
        public Dictionary<object, object?> DownstreamToTarget { get; } =
            new(ReferenceEqualityComparer.Instance);
        public Dictionary<object, HashSet<object>> TargetToDownstreams { get; } =
            new(ReferenceEqualityComparer.Instance);

        public void Add(object source)
        {
            var target = input.Project(source);
            DownstreamToTarget.Add(source, target);
            if (target is null) return;
            if (!TargetToDownstreams.TryGetValue(target, out var downstream))
                TargetToDownstreams.Add(target,
                    downstream = new HashSet<object>(ReferenceEqualityComparer.Instance));
            downstream.Add(source);
        }

        public void Remove(object source)
        {
            if (!DownstreamToTarget.Remove(source, out var target) || target is null) return;
            var downstream = TargetToDownstreams[target];
            downstream.Remove(source);
            if (downstream.Count == 0) TargetToDownstreams.Remove(target);
        }

        public EntryState CaptureState() => new(
            DownstreamToTarget.ToArray(),
            TargetToDownstreams.Select(pair =>
                new KeyValuePair<object, object[]>(pair.Key, pair.Value.ToArray())).ToArray());

        public void RestoreState(EntryState state)
        {
            DownstreamToTarget.Clear();
            foreach (var pair in state.DownstreamToTarget)
                DownstreamToTarget.Add(pair.Key, pair.Value);
            TargetToDownstreams.Clear();
            foreach (var pair in state.TargetToDownstreams)
                TargetToDownstreams.Add(pair.Key,
                    new HashSet<object>(pair.Value, ReferenceEqualityComparer.Instance));
        }
    }

    private sealed record EntryState(
        KeyValuePair<object, object?>[] DownstreamToTarget,
        KeyValuePair<object, object[]>[] TargetToDownstreams);
}
