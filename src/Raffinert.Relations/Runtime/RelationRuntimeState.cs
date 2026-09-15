using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

internal interface IRelationRuntimeState
{
    IObjectSetDefinition LeftSet { get; }
    IObjectSetDefinition RightSet { get; }
    bool HasExactPropagation { get; }
    int MaterializedPairCount { get; }
    int ForwardIndexEntryCount { get; }
    int ReverseIndexEntryCount { get; }
    long PredicateEvaluationCount { get; }
    object CaptureState();
    void RestoreState(object snapshot);
    object CaptureTouchedState(
        IReadOnlyCollection<object> touchedLefts,
        IReadOnlyCollection<object> touchedRights);
    void RestoreTouchedState(object state);
    int GetTouchedStateEntryCount(object state);
    void ResetDiagnostics();
    void EnableExactPropagation();
    RelationDelta AddLeft(object instance);
    RelationDelta RemoveLeft(object instance);
    RelationDelta AddRight(object instance);
    RelationDelta RemoveRight(object instance);
    RelationDelta ReindexRight(object instance);
    void ReindexLeft(object instance);
    RelationDelta RefreshMembership(IEnumerable<object> lefts, IEnumerable<object> rights);
    IReadOnlyCollection<object> GetLeftsForRights(IEnumerable<object> rights);
}

