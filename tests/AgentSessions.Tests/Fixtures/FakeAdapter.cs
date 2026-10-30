using AgentSessions.Model;

namespace AgentSessions.Tests.Fixtures;

public sealed class FakeAdapter : ISessionAdapter
{
    private readonly Func<DiscoveryResult> _discover;

    public FakeAdapter(string name, AdapterCapabilities capabilities, Func<DiscoveryResult> discover)
    {
        Name = name;
        Capabilities = capabilities;
        _discover = discover;
    }

    public FakeAdapter(string name, AdapterCapabilities capabilities, IEnumerable<SessionInfo> sessions,
        IEnumerable<SkippedSession>? skipped = null)
        : this(name, capabilities, () => new DiscoveryResult(sessions.ToList(), (skipped ?? []).ToList())) { }

    public static FakeAdapter Throwing(string name, Exception ex) =>
        new(name, AdapterCapabilities.List, () => throw ex);

    public string Name { get; }
    public AdapterCapabilities Capabilities { get; }
    public DiscoveryResult Discover() => _discover();
    public IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s) => [];
    public LaunchSpec BuildResume(SessionInfo s) => new(Name, ["resume", s.Id], s.ProjectPath ?? ".");
    public LaunchSpec BuildNew(string projectPath, string prompt) => new(Name, [prompt], projectPath);
    public IReadOnlyList<string> OwnedPaths(SessionInfo s) => [s.StorePath];
}
