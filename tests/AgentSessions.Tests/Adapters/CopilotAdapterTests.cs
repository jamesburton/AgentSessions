using System.Text;
using AgentSessions.Adapters;
using AgentSessions.Model;
using AgentSessions.Tests.Fixtures;

namespace AgentSessions.Tests.Adapters;

public class CopilotAdapterTests
{
    private const string Id = "00000000-0000-4000-8000-000000000001";

    private static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private static string Start(string ts = "2026-01-01T10:00:00.000Z") =>
        J(new { type = "session.start", data = new { sessionId = Id, context = new { cwd = "C:\\Dev\\ctx" } }, id = "e1", timestamp = ts, parentId = (string?)null });

    private static string UserMsg(string text, string ts = "2026-01-01T10:00:02.000Z") =>
        J(new { type = "user.message", data = new { content = text, transformedContent = "<x>" + text }, id = "e2", timestamp = ts, parentId = "e1" });

    private static string AsstMsg(string text, string ts = "2026-01-01T10:01:00.000Z") =>
        J(new { type = "assistant.message", data = new { messageId = "m1", content = text, toolRequests = Array.Empty<object>() }, id = "e3", timestamp = ts, parentId = "e2" });

    private static string ToolStart(string tool, string ts = "2026-01-01T10:02:00.000Z") =>
        J(new { type = "tool.execution_start", data = new { toolCallId = "t1", toolName = tool, arguments = new { path = "C:\\Dev\\proj\\a.cs" } }, id = "e4", timestamp = ts, parentId = "e3" });

    private static string ToolDone(string content, string ts = "2026-01-01T10:03:00.000Z") =>
        J(new { type = "tool.execution_complete", data = new { toolCallId = "t1", success = true, result = new { content } }, id = "e5", timestamp = ts, parentId = "e4" });

    private static string SessionDir(string home, string id = Id) =>
        Path.Combine(home, ".copilot", "session-state", id);

    private static string Events(string home, string id = Id) => Path.Combine(SessionDir(home, id), "events.jsonl");

