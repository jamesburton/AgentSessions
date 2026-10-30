namespace AgentSessions.Model;

public record SessionInfo(
    string Agent,
    string Id,
    string? ProjectPath,
    string Title,
    DateTimeOffset LastActivity,
    long SizeBytes,
    string StorePath);

public record SkippedSession(string Path, string Reason);

public record DiscoveryResult(IReadOnlyList<SessionInfo> Sessions, IReadOnlyList<SkippedSession> Skipped);
