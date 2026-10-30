using System.Diagnostics;
using System.Text;
using AgentSessions.Adapters;
using AgentSessions.Model;
using AgentSessions.Tests.Fixtures;

namespace AgentSessions.Tests.Adapters;

public class ClaudeAdapterTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    private static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private static string UserLine(string text, string cwd, string ts = "2026-01-01T10:00:00.000Z") =>
        J(new { type = "user", sessionId = Id, timestamp = ts, cwd, message = new { role = "user", content = text } });

    private static string AssistantText(string text, string ts = "2026-01-01T10:01:00.000Z") =>
        J(new { type = "assistant", sessionId = Id, timestamp = ts, message = new { role = "assistant", content = new object[] { new { type = "text", text } } } });

    private static string AssistantTool(string file, string ts = "2026-01-01T10:02:00.000Z") =>
        J(new { type = "assistant", sessionId = Id, timestamp = ts, message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = "t1", name = "Edit", input = new { file_path = file } } } } });

    private static string SessionPath(string home, string folder = "C--Dev-proj", string id = Id) =>
        Path.Combine(home, ".claude", "projects", folder, id + ".jsonl");

    [Fact]
    public void Discover_lists_session_with_title_from_first_user_prompt()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home),
            UserLine("Fix the parser bug", "C:\\Dev\\proj"),
            AssistantText("ok"));

        var result = new ClaudeAdapter(home).Discover();

        var s = Assert.Single(result.Sessions);
        Assert.Equal("claude", s.Agent);
        Assert.Equal(Id, s.Id);
        Assert.Equal("Fix the parser bug", s.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:01:00Z"), s.LastActivity);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Discover_recovers_project_path_from_session_content()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home, "C--Dev-my-app"),
            UserLine("hi", "C:\\Dev\\my app"));

        var s = Assert.Single(new ClaudeAdapter(home).Discover().Sessions);
        Assert.Equal("C:\\Dev\\my app", s.ProjectPath);
    }

    [Fact]
    public void Discover_skips_subagents_folder_as_separate_sessions()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home), UserLine("main", "C:\\Dev\\proj"));
        var sub = Path.Combine(home, ".claude", "projects", "C--Dev-proj", Id, "subagents", "agent-abc.jsonl");
        SessionFixtures.WriteLines(sub, UserLine("sub", "C:\\Dev\\proj"));

        var result = new ClaudeAdapter(home).Discover();
        Assert.Single(result.Sessions);
    }

    [Fact]
    public void Truncated_final_line_still_lists_session_and_reads_earlier_entries()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            UserLine("first prompt", "C:\\Dev\\proj") + "\n" +
            AssistantText("reply") + "\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-01-01T11:00:00.0");

        var adapter = new ClaudeAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        Assert.Equal("first prompt", s.Title);
        var entries = adapter.ReadTranscript(s).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(Role.User, entries[0].Role);
        Assert.Equal(Role.Assistant, entries[1].Role);
    }

    [Fact]
    public void Read_succeeds_while_file_is_open_for_write()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(UserLine("live", "C:\\Dev\\proj") + "\n");
        fs.Write(bytes);
        fs.Flush();

        var rows = JsonlReader.Read(path).Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Equal(new[] { "user" }, rows);
        var s = Assert.Single(new ClaudeAdapter(home).Discover().Sessions);
        Assert.Equal("live", s.Title);
    }

    [Fact]
    public void ReadTranscript_streams_large_file_without_loading_whole()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var filler = new string('x', 1000);
        const int count = 20_000; // ~20 MB
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(UserLine("first", "C:\\Dev\\proj"));
            for (var i = 0; i < count; i++) w.WriteLine(AssistantText(filler));
        }
        Assert.True(new FileInfo(path).Length > 19_000_000);

        var adapter = new ClaudeAdapter(home);
        var info = new SessionInfo("claude", Id, "C:\\Dev\\proj", "t", DateTimeOffset.UtcNow, new FileInfo(path).Length, path);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var first = adapter.ReadTranscript(info).First();
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("first", first.Text);
        Assert.True(allocated < 5_000_000, $"allocated {allocated} bytes before first entry");

        var total = adapter.ReadTranscript(info).Count();
        Assert.Equal(count + 1, total);
    }

    [Fact]
    public void BuildResume_returns_claude_resume_with_session_id_in_project_dir()
    {
        var adapter = new ClaudeAdapter(SessionFixtures.TempHome());
        var s = new SessionInfo("claude", Id, "C:\\Dev\\my app", "t", DateTimeOffset.UtcNow, 1, "x");
        var spec = adapter.BuildResume(s);
        Assert.Equal("claude", spec.FileName);
        Assert.Equal(new[] { "--resume", Id }, spec.Args);
        Assert.Equal("C:\\Dev\\my app", spec.WorkingDirectory);
    }

    [Fact]
    public void BuildNew_passes_prompt_as_positional_argument()
    {
        var spec = new ClaudeAdapter(SessionFixtures.TempHome()).BuildNew("C:\\Dev\\proj", "do the thing");
        Assert.Equal("claude", spec.FileName);
        Assert.Equal(new[] { "do the thing" }, spec.Args);
        Assert.Equal("C:\\Dev\\proj", spec.WorkingDirectory);
    }

    [Fact]
    public void OwnedPaths_includes_session_jsonl_and_its_sibling_folder()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        SessionFixtures.WriteLines(path, UserLine("x", "C:\\Dev\\proj"));
        var sibling = Path.Combine(Path.GetDirectoryName(path)!, Id);
        Directory.CreateDirectory(sibling);

        var adapter = new ClaudeAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        var owned = adapter.OwnedPaths(s);
        Assert.Contains(path, owned);
        Assert.Contains(sibling, owned);

        Directory.Delete(sibling);
        Assert.Equal(new[] { path }, adapter.OwnedPaths(s));
    }

    [Fact]
    public void ReadTranscript_maps_roles_tools_and_files()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        SessionFixtures.WriteLines(path,
            UserLine("go", "C:\\Dev\\proj"),
            AssistantTool("C:\\Dev\\proj\\a.cs"),
            "{\"type\":\"api-request-blob\",\"message\":{\"role\":\"user\",\"content\":\"ignore\"}}");
        var adapter = new ClaudeAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        var e = adapter.ReadTranscript(s).ToList();
        Assert.Equal(2, e.Count);
        Assert.Equal(Role.Tool, e[1].Role);
        Assert.Equal("Edit", e[1].ToolName);
        Assert.Equal(new[] { "C:\\Dev\\proj\\a.cs" }, e[1].FilesTouched);
    }
}
