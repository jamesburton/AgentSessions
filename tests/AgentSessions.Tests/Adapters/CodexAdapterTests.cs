using System.Text;
using AgentSessions.Adapters;
using AgentSessions.Model;
using AgentSessions.Tests.Fixtures;

namespace AgentSessions.Tests.Adapters;

public class CodexAdapterTests
{
    private const string Id = "00000000-0000-7000-8000-000000000001";

    private static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private static string Meta(string cwd, string ts = "2026-01-01T10:00:00.000Z") =>
        J(new { timestamp = ts, type = "session_meta", payload = new { id = Id, timestamp = ts, cwd, originator = "codex_cli" } });

    private static string Preamble() =>
        J(new { timestamp = "2026-01-01T10:00:01.000Z", type = "response_item", payload = new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "<environment_context>x</environment_context>" } } } });

    private static string UserMsg(string text, string ts = "2026-01-01T10:00:02.000Z") =>
        J(new { timestamp = ts, type = "event_msg", payload = new { type = "user_message", message = text } });

    private static string AgentMsg(string text, string ts = "2026-01-01T10:01:00.000Z") =>
        J(new { timestamp = ts, type = "event_msg", payload = new { type = "agent_message", message = text } });

    private static string Patch(string file, string ts = "2026-01-01T10:02:00.000Z") =>
        J(new { timestamp = ts, type = "response_item", payload = new { type = "custom_tool_call", name = "apply_patch", call_id = "c1", input = "*** Begin Patch\n*** Update File: " + file + "\n@@\n-a\n+b\n*** End Patch" } });

    private static string Shell(string ts = "2026-01-01T10:03:00.000Z") =>
        J(new { timestamp = ts, type = "response_item", payload = new { type = "function_call", name = "shell_command", call_id = "c2", arguments = "{\"command\":\"ls\"}" } });

    private static string SessionPath(string home, string id = Id) =>
        Path.Combine(home, ".codex", "sessions", "2026", "01", "01", $"rollout-2026-01-01T10-00-00-{id}.jsonl");

    [Fact]
    public void Discover_lists_rollout_with_id_from_filename()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home),
            Meta("C:\\Dev\\proj"), Preamble(), UserMsg("Fix the parser bug"), AgentMsg("ok"));

        var result = new CodexAdapter(home).Discover();

        var s = Assert.Single(result.Sessions);
        Assert.Equal("codex", s.Agent);
        Assert.Equal(Id, s.Id);
        Assert.Equal("Fix the parser bug", s.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:01:00Z"), s.LastActivity);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Discover_recovers_project_path_from_session_meta()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home), Meta("C:\\Dev\\my app"), UserMsg("hi"));
        var s = Assert.Single(new CodexAdapter(home).Discover().Sessions);
        Assert.Equal("C:\\Dev\\my app", s.ProjectPath);
    }

    [Fact]
    public void Truncated_final_line_still_lists_session()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            Meta("C:\\Dev\\proj") + "\n" + UserMsg("first prompt") + "\n" + AgentMsg("reply") + "\n" +
            "{\"timestamp\":\"2026-01-01T11:00:00.0");

        var adapter = new CodexAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        Assert.Equal("first prompt", s.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:01:00Z"), s.LastActivity);
        Assert.Equal(2, adapter.ReadTranscript(s).Count());
    }

    [Fact]
    public void Discover_large_session_does_not_parse_whole_file()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var filler = new string('x', 1000);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(Meta("C:\\Dev\\my app"));
            w.WriteLine(UserMsg("big one"));
            for (var i = 0; i < 20_000; i++) w.WriteLine(AgentMsg(filler));
            w.WriteLine(AgentMsg("last", "2026-02-02T12:00:00.000Z"));
            w.Write("{\"timestamp\":\"2026-03-03T00:00:00.0"); // truncated
        }
        Assert.True(new FileInfo(path).Length > 19_000_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new CodexAdapter(home).Discover();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var s = Assert.Single(result.Sessions);
        Assert.Equal("big one", s.Title);
        Assert.Equal("C:\\Dev\\my app", s.ProjectPath);
        Assert.Equal(DateTimeOffset.Parse("2026-02-02T12:00:00Z"), s.LastActivity);
        Assert.True(allocated < 3_000_000, $"Discover allocated {allocated} bytes");
    }

    [Fact]
    public void BuildResume_returns_codex_resume_with_id()
    {
        var s = new SessionInfo("codex", Id, "C:\\Dev\\my app", "t", DateTimeOffset.UtcNow, 1, "x");
        var spec = new CodexAdapter(SessionFixtures.TempHome()).BuildResume(s);
        Assert.Equal("codex", spec.FileName);
        Assert.Equal(new[] { "resume", Id }, spec.Args);
        Assert.Equal("C:\\Dev\\my app", spec.WorkingDirectory);
    }

    [Fact]
    public void BuildNew_passes_prompt_positionally()
    {
        var spec = new CodexAdapter(SessionFixtures.TempHome()).BuildNew("C:\\Dev\\proj", "do the thing");
        Assert.Equal("codex", spec.FileName);
        Assert.Equal(new[] { "do the thing" }, spec.Args);
        Assert.Equal("C:\\Dev\\proj", spec.WorkingDirectory);
    }

    [Fact]
    public void OwnedPaths_returns_the_rollout_file()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        SessionFixtures.WriteLines(path, Meta("C:\\Dev\\proj"), UserMsg("x"));
        var adapter = new CodexAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        Assert.Equal(new[] { path }, adapter.OwnedPaths(s));
    }

    [Fact]
    public void Discover_includes_archived_sessions()
    {
        var home = SessionFixtures.TempHome();
        var archId = "00000000-0000-7000-8000-000000000002";
        var arch = Path.Combine(home, ".codex", "archived_sessions", $"rollout-2026-01-02T10-00-00-{archId}.jsonl");
        SessionFixtures.WriteLines(SessionPath(home), Meta("C:\\Dev\\proj"), UserMsg("active"));
        SessionFixtures.WriteLines(arch, Meta("C:\\Dev\\old"), UserMsg("archived"));

        var sessions = new CodexAdapter(home).Discover().Sessions;
        Assert.Equal(2, sessions.Count);
        var a = Assert.Single(sessions, x => x.Id == archId);
        Assert.Equal(arch, a.StorePath);
        Assert.Contains("archived_sessions", a.StorePath);
        Assert.DoesNotContain("archived_sessions", sessions.Single(x => x.Id == Id).StorePath);
    }

    [Fact]
    public void ReadTranscript_maps_roles_tools_and_files()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home),
            Meta("C:\\Dev\\proj"), Preamble(), UserMsg("go"), AgentMsg("sure"),
            Patch("C:\\Dev\\proj\\a.cs"), Shell(),
            J(new { timestamp = "2026-01-01T10:04:00.000Z", type = "response_item", payload = new { type = "function_call_output", call_id = "c2", output = "done" } }));
        var adapter = new CodexAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        var e = adapter.ReadTranscript(s).ToList();
        Assert.Equal(new[] { Role.User, Role.Assistant, Role.Tool, Role.Tool, Role.Tool }, e.Select(x => x.Role));
        Assert.Equal("apply_patch", e[2].ToolName);
        Assert.Equal(new[] { "C:\\Dev\\proj\\a.cs" }, e[2].FilesTouched);
        Assert.Equal("shell_command", e[3].ToolName);
        Assert.Equal("done", e[4].Text);
    }

    [Fact]
    public void ReadTranscript_streams_large_file_without_loading_whole()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var filler = new string('x', 1000);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(Meta("C:\\Dev\\proj"));
            w.WriteLine(UserMsg("first"));
            for (var i = 0; i < 20_000; i++) w.WriteLine(AgentMsg(filler));
        }
        var adapter = new CodexAdapter(home);
        var info = new SessionInfo("codex", Id, "C:\\Dev\\proj", "t", DateTimeOffset.UtcNow, new FileInfo(path).Length, path);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = adapter.ReadTranscript(info).First();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("first", first.Text);
        Assert.True(allocated < 5_000_000, $"allocated {allocated} bytes before first entry");
        Assert.Equal(20_001, adapter.ReadTranscript(info).Count());
    }

    [Fact]
    public void Discover_final_line_larger_than_tail_window_uses_file_mtime()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(Meta("C:\\Dev\\proj"));
            w.WriteLine(UserMsg("hello"));
            w.WriteLine(AgentMsg(new string('x', 100_000), "2026-01-01T10:05:00.000Z"));
        }
        var mtime = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, mtime);

        var s = Assert.Single(new CodexAdapter(home).Discover().Sessions);
        Assert.Equal(new DateTimeOffset(mtime), s.LastActivity);
    }

    [Fact]
    public void Discover_large_file_without_prompt_or_cwd_is_bounded_and_uses_placeholders()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var filler = new string('x', 1000);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < 20_000; i++) w.WriteLine(AgentMsg(filler));
            w.WriteLine(AgentMsg("last", "2026-02-02T12:00:00.000Z"));
        }
        Assert.True(new FileInfo(path).Length > 19_000_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new CodexAdapter(home).Discover();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var s = Assert.Single(result.Sessions);
        Assert.Equal("(no prompt)", s.Title);
        Assert.Null(s.ProjectPath);
        Assert.Equal(DateTimeOffset.Parse("2026-02-02T12:00:00Z"), s.LastActivity);
        Assert.True(allocated < 3_000_000, $"Discover allocated {allocated} bytes");
    }

    [Fact]
    public void Discover_uses_turn_context_cwd_when_session_meta_has_none()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home),
            J(new { timestamp = "2026-01-01T10:00:00.000Z", type = "turn_context", payload = new { cwd = "C:\\Dev\\tc" } }),
            UserMsg("hi"));
        var s = Assert.Single(new CodexAdapter(home).Discover().Sessions);
        Assert.Equal("C:\\Dev\\tc", s.ProjectPath);
    }
}
