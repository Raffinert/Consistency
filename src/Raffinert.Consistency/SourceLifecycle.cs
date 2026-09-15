namespace Raffinert.Consistency;

internal interface ISourceLifecycleParticipant
{
    IObjectSetDefinition SourceSet { get; }
    void OnSourceAdded(object source);
    void OnSourceRemoved(object source);
}
