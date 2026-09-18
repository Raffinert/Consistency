namespace Raffinert.Consistency.DerivedStorageConcept;

internal sealed class ModelCPropertyBacked : IStorageSemantics
{
    public string Name => "C Property-backed (hybrid for runtime-only values)";
    public bool SynchronizeOnGet => true;
    public bool SynchronizeFreshIncrementalValues => true;
    public bool ReadFreshValueFromProperty => true;
    public string DeclarationMeaning => "Derive(target, compute) makes the target the storage contract.";
}
