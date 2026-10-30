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
        var only = Assert.Single(result.Sessions);
        Assert.Equal(Id, only.Id);
        Assert.Equal(SessionPath(home), only.StorePath);
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
        var first = adapter.ReadTranscript(info).First();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("first", first.Text);
        Assert.True(allocated < 5_000_000, $"allocated {allocated} bytes before first entry");

        var total = adapter.ReadTranscript(info).Count();
        Assert.Equal(count + 1, total);
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
            w.WriteLine(UserLine("big one", "C:\\Dev\\my app"));
            for (var i = 0; i < 20_000; i++) w.WriteLine(AssistantText(filler));
            w.WriteLine(AssistantText("last", "2026-02-02T12:00:00.000Z"));
            w.Write("{\"type\":\"assistant\",\"timestamp\":\"2026-03-03T00:00:00.0"); // truncated
        }
        Assert.True(new FileInfo(path).Length > 19_000_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new ClaudeAdapter(home).Discover();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var s = Assert.Single(result.Sessions);
        Assert.Equal("big one", s.Title);
        Assert.Equal("C:\\Dev\\my app", s.ProjectPath);
        Assert.Equal(DateTimeOffset.Parse("2026-02-02T12:00:00Z"), s.LastActivity);
        Assert.True(allocated < 3_000_000, $"Discover allocated {allocated} bytes");
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

    [Fact]
    public void Discover_skips_sessions_whose_id_could_become_a_cli_flag_or_is_unsafe()
    {
        var home = SessionFixtures.TempHome();
        foreach (var id in new[] { "--dangerously-skip-permissions", "a b", "x&y", "ok-id_1.2" })
            SessionFixtures.WriteLines(SessionPath(home, id: id), UserLine("p " + id, "C:\\Dev\\proj"));

        var result = new ClaudeAdapter(home).Discover();

        Assert.Equal("ok-id_1.2", Assert.Single(result.Sessions).Id);
        Assert.Equal(3, result.Skipped.Count);
        Assert.All(result.Skipped, k => Assert.Contains("unsafe session id", k.Reason));
    }

    [WindowsFact]
    public void One_unreadable_project_folder_does_not_hide_the_other_projects()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home, "C--Dev-good"), UserLine("good one", "C:\\Dev\\good"));
        // A dangling junction is listed as a project folder but cannot be enumerated.
        var projects = Path.Combine(home, ".claude", "projects");
        var target = Path.Combine(home, "gone");
        Directory.CreateDirectory(target);
        var link = Path.Combine(projects, "C--Dev-broken");
        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        mk.WaitForExit();
        Assert.Equal(0, mk.ExitCode);
        Directory.Delete(target);

        var result = new ClaudeAdapter(home).Discover();

        Assert.Equal("good one", Assert.Single(result.Sessions).Title);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(link, skipped.Path);
    }

    private static string ToolUse(string name, object input, string ts = "2026-01-01T10:02:00.000Z") =>
        J(new { type = "assistant", sessionId = Id, timestamp = ts, message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = "t9", name, input } } } });

    [Fact]
    public void ReadTranscript_puts_the_command_text_on_tool_invocations()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home),
            UserLine("go", "C:\\Dev\\proj"),
            ToolUse("Bash", new { command = "dotnet test --no-build", description = "Run tests" }),
            ToolUse("Grep", new { pattern = "TODO", path = "src" }),
            J(new { type = "user", sessionId = Id, timestamp = "2026-01-01T10:03:00.000Z", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t9", content = "Passed!" } } } }));
        var adapter = new ClaudeAdapter(home);
        var e = adapter.ReadTranscript(Assert.Single(adapter.Discover().Sessions)).ToList();

        Assert.Equal(4, e.Count);
        Assert.Equal(("Bash", "dotnet test --no-build"), (e[1].ToolName, e[1].Text));
        Assert.Equal("Grep", e[2].ToolName);
        Assert.Contains("\"pattern\":\"TODO\"", e[2].Text);   // no command: a one-line compact JSON of the input
        Assert.DoesNotContain("\n", e[2].Text);
        Assert.Equal((Role.Tool, (string?)null, "Passed!"), (e[3].Role, e[3].ToolName, e[3].Text));   // output marker
    }

    [Fact]
    public void ReadTranscript_truncates_a_long_compact_json_summary()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home), UserLine("go", "C:\\Dev\\proj"),
            ToolUse("Agent", new { prompt = new string('p', 5000) }));
        var adapter = new ClaudeAdapter(home);
        var e = adapter.ReadTranscript(Assert.Single(adapter.Discover().Sessions)).ToList();
        Assert.True(e[1].Text.Length <= 201, $"length {e[1].Text.Length}");
        Assert.EndsWith("…", e[1].Text);
    }

    [Fact]
    public void ReadTranscript_skips_meta_and_sidechain_entries_like_the_title_does()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home),
            J(new { type = "user", isMeta = true, sessionId = Id, timestamp = "2026-01-01T10:00:00.000Z", cwd = "C:\\Dev\\proj", message = new { role = "user", content = "Caveat: the messages below were generated by the user while running local commands." } }),
            J(new { type = "user", isSidechain = true, sessionId = Id, timestamp = "2026-01-01T10:00:01.000Z", message = new { role = "user", content = "subagent prompt" } }),
            J(new { type = "assistant", isSidechain = true, sessionId = Id, timestamp = "2026-01-01T10:00:02.000Z", message = new { role = "assistant", content = new object[] { new { type = "text", text = "subagent reply" } } } }),
            UserLine("The real prompt", "C:\\Dev\\proj", "2026-01-01T10:00:03.000Z"));
        var adapter = new ClaudeAdapter(home);
        var s = Assert.Single(adapter.Discover().Sessions);
        var e = adapter.ReadTranscript(s).ToList();

        Assert.Equal("The real prompt", s.Title);
        var only = Assert.Single(e);
        Assert.Equal((Role.User, "The real prompt"), (only.Role, only.Text));
    }

    [Fact]
    public void Discover_final_line_larger_than_tail_window_uses_file_mtime()
    {
        var home = SessionFixtures.TempHome();
        var path = SessionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.WriteLine(UserLine("hello", "C:\\Dev\\proj"));
            w.WriteLine(AssistantText(new string('x', 100_000), "2026-01-01T10:05:00.000Z"));
        }
        var mtime = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, mtime);

        var s = Assert.Single(new ClaudeAdapter(home).Discover().Sessions);
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
            for (var i = 0; i < 20_000; i++) w.WriteLine(AssistantText(filler));
            w.WriteLine(AssistantText("last", "2026-02-02T12:00:00.000Z"));
        }
        Assert.True(new FileInfo(path).Length > 19_000_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new ClaudeAdapter(home).Discover();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var s = Assert.Single(result.Sessions);
        Assert.Equal("(no prompt)", s.Title);
        Assert.Null(s.ProjectPath);
        Assert.Equal(DateTimeOffset.Parse("2026-02-02T12:00:00Z"), s.LastActivity);
        Assert.True(allocated < 3_000_000, $"Discover allocated {allocated} bytes");
    }
}
