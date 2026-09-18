namespace Raffinert.Consistency.DerivedStorageConcept;

internal sealed class ModelBGetSynchronizesProperty : IStorageSemantics
{
    public string Name => "B Get synchronizes property";
    public bool SynchronizeOnGet => true;
    public bool SynchronizeFreshIncrementalValues => false;
    public bool ReadFreshValueFromProperty => false;
    public string DeclarationMeaning => "Select(...).MaterializeTo(...) is a mirror synchronized by Get and boundaries.";
}
