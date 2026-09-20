namespace Raffinert.Consistency;

public sealed partial class ConsistencyRuntime
{
    private IRuntimeMaterializationIntegration? _materializationIntegration;
    private IReadOnlyList<MaterializationDescriptor> _materializations = [];
    private IReadOnlyDictionary<IDerivedDefinition, MaterializationDescriptor> _materializationByDefinition =
        new Dictionary<IDerivedDefinition, MaterializationDescriptor>();
    private IReadOnlyDictionary<IDerivedDefinition, int> _materializationOrder =
        new Dictionary<IDerivedDefinition, int>();
    private IReadOnlyDictionary<IObjectSetDefinition, IReadOnlyList<MaterializationDescriptor>>
        _materializationsBySet =
            new Dictionary<IObjectSetDefinition, IReadOnlyList<MaterializationDescriptor>>();
    private readonly Dictionary<object, HashSet<IObjectSetDefinition>> _sourceMemberships =
        new(ReferenceEqualityComparer.Instance);

    internal IReadOnlyList<MaterializationDescriptor> Materializations => _materializations;

    internal void AttachMaterializationIntegration(IRuntimeMaterializationIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(integration);
        if (_materializationIntegration is not null &&
            !ReferenceEquals(_materializationIntegration, integration))
            throw new InvalidOperationException("A materialization integration is already attached to this runtime.");
        _materializationIntegration = integration;
    }

    private void InitializeMaterializations(IReadOnlyList<MaterializationDescriptor> materializations)
    {
        _materializations = materializations;
        _materializationByDefinition = materializations.ToDictionary(value => value.Definition);
        _materializationOrder = materializations.Select((value, order) => (value.Definition, order))
            .ToDictionary(value => value.Definition, value => value.order);
        _materializationsBySet = materializations
            .GroupBy(value => value.SourceSet)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MaterializationDescriptor>)group.ToArray());
    }

    /// <summary>Makes the logical value current and returns it without writing a materialized mirror.</summary>
    public TValue Evaluate<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (!_derivedStates.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived state does not belong to this compiled model.", nameof(derived));
        EnsureRegistered(derived.Definition.SourceSet, source, "source");
        ValidateProjectedTargets(derived.Definition, source);
        return (TValue)state.GetValue(source)!;
    }

    /// <summary>Evaluates and synchronizes exactly one configured materialized representation.</summary>
    public TValue Materialize<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (_materializationIntegration?.TryMaterializeDerived(
                this, derived.Definition, source, out var integratedValue) == true)
            return (TValue)integratedValue!;
        if (!_derivedStates.ContainsKey(derived.Definition))
            throw new ArgumentException("The derived definition belongs to another compiled model.", nameof(derived));
        if (!_materializationByDefinition.TryGetValue(derived.Definition, out var descriptor))
            throw new InvalidOperationException(
                "This derived definition has no materialization target. Use Evaluate instead.");
        EnsureRegistered(derived.Definition.SourceSet, source, "source");
        var value = Evaluate(derived, source);
        ApplyMaterializations([Prepare(descriptor, source, value)]);
        return value;
    }

    /// <summary>
    /// Synchronizes every configured representation physically located on the registered source object.
    /// Logical values are prepared before any property is written; repair requests are not dispatched.
    /// </summary>
    public void Materialize<TSource>(TSource source) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_materializationIntegration?.TryMaterializeObject(this, source) == true)
            return;
        if (!_sourceMemberships.TryGetValue(source, out var memberships) || memberships.Count == 0)
            throw new InvalidOperationException("The source instance is not registered in this runtime.");
        var descriptors = memberships
            .SelectMany(set => _materializationsBySet.TryGetValue(set, out var values) ? values : [])
            .DistinctBy(value => value.Definition)
            .OrderBy(value => _materializationOrder[value.Definition])
            .ToArray();
        if (descriptors.Length == 0)
            return;

        var prepared = new List<PreparedMaterialization>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            ValidateProjectedTargets(descriptor.Definition, source);
            var value = _derivedStates[descriptor.Definition].GetValue(source);
            prepared.Add(Prepare(descriptor, source, value));
        }
        ApplyMaterializations(prepared);
    }

    private static PreparedMaterialization Prepare(
        MaterializationDescriptor descriptor,
        object source,
        object? value) => new(descriptor, source, value, descriptor.Read(source));

    private static void ApplyMaterializations(IReadOnlyList<PreparedMaterialization> prepared)
    {
        var applied = new Stack<PreparedMaterialization>();
        try
        {
            foreach (var write in prepared)
            {
                if (write.Descriptor.ValuesEqual(write.Previous, write.Value))
                    continue;
                applied.Push(write);
                write.Descriptor.Write(write.Source, write.Value);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            while (applied.TryPop(out var write))
            {
                try
                {
                    if (!write.Descriptor.ValuesEqual(write.Descriptor.Read(write.Source), write.Previous))
                        write.Descriptor.Write(write.Source, write.Previous);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException(
                    "Materialization failed and physical rollback was incomplete.",
                    [error, .. rollbackErrors]);
            throw;
        }
    }

    private void AddSourceMembership(IObjectSetDefinition set, object source)
    {
        if (!_sourceMemberships.TryGetValue(source, out var memberships))
            _sourceMemberships.Add(source, memberships = new HashSet<IObjectSetDefinition>());
        memberships.Add(set);
    }

    private void RemoveSourceMembership(IObjectSetDefinition set, object source)
    {
        if (!_sourceMemberships.TryGetValue(source, out var memberships))
            return;
        memberships.Remove(set);
        if (memberships.Count == 0)
            _sourceMemberships.Remove(source);
    }

    private void SynchronizeSourceMembership(IObjectSetDefinition set, object source)
    {
        if (_sets[set].Contains(source))
            AddSourceMembership(set, source);
        else
            RemoveSourceMembership(set, source);
    }

    private sealed record PreparedMaterialization(
        MaterializationDescriptor Descriptor,
        object Source,
        object? Value,
        object? Previous);
}
