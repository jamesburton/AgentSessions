using AgentSessions.Config;
using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;

namespace AgentSessions.Tests.Services;

public class TransferServiceTests : IDisposable
{
    private readonly string _root = SessionFixtures.TempHome();
    private readonly List<LaunchSpec> _runs = [];

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class Runner(List<LaunchSpec> runs) : IProcessRunner
    {
        public int Run(LaunchSpec s) { runs.Add(s); return 0; }
    }

    private sealed class Clip : IClipboard { public void Set(string t) { } }

    private sealed class Caller(Func<string, string> f) : IModelCaller { public string Polish(string m) => f(m); }

    private const AdapterCapabilities Caps = AdapterCapabilities.List | AdapterCapabilities.Transcript | AdapterCapabilities.LaunchWithPrompt;

    private TransferService Make(AppSettings? settings = null, IModelCaller? caller = null) =>
        new(new HandoffWriter(() => DateTimeOffset.UtcNow),
            new HandoffPolisher(settings ?? new AppSettings(), caller),
            new LaunchService(new Runner(_runs), new Clip()));

    private SessionInfo Source(string? project) =>
        new("claude", "c1", project, "Title", DateTimeOffset.UtcNow, 1, Path.Combine(_root, "s.jsonl"));

    [Fact]
    public void Unknown_project_and_declined_prompt_writes_and_launches_nothing()
    {
        var svc = Make();
        var r = svc.Transfer(new FakeAdapter("claude", Caps, []), Source(null), new FakeAdapter("codex", Caps, []), false, () => null);

        Assert.False(r.Launched);
        Assert.NotNull(r.Note);
        Assert.Empty(_runs);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Nonexistent_chosen_folder_writes_and_launches_nothing()
    {
        var svc = Make();
        var r = svc.Transfer(new FakeAdapter("claude", Caps, []), Source(null), new FakeAdapter("codex", Caps, []), false,
            () => Path.Combine(_root, "missing"));

        Assert.False(r.Launched);
        Assert.Empty(_runs);
        Assert.False(Directory.Exists(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Prompted_folder_is_used_when_project_unknown()
    {
        var svc = Make();
        var r = svc.Transfer(new FakeAdapter("claude", Caps, []), Source(null), new FakeAdapter("codex", Caps, []), false, () => _root);

        Assert.True(r.Launched);
        Assert.True(File.Exists(Path.Combine(_root, ".docs", "handoff.md")));
    }

    [Fact]
    public void Existing_handoff_is_backed_up()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".docs"));
        File.WriteAllText(Path.Combine(_root, ".docs", "handoff.md"), "OLD");
        var svc = Make();

        var r = svc.Transfer(new FakeAdapter("claude", Caps, []), Source(_root), new FakeAdapter("codex", Caps, []), false, () => null);

        Assert.True(r.Launched);
        Assert.NotNull(r.BackupPath);
        Assert.Equal("OLD", File.ReadAllText(r.BackupPath!));
        Assert.Single(_runs);
    }

    [Fact]
    public void Polish_note_never_appears_in_the_written_file()
    {
        var svc = Make(new AppSettings(PolishEnabled: true), new Caller(_ => throw new InvalidOperationException("STDERR-EXCERPT")));

        var r = svc.Transfer(new FakeAdapter("claude", Caps, []), Source(_root), new FakeAdapter("codex", Caps, []), true, () => null);

        Assert.Contains("STDERR-EXCERPT", r.Note);
        var text = File.ReadAllText(r.HandoffPath);
        Assert.DoesNotContain("STDERR-EXCERPT", text);
        Assert.DoesNotContain("Polish failed", text);
    }

    [Fact]
    public void Unwritable_handoff_launches_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".docs", "handoff.md")); // a directory occupies the path
        var svc = Make();

        var r = svc.Transfer(new FakeAdapter("claude", Caps, []), Source(_root), new FakeAdapter("codex", Caps, []), false, () => null);

        Assert.False(r.Launched);
        Assert.Empty(_runs);
        Assert.Contains("Could not write", r.Note);
    }
}
