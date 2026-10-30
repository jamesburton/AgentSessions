using System.Text.Json;
using AgentSessions.Model;

namespace AgentSessions.Adapters;

/// <summary>
/// Copilot CLI sessions: one folder per session under ~/.copilot/session-state.
/// Archive/Delete are deliberately NOT offered: ~/.copilot/session-store.db keeps per-session rows
/// (including FTS data and message copies) that cannot be kept consistent safely (docs/formats/copilot.md).
/// </summary>
public class CopilotAdapter(string homeDir) : ISessionAdapter
{
    private const int MaxTitle = 80;
    private const int MaxCheckpointLines = 60;
    private readonly string _root = Path.Combine(homeDir, ".copilot", "session-state");

    public string Name => "copilot";

    public AdapterCapabilities Capabilities =>
        AdapterCapabilities.List | AdapterCapabilities.Resume | AdapterCapabilities.LaunchWithPrompt |
        AdapterCapabilities.Transcript;

    public DiscoveryResult Discover()
    {
        var sessions = new List<SessionInfo>();
        var skipped = new List<SkippedSession>();
        if (!Directory.Exists(_root)) return new DiscoveryResult(sessions, skipped);

        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(_root).ToList(); }
        catch (Exception ex) { skipped.Add(new SkippedSession(_root, ex.Message)); return new DiscoveryResult(sessions, skipped); }

