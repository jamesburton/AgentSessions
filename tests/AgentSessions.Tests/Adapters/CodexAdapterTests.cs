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
    public void Discover_skips_sessions_with_unsafe_ids()
    {
        var home = SessionFixtures.TempHome();
        var dir = Path.GetDirectoryName(SessionPath(home))!;
        SessionFixtures.WriteLines(SessionPath(home), Meta("C:\\Dev\\proj"), UserMsg("good"));
        SessionFixtures.WriteLines(Path.Combine(dir, "rollout-a b&c.jsonl"), Meta("C:\\Dev\\proj"), UserMsg("bad"));

        var result = new CodexAdapter(home).Discover();

        Assert.Equal(Id, Assert.Single(result.Sessions).Id);
        Assert.Contains("unsafe session id", Assert.Single(result.Skipped).Reason);
    }

    private static string Ri(object payload, string ts = "2026-01-01T10:05:00.000Z") =>
        J(new { timestamp = ts, type = "response_item", payload });

    private static string RiMsg(string role, params string[] texts) =>
        Ri(new { type = "message", role, content = texts.Select(t => new { type = role == "assistant" ? "output_text" : "input_text", text = t }).ToArray() });

    private List<TranscriptEntry> Transcript(params string[] lines)
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home), [Meta("C:\\Dev\\proj"), .. lines]);
        var adapter = new CodexAdapter(home);
        return adapter.ReadTranscript(Assert.Single(adapter.Discover().Sessions)).ToList();
    }

    [Fact]
    public void ReadTranscript_puts_shell_commands_on_function_calls()
    {
        var e = Transcript(
            Ri(new { type = "function_call", name = "shell_command", call_id = "a", arguments = "{\"command\":\"dotnet test\",\"workdir\":\"C:\\\\p\"}" }),
            Ri(new { type = "function_call", name = "shell", call_id = "b", arguments = "{\"command\":[\"git\",\"status\",\"--short\"]}" }),
            Ri(new { type = "function_call", name = "wait_agent", call_id = "c", arguments = "{\"timeout_ms\":5}" }),
            Ri(new { type = "function_call", name = "broken", call_id = "d", arguments = "{not json" }));

        Assert.Equal(("shell_command", "dotnet test"), (e[0].ToolName, e[0].Text));
        Assert.Equal(("shell", "git status --short"), (e[1].ToolName, e[1].Text));
        Assert.Equal(("wait_agent", "{\"timeout_ms\":5}"), (e[2].ToolName, e[2].Text));
        Assert.Equal(("broken", "{not json"), (e[3].ToolName, e[3].Text));
    }

    [Fact]
    public void ReadTranscript_extracts_commands_and_patch_files_from_exec_code_cells()
    {
        // Modern Codex wraps tool use in a JS "exec" cell; the command and any patch are JS string literals.
        const string cell = "const r = await tools.exec_command({cmd: \"dotnet build -c Release\", yield_time_ms: 1000});\ntext(r.output);";
        const string patchCell = "await tools.apply_patch(\"*** Begin Patch\\n*** Update File: C:\\\\Dev\\\\proj\\\\b.cs\\n@@\\n-a\\n+b\\n*** Add File: C:\\\\Dev\\\\proj\\\\c.cs\\n+x\\n*** End Patch\");";
        const string other = "\n// list agents\nconst x = await tools.list_agents({});";
        var e = Transcript(
            Ri(new { type = "custom_tool_call", name = "exec", call_id = "a", input = cell }),
            Ri(new { type = "custom_tool_call_output", call_id = "a", output = new object[] { new { type = "input_text", text = "Build succeeded." } } }),
            Ri(new { type = "custom_tool_call", name = "exec", call_id = "b", input = patchCell }),
            Ri(new { type = "custom_tool_call", name = "exec", call_id = "c", input = other }));

        Assert.Equal(("exec_command", "dotnet build -c Release"), (e[0].ToolName, e[0].Text));
        Assert.Equal((Role.Tool, (string?)null, "Build succeeded."), (e[1].Role, e[1].ToolName, e[1].Text));
        Assert.Equal("apply_patch", e[2].ToolName);
        Assert.Equal(new[] { "C:\\Dev\\proj\\b.cs", "C:\\Dev\\proj\\c.cs" }, e[2].FilesTouched);
        Assert.Contains("b.cs", e[2].Text);
        Assert.Equal(("exec", "// list agents"), (e[3].ToolName, e[3].Text));
    }

    [Fact]
    public void Patch_file_paths_keep_trailing_punctuation_in_both_patch_forms()
    {
        const string real = "*** Begin Patch\n*** Update File: C:\\Dev\\proj\\Foo (1).cs\n@@\n-a\n+b\n*** End Patch";
        const string cell = "await tools.apply_patch(\"*** Begin Patch\\n*** Add File: C:\\\\Dev\\\\proj\\\\data,v2;\\n+x\\n*** End Patch\");";
        var e = Transcript(
            Ri(new { type = "custom_tool_call", name = "apply_patch", call_id = "a", input = real }),
            Ri(new { type = "custom_tool_call", name = "exec", call_id = "b", input = cell }));

        Assert.Equal(new[] { "C:\\Dev\\proj\\Foo (1).cs" }, e[0].FilesTouched);
        Assert.Equal(new[] { "C:\\Dev\\proj\\data,v2;" }, e[1].FilesTouched);
    }

    [Fact]
    public void ReadTranscript_summarises_apply_patch_custom_calls()
    {
        var e = Transcript(Patch("C:\\Dev\\proj\\a.cs"));
        Assert.Equal("apply_patch", e[0].ToolName);
        Assert.Contains("a.cs", e[0].Text);
    }

    [Fact]
    public void ReadTranscript_reads_modern_sessions_that_only_have_response_item_messages()
    {
        var e = Transcript(
            RiMsg("developer", "system rules"),
            RiMsg("user", "# AGENTS.md instructions for C:\\Dev\\proj\n\nrules"),
            RiMsg("user", "<environment_context>\n<cwd>C:\\Dev\\proj</cwd>\n</environment_context>"),
            RiMsg("user", "Refactor the uploader"),
            RiMsg("assistant", "Refactored."));

        Assert.Equal(new[] { (Role.User, "Refactor the uploader"), (Role.Assistant, "Refactored.") }, e.Select(x => (x.Role, x.Text)));
    }

    [Fact]
    public void ReadTranscript_does_not_duplicate_messages_present_both_as_event_and_response_item()
    {
        var e = Transcript(
            RiMsg("user", "go"), UserMsg("go"),
            AgentMsg("sure"), RiMsg("assistant", "sure"),
            UserMsg("continue"), RiMsg("user", "continue"),
            UserMsg("continue"), RiMsg("user", "continue"));

        Assert.Equal(new[] { "go", "sure", "continue", "continue" }, e.Select(x => x.Text));
    }

    [Fact]
    public void Discover_title_falls_back_to_the_first_real_response_item_prompt()
    {
        var home = SessionFixtures.TempHome();
        SessionFixtures.WriteLines(SessionPath(home), Meta("C:\\Dev\\proj"),
            RiMsg("user", "# AGENTS.md instructions for x"), RiMsg("user", "<environment_context>x</environment_context>"),
            RiMsg("user", "Modern prompt here"));
        Assert.Equal("Modern prompt here", Assert.Single(new CodexAdapter(home).Discover().Sessions).Title);
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