internal sealed class RelationRuntimeState<TLeft, TRight> : IRelationRuntimeState
    where TLeft : class where TRight : class
{
    private readonly RelationDefinition<TLeft, TRight> _definition;
    private readonly ObjectSetRuntime _leftObjects;
    private readonly ObjectSetRuntime _rightObjects;
    private readonly Dictionary<CompositeKey, HashSet<TRight>> _index = [];
    private readonly Dictionary<TRight, CompositeKey> _keys = new(ReferenceEqualityComparer<TRight>.Instance);
    private readonly Dictionary<CompositeKey, HashSet<TLeft>> _leftIndex = [];
    private readonly Dictionary<TLeft, CompositeKey> _leftKeys = new(ReferenceEqualityComparer<TLeft>.Instance);
    private readonly Dictionary<TLeft, HashSet<TRight>> _rightsByLeft = new(ReferenceEqualityComparer<TLeft>.Instance);
    private readonly Dictionary<TRight, HashSet<TLeft>> _leftsByRight = new(ReferenceEqualityComparer<TRight>.Instance);
    private bool _hasExactPropagation;
    private long _predicateEvaluationCount;

    public RelationRuntimeState(
        RelationDefinition<TLeft, TRight> definition,
        ObjectSetRuntime leftObjects,
        ObjectSetRuntime rightObjects)
    {
        _definition = definition;
        _leftObjects = leftObjects;
        _rightObjects = rightObjects;
    }

    public IObjectSetDefinition LeftSet => _definition.Left;
    public IObjectSetDefinition RightSet => _definition.Right;
    public bool HasExactPropagation => _hasExactPropagation;
    public int MaterializedPairCount => _rightsByLeft.Sum(pair => pair.Value.Count);
    public int ForwardIndexEntryCount => _keys.Count;
    public int ReverseIndexEntryCount => _leftKeys.Count;
    public long PredicateEvaluationCount => _predicateEvaluationCount;

    public void ResetDiagnostics() => _predicateEvaluationCount = 0;

    public object CaptureState() => new State(
        Copy(_index, ReferenceEqualityComparer<TRight>.Instance),
        new Dictionary<TRight, CompositeKey>(_keys, ReferenceEqualityComparer<TRight>.Instance),
        Copy(_leftIndex, ReferenceEqualityComparer<TLeft>.Instance),
        new Dictionary<TLeft, CompositeKey>(_leftKeys, ReferenceEqualityComparer<TLeft>.Instance),
        Copy(_rightsByLeft, ReferenceEqualityComparer<TRight>.Instance, ReferenceEqualityComparer<TLeft>.Instance),
        Copy(_leftsByRight, ReferenceEqualityComparer<TLeft>.Instance, ReferenceEqualityComparer<TRight>.Instance),
        _predicateEvaluationCount);

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        Replace(_index, state.Index);
        Replace(_keys, state.Keys);
        Replace(_leftIndex, state.LeftIndex);
        Replace(_leftKeys, state.LeftKeys);
        Replace(_rightsByLeft, state.RightsByLeft);
        Replace(_leftsByRight, state.LeftsByRight);
        _predicateEvaluationCount = state.PredicateEvaluationCount;
    }

    public object CaptureTouchedState(
        IReadOnlyCollection<object> touchedLefts,
        IReadOnlyCollection<object> touchedRights)
    {
        var lefts = touchedLefts.Cast<TLeft>()
            .ToHashSet(ReferenceEqualityComparer<TLeft>.Instance);
        var rights = touchedRights.Cast<TRight>()
            .ToHashSet(ReferenceEqualityComparer<TRight>.Instance);

        foreach (var left in lefts.ToArray())
            if (_rightsByLeft.TryGetValue(left, out var related))
                rights.UnionWith(related);
        foreach (var right in rights.ToArray())
            if (_leftsByRight.TryGetValue(right, out var related))
                lefts.UnionWith(related);

        ExpandPotentialCounterparts(lefts, rights);
        foreach (var left in lefts.ToArray())
            if (_rightsByLeft.TryGetValue(left, out var related))
                rights.UnionWith(related);
        foreach (var right in rights.ToArray())
            if (_leftsByRight.TryGetValue(right, out var related))
                lefts.UnionWith(related);

        var rightKeys = new HashSet<CompositeKey>();
        foreach (var right in rights)
        {
            if (_keys.TryGetValue(right, out var oldKey))
                rightKeys.Add(oldKey);
            if (_definition.AccessPlan is HashJoinAccessPlan plan)
                rightKeys.Add(ReadRightKey(plan, right));
        }
        var leftKeys = new HashSet<CompositeKey>();
        foreach (var left in lefts)
        {
            if (_leftKeys.TryGetValue(left, out var oldKey))
                leftKeys.Add(oldKey);
            if (_definition.ReverseAccessPlan is HashJoinAccessPlan plan)
                leftKeys.Add(ReadLeftKey(plan, left));
        }

        return new TouchedState(
            rightKeys.ToDictionary(key => key,
                key => CaptureSet(_index, key, ReferenceEqualityComparer<TRight>.Instance)),
            rights.ToDictionary(right => right, right => Capture(_keys, right),
                ReferenceEqualityComparer<TRight>.Instance),
            leftKeys.ToDictionary(key => key,
                key => CaptureSet(_leftIndex, key, ReferenceEqualityComparer<TLeft>.Instance)),
            lefts.ToDictionary(left => left, left => Capture(_leftKeys, left),
                ReferenceEqualityComparer<TLeft>.Instance),
            lefts.ToDictionary(left => left,
                left => CaptureSet(_rightsByLeft, left, ReferenceEqualityComparer<TRight>.Instance),
                ReferenceEqualityComparer<TLeft>.Instance),
            rights.ToDictionary(right => right,
                right => CaptureSet(_leftsByRight, right, ReferenceEqualityComparer<TLeft>.Instance),
                ReferenceEqualityComparer<TRight>.Instance),
            _predicateEvaluationCount);
    }

    public void RestoreTouchedState(object snapshot)
    {
        var state = (TouchedState)snapshot;
        RestoreEntries(_index, state.Index, ReferenceEqualityComparer<TRight>.Instance);
        RestoreEntries(_keys, state.Keys);
        RestoreEntries(_leftIndex, state.LeftIndex, ReferenceEqualityComparer<TLeft>.Instance);
        RestoreEntries(_leftKeys, state.LeftKeys);
        RestoreEntries(_rightsByLeft, state.RightsByLeft, ReferenceEqualityComparer<TRight>.Instance);
        RestoreEntries(_leftsByRight, state.LeftsByRight, ReferenceEqualityComparer<TLeft>.Instance);
        _predicateEvaluationCount = state.PredicateEvaluationCount;
    }

    public int GetTouchedStateEntryCount(object snapshot)
    {
        var state = (TouchedState)snapshot;
        return Count(state.Index) + state.Keys.Count + Count(state.LeftIndex) + state.LeftKeys.Count +
            Count(state.RightsByLeft) + Count(state.LeftsByRight);
    }

    private void ExpandPotentialCounterparts(HashSet<TLeft> lefts, HashSet<TRight> rights)
    {
        if (_definition.AccessPlan is HashJoinAccessPlan access)
        {
            foreach (var left in lefts.ToArray())
            {
                var keys = new HashSet<CompositeKey> { ReadLeftKey(access, left) };
                if (_leftKeys.TryGetValue(left, out var oldKey))
                    keys.Add(oldKey);
                foreach (var key in keys)
                    if (_index.TryGetValue(key, out var bucket))
                        rights.UnionWith(bucket);
            }
        }
        else if (lefts.Count > 0)
            rights.UnionWith(_rightObjects.Instances.Cast<TRight>());

        if (_definition.ReverseAccessPlan is HashJoinAccessPlan reverse)
        {
            foreach (var right in rights.ToArray())
            {
                var keys = new HashSet<CompositeKey> { ReadRightKey(reverse, right) };
                if (_keys.TryGetValue(right, out var oldKey))
                    keys.Add(oldKey);
                foreach (var key in keys)
                    if (_leftIndex.TryGetValue(key, out var bucket))
                        lefts.UnionWith(bucket);
            }
        }
        else if (rights.Count > 0 && _hasExactPropagation)
            lefts.UnionWith(_leftObjects.Instances.Cast<TLeft>());
    }

    private static Entry<TValue> Capture<TKey, TValue>(Dictionary<TKey, TValue> values, TKey key)
        where TKey : notnull => values.TryGetValue(key, out var value)
        ? new Entry<TValue>(true, value)
        : new Entry<TValue>(false, default);

    private static Entry<HashSet<TValue>> CaptureSet<TKey, TValue>(
        Dictionary<TKey, HashSet<TValue>> values,
        TKey key,
        IEqualityComparer<TValue> comparer)
        where TKey : notnull where TValue : class => values.TryGetValue(key, out var value)
        ? new Entry<HashSet<TValue>>(true, value.ToHashSet(comparer))
        : new Entry<HashSet<TValue>>(false, null);

    private static void RestoreEntries<TKey, TValue>(
        Dictionary<TKey, TValue> target,
        IReadOnlyDictionary<TKey, Entry<TValue>> entries)
        where TKey : notnull
    {
        foreach (var pair in entries)
            if (pair.Value.Exists)
                target[pair.Key] = pair.Value.Value!;
            else
                target.Remove(pair.Key);
    }

    private static void RestoreEntries<TKey, TValue>(
        Dictionary<TKey, HashSet<TValue>> target,
        IReadOnlyDictionary<TKey, Entry<HashSet<TValue>>> entries,
        IEqualityComparer<TValue> comparer)
        where TKey : notnull where TValue : class
    {
        foreach (var pair in entries)
            if (pair.Value.Exists)
                target[pair.Key] = pair.Value.Value!.ToHashSet(comparer);
            else
                target.Remove(pair.Key);
    }

    private static int Count<TKey, TValue>(IReadOnlyDictionary<TKey, Entry<HashSet<TValue>>> entries)
        where TKey : notnull => entries.Count + entries.Values.Where(value => value.Exists)
        .Sum(value => value.Value!.Count);

    public int RelatedCount(TLeft left) =>
        _rightsByLeft.TryGetValue(left, out var rights) ? rights.Count : 0;

    public bool IsRelated(TLeft left, TRight right) =>
        _rightsByLeft.TryGetValue(left, out var rights) && rights.Contains(right);

    public void EnableExactPropagation() => _hasExactPropagation = true;

    public RelationDelta AddLeft(object instance)
    {
        var delta = new RelationDelta();
        var left = (TLeft)instance;
        if (_definition.ReverseAccessPlan is not null)
            AddLeftToIndex(left);
        if (!_hasExactPropagation)
            return delta;
        foreach (var right in Related(left))
            AddPair(left, right, delta);
        return delta;
    }

    public RelationDelta RemoveLeft(object instance)
    {
        var delta = new RelationDelta();
        var left = (TLeft)instance;
        if (_definition.ReverseAccessPlan is not null)
            RemoveLeftFromIndex(left);
        if (!_hasExactPropagation)
            return delta;
        if (!_rightsByLeft.Remove(left, out var rights))
            return delta;
        foreach (var right in rights)
        {
            RemoveReversePair(left, right);
            delta.Remove(left, right);
        }
        return delta;
    }

    public RelationDelta AddRight(object instance)
    {
        var delta = new RelationDelta();
        var right = (TRight)instance;
        AddToIndex(right);
        if (_hasExactPropagation)
            foreach (var left in RelatedFromRightCore(right))
                AddPair(left, right, delta);
        else if (_definition.PropagationPlan == RelationPropagationPlan.ConservativeInvalidation)
            foreach (var left in ConservativeCandidates(right))
                delta.Affect(left, right);
        return delta;
    }

    public RelationDelta RemoveRight(object instance)
    {
        var delta = new RelationDelta();
        var right = (TRight)instance;
        if (!_hasExactPropagation && _definition.PropagationPlan == RelationPropagationPlan.ConservativeInvalidation)
            foreach (var left in ConservativeCandidates(right))
                delta.Affect(left, right);
        if (_hasExactPropagation && _leftsByRight.Remove(right, out var lefts))
            foreach (var left in lefts)
            {
                _rightsByLeft[left].Remove(right);
                if (_rightsByLeft[left].Count == 0)
                    _rightsByLeft.Remove(left);
                delta.Remove(left, right);
            }
        RemoveFromIndex(right);
        return delta;
    }

    public IReadOnlyList<TRight> Related(TLeft left)
    {
        IEnumerable<TRight> candidates = _definition.AccessPlan switch
        {
            ScanAccessPlan => _rightObjects.Instances.Cast<TRight>(),
            HashJoinAccessPlan hashPlan => ReadHashCandidates(hashPlan, left),
            _ => throw new NotSupportedException($"Unsupported access plan '{_definition.AccessPlan.GetType().Name}'.")
        };
        return candidates.Where(right => Evaluate(left, right)).ToArray();
    }

    public IReadOnlyList<TLeft> RelatedFromRight(TRight right) => RelatedFromRightCore(right);

    public RelationDelta ReindexRight(object instance)
    {
        var delta = new RelationDelta();
        var right = (TRight)instance;
        if (!_hasExactPropagation && _definition.PropagationPlan == RelationPropagationPlan.ConservativeInvalidation)
            foreach (var left in ConservativeCandidates(right))
                delta.Affect(left, right);
        Reindex(right);
        if (!_hasExactPropagation && _definition.PropagationPlan == RelationPropagationPlan.ConservativeInvalidation)
            foreach (var left in ConservativeCandidates(right))
                delta.Affect(left, right);
        return delta;
    }

    public void ReindexLeft(object instance)
    {
        if (_definition.ReverseAccessPlan is null)
            return;
        var left = (TLeft)instance;
        RemoveLeftFromIndex(left);
        AddLeftToIndex(left);
    }

    public RelationDelta RefreshMembership(IEnumerable<object> lefts, IEnumerable<object> rights)
    {
        var delta = new RelationDelta();
        if (!_hasExactPropagation)
        {
            foreach (var left in lefts)
                delta.Affect(left, left);
            foreach (var right in rights.Cast<TRight>())
                foreach (var left in ConservativeCandidates(right))
                    delta.Affect(left, right);
            return delta;
        }

        var typedLefts = lefts.Cast<TLeft>().ToHashSet(ReferenceEqualityComparer<TLeft>.Instance);
        var typedRights = rights.Cast<TRight>().ToHashSet(ReferenceEqualityComparer<TRight>.Instance);
        foreach (var left in typedLefts)
        {
            var oldRights = _rightsByLeft.TryGetValue(left, out var existing)
                ? existing.ToHashSet(ReferenceEqualityComparer<TRight>.Instance)
                : new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance);
            var newRights = Related(left).ToHashSet(ReferenceEqualityComparer<TRight>.Instance);
            foreach (var right in oldRights.Except(newRights, ReferenceEqualityComparer<TRight>.Instance).ToArray())
                RemovePair(left, right, delta);
            foreach (var right in newRights.Except(oldRights, ReferenceEqualityComparer<TRight>.Instance))
                AddPair(left, right, delta);
        }

        foreach (var right in typedRights)
        {
            var oldLefts = _leftsByRight.TryGetValue(right, out var existing)
                ? existing.ToHashSet(ReferenceEqualityComparer<TLeft>.Instance)
                : new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance);
            var newLefts = RelatedFromRightCore(right)
                .ToHashSet(ReferenceEqualityComparer<TLeft>.Instance);
            foreach (var left in oldLefts.Except(newLefts, ReferenceEqualityComparer<TLeft>.Instance).ToArray())
                RemovePair(left, right, delta);
            foreach (var left in newLefts.Except(oldLefts, ReferenceEqualityComparer<TLeft>.Instance))
                AddPair(left, right, delta);
            foreach (var left in newLefts.Intersect(oldLefts, ReferenceEqualityComparer<TLeft>.Instance))
                delta.Affect(left, right, RelationImpactCauseKind.RelatedItemChanged, ImpactCausePrecision.Exact);
        }
        return delta;
    }

    public IReadOnlyCollection<object> GetLeftsForRights(IEnumerable<object> rights)
    {
        var lefts = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!_hasExactPropagation)
        {
            if (_definition.PropagationPlan == RelationPropagationPlan.ConservativeInvalidation)
                foreach (var right in rights.Cast<TRight>())
                    lefts.UnionWith(ConservativeCandidates(right));
            return lefts;
        }
        foreach (var right in rights.Cast<TRight>())
            if (_leftsByRight.TryGetValue(right, out var related))
                lefts.UnionWith(related);
        return lefts;
    }

    private void Reindex(TRight right)
    {
        RemoveFromIndex(right);
        AddToIndex(right);
    }

    private void AddToIndex(TRight right)
    {
        if (_definition.AccessPlan is not HashJoinAccessPlan hashPlan)
            return;
        var key = ReadRightKey(hashPlan, right);
        if (!_index.TryGetValue(key, out var bucket))
            _index.Add(key, bucket = new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance));
        bucket.Add(right);
        _keys[right] = key;
    }

    private void AddLeftToIndex(TLeft left)
    {
        if (_definition.ReverseAccessPlan is not HashJoinAccessPlan hashPlan)
            return;
        var key = ReadLeftKey(hashPlan, left);
        if (!_leftIndex.TryGetValue(key, out var bucket))
            _leftIndex.Add(key, bucket = new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance));
        bucket.Add(left);
        _leftKeys[left] = key;
    }

    private void RemoveLeftFromIndex(TLeft left)
    {
        if (!_leftKeys.Remove(left, out var key))
            return;
        if (!_leftIndex.TryGetValue(key, out var bucket))
            return;
        bucket.Remove(left);
        if (bucket.Count == 0)
            _leftIndex.Remove(key);
    }

    private void RemoveFromIndex(TRight right)
    {
        if (!_keys.Remove(right, out var key)) return;
        if (_index.TryGetValue(key, out var bucket))
        {
            bucket.Remove(right);
            if (bucket.Count == 0) _index.Remove(key);
        }
    }

    private void AddPair(TLeft left, TRight right, RelationDelta delta)
    {
        if (!_rightsByLeft.TryGetValue(left, out var rights))
            _rightsByLeft.Add(left, rights = new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance));
        if (!rights.Add(right))
            return;
        if (!_leftsByRight.TryGetValue(right, out var lefts))
            _leftsByRight.Add(right, lefts = new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance));
        lefts.Add(left);
        delta.Add(left, right);
    }

    private void RemovePair(TLeft left, TRight right, RelationDelta delta)
    {
        if (!_rightsByLeft.TryGetValue(left, out var rights) || !rights.Remove(right))
            return;
        if (rights.Count == 0)
            _rightsByLeft.Remove(left);
        RemoveReversePair(left, right);
        delta.Remove(left, right);
    }

    private void RemoveReversePair(TLeft left, TRight right)
    {
        if (!_leftsByRight.TryGetValue(right, out var lefts))
            return;
        lefts.Remove(left);
        if (lefts.Count == 0)
            _leftsByRight.Remove(right);
    }

    private IEnumerable<TRight> ReadHashCandidates(HashJoinAccessPlan plan, TLeft left)
    {
        var key = CreateKey(plan.JoinKeyParts.Select(part => part.Left.Read(left)), plan);
        return _index.TryGetValue(key, out var bucket) ? bucket : [];
    }

    private IReadOnlyList<TLeft> RelatedFromRightCore(TRight right)
    {
        IEnumerable<TLeft> candidates = _definition.ReverseAccessPlan switch
        {
            null or ScanAccessPlan => _leftObjects.Instances.Cast<TLeft>(),
            HashJoinAccessPlan hashPlan => ReadReverseHashCandidates(hashPlan, right),
            _ => throw new NotSupportedException(
                $"Unsupported reverse access plan '{_definition.ReverseAccessPlan.GetType().Name}'.")
        };
        return candidates.Where(left => Evaluate(left, right)).ToArray();
    }

    private IEnumerable<TLeft> ConservativeCandidates(TRight right)
    {
        if (_definition.ReverseAccessPlan is not HashJoinAccessPlan)
            return _leftObjects.Instances.Cast<TLeft>();
        return _keys.TryGetValue(right, out var key) && _leftIndex.TryGetValue(key, out var bucket)
            ? bucket
            : [];
    }

    private bool Evaluate(TLeft left, TRight right)
    {
        _predicateEvaluationCount++;
        return _definition.Predicate(left, right);
    }

    private IEnumerable<TLeft> ReadReverseHashCandidates(HashJoinAccessPlan plan, TRight right)
    {
        var key = ReadRightKey(plan, right);
        return _leftIndex.TryGetValue(key, out var bucket) ? bucket : [];
    }

    private static CompositeKey ReadLeftKey(HashJoinAccessPlan plan, TLeft left) =>
        CreateKey(plan.JoinKeyParts.Select(part => part.Left.Read(left)), plan);

    private static CompositeKey ReadRightKey(HashJoinAccessPlan plan, TRight right) =>
        CreateKey(plan.JoinKeyParts.Select(part => part.Right.Read(right)), plan);

    private static CompositeKey CreateKey(IEnumerable<object?> components, HashJoinAccessPlan plan) =>
        new(components.ToArray(), plan.JoinKeyParts.Select(part => part.Comparer).ToArray());

    private static Dictionary<TKey, HashSet<TValue>> Copy<TKey, TValue>(
        Dictionary<TKey, HashSet<TValue>> source,
        IEqualityComparer<TValue> valueComparer,
        IEqualityComparer<TKey>? keyComparer = null) where TKey : notnull where TValue : class =>
        source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet(valueComparer),
            keyComparer ?? source.Comparer);

    private static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source)
        where TKey : notnull
    {
        target.Clear();
        foreach (var pair in source)
            target.Add(pair.Key, pair.Value);
    }

    private sealed record State(
        Dictionary<CompositeKey, HashSet<TRight>> Index,
        Dictionary<TRight, CompositeKey> Keys,
        Dictionary<CompositeKey, HashSet<TLeft>> LeftIndex,
        Dictionary<TLeft, CompositeKey> LeftKeys,
        Dictionary<TLeft, HashSet<TRight>> RightsByLeft,
        Dictionary<TRight, HashSet<TLeft>> LeftsByRight,
        long PredicateEvaluationCount);

    private sealed record Entry<TValue>(bool Exists, TValue? Value);

    private sealed record TouchedState(
        IReadOnlyDictionary<CompositeKey, Entry<HashSet<TRight>>> Index,
        IReadOnlyDictionary<TRight, Entry<CompositeKey>> Keys,
        IReadOnlyDictionary<CompositeKey, Entry<HashSet<TLeft>>> LeftIndex,
        IReadOnlyDictionary<TLeft, Entry<CompositeKey>> LeftKeys,
        IReadOnlyDictionary<TLeft, Entry<HashSet<TRight>>> RightsByLeft,
        IReadOnlyDictionary<TRight, Entry<HashSet<TLeft>>> LeftsByRight,
        long PredicateEvaluationCount);

}

internal readonly struct CompositeKey : IEquatable<CompositeKey>
{
    private readonly object?[] _components;
    private readonly IReadOnlyList<IEqualityComparer<object?>> _comparers;

    public CompositeKey(object?[] components, IReadOnlyList<IEqualityComparer<object?>> comparers)
    {
        _components = components;
        _comparers = comparers;
    }

    public bool Equals(CompositeKey other)
    {
        if (_components.Length != other._components.Length)
            return false;
        for (var index = 0; index < _components.Length; index++)
            if (!_comparers[index].Equals(_components[index], other._components[index]))
                return false;
        return true;
    }
    public override bool Equals(object? obj) => obj is CompositeKey other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var index = 0; index < _components.Length; index++)
        {
            var component = _components[index];
            hash.Add(component is null ? 0 : _comparers[index].GetHashCode(component!));
        }
        return hash.ToHashCode();
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static ReferenceEqualityComparer<T> Instance { get; } = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
