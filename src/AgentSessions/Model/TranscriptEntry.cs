namespace AgentSessions.Model;

public enum Role { User, Assistant, Tool, System }

/// <summary>
/// One transcript item. Contract for <see cref="Role.Tool"/> entries, shared by every adapter and the handoff builder:
/// <list type="bullet">
/// <item>A tool INVOCATION has a non-null <see cref="ToolName"/>; its <see cref="Text"/> is a short description of the
/// call (the command line for shell tools, else a one-line summary of the input), and <see cref="FilesTouched"/>
/// holds any file paths the call names.</item>
/// <item>A tool OUTPUT (result) has <see cref="ToolName"/> null; its <see cref="Text"/> is the raw output, which can be
/// very large (consumers may omit it and only count it, see <see cref="IsToolOutput"/>).</item>
/// </list>
/// </summary>
public record TranscriptEntry(
    Role Role,
    string Text,
    DateTimeOffset? Timestamp,
    string? ToolName,
    IReadOnlyList<string> FilesTouched)
{
    /// <summary>True for a tool result (Role.Tool without a tool name): the output marker of the contract above.</summary>
    public bool IsToolOutput => Role == Role.Tool && ToolName is null;

    /// <summary>True for a tool call (Role.Tool with a tool name).</summary>
    public bool IsToolCall => Role == Role.Tool && ToolName is not null;
}
