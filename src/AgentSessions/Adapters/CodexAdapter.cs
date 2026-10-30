using System.Text.Json;
using AgentSessions.Model;

namespace AgentSessions.Adapters;

public partial class CodexAdapter(string homeDir) : ISessionAdapter
{
    private const int MaxTitle = 80;
    private readonly string _root = Path.Combine(homeDir, ".codex");

    public string Name => "codex";

    public AdapterCapabilities Capabilities =>
        AdapterCapabilities.List | AdapterCapabilities.Resume | AdapterCapabilities.LaunchWithPrompt |
        AdapterCapabilities.Transcript | AdapterCapabilities.Archive | AdapterCapabilities.Delete;

    public DiscoveryResult Discover()
    {
        var sessions = new List<SessionInfo>();
        var skipped = new List<SkippedSession>();
        foreach (var dir in new[] { Path.Combine(_root, "sessions"), Path.Combine(_root, "archived_sessions") })
        {
            if (!Directory.Exists(dir)) continue;
            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) { skipped.Add(new SkippedSession(dir, ex.Message)); continue; }

            foreach (var file in files)
            {
                try { sessions.Add(Summarize(file)); }
                catch (Exception ex) { skipped.Add(new SkippedSession(file, ex.Message)); }
            }
        }
        return new DiscoveryResult(sessions, skipped);
    }

    private SessionInfo Summarize(string file)
    {
        var fi = new FileInfo(file);
        string? cwd = null, title = null, fallbackTitle = null;
        DateTimeOffset? newest = null;

        // Head scan: stop as soon as cwd and title are known. The title prefers event_msg/user_message (older CLIs);
        // modern CLIs write only response_item messages, so the first real (non-injected) user message is the fallback.
        foreach (var e in JsonlReader.Read(file, JsonlReader.HeadScanBytes))
        {
            if (ParseTs(e) is { } ts && (newest is null || ts > newest)) newest = ts;
            var type = TryString(e, "type");
            if (cwd is null && type is ("session_meta" or "turn_context") && Payload(e) is { } meta &&
                TryString(meta, "cwd") is { Length: > 0 } c) cwd = c;
            if (title is null && type == "event_msg" && Payload(e) is { } p &&
                TryString(p, "type") == "user_message" && TryString(p, "message") is { } msg &&
                !string.IsNullOrWhiteSpace(msg)) title = Title(msg);
            if (fallbackTitle is null && type == "response_item" && Payload(e) is { } rp &&
                TryString(rp, "type") == "message" && TryString(rp, "role") == "user" &&
                MessageText(rp, user: true) is { } um && !um.TrimStart().StartsWith('<')) fallbackTitle = Title(um);
            if (cwd is not null && title is not null) break;
        }
        // Tail scan: newest timestamp lives near the end of the file.
        if (JsonlReader.TailNewest(file, ParseTs) is { } tail && (newest is null || tail > newest)) newest = tail;

        return new SessionInfo(Name, IdFromFile(file), cwd, title ?? fallbackTitle ?? "(no prompt)",
            newest ?? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero), fi.Length, file);
    }

    /// <summary>
    /// User/assistant text comes from BOTH sources: <c>event_msg</c> user_message/agent_message (older CLIs) and
    /// <c>response_item</c> messages (all CLIs; the only source in modern sessions). Where both carry the same message it
    /// is emitted once (<see cref="MessageDedupe"/>). Injected user blocks (AGENTS.md, environment context, ...) are skipped.
    /// Tool calls carry their command (see <see cref="ToolText"/>); outputs keep their text with ToolName null.
    /// </summary>
    public IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s)
    {
        var dedupe = new MessageDedupe();
        foreach (var e in JsonlReader.Read(s.StorePath))
        {
            if (Payload(e) is not { } p) continue;
            var type = TryString(e, "type");
            var ptype = TryString(p, "type");
            var ts = ParseTs(e);

            if (type == "event_msg")
            {
                Role? role = ptype switch { "user_message" => Role.User, "agent_message" => Role.Assistant, _ => null };
                if (role is { } r && TryString(p, "message") is { } text && dedupe.Accept(r, fromEvent: true, text))
                    yield return new TranscriptEntry(r, text, ts, null, []);
            }
            else if (type == "response_item")
            {
                switch (ptype)
                {
                    case "message":
                    {
                        var role = TryString(p, "role") switch { "user" => Role.User, "assistant" => Role.Assistant, _ => (Role?)null };
                        if (role is { } r && MessageText(p, r == Role.User) is { } text && dedupe.Accept(r, fromEvent: false, text))
                            yield return new TranscriptEntry(r, text, ts, null, []);
                        break;
                    }
                    case "function_call":
                        yield return new TranscriptEntry(Role.Tool, FunctionCallText(TryString(p, "arguments") ?? ""), ts,
                            TryString(p, "name") ?? "tool", []);
                        break;
                    case "custom_tool_call":
                        foreach (var call in CustomToolCall(TryString(p, "name") ?? "tool", TryString(p, "input") ?? "", ts))
                            yield return call;
                        break;
                    case "function_call_output":
                    case "custom_tool_call_output":
                        yield return new TranscriptEntry(Role.Tool, OutputText(p), ts, null, []);
                        break;
                }
            }
        }
    }

    private static string FunctionCallText(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return ToolText.CommandOrSummary(doc.RootElement);
        }
        catch (JsonException) { return ToolText.OneLine(arguments, ToolText.MaxSummary); }
    }

    /// <summary>
    /// <c>apply_patch</c>: a summary naming the patched files. <c>exec</c> (modern CLIs: a JS code cell calling
    /// <c>tools.exec_command({cmd: ...})</c> / <c>tools.apply_patch("...")</c>): one entry per command, plus one for an
    /// embedded patch; a cell with neither is summarised by its first line.
    /// </summary>
    private static IEnumerable<TranscriptEntry> CustomToolCall(string name, string input, DateTimeOffset? ts)
    {
        if (name == "exec")
        {
            var any = false;
            foreach (var cmd in ToolText.CellCommands(input))
            {
                any = true;
                yield return new TranscriptEntry(Role.Tool, cmd, ts, "exec_command", []);
            }
            if (ToolText.IsPatch(input))
            {
                any = true;
                var files = ToolText.PatchFiles(input);
                yield return new TranscriptEntry(Role.Tool, ToolText.PatchSummary("apply_patch", input, files), ts, "apply_patch", files);
            }
            if (!any) yield return new TranscriptEntry(Role.Tool, ToolText.FirstLine(input), ts, name, []);
            yield break;
        }
        if (name == "apply_patch" || ToolText.IsPatch(input))
        {
            var files = ToolText.PatchFiles(input);
            yield return new TranscriptEntry(Role.Tool, ToolText.PatchSummary(name, input, files), ts, name, files);
            yield break;
        }
        yield return new TranscriptEntry(Role.Tool, FunctionCallText(input), ts, name, []);
    }

    /// <summary>Tool output: a string, or (modern CLIs) an array of <c>{type, text}</c> items joined with newlines.</summary>
    private static string OutputText(JsonElement p)
    {
        if (!p.TryGetProperty("output", out var o)) return "";
        if (o.ValueKind == JsonValueKind.String) return o.GetString() ?? "";
        if (o.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", o.EnumerateArray().Select(x => TryString(x, "text")).Where(x => x is not null));
    }

    /// <summary>
    /// Text of a response_item message: its text blocks joined with newlines. For user messages, injected blocks
    /// (<see cref="IsInjectedBlock"/>) are dropped. Null when nothing is left.
    /// </summary>
    private static string? MessageText(JsonElement p, bool user)
    {
        if (!p.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var b in content.EnumerateArray())
            if (TryString(b, "text") is { } t && !string.IsNullOrWhiteSpace(t) && !(user && IsInjectedBlock(t))) parts.Add(t);
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static readonly HashSet<string> InjectedTags = new(StringComparer.Ordinal)
    {
        "environment_context", "user_instructions", "recommended_plugins", "skill", "turn_aborted",
        "subagent_notification", "permissions instructions",
    };

    /// <summary>Text the CLI injects into user messages (not typed by the user): AGENTS.md and known context tags.</summary>
    private static bool IsInjectedBlock(string text)
    {
        var t = text.TrimStart();
        if (t.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal)) return true;
        if (!t.StartsWith('<')) return false;
        var end = t.IndexOf('>');
        return end > 1 && InjectedTags.Contains(t[1..end]);
    }

    /// <summary>
    /// Pairs a message seen in one source (event_msg vs response_item) with the identical message in the other, so each
    /// is emitted once. A repeat from the SAME source is a real repeat and is kept. Bounded: at most 16 pending.
    /// </summary>
    private sealed class MessageDedupe
    {
        private const int Max = 16;
        private readonly List<(Role Role, bool FromEvent, string Text)> _pending = [];

        public bool Accept(Role role, bool fromEvent, string text)
        {
            var key = text.Trim();
            var i = _pending.FindIndex(x => x.Role == role && x.FromEvent != fromEvent && x.Text == key);
            if (i >= 0) { _pending.RemoveAt(i); return false; }
            if (_pending.Count == Max) _pending.RemoveAt(0);
            _pending.Add((role, fromEvent, key));
            return true;
        }
    }

    public LaunchSpec BuildResume(SessionInfo s) =>
        new("codex", ["resume", s.Id], s.ProjectPath ?? Directory.GetCurrentDirectory());

    public LaunchSpec BuildNew(string projectPath, string prompt) =>
        new("codex", [prompt], projectPath);

    public IReadOnlyList<string> OwnedPaths(SessionInfo s) => [s.StorePath];

    // ---- helpers ----

    private static string IdFromFile(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        // rollout-<timestamp>-<uuid>: the UUID is the last 36 chars.
        return stem.Length >= 36 && Guid.TryParse(stem[^36..], out _) ? stem[^36..] : stem;
    }

    private static JsonElement? Payload(JsonElement e) =>
        e.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

    private static string? TryString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTimeOffset? ParseTs(JsonElement e) =>
        TryString(e, "timestamp") is { } s &&
        DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var ts) ? ts : null;

    private static string Title(string text)
    {
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= MaxTitle ? t : t[..(MaxTitle - 1)] + "…";
    }
}
