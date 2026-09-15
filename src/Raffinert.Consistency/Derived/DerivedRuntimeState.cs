using System.Linq.Expressions;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

internal sealed class SourceDerivedRuntimeState<TSource, TValue>(
    IDerivedDefinition definition,
    Func<TSource, TValue> computation) : IDerivedRuntimeState
    where TSource : class
{
    private readonly Dictionary<TSource, CacheEntry> _cache = new(ReferenceEqualityComparer<TSource>.Instance);
    public IDerivedDefinition Definition => definition;
    public IObjectSetDefinition SourceSet => definition.SourceSet;
    public int SourceStateEntryCount => _cache.Count;
    public long FullRecomputationCount { get; private set; }
    public long IncrementalUpdateCount => 0;

    public object? GetValue(object source)
    {
        var typed = (TSource)source;
        if (_cache.TryGetValue(typed, out var entry) && entry.State == DerivedValueState.Fresh)
            return entry.Value;
        var value = computation(typed);
        FullRecomputationCount++;
        _cache[typed] = new CacheEntry(value, DerivedValueState.Fresh);
        return value;
    }

    public DerivedValueState GetValueState(object source) =>
        _cache.TryGetValue((TSource)source, out var entry) ? entry.State : DerivedValueState.Dirty;

    public void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact)
    {
        foreach (var source in sources.Cast<TSource>())
            if (_cache.TryGetValue(source, out var entry))
                entry.State = DependencyStateTransitions.Apply(entry.State, impact);
    }

    public IReadOnlyCollection<object> ApplyIncremental(
        IEnumerable<object> sources,
        RelationImpact? relationImpact,
        IReadOnlyList<PropertyChange> changes) => [];

    public void ResetDiagnostics() => FullRecomputationCount = 0;
    public object CaptureState() => new State(
        _cache.ToDictionary(pair => pair.Key, pair => new CacheEntry(pair.Value.Value, pair.Value.State),
            ReferenceEqualityComparer<TSource>.Instance),
        FullRecomputationCount);

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        _cache.Clear();
        foreach (var pair in state.Cache)
            _cache.Add(pair.Key, pair.Value);
        FullRecomputationCount = state.FullRecomputationCount;
    }

    public object CaptureSourcesState(IEnumerable<object> sources) => new SourcesState(
        sources.Cast<TSource>().Distinct(ReferenceEqualityComparer<TSource>.Instance)
            .ToDictionary(
                source => source,
                source => _cache.TryGetValue(source, out var entry)
                    ? new SourceEntry(true, entry.Value, entry.State)
                    : new SourceEntry(false, default, default),
                ReferenceEqualityComparer<TSource>.Instance),
        FullRecomputationCount);

    public void RestoreSourcesState(object snapshot)
    {
        var state = (SourcesState)snapshot;
        foreach (var pair in state.Entries)
            if (pair.Value.Exists)
                _cache[pair.Key] = new CacheEntry(pair.Value.Value!, pair.Value.State);
            else
                _cache.Remove(pair.Key);
        FullRecomputationCount = state.FullRecomputationCount;
    }

    public int GetSourcesStateEntryCount(object state) => ((SourcesState)state).Entries.Count;

    public void OnSourceAdded(object source) => _cache.Remove((TSource)source);
    public void OnSourceRemoved(object source) => _cache.Remove((TSource)source);

    private sealed class CacheEntry(TValue value, DerivedValueState state)
    {
        public TValue Value { get; } = value;
        public DerivedValueState State { get; set; } = state;
    }

    private sealed record State(Dictionary<TSource, CacheEntry> Cache, long FullRecomputationCount);
    private sealed record SourceEntry(bool Exists, TValue? Value, DerivedValueState State);
    private sealed record SourcesState(
        IReadOnlyDictionary<TSource, SourceEntry> Entries,
        long FullRecomputationCount);
}

internal interface IDerivedRuntimeState : ISourceLifecycleParticipant
{
    IDerivedDefinition Definition { get; }
    int SourceStateEntryCount { get; }
    long FullRecomputationCount { get; }
    long IncrementalUpdateCount { get; }
    void ResetDiagnostics();
    object CaptureState();
    void RestoreState(object snapshot);
    object CaptureSourcesState(IEnumerable<object> sources);
    void RestoreSourcesState(object state);
    int GetSourcesStateEntryCount(object state);
    object? GetValue(object source);
    DerivedValueState GetValueState(object source);
    void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact);
    IReadOnlyCollection<object> ApplyIncremental(
        IEnumerable<object> sources,
        RelationImpact? relationImpact,
        IReadOnlyList<PropertyChange> changes);
}