    private static void Yaml(string home, string cwd, string? updated = "2026-01-01T10:09:00.000Z", string id = Id)
    {
        var dir = SessionDir(home, id);
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine($"id: {id}");
        sb.AppendLine($"cwd: {cwd}");
        sb.AppendLine($"git_root: {cwd}");
        sb.AppendLine("branch: main");
        if (updated is not null) sb.AppendLine($"updated_at: {updated}");
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), sb.ToString());
    }

    private static void Checkpoint(string home, string name, string body, string id = Id)
    {
        var dir = Path.Combine(SessionDir(home, id), "checkpoints");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), body);
    }

    [Fact]
    public void Discover_lists_session_with_title_from_checkpoint()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj");
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("first prompt"), AsstMsg("ok"));
        Checkpoint(home, "index.md", "# Checkpoint History\n\n| # | Title | File |\n|---|---|---|\n| 1 | Old | 001-old.md |\n| 2 | New | 002-new.md |\n");
        Checkpoint(home, "001-old.md", "# Old summary\n\n<overview>\nold\n</overview>\n");
        Checkpoint(home, "002-new-work.md", "# Newest summary heading\n\n<overview>\nnew\n</overview>\n");

        var result = new CopilotAdapter(home).Discover();

        var s = Assert.Single(result.Sessions);
        Assert.Equal("copilot", s.Agent);
        Assert.Equal(Id, s.Id);
        Assert.Equal("Newest summary heading", s.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:01:00Z"), s.LastActivity);
        Assert.Equal(SessionDir(home), s.StorePath);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Discover_title_falls_back_to_overview_then_first_prompt()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj");
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("first prompt"));
        Checkpoint(home, "001-a.md", "<overview>\nOverview line one\nmore\n</overview>\n");
        Assert.Equal("Overview line one", Assert.Single(new CopilotAdapter(home).Discover().Sessions).Title);

        File.Delete(Path.Combine(SessionDir(home), "checkpoints", "001-a.md"));
        Assert.Equal("first prompt", Assert.Single(new CopilotAdapter(home).Discover().Sessions).Title);
    }

    [Fact]
    public void Discover_lists_session_while_agent_holds_workspace_yaml_and_checkpoint_open_for_write()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\live");
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("first prompt"));
        Checkpoint(home, "001-a.md", "# Live heading\n");
        using var y = new FileStream(Path.Combine(SessionDir(home), "workspace.yaml"), FileMode.Open,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var c = new FileStream(Path.Combine(SessionDir(home), "checkpoints", "001-a.md"), FileMode.Open,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var result = new CopilotAdapter(home).Discover();

        var s = Assert.Single(result.Sessions);
        Assert.Equal("C:\\Dev\\live", s.ProjectPath);
        Assert.Equal("Live heading", s.Title);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Discover_does_not_enumerate_snapshot_trees()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj");
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("hi"));
        var snaps = Path.Combine(SessionDir(home), "rewind-snapshots");
        Directory.CreateDirectory(snaps);
        for (var i = 0; i < 3000; i++) File.WriteAllText(Path.Combine(snaps, $"snap-{i:D5}.txt"), "x");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new CopilotAdapter(home).Discover();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var s = Assert.Single(result.Sessions);
        Assert.Equal(new FileInfo(Events(home)).Length +
                     new FileInfo(Path.Combine(SessionDir(home), "workspace.yaml")).Length, s.SizeBytes);
        Assert.True(allocated < 300_000, $"Discover allocated {allocated} bytes");
    }

    [Fact]
    public void Checkpoint_heading_after_a_section_tag_does_not_override_overview()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj");
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("first prompt"));
        Checkpoint(home, "001-a.md", "<overview>\nOverview wins\n</overview>\n<history>\n# Inner heading\n</history>\n");
        Assert.Equal("Overview wins", Assert.Single(new CopilotAdapter(home).Discover().Sessions).Title);
    }

    [Fact]
    public void Discover_recovers_project_path_from_workspace_yaml()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\my app");
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("hi"));
        var s = Assert.Single(new CopilotAdapter(home).Discover().Sessions);
        Assert.Equal("C:\\Dev\\my app", s.ProjectPath);
    }

    [Fact]
    public void Discover_uses_session_start_context_cwd_when_no_workspace_yaml()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("hi"));
        var s = Assert.Single(new CopilotAdapter(home).Discover().Sessions);
        Assert.Equal("C:\\Dev\\ctx", s.ProjectPath);
    }

    [Fact]
    public void Truncated_events_line_still_lists_session()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj");
        Directory.CreateDirectory(SessionDir(home));
        File.WriteAllText(Events(home),
            Start() + "\n" + UserMsg("first prompt") + "\n" + AsstMsg("reply") + "\n" +
            "{\"type\":\"user.message\",\"timestamp\":\"2026-01-01T11:00:00.0");

        var adapter = new CopilotAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        Assert.Equal("first prompt", s.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:01:00Z"), s.LastActivity);
        Assert.Equal(2, adapter.ReadTranscript(s).Count());
    }

    [Fact]
    public void Discover_without_events_uses_workspace_updated_at()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj", "2026-04-04T08:00:00.000Z");
        var s = Assert.Single(new CopilotAdapter(home).Discover().Sessions);
        Assert.Equal(DateTimeOffset.Parse("2026-04-04T08:00:00Z"), s.LastActivity);
        Assert.Equal("(no prompt)", s.Title);
    }

    [Fact]
    public void Discover_ignores_loose_files_and_reports_empty_folders_as_skipped()
    {
        var home = SessionFixtures.TempHome();
        var root = Path.Combine(home, ".copilot", "session-state");
        Directory.CreateDirectory(Path.Combine(root, "empty-one"));
        File.WriteAllText(Path.Combine(root, Id + ".lock"), "");
        var result = new CopilotAdapter(home).Discover();
        Assert.Empty(result.Sessions);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Discover_ignores_dot_prefixed_bookkeeping_folders()
    {
        // Found by the real-data smoke test: the agent keeps a hidden lock folder beside the sessions.
        var home = SessionFixtures.TempHome();
        var root = Path.Combine(home, ".copilot", "session-state");
        Directory.CreateDirectory(Path.Combine(root, ".session-operation-locks"));
        var result = new CopilotAdapter(home).Discover();
        Assert.Empty(result.Sessions);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Discover_large_session_does_not_parse_whole_file()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\my app");
        var path = Events(home);
        var filler = new string('x', 1000);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(Start());
            w.WriteLine(UserMsg("big one"));
            for (var i = 0; i < 20_000; i++) w.WriteLine(AsstMsg(filler));
            w.WriteLine(AsstMsg("last", "2026-02-02T12:00:00.000Z"));
            w.Write("{\"timestamp\":\"2026-03-03T00:00:00.0"); // truncated
        }
        Assert.True(new FileInfo(path).Length > 19_000_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new CopilotAdapter(home).Discover();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var s = Assert.Single(result.Sessions);
        Assert.Equal("big one", s.Title);
        Assert.Equal("C:\\Dev\\my app", s.ProjectPath);
        Assert.Equal(DateTimeOffset.Parse("2026-02-02T12:00:00Z"), s.LastActivity);
        Assert.True(allocated < 3_000_000, $"Discover allocated {allocated} bytes");
    }

    [Fact]
    public void Discover_final_line_larger_than_tail_window_uses_file_mtime()
    {
        var home = SessionFixtures.TempHome();
        var path = Events(home);
        Directory.CreateDirectory(SessionDir(home));
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(Start());
            w.WriteLine(UserMsg("hello"));
            w.WriteLine(AsstMsg(new string('x', 100_000), "2026-01-01T10:05:00.000Z"));
        }
        var mtime = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, mtime);

        var s = Assert.Single(new CopilotAdapter(home).Discover().Sessions);
        Assert.Equal(new DateTimeOffset(mtime), s.LastActivity);
    }

    [Fact]
    public void Discover_large_file_without_prompt_or_cwd_is_bounded_and_uses_placeholders()
    {
        var home = SessionFixtures.TempHome();
        var path = Events(home);
        Directory.CreateDirectory(SessionDir(home));
        var filler = new string('x', 1000);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < 20_000; i++) w.WriteLine(AsstMsg(filler));
            w.WriteLine(AsstMsg("last", "2026-02-02T12:00:00.000Z"));
        }
        Assert.True(new FileInfo(path).Length > 19_000_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new CopilotAdapter(home).Discover();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var s = Assert.Single(result.Sessions);
        Assert.Equal("(no prompt)", s.Title);
        Assert.Null(s.ProjectPath);
        Assert.Equal(DateTimeOffset.Parse("2026-02-02T12:00:00Z"), s.LastActivity);
        Assert.True(allocated < 3_000_000, $"Discover allocated {allocated} bytes");
    }

    [Fact]
    public void ReadTranscript_maps_roles_and_tools()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj");
        SessionFixtures.WriteLines(Events(home), Start(), UserMsg("go"), AsstMsg("sure"),
            ToolStart("edit"), ToolDone("done"));
        var adapter = new CopilotAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        var e = adapter.ReadTranscript(s).ToList();
        Assert.Equal(new[] { Role.User, Role.Assistant, Role.Tool, Role.Tool }, e.Select(x => x.Role));
        Assert.Equal("go", e[0].Text);
        Assert.Equal("edit", e[2].ToolName);
        Assert.Equal(new[] { "C:\\Dev\\proj\\a.cs" }, e[2].FilesTouched);
        Assert.Equal("done", e[3].Text);
    }

    [Fact]
    public void ReadTranscript_streams_large_file_without_loading_whole()
    {
        var home = SessionFixtures.TempHome();
        Directory.CreateDirectory(SessionDir(home));
        var path = Events(home);
        var filler = new string('x', 1000);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(UserMsg("first"));
            for (var i = 0; i < 20_000; i++) w.WriteLine(AsstMsg(filler));
        }
        var adapter = new CopilotAdapter(home);
        var info = new SessionInfo("copilot", Id, null, "t", DateTimeOffset.UtcNow, 1, SessionDir(home));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = adapter.ReadTranscript(info).First();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("first", first.Text);
        Assert.True(allocated < 5_000_000, $"allocated {allocated} bytes before first entry");
        Assert.Equal(20_001, adapter.ReadTranscript(info).Count());
    }

    [Fact]
    public void BuildResume_returns_copilot_resume_with_id()
    {
        var s = new SessionInfo("copilot", Id, "C:\\Dev\\my app", "t", DateTimeOffset.UtcNow, 1, "x");
        var spec = new CopilotAdapter(SessionFixtures.TempHome()).BuildResume(s);
        Assert.Equal("copilot", spec.FileName);
        Assert.Equal(new[] { "--resume", Id }, spec.Args);
        Assert.Equal("C:\\Dev\\my app", spec.WorkingDirectory);
    }

    [Fact]
    public void BuildNew_uses_interactive_prompt_flag()
    {
        var spec = new CopilotAdapter(SessionFixtures.TempHome()).BuildNew("C:\\Dev\\proj", "do the thing");
        Assert.Equal("copilot", spec.FileName);
        Assert.Equal(new[] { "-i", "do the thing" }, spec.Args);
        Assert.Equal("C:\\Dev\\proj", spec.WorkingDirectory);
    }

    [Fact]
    public void OwnedPaths_is_only_the_session_folder()
    {
        var home = SessionFixtures.TempHome();
        Yaml(home, "C:\\Dev\\proj");
        var adapter = new CopilotAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        Assert.Equal(new[] { SessionDir(home) }, adapter.OwnedPaths(s));
    }

    // Safety spike verdict: UNSAFE (see docs/formats/copilot.md). Cleanup is declined.
    [Fact]
    public void Capabilities_exclude_archive_and_delete()
    {
        var caps = new CopilotAdapter(SessionFixtures.TempHome()).Capabilities;
        Assert.Equal(AdapterCapabilities.List | AdapterCapabilities.Resume |
                     AdapterCapabilities.LaunchWithPrompt | AdapterCapabilities.Transcript, caps);
        Assert.False(caps.HasFlag(AdapterCapabilities.Archive));
        Assert.False(caps.HasFlag(AdapterCapabilities.Delete));
    }
}
