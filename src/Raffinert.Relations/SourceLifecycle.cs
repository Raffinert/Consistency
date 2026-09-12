namespace Raffinert.Relations;

internal interface ISourceLifecycleParticipant
{
    IObjectSetDefinition SourceSet { get; }
    void OnSourceAdded(object source);
    void OnSourceRemoved(object source);
}
