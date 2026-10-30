using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSessions.Adapters;

/// <summary>
/// Builds the short text carried by tool-invocation transcript entries (see <c>TranscriptEntry</c>): the command line
/// when the tool runs one, else a bounded one-line summary. Shared by the adapters so every agent reads the same way.
/// </summary>
internal static partial class ToolText
{
    /// <summary>Longest command kept on an invocation entry (the handoff shows less).</summary>
    public const int MaxCommand = 2000;

    /// <summary>Longest one-line summary (compact JSON, first line) kept on an invocation entry.</summary>
    public const int MaxSummary = 200;

    /// <summary>The command of a tool input object: <c>command</c> or <c>cmd</c>, as a string or an array joined with spaces.</summary>
    public static string? Command(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "command", "cmd" })
        {
            if (!input.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s)) return Clip(s.Trim(), MaxCommand);
            if (v.ValueKind == JsonValueKind.Array)
            {
                var parts = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
                if (parts.Count > 0) return Clip(string.Join(' ', parts), MaxCommand);
            }
        }
        return null;
    }

    /// <summary>The command when there is one, else a one-line compact JSON of the input (truncated).</summary>
    public static string CommandOrSummary(JsonElement input) => Command(input) ?? Summary(input);

    /// <summary>One-line compact JSON of a value, truncated to <see cref="MaxSummary"/>.</summary>
    public static string Summary(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : OneLine(value.GetRawText(), MaxSummary);

    /// <summary>First non-blank line of free text, truncated to <see cref="MaxSummary"/>.</summary>
    public static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) return OneLine(line.Trim(), MaxSummary);
        return "";
    }

    /// <summary>Collapses whitespace runs (including newlines) to single spaces and truncates with an ellipsis.</summary>
    public static string OneLine(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
            if (sb.Length > max) break;
        }
        return Clip(sb.ToString(), max);
    }

    /// <summary>Cuts to at most <paramref name="max"/> chars plus an ellipsis, never splitting a surrogate pair.</summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    // ---- patches (apply_patch format) ----

    [GeneratedRegex(@"^\*\*\* (Add File|Update File|Delete File|Move to): (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex PatchFileRegex();

    /// <summary>True when the text contains an apply_patch body (possibly inside an escaped string literal).</summary>
    public static bool IsPatch(string text) => text.Contains("*** Begin Patch", StringComparison.Ordinal);

    /// <summary>
    /// File paths named by an apply_patch body. Accepts real newlines, or a patch embedded in a JS/JSON string
    /// literal with escaped <c>\n</c> separators (then the literal is unescaped first, e.g. <c>C:\\a</c> -> <c>C:\a</c>).
    /// </summary>
    public static IReadOnlyList<string> PatchFiles(string text)
    {
        if (!text.Contains("*** ", StringComparison.Ordinal)) return [];
        var body = text.Contains("\\n*** ", StringComparison.Ordinal) && !text.Contains("\n*** ", StringComparison.Ordinal)
            ? Unescape(text) : text;
        var files = new List<string>();
        foreach (Match m in PatchFileRegex().Matches(body))
        {
            // Taken verbatim: after unescaping, each file line ends at a real newline (the literal's closing quote is on
            // the "*** End Patch" line, which never matches), and real paths can end in ')', ',' or ';'.
            var p = m.Groups[2].Value;
            if (p.Length > 0 && !files.Contains(p)) files.Add(p);
        }
        return files;
    }

    /// <summary>A one-line summary of a patch: the tool name and the files it changes, else its first line.</summary>
    public static string PatchSummary(string tool, string text, IReadOnlyList<string> files) =>
        files.Count > 0 ? OneLine($"{tool}: {string.Join(", ", files)}", MaxSummary) : OneLine($"{tool}: {FirstLine(text)}", MaxSummary);

    // ---- Codex "exec" code cells ----

    [GeneratedRegex("""\bcmd\s*:\s*("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|`(?:[^`\\]|\\.)*`)""", RegexOptions.Singleline)]
    private static partial Regex CmdLiteralRegex();

    /// <summary>The <c>cmd:</c> string literals of a JS code cell (e.g. <c>tools.exec_command({cmd: "..."})</c>), unescaped.</summary>
    public static IReadOnlyList<string> CellCommands(string cell)
    {
        var list = new List<string>();
        foreach (Match m in CmdLiteralRegex().Matches(cell))
        {
            var lit = m.Groups[1].Value;
            var cmd = Unescape(lit[1..^1]).Trim();
            if (cmd.Length > 0) list.Add(Clip(cmd, MaxCommand));
        }
        return list;
    }

    /// <summary>Minimal JS/JSON string-literal unescape: \n \r \t \\ \" \' \` and \uXXXX; anything else keeps the char.</summary>
    public static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u' when i + 4 < s.Length && int.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                    sb.Append((char)code); i += 4; break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }
}
