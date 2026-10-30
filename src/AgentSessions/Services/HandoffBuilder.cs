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
/// <para>
/// Tool entries follow the <see cref="TranscriptEntry"/> contract: invocations (with a tool name) are shown as one-line
/// <c>**Tool (name)**</c> rows (skipped when they carry no text); outputs (no tool name) can be huge and are omitted,
/// only counted. User/assistant entries have their own ring, so a long run of tool calls can never evict the conversation.
/// </para>
/// </summary>
public static class HandoffBuilder
{
    private const int MaxFilesListed = 50;
    private const int MaxTrackedFiles = 10_000;
    private const int MaxCommandChars = 300;
    private const int MaxToolNameChars = 60;

    private static readonly HashSet<string> ShellToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "shell", "shell_command", "powershell", "cmd", "exec_command", "run_command", "local_shell",
    };

    // Raw text already cut to the cap; Omitted is the number of raw chars dropped by the cut.
    private readonly record struct Clipped(string Text, int Omitted);

    // Seq orders entries from the two rings when they are merged for display. Tool is null for user/assistant rows.
    private readonly record struct Shown(long Seq, Role Role, string? Tool, Clipped Body);

    public static string Build(SessionInfo s, IEnumerable<TranscriptEntry> transcript, HandoffOptions? options = null)
    {
        options ??= new HandoffOptions();
        var maxRecent = Math.Max(0, options.MaxRecentEntries);
        var maxCommands = Math.Max(0, options.MaxCommands);
        var maxChars = Math.Max(1, options.MaxCharsPerEntry);

        Queue<Shown> conversation = new();   // last N user/assistant entries
        Queue<Shown> toolCalls = new();      // last N tool invocations with text
        Queue<string> commands = new();
        HashSet<string> seenFiles = new(StringComparer.Ordinal);
        List<string> files = [];
        var filesCapped = false;
        Clipped? request = null;
        Clipped? fallbackRequest = null;
        Shown? lastMessage = null;
        Shown? lastTool = null;
        long seq = 0;
        var outputsOmitted = 0;

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
            seq++;

            if (e.Role == Role.Tool)
            {
                if (e.IsToolOutput) { outputsOmitted++; continue; }
                // One line, at most MaxCommandChars; only the head of a huge text is ever copied.
                var trimmed = text.AsSpan().Trim();
                var line = OneLine(trimmed[..Math.Min(trimmed.Length, MaxCommandChars + 1)].ToString()).Trim();
                if (line.Length == 0) continue;   // nothing to say about this call (e.g. an adapter with no input text)
                var cut = Clip(line, MaxCommandChars).Text;
                var c = new Clipped(cut, Math.Max(0, trimmed.Length - cut.Length));
                var call = new Shown(seq, Role.Tool, ToolLabel(e.ToolName!), c);
                lastTool = call;
                Push(toolCalls, call, maxRecent);
                if (maxCommands > 0 && ShellToolNames.Contains(e.ToolName!.Trim()))
                {
                    if (commands.Count == maxCommands) commands.Dequeue();
                    commands.Enqueue(c.Text + Marker(c.Omitted));
                }
                continue;
            }

            // A message without text (e.g. an assistant turn that only requested tools) says nothing; skipping it keeps
            // the bounded ring for messages that do.
            if (text.AsSpan().IsWhiteSpace()) continue;
            var shown = new Shown(seq, e.Role, null, Clip(text, maxChars));
            lastMessage = shown;
            Push(conversation, shown, maxRecent);
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
        // The two rings merged in transcript order: the last N messages and the last N tool calls.
        var recent = conversation.Concat(toolCalls).OrderBy(x => x.Seq).ToList();
        if (recent.Count == 0) sb.Append("_(none: no activity found in the transcript)_\n");
        var n = 0;
        foreach (var item in recent)
        {
            if (n++ > 0) sb.Append('\n');
            AppendShown(sb, item);
        }
        if (outputsOmitted > 0)
            sb.Append("\n_(").Append(outputsOmitted).Append(outputsOmitted == 1 ? " tool result" : " tool results")
              .Append(" omitted; the full original session has them.)_\n");

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
        if (lastMessage is { } l)
        {
            AppendShown(sb, l);
            if (lastTool is { } t && t.Seq > l.Seq)
            {
                sb.Append("\nAfter that, the last tool call:\n\n");
                AppendShown(sb, t);
            }
        }
        else if (lastTool is { } only) AppendShown(sb, only);
        else sb.Append("_(none: the transcript is empty)_\n");

        return sb.ToString();
    }

    private static void Push(Queue<Shown> ring, Shown item, int max)
    {
        if (max <= 0) return;
        if (ring.Count == max) ring.Dequeue();
        ring.Enqueue(item);
    }

    private static void AppendShown(StringBuilder sb, Shown item)
    {
        sb.Append("**").Append(item.Tool is null ? item.Role.ToString() : $"Tool ({item.Tool})").Append("**:\n");
        AppendQuote(sb, item.Body);
    }

    // A tool name as a bold label: one line, bounded, with no characters that could end the bold or start code.
    private static string ToolLabel(string name)
    {
        var plain = new string(OneLine(name).Trim().Where(ch => ch is not ('*' or '`' or '[' or ']')).ToArray());
        if (plain.Length == 0) plain = "tool";
        return plain.Length <= MaxToolNameChars ? plain : plain[..MaxToolNameChars] + "…";
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
