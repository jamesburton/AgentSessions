using System.Diagnostics;
using System.Text.Json;
using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;
using Xunit;

namespace AgentSessions.Tests.Services;

public sealed class CleanupServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private const AdapterCapabilities All = AdapterCapabilities.List | AdapterCapabilities.Archive | AdapterCapabilities.Delete;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "as-cleanup-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_tmp, "home");
    private string Store => Path.Combine(_tmp, "home", "store");
    private string ArchiveRoot => Path.Combine(_tmp, "archive");

    public CleanupServiceTests() => Directory.CreateDirectory(Store);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    private sealed class TestAdapter(string name, AdapterCapabilities caps, Func<SessionInfo, IReadOnlyList<string>> owned) : ISessionAdapter
    {
        public List<string> AfterRemoved { get; } = [];
        public Exception? AfterRemoveThrows { get; set; }
        public string Name => name;
        public AdapterCapabilities Capabilities => caps;
        public DiscoveryResult Discover() => new([], []);
        public IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s) => [];
        public LaunchSpec BuildResume(SessionInfo s) => new(name, [], ".");
        public LaunchSpec BuildNew(string projectPath, string prompt) => new(name, [], projectPath);
        public IReadOnlyList<string> OwnedPaths(SessionInfo s) => owned(s);
        public void AfterRemove(SessionInfo s)
        {
            if (AfterRemoveThrows is not null) throw AfterRemoveThrows;
            AfterRemoved.Add(s.Id);
        }
    }

    private static SessionInfo Info(string id, string path, string agent = "claude") =>
        new(agent, id, "C:/p", "t", Now, 1, path);

    private static ListedSession Listed(SessionInfo i, bool active = false, AdapterCapabilities caps = All) =>
        new(i, false, active, caps);

    private string Write(string rel, string content = "data")
    {
        var p = Path.GetFullPath(Path.Combine(Store, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private (CleanupService Svc, ArchiveStore Arc, TestAdapter Adapter) Make(
        AdapterCapabilities caps = All, Func<SessionInfo, IReadOnlyList<string>>? owned = null)
    {
        var adapter = new TestAdapter("claude", caps, owned ?? (s => [s.StorePath]));
        var arc = new ArchiveStore(ArchiveRoot, Home);
        var svc = new CleanupService(new Dictionary<string, ISessionAdapter> { ["claude"] = adapter }, arc, Home);
        return (svc, arc, adapter);
    }

    private static List<string> Snapshot(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order().ToList()
            : [];

    [Fact]
    public void Plan_has_no_side_effects()
    {
        var f = Write("a.jsonl");
        var (svc, _, adapter) = Make();
        var before = Snapshot(_tmp);
        var plan = svc.Plan([Listed(Info("a", f))], CleanupAction.Delete);
        Assert.Equal(before, Snapshot(_tmp));
        Assert.True(File.Exists(f));
        Assert.Empty(adapter.AfterRemoved);
        Assert.Equal([f], plan.Single().Paths);
        Assert.Null(plan.Single().Declined);
    }

    [Fact]
    public void Plan_declines_action_when_adapter_lacks_capability()
    {
        var f = Write("a.jsonl");
        var (svc, _, _) = Make(AdapterCapabilities.List | AdapterCapabilities.Archive);
        var plan = svc.Plan([Listed(Info("a", f))], CleanupAction.Delete);
        Assert.False(string.IsNullOrWhiteSpace(plan.Single().Declined));
        Assert.Equal(CleanupAction.Delete, plan.Single().Action);
        var archivePlan = svc.Plan([Listed(Info("a", f))], CleanupAction.Archive);
        Assert.Null(archivePlan.Single().Declined);
        // Unknown agent is declined too, not thrown.
        var unknown = svc.Plan([Listed(Info("z", f, "nope"))], CleanupAction.Archive);
        Assert.NotNull(unknown.Single().Declined);
    }

    [Fact]
    public void Plan_warns_for_active_session_but_allows()
    {
        var f = Write("a.jsonl");
        var (svc, _, _) = Make();
        var item = svc.Plan([Listed(Info("a", f), active: true)], CleanupAction.Delete).Single();
        Assert.False(string.IsNullOrWhiteSpace(item.Warning));
        Assert.Null(item.Declined);
        Assert.Null(svc.Plan([Listed(Info("a", f), active: false)], CleanupAction.Delete).Single().Warning);
    }

    [Fact]
    public void Archive_moves_files_and_writes_manifest_preserving_relative_path()
    {
        var f = Write("proj/a.jsonl", "hello");
        var dirPath = Path.Combine(Store, "tree");
        Directory.CreateDirectory(Path.Combine(dirPath, "sub"));
        File.WriteAllText(Path.Combine(dirPath, "sub", "x.txt"), "x");
        var missing = Path.Combine(Store, "gone.txt");
        var arc = new ArchiveStore(ArchiveRoot, Home);

        var dir = arc.Archive(Info("s1", f), [f, dirPath, missing]);

        Assert.Equal(Path.Combine(ArchiveRoot, "claude", "s1"), dir);
        Assert.False(File.Exists(f));
        Assert.False(Directory.Exists(dirPath));
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")),
            new JsonSerializerOptions())!;
        Assert.Equal("claude", manifest.Agent);
        Assert.Equal("s1", manifest.Id);
        Assert.Equal(string.Join("|", new[] { f, dirPath, missing }), string.Join("|", manifest.Entries.Select(e => e.OriginalPath)));
        Assert.Equal("missing", manifest.Entries[2].Kind);
        var archivedFile = Path.GetFullPath(Path.Combine(dir, manifest.Entries[0].ArchivedPath!));
        Assert.Equal("hello", File.ReadAllText(archivedFile));
        Assert.EndsWith(Path.Combine("proj", "a.jsonl"), archivedFile);
        Assert.True(File.Exists(Path.Combine(dir, manifest.Entries[1].ArchivedPath!, "sub", "x.txt")));
    }

    [Fact]
    public void Archive_refuses_when_archive_dir_exists_and_changes_nothing()
    {
        var f = Write("a.jsonl");
        var arc = new ArchiveStore(ArchiveRoot, Home);
        Directory.CreateDirectory(Path.Combine(ArchiveRoot, "claude", "s1"));
        File.WriteAllText(Path.Combine(ArchiveRoot, "claude", "s1", "keep.txt"), "k");
        Assert.Throws<IOException>(() => arc.Archive(Info("s1", f), [f]));
        Assert.True(File.Exists(f));
        Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(Path.Combine(ArchiveRoot, "claude", "s1")).Select(Path.GetFileName));
    }

    [Fact]
    public void Restore_puts_files_back_exactly()
    {
        var f = Write("proj/a.jsonl", "hello");
        var dirPath = Path.Combine(Store, "tree");
        Directory.CreateDirectory(Path.Combine(dirPath, "sub"));
        File.WriteAllText(Path.Combine(dirPath, "sub", "x.txt"), "x");
        var arc = new ArchiveStore(ArchiveRoot, Home);
        var dir = arc.Archive(Info("s1", f), [f, dirPath]);
        Directory.Delete(Path.Combine(Store, "proj"), true);   // parent folder gone: Restore must recreate it

        arc.Restore(dir);

        Assert.Equal("hello", File.ReadAllText(f));
        Assert.Equal("x", File.ReadAllText(Path.Combine(dirPath, "sub", "x.txt")));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Restore_refuses_when_destination_exists_and_changes_nothing()
    {
        var f1 = Write("a.jsonl", "one");
        var f2 = Write("b.jsonl", "two");
        var arc = new ArchiveStore(ArchiveRoot, Home);
        var dir = arc.Archive(Info("s1", f1), [f1, f2]);
        File.WriteAllText(f2, "NEWER");   // conflict on the second item only
        var archiveBefore = Snapshot(dir);

        var ex = Assert.Throws<IOException>(() => arc.Restore(dir));

        Assert.Contains(f2, ex.Message);
        Assert.Equal("NEWER", File.ReadAllText(f2));
        Assert.False(File.Exists(f1));                      // first item NOT restored
        Assert.Equal(archiveBefore, Snapshot(dir));         // archive untouched
    }

    [Fact]
    public void Delete_removes_owned_paths_only()
    {
        var owned = Write("a.jsonl");
        var sibling = Write("b.jsonl");
        var (svc, _, adapter) = Make();
        var item = svc.Plan([Listed(Info("a", owned))], CleanupAction.Delete);
        var result = svc.Apply(item);
        Assert.Single(result.Done);
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(sibling));
        Assert.True(Directory.Exists(Store));
        Assert.Equal(["a"], adapter.AfterRemoved);
    }

    [Fact]
    public void Apply_calls_AfterRemove()
    {
        var f = Write("a.jsonl");
        var (svc, _, adapter) = Make();
        var result = svc.Apply(svc.Plan([Listed(Info("a", f))], CleanupAction.Archive));
        Assert.Single(result.Done);
        Assert.Empty(result.Failed);
        Assert.Equal(["a"], adapter.AfterRemoved);
        Assert.False(File.Exists(f));
        Assert.True(Directory.Exists(Path.Combine(ArchiveRoot, "claude", "a")));
    }

    [Fact]
    public void Apply_with_one_failure_continues_and_reports()
    {
        var good1 = Write("a.jsonl");
        var good2 = Write("c.jsonl");
        var (svc, _, adapter) = Make();
        var bad = Info("b", Path.Combine(Store, "b.jsonl"));
        var items = svc.Plan([Listed(Info("a", good1)), Listed(bad), Listed(Info("c", good2))], CleanupAction.Archive);
        // Make the middle one fail: archive dir already exists.
        Directory.CreateDirectory(Path.Combine(ArchiveRoot, "claude", "b"));
        Write("b.jsonl");

        var result = svc.Apply(items);

        Assert.Equal(["a", "c"], result.Done.Select(i => i.Session.Info.Id));
        Assert.Equal(["b"], result.Failed.Select(f => f.Item.Session.Info.Id));
        Assert.True(File.Exists(Path.Combine(Store, "b.jsonl")));
        Assert.Equal(["a", "c"], adapter.AfterRemoved);
    }

    [Fact]
    public void Apply_skips_declined_items_neither_done_nor_failed()
    {
        var f = Write("a.jsonl");
        var (svc, _, adapter) = Make(AdapterCapabilities.List);
        var result = svc.Apply(svc.Plan([Listed(Info("a", f))], CleanupAction.Delete));
        Assert.Empty(result.Done);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(f));
        Assert.Empty(adapter.AfterRemoved);
    }

    [Fact]
    public void Apply_reports_failure_when_AfterRemove_throws_and_says_files_were_already_removed()
    {
        var f = Write("a.jsonl");
        var (svc, _, adapter) = Make();
        adapter.AfterRemoveThrows = new InvalidOperationException("index locked");
        var result = svc.Apply(svc.Plan([Listed(Info("a", f))], CleanupAction.Delete));
        Assert.Empty(result.Done);
        var failure = Assert.Single(result.Failed);
        Assert.Contains("already deleted", failure.Error);
        Assert.Contains("index locked", failure.Error);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Archive_rolls_back_earlier_moves_when_a_later_move_fails()
    {
        var f1 = Write("a.jsonl", "one");
        var f2 = Write("b.jsonl", "two");
        var arc = new ArchiveStore(ArchiveRoot, Home);
        using (new FileStream(f2, FileMode.Open, FileAccess.Read, FileShare.None))   // lock: move of f2 fails
        {
            Assert.Throws<IOException>(() => arc.Archive(Info("s1", f1), [f1, f2]));
        }
        Assert.Equal("one", File.ReadAllText(f1));
        Assert.Equal("two", File.ReadAllText(f2));
        Assert.False(Directory.Exists(Path.Combine(ArchiveRoot, "claude", "s1")));
    }

    [Fact]
    public void Archive_after_failed_attempt_can_be_retried()
    {
        var f1 = Write("a.jsonl", "one");
        var f2 = Write("b.jsonl", "two");
        var arc = new ArchiveStore(ArchiveRoot, Home);
        using (new FileStream(f2, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => arc.Archive(Info("s1", f1), [f1, f2]));
        arc.Archive(Info("s1", f1), [f1, f2]);
        Assert.False(File.Exists(f1));
    }

    [Fact]
    public void Guards_reject_unsafe_paths_for_delete_and_touch_nothing()
    {
        var safe = Write("a.jsonl");
        var driveRoot = Path.GetPathRoot(_tmp)!;
        string[] unsafePaths = [driveRoot, Home, "", "relative/path.jsonl", Path.GetDirectoryName(Home)!, Path.Combine(Home, "..")];
        foreach (var bad in unsafePaths)
        {
            var (svc, _, adapter) = Make(owned: _ => [safe, bad]);
            var result = svc.Apply(svc.Plan([Listed(Info("a", safe))], CleanupAction.Delete));
            Assert.Empty(result.Done);
            Assert.Single(result.Failed);
            Assert.True(File.Exists(safe), $"safe file deleted despite unsafe path '{bad}'");
            Assert.True(Directory.Exists(Home));
            Assert.Empty(adapter.AfterRemoved);
        }
    }

    [Fact]
    public void Guards_reject_unsafe_paths_for_archive_and_touch_nothing()
    {
        var safe = Write("a.jsonl");
        var arc = new ArchiveStore(ArchiveRoot, Home);
        foreach (var bad in new[] { Path.GetPathRoot(_tmp)!, Home, "", "rel/x" })
        {
            Assert.ThrowsAny<Exception>(() => arc.Archive(Info("s1", safe), [safe, bad]));
            Assert.True(File.Exists(safe));
            Assert.False(Directory.Exists(Path.Combine(ArchiveRoot, "claude", "s1")));
        }
    }

    [Fact]
    public void Delete_refuses_path_that_contains_the_archive()
    {
        var (svc, _, _) = Make(owned: _ => [_tmp]);
        Directory.CreateDirectory(ArchiveRoot);
        var home = new CleanupService(
            new Dictionary<string, ISessionAdapter> { ["claude"] = new TestAdapter("claude", All, _ => [_tmp]) },
            new ArchiveStore(ArchiveRoot, Home), Path.Combine(_tmp, "elsewhere-home"));
        var result = home.Apply(home.Plan([Listed(Info("a", _tmp))], CleanupAction.Delete));
        Assert.Single(result.Failed);
        Assert.True(Directory.Exists(ArchiveRoot));
    }

    [Fact]
    public void Delete_removes_junction_but_not_its_target()
    {
        var target = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "keep");
        var owned = Path.Combine(Store, "owned");
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "mine.txt"), "m");
        var link = Path.Combine(owned, "link");
        MakeJunction(link, target);

        var (svc, _, _) = Make(owned: _ => [owned]);
        var result = svc.Apply(svc.Plan([Listed(Info("a", owned))], CleanupAction.Delete));

        Assert.Single(result.Done);
        Assert.False(Directory.Exists(owned));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "precious.txt")));
    }

    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && Directory.Exists(link), "could not create test junction");
    }
}
