namespace AgentSessions.Model;

public record LaunchSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory);
