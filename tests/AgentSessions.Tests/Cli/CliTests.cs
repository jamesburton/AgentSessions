using AgentSessions.Cli;
using AgentSessions.Config;
using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;
using Spectre.Console.Testing;

namespace AgentSessions.Tests.Cli;

public class CliTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = SessionFixtures.TempHome();
    private readonly FakeRunner _runner = new();
    private readonly FakeClipboard _clipboard = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<LaunchSpec> Runs { get; } = [];
        public int Run(LaunchSpec spec) { Runs.Add(spec); return 0; }
    }

    private sealed class FakeClipboard : IClipboard { public string? Text; public void Set(string t) => Text = t; }

    private sealed class ThrowingCaller : IModelCaller
    {
        public string Polish(string md) => throw new InvalidOperationException("model SECRET-STDERR down");
    }

    private string Project => Path.Combine(_root, "proj");

    private SessionInfo Session(string agent, string id, string title, int ageDays, string? project = "", long size = 2048)
    {
        var store = Path.Combine(_root, "store", agent, id + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, "data");
        project = project == "" ? Project : project;
        return new SessionInfo(agent, id, project, title, Now.AddDays(-ageDays), size, store);
    }

    private (AppServices Svc, TestConsole Console) Make(IEnumerable<ISessionAdapter> adapters, AppSettings? settings = null,
        IModelCaller? caller = null, bool interactive = false)
    {
        Directory.CreateDirectory(Project);
        var console = new TestConsole().Width(300);
        if (interactive) console.Interactive();
        var svc = new AppServices(adapters, settings ?? new AppSettings(), _runner, _clipboard, () => Now,
            Path.Combine(_root, "archive"), caller, _root);
        return (svc, console);
    }

    private static AdapterCapabilities Full => AdapterCapabilities.List | AdapterCapabilities.Resume
        | AdapterCapabilities.LaunchWithPrompt | AdapterCapabilities.Transcript | AdapterCapabilities.Archive | AdapterCapabilities.Delete;

    [Fact]
    public void list_prints_agent_project_age_title_and_stale_tag()
    {
        var fresh = Session("claude", "c1", "Fix the parser", 0);
        var stale = Session("codex", "x1", "Old work", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [fresh]), new FakeAdapter("codex", Full, [stale], [new SkippedSession("p", "bad")])]);

        var code = Program.Run(["list"], console, svc);

        Assert.Equal(0, code);
        var o = console.Output;
        Assert.Contains("claude", o);
        Assert.Contains(Project, o.Replace("\n", ""));
        Assert.Contains("Fix the parser", o);
        Assert.Contains("3mo ago", o);
        Assert.Contains("2.0 KB", o);
        Assert.Contains("Old work [stale]", o);
        Assert.DoesNotContain("Fix the parser [stale]", o);
        Assert.Contains("1 session(s) skipped", o);
    }

    [Fact]
    public void list_prints_markup_looking_titles_literally()
    {
        var a = Session("claude", "c1", "[red]x[/]", 0);
        var b = Session("claude", "c2", "[/]", 0);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a, b])]);

        var code = Program.Run(["list"], console, svc);

        Assert.Equal(0, code);
        Assert.Contains("[red]x[/]", console.Output);
        Assert.Contains("[/]", console.Output);
    }

    [Fact]
    public void clean_without_apply_changes_nothing_and_prints_plan()
    {
        var s = Session("claude", "c1", "Old [/] work", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = Program.Run(["clean"], console, svc);

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));
        Assert.Contains("claude:c1", console.Output);
        Assert.Contains("Dry run", console.Output);
    }

    [Fact]
    public void clean_apply_archives_stale_sessions()
    {
        var stale = Session("claude", "c1", "Old", 90);
        var fresh = Session("claude", "c2", "New", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [stale, fresh])]);

        var code = Program.Run(["clean", "--apply", "--yes"], console, svc);

        Assert.Equal(0, code);
        Assert.False(File.Exists(stale.StorePath));
        Assert.True(File.Exists(fresh.StorePath));
        Assert.True(Directory.Exists(Path.Combine(_root, "archive", "claude", "c1")));
        Assert.Contains("Done", console.Output);
    }

    [Fact]
    public void clean_uses_older_than_flag_over_setting()
    {
        var s = Session("claude", "c1", "Ten days", 10);   // not stale under StaleDays=30
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Program.Run(["clean"], console, svc);
        Assert.Contains("Plan: archive 0 session(s)", console.Output);

        var (svc2, console2) = Make([new FakeAdapter("claude", Full, [s])]);
        Program.Run(["clean", "--older-than", "5"], console2, svc2);
        Assert.Contains("claude:c1", console2.Output);
    }

    [Fact]
    public void clean_reports_declined_for_agent_without_capability()
    {
        var s = Session("copilot", "p1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("copilot", AdapterCapabilities.List | AdapterCapabilities.Resume, [s])]);

        var code = Program.Run(["clean", "--apply", "--yes"], console, svc);

        Assert.Contains("Declined", console.Output);
        Assert.Contains("does not support archive", console.Output);
        Assert.True(File.Exists(s.StorePath));
        Assert.Equal(1, code);
    }

    [Fact]
    public void clean_delete_without_apply_does_not_delete()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = Program.Run(["clean", "--delete"], console, svc);

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.Contains("Dry run", console.Output);
    }

    [Fact]
    public void clean_apply_declined_at_prompt_changes_nothing()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], interactive: true);
        console.Input.PushTextWithEnter("n");

        var code = Program.Run(["clean", "--apply"], console, svc);

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.Contains("Cancelled", console.Output);
    }

    [Fact]
    public void clean_apply_with_empty_answer_defaults_to_no()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], interactive: true);
        console.Input.PushTextWithEnter("");

        Program.Run(["clean", "--apply"], console, svc);

        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void clean_apply_confirmed_at_prompt_archives()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], interactive: true);
        console.Input.PushTextWithEnter("y");

        Program.Run(["clean", "--apply"], console, svc);

        Assert.False(File.Exists(s.StorePath));
    }

    [Fact]
    public void clean_apply_non_interactive_without_yes_changes_nothing()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Program.Run(["clean", "--apply"], console, svc);

        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void clean_archive_and_delete_together_is_a_usage_error()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(2, Program.Run(["clean", "--archive", "--delete"], console, svc));
    }

    [Fact]
    public void clean_with_unknown_ids_changes_nothing_and_fails()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = Program.Run(["clean", "--apply", "--yes", "--ids", "claude:c1,claude:nope"], console, svc);

        Assert.Equal(1, code);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void transfer_writes_handoff_backs_up_existing_and_launches_target()
    {
        var s = Session("claude", "c1", "Work", 1);
        Directory.CreateDirectory(Path.Combine(Project, ".docs"));
        File.WriteAllText(Path.Combine(Project, ".docs", "handoff.md"), "OLD HANDOFF");
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s]), new FakeAdapter("codex", Full, [])]);

        var code = Program.Run(["transfer", "claude", "c1", "--to", "codex"], console, svc);

        Assert.Equal(0, code);
        var handoff = Path.Combine(Project, ".docs", "handoff.md");
        Assert.True(File.Exists(handoff));
        Assert.DoesNotContain("OLD HANDOFF", File.ReadAllText(handoff));
        var backup = Directory.GetFiles(Path.Combine(Project, ".docs"), "handoff.*.md").Single();
        Assert.Equal("OLD HANDOFF", File.ReadAllText(backup));
        Assert.Single(_runner.Runs);
        Assert.Equal("codex", _runner.Runs[0].FileName);
        Assert.Contains("Handoff:", console.Output);
        Assert.Contains("backed up", console.Output);
    }

    [Fact]
    public void transfer_with_polish_falls_back_when_model_fails()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s]), new FakeAdapter("codex", Full, [])],
            new AppSettings(PolishEnabled: true), new ThrowingCaller());

        var code = Program.Run(["transfer", "claude", "c1", "--to", "codex", "--polish"], console, svc);

        Assert.Equal(0, code);
        Assert.Contains("Polish failed", console.Output);
        Assert.Contains("unpolished", console.Output);
        var text = File.ReadAllText(Path.Combine(Project, ".docs", "handoff.md"));
        Assert.DoesNotContain("SECRET-STDERR", text);
        Assert.DoesNotContain("Polish failed", text);
        Assert.Single(_runner.Runs);
    }

    [Fact]
    public void transfer_without_to_is_a_usage_error()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        Assert.Equal(2, Program.Run(["transfer", "claude", "c1"], console, svc));
    }

    [Fact]
    public void resume_with_unknown_id_exits_nonzero()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = Program.Run(["resume", "claude", "nope"], console, svc);

        Assert.NotEqual(0, code);
        Assert.Contains("No claude session matches", console.Output);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void resume_with_ambiguous_prefix_exits_nonzero()
    {
        var a = Session("claude", "abc1", "A", 1);
        var b = Session("claude", "abc2", "B", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a, b])]);

        Assert.NotEqual(0, Program.Run(["resume", "claude", "abc"], console, svc));
        Assert.Contains("ambiguous", console.Output);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void resume_with_unique_prefix_launches_agent()
    {
        var a = Session("claude", "abc1", "A", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a])]);

        Assert.Equal(0, Program.Run(["resume", "claude", "ab"], console, svc));
        Assert.Equal("resume", _runner.Runs.Single().Args[0]);
    }

    [Fact]
    public void resume_without_project_refuses_when_not_interactive()
    {
        var a = Session("claude", "c1", "A", 1, project: null);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a])]);

        Assert.Equal(1, Program.Run(["resume", "claude", "c1"], console, svc));
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void restore_with_bad_directory_prints_error_and_exits_nonzero()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(1, Program.Run(["restore", Path.Combine(_root, "nothing")], console, svc));
        Assert.Contains("manifest.json", console.Output);
    }

    [Fact]
    public void clean_apply_then_restore_round_trips()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        Program.Run(["clean", "--apply", "--yes"], console, svc);
        Assert.False(File.Exists(s.StorePath));

        var (svc2, console2) = Make([new FakeAdapter("claude", Full, [])]);
        var code = Program.Run(["restore", Path.Combine(_root, "archive", "claude", "c1")], console2, svc2);

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void no_arguments_prints_help()
    {
        var (svc, console) = Make([]);
        Program.Run([], console, svc);
        Assert.Contains("USAGE", console.Output.ToUpperInvariant());
    }
}
