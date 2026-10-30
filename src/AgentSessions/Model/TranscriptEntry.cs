namespace AgentSessions.Model;

public enum Role { User, Assistant, Tool, System }

public record TranscriptEntry(
    Role Role,
    string Text,
    DateTimeOffset? Timestamp,
    string? ToolName,
    IReadOnlyList<string> FilesTouched);