        foreach (var dir in dirs)
        {
            try
            {
                if (Summarize(dir) is { } s) sessions.Add(s);
                else skipped.Add(new SkippedSession(dir, "no workspace.yaml or events.jsonl"));
            }
            catch (Exception ex) { skipped.Add(new SkippedSession(dir, ex.Message)); }
        }
        return new DiscoveryResult(sessions, skipped);
    }

    private SessionInfo? Summarize(string dir)
    {
        var events = Path.Combine(dir, "events.jsonl");
        var yamlPath = Path.Combine(dir, "workspace.yaml");
        var hasEvents = File.Exists(events);
        var hasYaml = File.Exists(yamlPath);
        if (!hasEvents && !hasYaml) return null;

        var yaml = hasYaml ? ReadYamlKeys(yamlPath) : [];
        string? cwd = yaml.GetValueOrDefault("cwd") is { Length: > 0 } c ? c
            : yaml.GetValueOrDefault("git_root") is { Length: > 0 } g ? g : null;
        string? prompt = null;
        DateTimeOffset? newest = null;

        if (hasEvents)
        {
            // Head scan: stop as soon as cwd (only needed without workspace.yaml) and prompt are known.
            foreach (var e in JsonlReader.Read(events, JsonlReader.HeadScanBytes))
            {
                var data = Data(e);
                if (cwd is null && TryString(e, "type") == "session.start" && data is { } sd &&
                    sd.TryGetProperty("context", out var ctx) && TryString(ctx, "cwd") is { Length: > 0 } cc) cwd = cc;
                if (prompt is null && TryString(e, "type") == "user.message" && data is { } ud &&
                    TryString(ud, "content") is { } msg && !string.IsNullOrWhiteSpace(msg)) prompt = Title(msg);
                if (cwd is not null && prompt is not null) break;
            }
            newest = JsonlReader.TailNewest(events, ParseTs);
        }
        newest ??= ParseTime(yaml.GetValueOrDefault("updated_at"));
        newest ??= new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);

        var title = CheckpointTitle(dir) ?? prompt ?? "(no prompt)";
        return new SessionInfo(Name, Path.GetFileName(dir), cwd, title, newest.Value, FolderSize(dir), dir);
    }

    public IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s)
    {
        var events = Path.Combine(s.StorePath, "events.jsonl");
        if (!File.Exists(events)) yield break;
        foreach (var e in JsonlReader.Read(events))
        {
            if (Data(e) is not { } d) continue;
            var ts = ParseTs(e);
            switch (TryString(e, "type"))
            {
                case "user.message":
                    yield return new TranscriptEntry(Role.User, TryString(d, "content") ?? "", ts, null, []);
                    break;
                case "assistant.message":
                    yield return new TranscriptEntry(Role.Assistant, TryString(d, "content") ?? "", ts, null, []);
                    break;
                case "tool.execution_start":
                    var files = d.TryGetProperty("arguments", out var a) && TryString(a, "path") is { Length: > 0 } p
                        ? new[] { p } : [];
                    yield return new TranscriptEntry(Role.Tool, "", ts, TryString(d, "toolName"), files);
                    break;
                case "tool.execution_complete":
                    var text = d.TryGetProperty("result", out var r) ? TryString(r, "content") ?? "" : "";
                    yield return new TranscriptEntry(Role.Tool, text, ts, null, []);
                    break;
            }
        }
    }

    public LaunchSpec BuildResume(SessionInfo s) =>
        new("copilot", ["--resume", s.Id], s.ProjectPath ?? Directory.GetCurrentDirectory());

    public LaunchSpec BuildNew(string projectPath, string prompt) =>
        new("copilot", ["-i", prompt], projectPath);

    // Only the session folder; never anything in session-store.db.
    public IReadOnlyList<string> OwnedPaths(SessionInfo s) => [s.StorePath];

    // ---- helpers ----

    /// <summary>Title from the newest NNN-*.md checkpoint: its first "# " heading, else the first line of its overview.</summary>
    private static string? CheckpointTitle(string dir)
    {
        var cpDir = Path.Combine(dir, "checkpoints");
        if (!Directory.Exists(cpDir)) return null;
        var newest = Directory.EnumerateFiles(cpDir, "*.md")
            .Where(f => char.IsDigit(Path.GetFileName(f)[0]))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest is null) return null;

        string? overview = null;
        var inOverview = false;
        var sawSection = false;
        foreach (var raw in JsonlReader.ReadTextLines(newest).Take(MaxCheckpointLines))
        {
            var line = raw.Trim();
            // A heading only counts before the first <section> tag; later ones (inside <history> etc.) are content.
            if (!sawSection && line.StartsWith('#') && line.TrimStart('#').Trim() is { Length: > 0 } h) return Title(h);
            if (line == "<overview>") { inOverview = true; sawSection = true; }
            else if (line == "</overview>") inOverview = false;
            else if (line.Length > 2 && line[0] == '<' && line[1] != '/' && line[^1] == '>') sawSection = true;
            else if (inOverview && overview is null && line.Length > 0) overview = Title(line);
        }
        return overview;
    }

    private static Dictionary<string, string> ReadYamlKeys(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in JsonlReader.ReadTextLines(path).Take(100))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var v = line[(i + 1)..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");
            else if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') v = v[1..^1].Replace("''", "'");
            map[line[..i].Trim()] = v;
        }
        return map;
    }

    private static long FolderSize(string dir)
    {
        // Bounded approximation: never recurse (snapshots/files/research can hold thousands of files).
        long total = Len(Path.Combine(dir, "events.jsonl")) + Len(Path.Combine(dir, "workspace.yaml"));
        try
        {
            var cp = Path.Combine(dir, "checkpoints");
            if (Directory.Exists(cp))
                foreach (var f in Directory.EnumerateFiles(cp, "*.md")) total += Len(f);
        }
        catch (Exception) { /* unreadable checkpoints count as 0 */ }
        return total;

        static long Len(string f)
        {
            try { return new FileInfo(f).Length; } catch (Exception) { return 0; }
        }
    }

    private static JsonElement? Data(JsonElement e) =>
        e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;

    private static string? TryString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTimeOffset? ParseTs(JsonElement e) => ParseTime(TryString(e, "timestamp"));

    private static DateTimeOffset? ParseTime(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var ts)
            ? ts : null;

    private static string Title(string text)
    {
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= MaxTitle ? t : t[..(MaxTitle - 1)] + "…";
    }
}
