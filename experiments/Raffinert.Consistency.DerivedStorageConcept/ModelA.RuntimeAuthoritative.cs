namespace Raffinert.Consistency.DerivedStorageConcept;

internal sealed class ModelARuntimeAuthoritative : IStorageSemantics
{
    public string Name => "A Runtime authoritative";
    public bool SynchronizeOnGet => false;
    public bool SynchronizeFreshIncrementalValues => false;
    public bool ReadFreshValueFromProperty => false;
    public string DeclarationMeaning => "Select(...).MaterializeTo(...) is an output mirror.";
}
