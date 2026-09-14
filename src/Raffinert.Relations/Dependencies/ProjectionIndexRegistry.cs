namespace Raffinert.Relations;

using System.Reflection;

internal sealed class ProjectionIndexRegistry
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly IReadOnlyList<Entry> _entries;
    private readonly IReadOnlyDictionary<ProjectedUpstreamDerivedInput, Entry> _entryByInput;

    public ProjectionIndexRegistry(
        IReadOnlyList<IDerivedDefinition> definitions,
        IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets)
    {
        _sets = sets;
        var inputs = definitions.SelectMany(definition => definition.Inputs
            .OfType<ProjectedUpstreamDerivedInput>()
            .Select(input => (DownstreamSet: definition.SourceSet, Input: input))).ToArray();
        ConsumerCount = inputs.Length;
        var entries = new Dictionary<ProjectionEdgeKey, Entry>(ProjectionEdgeKeyComparer.Instance);
        var entryByInput = new Dictionary<ProjectedUpstreamDerivedInput, Entry>(ReferenceEqualityComparer.Instance);
        foreach (var (downstreamSet, input) in inputs)
        {
            var key = new ProjectionEdgeKey(
                downstreamSet,
                input.SelectorPath.Segments.Single().Member,
                input.UpstreamSet);
            if (!entries.TryGetValue(key, out var entry))
                entries.Add(key, entry = new Entry(downstreamSet, input));
            entryByInput.Add(input, entry);
        }
        _entries = entries.Values.ToArray();
        _entryByInput = entryByInput;
    }

    public int ConsumerCount { get; }
    public int EdgeCount => _entries.Count;
    public int ReverseEntryCount => _entries.Sum(entry => entry.DownstreamToTarget.Count);
    public int TargetCount => _entries.Sum(entry => entry.TargetToDownstreams.Count);

    public bool IsSelectorChange(IObjectSetDefinition set, MemberInfo member) =>
        _entries.Any(entry => ReferenceEquals(entry.DownstreamSet, set) &&
            entry.Input.SelectorPath.Segments.Any(segment => segment.Member == member));

    public bool IsDownstreamSet(IObjectSetDefinition set) =>
        _entries.Any(entry => ReferenceEquals(entry.DownstreamSet, set));

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
        var entry = _entryByInput[input];
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

    public void ValidateFinalState(
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes)
    {
        var view = new FinalSetMembershipView(_sets, lifecycleMutations);
        foreach (var entry in _entries)
        {
            var touchedDownstreams = lifecycleMutations
                .OfType<ObjectAdded>()
                .Where(value => ReferenceEquals(value.Set, entry.DownstreamSet))
                .Select(value => value.Instance)
                .Concat(changes.Where(value =>
                        ReferenceEquals(value.Set, entry.DownstreamSet) &&
                        value.Member == entry.SelectorMember)
                    .Select(value => value.Instance))
                .Distinct(ReferenceEqualityComparer.Instance);
            foreach (var source in touchedDownstreams)
            {
                if (!view.Contains(entry.DownstreamSet, source))
                    continue;
                ValidateTarget(entry, entry.Input.Project(source), view);
            }

            foreach (var removed in lifecycleMutations.OfType<ObjectRemoved>()
                         .Where(value => ReferenceEquals(value.Set, entry.Input.UpstreamSet)))
            {
                if (!entry.TargetToDownstreams.TryGetValue(removed.Instance, out var downstreams))
                    continue;
                foreach (var source in downstreams)
                    if (view.Contains(entry.DownstreamSet, source) &&
                        ReferenceEquals(entry.Input.Project(source), removed.Instance))
                        throw new InvalidOperationException(
                            "A projected dependency target cannot be removed while a registered downstream source still references it.");
            }
        }
    }

    private static void ValidateTarget(Entry entry, object? target, FinalSetMembershipView view)
    {
        if (target is null)
            throw new InvalidOperationException("A projected dependency target cannot be null.");
        if (!view.Contains(entry.Input.UpstreamSet, target))
            throw new InvalidOperationException(
                $"The projected target selected by '{entry.Input.SelectorExpression}' is not registered " +
                "in the exact upstream object set.");
    }

    public object CaptureState(
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes) => _entries.Select(entry => new EntryPatchState(
            entry,
            entry.CaptureSources(lifecycleMutations.Select(mutation => mutation switch
                {
                    ObjectAdded added when ReferenceEquals(added.Set, entry.DownstreamSet) => added.Instance,
                    ObjectRemoved removed when ReferenceEquals(removed.Set, entry.DownstreamSet) => removed.Instance,
                    _ => null
                }).OfType<object>()
                .Concat(changes.Where(change => ReferenceEquals(change.Set, entry.DownstreamSet) &&
                        change.Member == entry.SelectorMember)
                    .Select(change => change.Instance)))))
        .Where(state => state.Sources.Length > 0)
        .ToArray();

    public void RestoreState(object snapshot)
    {
        foreach (var state in (EntryPatchState[])snapshot)
            state.Entry.RestoreSources(state.Sources);
    }

    private sealed class Entry(
        IObjectSetDefinition downstreamSet,
        ProjectedUpstreamDerivedInput input)
    {
        public IObjectSetDefinition DownstreamSet => downstreamSet;
        public ProjectedUpstreamDerivedInput Input => input;
        public MemberInfo SelectorMember => input.SelectorPath.Segments.Single().Member;
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

        public SourceState[] CaptureSources(IEnumerable<object> sources) => sources
            .Distinct(ReferenceEqualityComparer.Instance)
            .Select(source => DownstreamToTarget.TryGetValue(source, out var target)
                ? new SourceState(source, true, target)
                : new SourceState(source, false, null))
            .ToArray();

        public void RestoreSources(IEnumerable<SourceState> states)
        {
            foreach (var state in states)
            {
                Remove(state.Source);
                if (!state.Exists)
                    continue;
                DownstreamToTarget.Add(state.Source, state.Target);
                if (state.Target is null)
                    continue;
                if (!TargetToDownstreams.TryGetValue(state.Target, out var downstreams))
                    TargetToDownstreams.Add(state.Target,
                        downstreams = new HashSet<object>(ReferenceEqualityComparer.Instance));
                downstreams.Add(state.Source);
            }
        }
    }

    private sealed record EntryPatchState(Entry Entry, SourceState[] Sources);
    private sealed record SourceState(object Source, bool Exists, object? Target);

    private sealed class FinalSetMembershipView
    {
        private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
        private readonly HashSet<(IObjectSetDefinition Set, object Instance)> _added =
            new(SetInstanceComparer.Instance);
        private readonly HashSet<(IObjectSetDefinition Set, object Instance)> _removed =
            new(SetInstanceComparer.Instance);

        public FinalSetMembershipView(
            IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets,
            IReadOnlyList<RuntimeMutation> mutations)
        {
            _sets = sets;
            foreach (var mutation in mutations)
                if (mutation is ObjectAdded added)
                    _added.Add((added.Set, added.Instance));
                else if (mutation is ObjectRemoved removed)
                    _removed.Add((removed.Set, removed.Instance));
        }

        public bool Contains(IObjectSetDefinition set, object instance) =>
            _added.Contains((set, instance)) ||
            (!_removed.Contains((set, instance)) && _sets[set].Contains(instance));
    }

    private sealed class SetInstanceComparer : IEqualityComparer<(IObjectSetDefinition Set, object Instance)>
    {
        public static SetInstanceComparer Instance { get; } = new();
        public bool Equals(
            (IObjectSetDefinition Set, object Instance) left,
            (IObjectSetDefinition Set, object Instance) right) =>
            ReferenceEquals(left.Set, right.Set) && ReferenceEquals(left.Instance, right.Instance);
        public int GetHashCode((IObjectSetDefinition Set, object Instance) value) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Set),
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Instance));
    }

    private readonly record struct ProjectionEdgeKey(
        IObjectSetDefinition DownstreamSet,
        MemberInfo SelectorMember,
        IObjectSetDefinition UpstreamSet);

    private sealed class ProjectionEdgeKeyComparer : IEqualityComparer<ProjectionEdgeKey>
    {
        public static ProjectionEdgeKeyComparer Instance { get; } = new();

        public bool Equals(ProjectionEdgeKey left, ProjectionEdgeKey right) =>
            ReferenceEquals(left.DownstreamSet, right.DownstreamSet) &&
            left.SelectorMember == right.SelectorMember &&
            ReferenceEquals(left.UpstreamSet, right.UpstreamSet);

        public int GetHashCode(ProjectionEdgeKey value) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.DownstreamSet),
            value.SelectorMember,
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.UpstreamSet));
    }
}
