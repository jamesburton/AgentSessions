using System.Text;
using AgentSessions.Model;

namespace AgentSessions.Services;

public record HandoffOptions(int MaxRecentEntries = 20, int MaxCharsPerEntry = 2000, int MaxCommands = 10);

/// <summary>
/// Builds a Markdown handoff from a transcript by direct extraction (no model, no network).
/// Single streaming pass; memory is bounded by the options, not by transcript length.
/// </summary>
public static class HandoffBuilder
{
    private const int MaxFilesListed = 50;
    private const int MaxCommandChars = 300;

    private static readonly HashSet<string> ShellToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "shell", "shell_command", "powershell", "cmd", "exec_command", "run_command", "local_shell",
    };

    public static string Build(SessionInfo s, IEnumerable<TranscriptEntry> transcript, HandoffOptions? options = null)
    {
        options ??= new HandoffOptions();
        var maxRecent = Math.Max(0, options.MaxRecentEntries);
        var maxCommands = Math.Max(0, options.MaxCommands);
        var maxChars = Math.Max(1, options.MaxCharsPerEntry);

        Queue<(Role Role, string Text)> recent = new();
        Queue<string> commands = new();
        HashSet<string> seenFiles = new(StringComparer.Ordinal);
        List<string> files = [];
        string? request = null;
        string? fallbackRequest = null;
        (Role Role, string Text)? last = null;

        foreach (var e in transcript)
        {
            var text = e.Text ?? "";

            if (e.Role == Role.User && request is null)
            {
                var trimmed = text.TrimStart();
                if (trimmed.Length > 0)
                {
                    if (trimmed[0] != '<') request = Truncate(text.Trim(), maxChars);
                    else fallbackRequest ??= Truncate(text.Trim(), maxChars);
                }
            }

            if (e.FilesTouched is { } touched)
                foreach (var f in touched)
                    if (!string.IsNullOrWhiteSpace(f) && seenFiles.Add(f) && files.Count < MaxFilesListed)
                        files.Add(f);

            if (e.Role == Role.System) continue;

            var shown = (e.Role, Truncate(text.Trim(), maxChars));
            last = shown;

            if (maxRecent > 0)
            {
                if (recent.Count == maxRecent) recent.Dequeue();
                recent.Enqueue(shown);
            }

            if (maxCommands > 0 && e.Role == Role.Tool && e.ToolName is { } tn && ShellToolNames.Contains(tn.Trim()))
            {
                if (commands.Count == maxCommands) commands.Dequeue();
                commands.Enqueue(OneLine(text, MaxCommandChars));
            }
        }

        request ??= fallbackRequest;

        var sb = new StringBuilder();
        sb.Append("# Session handoff\n\n");

        sb.Append("## Source\n\n");
        sb.Append("- Agent: ").Append(s.Agent).Append('\n');
        sb.Append("- Session id: ").Append(s.Id).Append('\n');
        sb.Append("- Full original session: ").Append(s.StorePath).Append('\n');
        sb.Append("- Project: ").Append(string.IsNullOrEmpty(s.ProjectPath) ? "(unknown)" : s.ProjectPath).Append('\n');
        sb.Append("- Last activity: ").Append(s.LastActivity.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');

        sb.Append("\n## Original request\n\n");
        sb.Append(request is null ? "_(none: no user prompt found in the transcript)_" : request).Append('\n');

        sb.Append("\n## Recent activity\n\n");
        if (recent.Count == 0) sb.Append("_(none: no activity found in the transcript)_\n");
        foreach (var (role, text) in recent)
            sb.Append("- **").Append(role).Append("**: ").Append(Indent(text)).Append('\n');

        sb.Append("\n## Files touched\n\n");
        if (files.Count == 0) sb.Append("_(none recorded)_\n");
        foreach (var f in files) sb.Append("- ").Append(f).Append('\n');
        if (seenFiles.Count > files.Count)
            sb.Append("- ...and ").Append(seenFiles.Count - files.Count).Append(" more\n");

        sb.Append("\n## Last commands\n\n");
        if (commands.Count == 0) sb.Append("_(none recorded)_\n");
        else
        {
            sb.Append("```\n");
            foreach (var cmd in commands) sb.Append(cmd.Replace("```", "'''")).Append('\n');
            sb.Append("```\n");
        }

        sb.Append("\n## Where it stopped\n\n");
        if (last is { } l) sb.Append("**").Append(l.Role).Append("**: ").Append(l.Text).Append('\n');
        else sb.Append("_(none: the transcript is empty)_\n");

        return sb.ToString();
    }

    private static string Truncate(string text, int max)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return text.Length <= max ? text : text[..max] + $"… [truncated, {text.Length - max} chars omitted]";
    }

    private static string OneLine(string text, int max)
    {
        var t = text.Trim().Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return t.Length <= max ? t : t[..max] + $"… [truncated, {t.Length - max} chars omitted]";
    }

    // Keeps multi-line text inside its list item / paragraph.
    private static string Indent(string text) => text.Replace("\n", "\n  ");
}
