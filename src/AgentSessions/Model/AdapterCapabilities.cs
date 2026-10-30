namespace AgentSessions.Model;

[Flags]
public enum AdapterCapabilities
{
    List = 1,
    Resume = 2,
    LaunchWithPrompt = 4,
    Transcript = 8,
    Archive = 16,
    Delete = 32
}