internal sealed class DerivedRuntimeState<TSource, TItem, TValue>(
    DerivedDefinition<TSource, TItem, TValue> definition,
    RelationRuntimeState<TSource, TItem> relationState) : IDerivedRuntimeState
    where TSource : class
    where TItem : class
{
    private readonly Dictionary<TSource, CacheEntry> _cache = new(ReferenceEqualityComparer<TSource>.Instance);

    public IDerivedDefinition Definition => definition;
    public IObjectSetDefinition SourceSet => definition.SourceSet;
    public int SourceStateEntryCount => _cache.Count;
    public long FullRecomputationCount { get; private set; }
    public long IncrementalUpdateCount { get; private set; }

    public TValue Get(TSource source)
    {
        if (_cache.TryGetValue(source, out var entry) && entry.State == DerivedValueState.Fresh)
            return entry.Value;
        var value = definition.Computation(source, relationState.Related(source));
        FullRecomputationCount++;
        _cache[source] = new CacheEntry(value, DerivedValueState.Fresh);
        return value;
    }

    public DerivedValueState GetState(TSource source) =>
        _cache.TryGetValue(source, out var entry) ? entry.State : DerivedValueState.Dirty;

    public object? GetValue(object source) => Get((TSource)source);
    public DerivedValueState GetValueState(object source) => GetState((TSource)source);

    public void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact)
    {
        foreach (var source in sources.Cast<TSource>())
        {
            if (!_cache.TryGetValue(source, out var entry))
                continue;
            entry.State = DependencyStateTransitions.Apply(entry.State, impact);
        }
    }

    public IReadOnlyCollection<object> ApplyIncremental(
        IEnumerable<object> sources,
        RelationImpact? relationImpact,
        IReadOnlyList<PropertyChange> changes)
    {
        if (definition.IncrementalPlan is null)
            return [];
        var updatedSources = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var source in sources.Cast<TSource>())
        {
            if (!_cache.TryGetValue(source, out var entry) || entry.State != DerivedValueState.Fresh ||
                !definition.IncrementalPlan.TryUpdate(
                    entry.Value, source, relationImpact, changes, relationState, out var updated))
                continue;
            entry.Value = updated;
            IncrementalUpdateCount++;
            updatedSources.Add(source);
        }
        return updatedSources;
    }

    public void ResetDiagnostics()
    {
        FullRecomputationCount = 0;
        IncrementalUpdateCount = 0;
    }

    public object CaptureState() => new State(
        _cache.ToDictionary(
            pair => pair.Key,
            pair => new CacheEntry(pair.Value.Value, pair.Value.State),
            ReferenceEqualityComparer<TSource>.Instance),
        FullRecomputationCount,
        IncrementalUpdateCount);

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        _cache.Clear();
        foreach (var pair in state.Cache)
            _cache.Add(pair.Key, pair.Value);
        FullRecomputationCount = state.FullRecomputationCount;
        IncrementalUpdateCount = state.IncrementalUpdateCount;
    }

    public object CaptureSourcesState(IEnumerable<object> sources) => new SourcesState(
        sources.Cast<TSource>().Distinct(ReferenceEqualityComparer<TSource>.Instance)
            .ToDictionary(
                source => source,
                source => _cache.TryGetValue(source, out var entry)
                    ? new SourceEntry(true, entry.Value, entry.State)
                    : new SourceEntry(false, default, default),
                ReferenceEqualityComparer<TSource>.Instance),
        FullRecomputationCount,
        IncrementalUpdateCount);

    public void RestoreSourcesState(object snapshot)
    {
        var state = (SourcesState)snapshot;
        foreach (var pair in state.Entries)
            if (pair.Value.Exists)
                _cache[pair.Key] = new CacheEntry(pair.Value.Value!, pair.Value.State);
            else
                _cache.Remove(pair.Key);
        FullRecomputationCount = state.FullRecomputationCount;
        IncrementalUpdateCount = state.IncrementalUpdateCount;
    }

    public int GetSourcesStateEntryCount(object state) => ((SourcesState)state).Entries.Count;

    public void OnSourceAdded(object source) => _cache.Remove((TSource)source);

    public void OnSourceRemoved(object source) => _cache.Remove((TSource)source);

    private sealed class CacheEntry(TValue value, DerivedValueState state)
    {
        public TValue Value { get; set; } = value;
        public DerivedValueState State { get; set; } = state;
    }

    private sealed record State(
        Dictionary<TSource, CacheEntry> Cache,
        long FullRecomputationCount,
        long IncrementalUpdateCount);

    private sealed record SourceEntry(bool Exists, TValue? Value, DerivedValueState State);
    private sealed record SourcesState(
        IReadOnlyDictionary<TSource, SourceEntry> Entries,
        long FullRecomputationCount,
        long IncrementalUpdateCount);
}

