using System.Text.Json;
using System.Text.RegularExpressions;
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
        string? cwd = null, title = null;
        DateTimeOffset? newest = null;

        // Head scan: stop as soon as cwd and title are known.
        foreach (var e in JsonlReader.Read(file))
        {
            if (ParseTs(e) is { } ts && (newest is null || ts > newest)) newest = ts;
            var type = TryString(e, "type");
            if (cwd is null && type == "session_meta" && Payload(e) is { } meta &&
                TryString(meta, "cwd") is { Length: > 0 } c) cwd = c;
            if (title is null && type == "event_msg" && Payload(e) is { } p &&
                TryString(p, "type") == "user_message" && TryString(p, "message") is { } msg &&
                !string.IsNullOrWhiteSpace(msg)) title = Title(msg);
            if (cwd is not null && title is not null) break;
        }
        // Tail scan: newest timestamp lives near the end of the file.
        foreach (var e in JsonlReader.ReadTail(file))
            if (ParseTs(e) is { } ts && (newest is null || ts > newest)) newest = ts;

        return new SessionInfo(Name, IdFromFile(file), cwd, title ?? "(no prompt)",
            newest ?? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero), fi.Length, file);
    }

    public IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s)
    {
        foreach (var e in JsonlReader.Read(s.StorePath))
        {
            if (Payload(e) is not { } p) continue;
            var type = TryString(e, "type");
            var ptype = TryString(p, "type");
            var ts = ParseTs(e);

            if (type == "event_msg")
            {
                if (ptype == "user_message")
                    yield return new TranscriptEntry(Role.User, TryString(p, "message") ?? "", ts, null, []);
                else if (ptype == "agent_message")
                    yield return new TranscriptEntry(Role.Assistant, TryString(p, "message") ?? "", ts, null, []);
            }
            else if (type == "response_item")
            {
                switch (ptype)
                {
                    case "function_call":
                        yield return new TranscriptEntry(Role.Tool, "", ts, TryString(p, "name"), []);
                        break;
                    case "custom_tool_call":
                        yield return new TranscriptEntry(Role.Tool, "", ts, TryString(p, "name"),
                            PatchFiles(TryString(p, "input") ?? ""));
                        break;
                    case "function_call_output":
                    case "custom_tool_call_output":
                        yield return new TranscriptEntry(Role.Tool, TryString(p, "output") ?? "", ts, null, []);
                        break;
                }
            }
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

    [GeneratedRegex(@"^\*\*\* (?:Add File|Update File|Delete File|Move to): (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex PatchFileRegex();

    private static IReadOnlyList<string> PatchFiles(string input) =>
        PatchFileRegex().Matches(input).Select(m => m.Groups[1].Value).ToList();
}
