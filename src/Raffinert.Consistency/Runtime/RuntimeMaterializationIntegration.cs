namespace Raffinert.Consistency;

internal interface IRuntimeMaterializationIntegration
{
    bool TryMaterializeObject(ConsistencyRuntime runtime, object source);

    bool TryMaterializeDerived(
        ConsistencyRuntime runtime,
        IDerivedDefinition definition,
        object source,
        out object? value);
}
