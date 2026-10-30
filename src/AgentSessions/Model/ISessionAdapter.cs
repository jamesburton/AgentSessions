namespace AgentSessions.Model;

public interface ISessionAdapter
{
    string Name { get; }
    AdapterCapabilities Capabilities { get; }
    DiscoveryResult Discover();
    IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s);   // streaming
    LaunchSpec BuildResume(SessionInfo s);
    LaunchSpec BuildNew(string projectPath, string prompt);
    IReadOnlyList<string> OwnedPaths(SessionInfo s);              // files/dirs to archive or delete
    void AfterRemove(SessionInfo s) { }                           // update agent index if any; default no-op
}
