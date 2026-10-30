using System.Text.Json;
using AgentSessions.Adapters;
using AgentSessions.Cli;
using AgentSessions.Config;
using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;
using Spectre.Console.Testing;

namespace AgentSessions.Tests;

/// <summary>
/// Whole-stack tests: the real Claude/Codex/Copilot adapters over synthetic files in a temp home, the real
/// composition root and the real CLI entry point. Only the process runner and clipboard are fakes; nothing
/// here ever touches the real home, settings, agents or clipboard.
/// </summary>
public class EndToEndTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = SessionFixtures.TempHome();
    private readonly List<LaunchSpec> _runs = [];

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeRunner(List<LaunchSpec> runs) : IProcessRunner
    {
        public int Run(LaunchSpec spec) { runs.Add(spec); return 0; }
    }

    private sealed class FakeClipboard : IClipboard { public void Set(string text) { } }

    private string Home => Path.Combine(_root, "home");
    private string Project => Path.Combine(_root, "project");
    private string ArchiveRoot => Path.Combine(_root, "archive");

    private (AppServices Svc, TestConsole Console) Make()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Project);
        ISessionAdapter[] adapters = [new ClaudeAdapter(Home), new CodexAdapter(Home), new CopilotAdapter(Home)];
        var svc = new AppServices(adapters, new AppSettings(), new FakeRunner(_runs), new FakeClipboard(), () => Now,
            ArchiveRoot, new LabelStore(Path.Combine(_root, "data", "labels.json")), homeDir: Home);
        return (svc, new TestConsole().Width(300));
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private static Dictionary<string, byte[]> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);

    private static void AssertSame(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (k, v) in expected) Assert.True(v.AsSpan().SequenceEqual(actual[k]), $"changed: {k}");
    }

    // ---- synthetic session writers -------------------------------------------------------------------------------

    private const string CodexId = "00000000-0000-7000-8000-0000000000e2";
    private const string ClaudeId = "11111111-2222-3333-4444-5555555555e2";
    private const string CopilotId = "00000000-0000-4000-8000-0000000000e2";

    private string WriteCodex(string ts = "2026-10-29T10:00:00.000Z")
    {
        var path = Path.Combine(Home, ".codex", "sessions", "2026", "10", "29", $"rollout-2026-10-29T10-00-00-{CodexId}.jsonl");
        SessionFixtures.WriteLines(path,
            J(new { timestamp = ts, type = "session_meta", payload = new { id = CodexId, timestamp = ts, cwd = Project, originator = "codex_cli" } }),
            J(new { timestamp = ts, type = "event_msg", payload = new { type = "user_message", message = "Add retry logic to the uploader" } }),
            J(new { timestamp = ts, type = "event_msg", payload = new { type = "agent_message", message = "Added a retry loop." } }));
        return path;
    }

    private string WriteClaude(string ts)
    {
        var path = Path.Combine(Home, ".claude", "projects", "C--proj", ClaudeId + ".jsonl");
        SessionFixtures.WriteLines(path,
            J(new { type = "user", sessionId = ClaudeId, timestamp = ts, cwd = Project, message = new { role = "user", content = "Old stale task" } }),
            J(new { type = "assistant", sessionId = ClaudeId, timestamp = ts, message = new { role = "assistant", content = new object[] { new { type = "text", text = "done" } } } }));
        return path;
    }

    private string WriteCopilot(string ts)
    {
        var dir = Path.Combine(Home, ".copilot", "session-state", CopilotId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"),
            $"id: {CopilotId}\ncwd: {Project}\ngit_root: {Project}\nbranch: main\nupdated_at: {ts}\n");
        SessionFixtures.WriteLines(Path.Combine(dir, "events.jsonl"),
            J(new { type = "session.start", data = new { sessionId = CopilotId, context = new { cwd = Project } }, id = "e1", timestamp = ts, parentId = (string?)null }),
            J(new { type = "user.message", data = new { content = "old copilot task", transformedContent = "x" }, id = "e2", timestamp = ts, parentId = "e1" }));
        return dir;
    }

    // ---- tests ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Fixture_codex_session_transfers_to_claude()
    {
        // The AppServices constructor overload used by Make() bypasses CreateDefault, so the real data dir is never read.
        var (svc, console) = Make();
        var codexFile = WriteCodex();
        var codexBefore = File.ReadAllBytes(codexFile);
        var docs = Path.Combine(Project, ".docs");
        Directory.CreateDirectory(docs);
        var handoff = Path.Combine(docs, "handoff.md");
        var previous = new byte[] { 0x23, 0x20, 0x70, 0x72, 0x65, 0x76, 0x69, 0x6F, 0x75, 0x73, 0x0D, 0x0A, 0xC3, 0xA9 };
        File.WriteAllBytes(handoff, previous);

        var code = Program.Run(["transfer", "codex", CodexId, "--to", "claude"], console, svc);

        Assert.Equal(0, code);
        var text = File.ReadAllText(handoff);
        Assert.Contains(CodexId, text);
        Assert.Contains(codexFile, text);

        var backup = Assert.Single(Directory.GetFiles(docs, "handoff.2026-10-30T1200*.md"));
        Assert.True(previous.AsSpan().SequenceEqual(File.ReadAllBytes(backup)));
        Assert.Contains(Path.GetFileName(backup), text);

        var run = Assert.Single(_runs);
        Assert.Equal("claude", run.FileName);
        Assert.Equal(Project, run.WorkingDirectory);
        Assert.Equal([$"Read {handoff} and continue the work described there."], run.Args);

        Assert.True(codexBefore.AsSpan().SequenceEqual(File.ReadAllBytes(codexFile)), "source session changed");
    }

    [Fact]
    public void Stale_session_is_archived_and_restored_byte_exact()
    {
        var (svc, console) = Make();
        var file = WriteClaude("2026-01-01T10:00:00.000Z");   // ~10 months before Now
        var original = File.ReadAllBytes(file);

        Assert.Equal(0, Program.Run(["list"], console, svc));
        Assert.Contains("Old stale task [stale]", console.Output.Replace("\n", " "));

        var clean = new TestConsole().Width(300);
        Assert.Equal(0, Program.Run(["clean", "--archive", "--apply", "--yes"], clean, svc));
        Assert.Contains("1 done, 0 failed", clean.Output);
        Assert.False(File.Exists(file));

        var after = new TestConsole().Width(300);
        Assert.Equal(0, Program.Run(["list"], after, svc));
        Assert.DoesNotContain(ClaudeId, after.Output.Replace("\n", ""));
        Assert.Contains("0 session(s)", after.Output);

        var archived = Path.Combine(ArchiveRoot, "claude", ClaudeId);
        Assert.True(File.Exists(Path.Combine(archived, "manifest.json")));
        var restore = new TestConsole().Width(300);
        Assert.Equal(0, Program.Run(["restore", archived], restore, svc));

        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(file)), "restored bytes differ");
        var relist = new TestConsole().Width(300);
        Program.Run(["list"], relist, svc);
        Assert.Contains(ClaudeId, relist.Output.Replace("\n", ""));
    }

    [Fact]
    public void Copilot_cleanup_is_declined_end_to_end()
    {
        var (svc, console) = Make();
        var dir = WriteCopilot("2026-01-01T10:00:00.000Z");
        var before = Snapshot(Home);

        var code = Program.Run(["clean", "--apply", "--yes"], console, svc);

        Assert.Equal(1, code);   // everything selected was declined: documented as exit 1, not a silent "ok"
        Assert.Contains("Declined", console.Output);
        Assert.Contains("copilot:" + CopilotId, console.Output.Replace("\n", ""));
        Assert.Contains("Nothing to do.", console.Output);
        Assert.True(Directory.Exists(dir));
        AssertSame(before, Snapshot(Home));
        Assert.False(Directory.Exists(ArchiveRoot));
    }
}
