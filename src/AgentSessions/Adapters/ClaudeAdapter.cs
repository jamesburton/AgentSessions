using System.Text.Json;
using AgentSessions.Model;

namespace AgentSessions.Adapters;

public class ClaudeAdapter(string homeDir) : ISessionAdapter
{
    private const int MaxTitle = 80;
    private readonly string _root = Path.Combine(homeDir, ".claude", "projects");

    public string Name => "claude";

    public AdapterCapabilities Capabilities =>
        AdapterCapabilities.List | AdapterCapabilities.Resume | AdapterCapabilities.LaunchWithPrompt |
        AdapterCapabilities.Transcript | AdapterCapabilities.Archive | AdapterCapabilities.Delete;

    public DiscoveryResult Discover()
    {
        var sessions = new List<SessionInfo>();
        var skipped = new List<SkippedSession>();
        if (!Directory.Exists(_root)) return new DiscoveryResult(sessions, skipped);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateDirectories(_root)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly))
                .ToList();
        }
        catch (Exception ex)
        {
            skipped.Add(new SkippedSession(_root, ex.Message));
            return new DiscoveryResult(sessions, skipped);
        }

        foreach (var file in files)
        {
            try { sessions.Add(Summarize(file)); }
            catch (Exception ex) { skipped.Add(new SkippedSession(file, ex.Message)); }
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
            if (cwd is null && TryString(e, "cwd") is { Length: > 0 } c) cwd = c;
            if (ParseTs(e) is { } ts && (newest is null || ts > newest)) newest = ts;
            if (title is null && IsPromptEntry(e)) title = Title(PromptText(e));
            if (cwd is not null && title is not null) break;
        }
        // Tail scan: newest timestamp lives near the end of the file.
        if (TailNewest(file) is { } tail && (newest is null || tail > newest)) newest = tail;

        return new SessionInfo(Name, Path.GetFileNameWithoutExtension(file), cwd,
            title ?? "(no prompt)", newest ?? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
            fi.Length, file);
    }

    private const int TailBytes = 64 * 1024;

    private static DateTimeOffset? TailNewest(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - TailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        if (start > 0)
        {
            var nl = text.IndexOf('\n');
            text = nl < 0 ? "" : text[(nl + 1)..]; // drop the partial first line
        }
        DateTimeOffset? newest = null;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && ParseTs(doc.RootElement) is { } ts
                    && (newest is null || ts > newest)) newest = ts;
            }
            catch (JsonException) { } // truncated/garbled line
        }
        return newest;
    }

    public IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s)
    {
        foreach (var e in JsonlReader.Read(s.StorePath))
        {
            var type = TryString(e, "type");
            if (type is not ("user" or "assistant")) continue;
            if (!e.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
            if (!msg.TryGetProperty("content", out var content)) continue;
            var ts = ParseTs(e);

            if (content.ValueKind == JsonValueKind.String)
            {
                yield return new TranscriptEntry(type == "user" ? Role.User : Role.Assistant,
                    content.GetString() ?? "", ts, null, []);
                continue;
            }
            if (content.ValueKind != JsonValueKind.Array) continue;

            foreach (var b in content.EnumerateArray())
            {
                var bt = TryString(b, "type");
                switch (bt)
                {
                    case "text":
                        yield return new TranscriptEntry(type == "user" ? Role.User : Role.Assistant,
                            TryString(b, "text") ?? "", ts, null, []);
                        break;
                    case "tool_use":
                        yield return new TranscriptEntry(Role.Tool, "", ts, TryString(b, "name"), FilesOf(b));
                        break;
                    case "tool_result":
                        yield return new TranscriptEntry(Role.Tool, ResultText(b), ts, null, []);
                        break;
                }
            }
        }
    }

    public LaunchSpec BuildResume(SessionInfo s) =>
        new("claude", ["--resume", s.Id], s.ProjectPath ?? Directory.GetCurrentDirectory());

    public LaunchSpec BuildNew(string projectPath, string prompt) =>
        new("claude", [prompt], projectPath);

    public IReadOnlyList<string> OwnedPaths(SessionInfo s)
    {
        var list = new List<string> { s.StorePath };
        var sibling = Path.Combine(Path.GetDirectoryName(s.StorePath) ?? "", s.Id);
        if (Directory.Exists(sibling)) list.Add(sibling);
        return list;
    }

    // ---- helpers ----

    private static string? TryString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTimeOffset? ParseTs(JsonElement e) =>
        TryString(e, "timestamp") is { } s &&
        DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var ts) ? ts : null;

    private static bool IsPromptEntry(JsonElement e)
    {
        if (TryString(e, "type") != "user") return false;
        if (e.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True) return false;
        if (e.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return false;
        var text = PromptText(e);
        return !string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('<');
    }

    private static string PromptText(JsonElement e)
    {
        if (!e.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return "";
        if (!msg.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
            foreach (var b in c.EnumerateArray())
                if (TryString(b, "type") == "text") return TryString(b, "text") ?? "";
        return "";
    }

    private static string Title(string text)
    {
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= MaxTitle ? t : t[..(MaxTitle - 1)] + "…";
    }

    private static IReadOnlyList<string> FilesOf(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return [];
        var files = new List<string>();
        foreach (var k in new[] { "file_path", "path", "notebook_path" })
            if (TryString(input, k) is { Length: > 0 } f) files.Add(f);
        return files;
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", c.EnumerateArray().Select(x => TryString(x, "text")).Where(x => x is not null));
    }
}
