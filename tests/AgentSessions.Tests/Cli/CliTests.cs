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
            Path.Combine(_root, "archive"), new LabelStore(Path.Combine(_root, "data", "labels.json")), caller, _root);
        return (svc, console);
    }

    private static AdapterCapabilities Full => AdapterCapabilities.List | AdapterCapabilities.Resume
        | AdapterCapabilities.LaunchWithPrompt | AdapterCapabilities.Transcript | AdapterCapabilities.Archive | AdapterCapabilities.Delete;

    private (AppServices Svc, TestConsole Console) Default(string? settingsJson)
    {
        var data = Path.Combine(_root, "defdata");
        Directory.CreateDirectory(data);
        if (settingsJson is not null) File.WriteAllText(Path.Combine(data, "settings.json"), settingsJson);
        return (AppServices.CreateDefault(Path.Combine(_root, "defhome"), data), new TestConsole().Width(300));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("clean")]
    public void Corrupt_settings_file_is_reported_and_exit_code_unchanged(string command)
    {
        var (svc, console) = Default("{ not json");
        Assert.Equal(0, Program.Run([command], console, svc));
        Assert.Contains("settings.json could not be read; using defaults", console.Output);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("clean")]
    public void Missing_or_valid_settings_file_prints_no_settings_warning(string command)
    {
        var (svc, console) = Default(null);
        Assert.Equal(0, Program.Run([command], console, svc));
        Assert.DoesNotContain("settings.json", console.Output);
        var (svc2, console2) = Default("{ \"StaleDays\": 10 }");
        Assert.Equal(0, Program.Run([command], console2, svc2));
        Assert.DoesNotContain("settings.json", console2.Output);
    }

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
    public void testconsole_is_non_interactive_by_default()
    {
        Assert.False(new TestConsole().Profile.Capabilities.Interactive);
    }

    [Fact]
    public void clean_apply_non_interactive_without_yes_changes_nothing()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        console.Profile.Capabilities.Interactive = false;
        console.Input.PushTextWithEnter("y");   // must not be read: the IsInteractive check refuses first

        var code = Program.Run(["clean", "--apply"], console, svc);

        Assert.Equal(1, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.Contains("--yes", console.Output);
    }

    [Fact]
    public void clean_apply_unreadable_prompt_refuses_with_exit_1()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], interactive: true);   // no input queued

        var code = Program.Run(["clean", "--apply"], console, svc);

        Assert.Equal(1, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.Contains("--yes", console.Output);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("36501")]
    [InlineData("99999999999")]
    public void clean_rejects_bad_older_than_with_usage_error(string value)
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Assert.Equal(2, Program.Run(["clean", "--older-than", value, "--apply", "--yes"], console, svc));
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void list_rejects_absurd_older_than_with_usage_error()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(2, Program.Run(["list", "--older-than", "36501"], console, svc));
    }

    [Fact]
    public void clean_accepts_max_older_than()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(0, Program.Run(["clean", "--older-than", "36500"], console, svc));
    }

    [Theory]
    [InlineData(",")]
    [InlineData(" , ,")]
    public void clean_rejects_ids_with_only_separators(string ids)
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(2, Program.Run(["clean", "--ids", ids], console, svc));
    }

    [Fact]
    public void clean_rejects_ids_together_with_older_than()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Assert.Equal(2, Program.Run(["clean", "--ids", "claude:c1", "--older-than", "5", "--apply", "--yes"], console, svc));
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void clean_prompt_states_action_count_and_active_warnings()
    {
        var s = Session("claude", "c1", "Fresh", 0);   // active (recent)
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], interactive: true);
        console.Input.PushTextWithEnter("n");

        Program.Run(["clean", "--ids", "claude:c1", "--apply"], console, svc);

        Assert.Contains("archive 1 session(s)", console.Output);
        Assert.Contains("1 with active-session warnings", console.Output);
        Assert.Contains("Proceed? [y/N]", console.Output);
        Assert.DoesNotContain("PERMANENTLY", console.Output);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void clean_delete_prompt_says_permanently_delete()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], interactive: true);
        console.Input.PushTextWithEnter("n");

        Program.Run(["clean", "--delete", "--apply"], console, svc);

        Assert.Contains("PERMANENTLY delete 1 session(s)", console.Output);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void clean_delete_apply_yes_deletes_owned_path_and_prints_done()
    {
        var s = Session("claude", "c1", "Old", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = Program.Run(["clean", "--delete", "--apply", "--yes"], console, svc);

        Assert.Equal(0, code);
        Assert.False(File.Exists(s.StorePath));
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));
        Assert.Contains("Done", console.Output);
    }

    [Fact]
    public void list_strips_terminal_escape_sequences_from_titles()
    {
        var s = Session("claude", "c1", "a\u001b[31mb\u001b]0;pwn\u0007c\u009bd\u007fe", 0);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Program.Run(["list"], console, svc);

        Assert.DoesNotContain(console.Output, c => c == '\u001b');
        Assert.DoesNotContain(console.Output, c => c == '\u0007');
        Assert.DoesNotContain(console.Output, c => c == '\u009b');
        Assert.DoesNotContain(console.Output, c => c == '\u007f');
        Assert.Contains("c", console.Output);
    }

    [Fact]
    public void clean_plan_strips_terminal_escape_sequences()
    {
        var s = Session("claude", "c1", "x\u001b[2Jy", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Program.Run(["clean"], console, svc);

        Assert.DoesNotContain(console.Output, c => c == 0x1b);
    }

    [Fact]
    public void transfer_to_same_agent_is_refused_with_resume_hint()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = Program.Run(["transfer", "claude", "c1", "--to", "claude"], console, svc);

        Assert.Equal(1, code);
        Assert.Contains("resume", console.Output);
        Assert.Empty(_runner.Runs);
        Assert.False(File.Exists(Path.Combine(Project, ".docs", "handoff.md")));
    }

    [Fact]
    public void transfer_polish_with_polish_disabled_writes_unpolished_and_says_so()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s]), new FakeAdapter("codex", Full, [])],
            new AppSettings(PolishEnabled: false), new ThrowingCaller());

        var code = Program.Run(["transfer", "claude", "c1", "--to", "codex", "--polish"], console, svc);

        Assert.Equal(0, code);
        Assert.Contains("polish is not enabled", console.Output);
        Assert.True(File.Exists(Path.Combine(Project, ".docs", "handoff.md")));
        Assert.DoesNotContain("SECRET-STDERR", console.Output);
        Assert.Single(_runner.Runs);
    }

    [Fact]
    public void startup_failure_prints_clean_message_and_exits_1()
    {
        var console = new TestConsole().Width(300);

        var code = Program.RunMain(["list"], console, () => throw new InvalidOperationException("settings [bad] corrupt"));

        Assert.Equal(1, code);
        Assert.Contains("settings [bad] corrupt", console.Output);
        Assert.DoesNotContain("   at ", console.Output);
    }

    [Fact]
    public void cleanup_selection_unknown_ids_are_reported_and_empty_ids_select_nothing()
    {
        var info = Session("claude", "c1", "Old", 90);
        var all = new List<ListedSession> { new(info, true, false, Full) };

        var bad = CleanupSelection.Select(all, ["claude:c1", "claude:zz"], null, Now);
        Assert.Equal(["claude:zz"], bad.UnknownIds);

        var none = CleanupSelection.Select(all, [], null, Now);
        Assert.Empty(none.Selected);
        Assert.Empty(none.UnknownIds);

        Assert.Equal(["claude:c1", "x:y"], CleanupSelection.ParseIds(" claude:c1 , ,x:y"));
    }

    [Fact]
    public void cleanup_selection_older_than_zero_is_strictly_older_than()
    {
        var now = new ListedSession(Session("claude", "now", "Now", 0) with { LastActivity = Now }, false, true, Full);
        var future = new ListedSession(Session("claude", "fut", "Future", 0) with { LastActivity = Now.AddMinutes(5) }, false, true, Full);
        var minute = new ListedSession(Session("claude", "min", "Minute", 0) with { LastActivity = Now.AddMinutes(-1) }, false, true, Full);

        var r = CleanupSelection.Select([now, future, minute], null, 0, Now);

        Assert.Equal([minute], r.Selected);   // a session active at this instant (or later) is never "older than 0 days"
    }

    [Theory]
    [InlineData("list")]
    [InlineData("clean")]
    public void settings_warnings_are_printed_once_each(string command)
    {
        var (svc, console) = Default("{ \"staleDays\": 0, \"archiveRoot\": \"relative\\\\dir\", \"recentActiveMinutes\": -1 }");

        Assert.Equal(0, Program.Run([command], console, svc));

        var o = console.Output.Replace("\n", " ");
        foreach (var w in new[] { "staleDays must be between 1 and 36500; using 30.", "archiveRoot must be an absolute path; using the default.",
                     "recentActiveMinutes must be 0 or more; using 15." })
            Assert.Equal(1, o.Split(w).Length - 1);
    }

    [Fact]
    public void relative_archive_root_never_reaches_the_archive_store()
    {
        var (svc, _) = Default("{ \"archiveRoot\": \"relative\" }");
        Assert.Equal(Path.Combine(_root, "defdata", "archive"), svc.Archive.Root);
        Assert.True(Path.IsPathFullyQualified(svc.Archive.Root));
    }

    [Fact]
    public void clean_delete_apply_yes_refuses_a_stale_threshold_below_one_day_from_settings()
    {
        // Load() normalises staleDays, but settings can also be constructed directly: the clean command must not
        // turn "every session is stale" into a scripted mass delete.
        var s = Session("claude", "c1", "Recent", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], new AppSettings(StaleDays: 0));

        var code = Program.Run(["clean", "--delete", "--apply", "--yes"], console, svc);

        Assert.Equal(1, code);
        Assert.Contains("below 1 day", console.Output);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void clean_delete_with_explicit_older_than_zero_is_the_users_choice()
    {
        var s = Session("claude", "c1", "Recent", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], new AppSettings(StaleDays: 0));

        var code = Program.Run(["clean", "--delete", "--apply", "--yes", "--older-than", "0"], console, svc);

        Assert.Equal(0, code);
        Assert.False(File.Exists(s.StorePath));
    }

    [Fact]
    public void list_with_unknown_agent_is_a_usage_error_naming_the_valid_agents()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, []), new FakeAdapter("codex", Full, [])]);

        var code = Program.Run(["list", "--agent", "bogus"], console, svc);

        Assert.Equal(2, code);
        Assert.Contains("Unknown agent 'bogus'", console.Output);
        Assert.Contains("claude, codex", console.Output);
        Assert.DoesNotContain("session(s).", console.Output);
    }

    [Fact]
    public void list_agent_filter_is_case_insensitive()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [Session("claude", "c1", "One", 0)])]);
        Assert.Equal(0, Program.Run(["list", "--agent", "CLAUDE"], console, svc));
        Assert.Contains("1 session(s).", console.Output);
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
    public void List_shows_label_instead_of_title()
    {
        var a = Session("claude", "c1", "Original title", 0);
        var b = Session("claude", "c2", "Old original", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a, b])]);
        svc.Labels.Set("claude", "c1", "[red]Mine[/]");
        svc.Labels.Set("claude", "c2", "Stale one");

        var code = Program.Run(["list"], console, svc);

        Assert.Equal(0, code);
        Assert.Contains("[red]Mine[/]", console.Output);
        Assert.Contains("Stale one [stale]", console.Output);
        Assert.DoesNotContain("Original title", console.Output);
        Assert.DoesNotContain("Old original", console.Output);
    }

    [Fact]
    public void rename_sets_and_clears_label()
    {
        var s = Session("claude", "abc123", "Original title", 0);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Assert.Equal(0, Program.Run(["rename", "claude", "abc", "Fix", "the", "[b]parser[/]"], console, svc));   // id prefix
        Assert.Equal("Fix the [b]parser[/]", svc.Labels.Load()["claude:abc123"]);
        Assert.Contains("Fix the [b]parser[/]", console.Output);

        var (svc2, console2) = Make([new FakeAdapter("claude", Full, [s])]);
        Assert.Equal(0, Program.Run(["rename", "claude", "abc123", "--clear"], console2, svc2));
        Assert.Empty(svc2.Labels.Load());
        Assert.Contains("cleared", console2.Output);

        svc2.Labels.Set("claude", "abc123", "again");
        Assert.Equal(0, Program.Run(["rename", "claude", "abc123"], console2, svc2));   // no words clears
        Assert.Empty(svc2.Labels.Load());

        // Over-long labels are truncated and the output says so; labels that sanitise to blank clear.
        Assert.Equal(0, Program.Run(["rename", "claude", "abc123", new string('z', 130)], console2, svc2));
        Assert.Equal(120, svc2.Labels.Load()["claude:abc123"].Length);
        Assert.Contains("truncated", console2.Output);
        Assert.Equal(0, Program.Run(["rename", "claude", "abc123", "\u0001\u0002"], console2, svc2));
        Assert.Empty(svc2.Labels.Load());
        Assert.Contains("blank", console2.Output);
    }

    [Fact]
    public void rename_unknown_id_exits_nonzero()
    {
        var a = Session("claude", "abc1", "A", 0);
        var b = Session("claude", "abc2", "B", 0);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a, b])]);

        Assert.Equal(1, Program.Run(["rename", "claude", "nope", "x"], console, svc));
        Assert.Contains("No claude session matches", console.Output);
        Assert.Equal(1, Program.Run(["rename", "claude", "abc", "x"], console, svc));   // ambiguous
        Assert.Contains("ambiguous", console.Output);
        Assert.Equal(1, Program.Run(["rename", "bogus", "abc1", "x"], console, svc));
        Assert.Empty(svc.Labels.Load());
    }

    [Fact]
    public void Rename_writes_only_labels_json_and_never_touches_agent_stores()
    {
        var a = Session("claude", "c1", "A", 0);
        var b = Session("codex", "x1", "B", 0);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a]), new FakeAdapter("codex", Full, [b])]);
        var dataDir = Path.Combine(_root, "data");
        var before = Hashes(_root);

        Assert.Equal(0, Program.Run(["rename", "claude", "c1", "Named"], console, svc));
        Assert.Equal(0, Program.Run(["rename", "codex", "x1", "Named too"], console, svc));
        Assert.Equal(0, Program.Run(["rename", "claude", "c1", "--clear"], console, svc));

        var after = Hashes(_root);
        foreach (var (file, hash) in before) Assert.Equal(hash, after[file]);          // agent stores byte-identical
        var created = after.Keys.Except(before.Keys).ToList();
        Assert.Equal([Path.Combine(dataDir, "labels.json")], created);                 // the only new file
        Assert.Equal(["labels.json"], Directory.GetFiles(dataDir).Select(Path.GetFileName));

        static Dictionary<string, string> Hashes(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
    }

    [Fact]
    public void rename_clear_on_an_unlabelled_session_creates_no_file()
    {
        var s = Session("claude", "c1", "A", 0);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        Assert.Equal(0, Program.Run(["rename", "claude", "c1", "--clear"], console, svc));
        Assert.Equal(0, Program.Run(["rename", "claude", "c1"], console, svc));

        Assert.False(File.Exists(Path.Combine(_root, "data", "labels.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void rename_says_when_it_kept_a_copy_of_an_unreadable_labels_file()
    {
        var s = Session("claude", "c1", "A", 0);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(Path.Combine(_root, "data", "labels.json"), "{ broken");

        Assert.Equal(0, Program.Run(["rename", "claude", "c1", "New"], console, svc));

        Assert.Contains("Existing labels file was unreadable; a copy was kept as", console.Output);
        Assert.Contains("labels.corrupt-", console.Output);
        Assert.Equal("New", svc.Labels.Load()["claude:c1"]);
    }

    [Fact]
    public void no_arguments_prints_help()
    {
        var (svc, console) = Make([]);
        Program.Run([], console, svc);
        Assert.Contains("USAGE", console.Output.ToUpperInvariant());
    }
}
