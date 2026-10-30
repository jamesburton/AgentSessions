using System.Globalization;
using System.Text;
using AgentSessions.Model;

namespace AgentSessions.Services;

public record HandoffOptions(int MaxRecentEntries = 20, int MaxCharsPerEntry = 2000, int MaxCommands = 10);

/// <summary>
/// Builds a Markdown handoff from a transcript by direct extraction (no model, no network).
/// Single streaming pass. Retained state is bounded by the options (ring buffers of at most
/// MaxCharsPerEntry chars per entry) plus a distinct-path set capped at <see cref="MaxTrackedFiles"/>.
/// All free text is rendered as blockquotes so it can never open fences or create headings.
/// </summary>
public static class HandoffBuilder
{
    private const int MaxFilesListed = 50;
    private const int MaxTrackedFiles = 10_000;
    private const int MaxCommandChars = 300;

    private static readonly HashSet<string> ShellToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "shell", "shell_command", "powershell", "cmd", "exec_command", "run_command", "local_shell",
    };

    // Raw text already cut to the cap; Omitted is the number of raw chars dropped by the cut.
    private readonly record struct Clipped(string Text, int Omitted);
    private readonly record struct Shown(Role Role, Clipped Body);

    public static string Build(SessionInfo s, IEnumerable<TranscriptEntry> transcript, HandoffOptions? options = null)
    {
        options ??= new HandoffOptions();
        var maxRecent = Math.Max(0, options.MaxRecentEntries);
        var maxCommands = Math.Max(0, options.MaxCommands);
        var maxChars = Math.Max(1, options.MaxCharsPerEntry);

        Queue<Shown> recent = new();
        Queue<string> commands = new();
        HashSet<string> seenFiles = new(StringComparer.Ordinal);
        List<string> files = [];
        var filesCapped = false;
        Clipped? request = null;
        Clipped? fallbackRequest = null;
        Shown? last = null;

        foreach (var e in transcript)
        {
            var text = e.Text ?? "";

            if (e.Role == Role.User && request is null)
            {
                var first = FirstNonWhitespace(text);
                if (first >= 0)
                {
                    if (text[first] != '<') request = Clip(text, maxChars);
                    else fallbackRequest ??= Clip(text, maxChars);
                }
            }

            if (e.FilesTouched is { } touched)
                foreach (var raw in touched)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var f = OneLine(raw);
                    if (seenFiles.Contains(f)) continue;
                    if (seenFiles.Count >= MaxTrackedFiles) { filesCapped = true; continue; }
                    seenFiles.Add(f);
                    if (files.Count < MaxFilesListed) files.Add(f);
                }

            if (e.Role == Role.System) continue;

            var shown = new Shown(e.Role, Clip(text, maxChars));
            last = shown;

            if (maxRecent > 0)
            {
                if (recent.Count == maxRecent) recent.Dequeue();
                recent.Enqueue(shown);
            }

            if (maxCommands > 0 && e.Role == Role.Tool && e.ToolName is { } tn && ShellToolNames.Contains(tn.Trim()))
            {
                if (commands.Count == maxCommands) commands.Dequeue();
                var c = Clip(text, MaxCommandChars);
                commands.Enqueue(OneLine(c.Text.Trim()) + Marker(c.Omitted));
            }
        }

        request ??= fallbackRequest;

        var sb = new StringBuilder();
        sb.Append("# Session handoff\n\n");

        sb.Append("## Source\n\n");
        sb.Append("- Agent: ").Append(OneLine(s.Agent)).Append('\n');
        sb.Append("- Session id: ").Append(OneLine(s.Id)).Append('\n');
        sb.Append("- Full original session: ").Append(OneLine(s.StorePath)).Append('\n');
        sb.Append("- Project: ").Append(string.IsNullOrEmpty(s.ProjectPath) ? "(unknown)" : OneLine(s.ProjectPath)).Append('\n');
        sb.Append("- Last activity: ").Append(s.LastActivity.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)).Append('\n');

        sb.Append("\n## Original request\n\n");
        if (request is { } r) AppendQuote(sb, r);
        else sb.Append("_(none: no user prompt found in the transcript)_\n");

        sb.Append("\n## Recent activity\n\n");
        if (recent.Count == 0) sb.Append("_(none: no activity found in the transcript)_\n");
        var n = 0;
        foreach (var item in recent)
        {
            if (n++ > 0) sb.Append('\n');
            sb.Append("**").Append(item.Role).Append("**:\n");
            AppendQuote(sb, item.Body);
        }

        sb.Append("\n## Files touched\n\n");
        if (files.Count == 0) sb.Append("_(none recorded)_\n");
        foreach (var f in files) sb.Append("- ").Append(f).Append('\n');
        if (seenFiles.Count > files.Count || filesCapped)
            sb.Append("- ...and ").Append(seenFiles.Count - files.Count).Append(filesCapped ? "+" : "").Append(" more\n");

        sb.Append("\n## Last commands\n\n");
        if (commands.Count == 0) sb.Append("_(none recorded)_\n");
        else
        {
            sb.Append("```\n");
            foreach (var cmd in commands) sb.Append(cmd.Replace("```", "'''")).Append('\n');
            sb.Append("```\n");
        }

        sb.Append("\n## Where it stopped\n\n");
        if (last is { } l)
        {
            sb.Append("**").Append(l.Role).Append("**:\n");
            AppendQuote(sb, l.Body);
        }
        else sb.Append("_(none: the transcript is empty)_\n");

        return sb.ToString();
    }

    private static int FirstNonWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i])) return i;
        return -1;
    }

    // Cuts to at most max chars without splitting a surrogate pair; copies only when it must cut.
    private static Clipped Clip(string text, int max)
    {
        if (text.Length <= max) return new Clipped(text, 0);
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return new Clipped(text[..cut], text.Length - cut);
    }

    private static string Marker(int omitted) => omitted > 0 ? $"… [truncated, {omitted} chars omitted]" : "";

    // Renders clipped text as a blockquote: every line prefixed, so no line can start a heading or fence.
    private static void AppendQuote(StringBuilder sb, Clipped c)
    {
        var body = c.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (body.Length == 0 && c.Omitted == 0) body = "(empty)";
        foreach (var line in body.Split('\n'))
        {
            sb.Append('>');
            if (line.Length > 0) sb.Append(' ').Append(line);
            sb.Append('\n');
        }
        if (c.Omitted > 0) sb.Append("> ").Append(Marker(c.Omitted)).Append('\n');
    }

    // Collapses control characters (including newlines) so a value stays on one line.
    private static string OneLine(string text)
    {
        var needs = false;
        foreach (var ch in text) if (char.IsControl(ch)) { needs = true; break; }
        if (!needs) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(char.IsControl(ch) ? ' ' : ch);
        return sb.ToString();
    }
}
