using AgentSessions.Cli;
using AgentSessions.Config;
using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;
using AgentSessions.Tui;
using Spectre.Console.Testing;

namespace AgentSessions.Tests.Tui;

public class MainScreenTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private const string ClearScreen = "\u001b[2J";
    private readonly string _root = SessionFixtures.TempHome();
    private readonly FakeRunner _runner = new();
    private ScriptedKeys _keys = null!;
    private Func<IEnumerable<CleanupPlanItem>, CleanupResult>? _apply;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<LaunchSpec> Runs { get; } = [];
        public int Run(LaunchSpec spec) { Runs.Add(spec); return 0; }
    }

    private sealed class FakeClipboard : IClipboard { public void Set(string t) { } }

    /// <summary>A scripted key `Ahead` flag models keys typed before a prompt: DiscardPending drops them.</summary>
    private readonly record struct Ks(ConsoleKeyInfo Key, bool Ahead = false)
    {
        public static implicit operator Ks(ConsoleKeyInfo k) => new(k);
    }

    private sealed class ScriptedKeys(IEnumerable<Ks> keys, Func<string> frame) : IKeySource
    {
        private readonly Queue<Ks> _q = new(keys);
        /// <summary>The visible screen (output since the last clear) at the moment each key was requested.</summary>
        public List<string> Frames { get; } = [];
        public int Discards { get; private set; }

        public ConsoleKeyInfo ReadKey()
        {
            Frames.Add(frame());
            return _q.Count > 0 ? _q.Dequeue().Key : throw new InvalidOperationException("script exhausted");
        }

        public void DiscardPending()
        {
            Discards++;
            while (_q.Count > 0 && _q.Peek().Ahead) _q.Dequeue();
        }
    }

    private static string LastFrame(TestConsole c)
    {
        var o = c.Output;
        var i = o.LastIndexOf(ClearScreen, StringComparison.Ordinal);
        return i < 0 ? o : o[i..];
    }

    private static ConsoleKeyInfo C(char c) =>
        new(c, c == ' ' ? ConsoleKey.Spacebar : c == '/' ? ConsoleKey.Oem2 : (ConsoleKey)char.ToUpperInvariant(c), false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Enter => new('\r', ConsoleKey.Enter, false, false, false);
    private static ConsoleKeyInfo P => C('.');   // "press any key" at a pause
    private static Ks Ahead(ConsoleKeyInfo k) => new(k, true);

    private string Project => Path.Combine(_root, "proj");

    private SessionInfo Session(string agent, string id, string title, int ageDays, string? project = "")
    {
        var store = Path.Combine(_root, "store", agent, id + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, "data");
        project = project == "" ? Project : project;
        return new SessionInfo(agent, id, project, title, Now.AddDays(-ageDays), 2048, store);
    }

    private static AdapterCapabilities Full => AdapterCapabilities.List | AdapterCapabilities.Resume
        | AdapterCapabilities.LaunchWithPrompt | AdapterCapabilities.Transcript | AdapterCapabilities.Archive | AdapterCapabilities.Delete;

    private (AppServices Svc, TestConsole Console) Make(IEnumerable<ISessionAdapter> adapters, bool interactive = true,
        AppSettings? settings = null, int width = 200)
    {
        Directory.CreateDirectory(Project);
        var console = new TestConsole().Width(width);
        if (interactive) console.Interactive();
        var svc = new AppServices(adapters, settings ?? new AppSettings(), _runner, new FakeClipboard(), () => Now,
            Path.Combine(_root, "archive"), new LabelStore(Path.Combine(_root, "data", "labels.json")), null, _root);
        return (svc, console);
    }

    private int RunScreen(AppServices svc, TestConsole console, params Ks[] keys)
    {
        _keys = new ScriptedKeys(keys, () => LastFrame(console));
        return new MainScreen(console, svc, _keys) { ApplyOverride = _apply }.Run();
    }

    [Fact]
    public void Renders_header_columns_and_stale_tag()
    {
        var fresh = Session("claude", "c1", "Brainstorm session-manager tool", 0);
        var stale = Session("codex", "x1", "Old work", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [fresh]), new FakeAdapter("codex", Full, [stale])]);

        var code = RunScreen(svc, console, C('s'), C('q'));

        Assert.Equal(0, code);
        var o = _keys.Frames[1];   // the redraw after 's'
        foreach (var h in new[] { "AGENT", "PROJECT", "LAST ACTIVE", "SIZE", "TITLE" }) Assert.Contains(h, o);
        Assert.Contains("filter: all agents", o);
        Assert.Contains("proj", o);
        Assert.DoesNotContain(Project, o.Replace("\n", ""));   // last path segment only
        Assert.Contains("Brainstorm session-manager tool", o);
        Assert.Contains("3mo ago", o);
        Assert.Contains("Old work [stale]", o);
        Assert.DoesNotContain("Brainstorm session-manager tool [stale]", o);
        Assert.Contains("1 marked", o);
        Assert.Contains("[Enter] resume", o);
    }

    [Fact]
    public void Archive_requires_confirmation_and_declining_changes_nothing()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = RunScreen(svc, console, C('a'), Enter /* default answer */, P, C('q'));

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));
        Assert.Contains("claude:c1", _keys.Frames[1]);   // the plan was on screen when asked
        Assert.Contains("Cancelled", _keys.Frames[2]);
    }

    [Fact]
    public void Archive_with_an_unreadable_answer_is_refused_and_changes_nothing()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        var code = RunScreen(svc, console, C('a') /* then the key source fails */);

        Assert.Equal(1, code);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void Archive_confirmed_moves_the_session_to_the_archive()
    {
        var s = Session("claude", "c1", "Work", 1);
        var keep = Session("claude", "c2", "Keep", 2);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s, keep])]);

        RunScreen(svc, console, C('a'), C('y'), P, C('q'));

        Assert.False(File.Exists(s.StorePath));
        Assert.True(File.Exists(keep.StorePath));
        Assert.True(Directory.Exists(Path.Combine(_root, "archive", "claude", "c1")));
        Assert.Contains("Done", _keys.Frames[2]);   // result still visible while waiting for a key
    }

    [Fact]
    public void Archive_acts_on_marked_rows_only()
    {
        var a = Session("claude", "c1", "A", 1);
        var b = Session("claude", "c2", "B", 2);
        var c = Session("claude", "c3", "C", 3);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a, b, c])]);

        RunScreen(svc, console, Key(ConsoleKey.DownArrow), C(' '), Key(ConsoleKey.DownArrow), C(' '), C('a'), C('y'), P, C('q'));

        Assert.True(File.Exists(a.StorePath));
        Assert.False(File.Exists(b.StorePath));
        Assert.False(File.Exists(c.StorePath));
    }

    [Fact]
    public void Delete_confirmation_says_permanently_and_default_is_no()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        RunScreen(svc, console, C('d'), Enter, P, C('q'));

        Assert.Contains("PERMANENTLY delete", _keys.Frames[1]);
        Assert.Contains("[y/N]", _keys.Frames[1]);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void Delete_confirmed_deletes()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        RunScreen(svc, console, C('d'), C('y'), P, C('q'));

        Assert.False(File.Exists(s.StorePath));
    }

    [Fact]
    public void Declined_agent_is_shown_and_not_applied()
    {
        var cop = Session("copilot", "p1", "Cop", 1);
        var cl = Session("claude", "c1", "Cl", 2);
        var noArchive = Full & ~AdapterCapabilities.Archive;
        var (svc, console) = Make([new FakeAdapter("copilot", noArchive, [cop]), new FakeAdapter("claude", Full, [cl])]);

        // Current row (copilot) only: every target is declined, so there is no prompt, just a pause.
        RunScreen(svc, console, C('a'), P, C('q'));
        Assert.Contains("Declined", _keys.Frames[1]);
        Assert.Contains("Nothing to do", _keys.Frames[1]);
        Assert.True(File.Exists(cop.StorePath));

        // Mixed: mark both; the plan shows the decline, only claude is applied.
        RunScreen(svc, console, C(' '), Key(ConsoleKey.DownArrow), C(' '), C('a'), C('y'), P, C('q'));
        Assert.Contains("Declined", _keys.Frames[4]);
        Assert.True(File.Exists(cop.StorePath));
        Assert.False(File.Exists(cl.StorePath));
    }

    [Fact]
    public void Declined_reasons_stay_visible_until_the_user_presses_a_key()
    {
        var cop = Session("copilot", "p1", "Cop", 1);
        var noArchive = Full & ~AdapterCapabilities.Archive;
        var (svc, console) = Make([new FakeAdapter("copilot", noArchive, [cop])]);

        RunScreen(svc, console, C('a'), P, C('q'));

        // Frame 1 is what is on screen while we wait for "press any key"; a redraw would have wiped it.
        Assert.Contains("does not support archive", _keys.Frames[1]);
        Assert.Contains("continue", _keys.Frames[1], StringComparison.OrdinalIgnoreCase);
        // After the key the list is redrawn without the plan.
        Assert.DoesNotContain("does not support archive", _keys.Frames[2]);
    }

    [Fact]
    public void Active_session_warning_is_shown_in_the_plan()
    {
        var s = Session("claude", "c1", "Live", 0);
        var live = s with { LastActivity = Now.AddMinutes(-1) };
        var (svc, console) = Make([new FakeAdapter("claude", Full, [live])]);

        RunScreen(svc, console, C('a'), Enter, P, C('q'));

        Assert.Contains("Warning", _keys.Frames[1]);
        Assert.Contains("active", _keys.Frames[1]);
    }

    [Fact]
    public void Typed_ahead_keys_cannot_answer_the_confirmation()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        // 'a', then y + Enter typed before the user saw the plan, then an explicit "n" after it.
        RunScreen(svc, console, C('a'), Ahead(C('y')), Ahead(Enter), C('n'), P, C('q'));

        Assert.True(File.Exists(s.StorePath));
        Assert.True(_keys.Discards >= 1);
        Assert.Contains("Cancelled", _keys.Frames[2]);
    }

    [Fact]
    public void Explicit_answer_after_the_plan_still_confirms_despite_typeahead()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);

        RunScreen(svc, console, C('a'), Ahead(C('y')), C('y'), Ahead(Enter), P, C('q'));

        Assert.False(File.Exists(s.StorePath));
        Assert.True(_keys.Discards >= 2);   // before the confirmation and before the pause
    }

    [Fact]
    public void Apply_that_throws_still_clears_marks_reloads_and_shows_the_error()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        _apply = _ => throw new InvalidOperationException("[disk] gone");

        var code = RunScreen(svc, console, C(' '), C('a'), C('y'), P, C('q'));

        Assert.Equal(0, code);
        Assert.Contains("[disk] gone", _keys.Frames[3]);
        Assert.Contains("0 marked", _keys.Frames[4]);
    }

    [Fact]
    public void Very_large_plans_are_summarised_before_the_confirmation()
    {
        var sessions = Enumerable.Range(0, 50).Select(i => Session("claude", $"s{i:00}", $"t{i:00}", 40 + i)).ToArray();
        var (svc, console) = Make([new FakeAdapter("claude", Full, sessions)]);

        RunScreen(svc, console, C('s'), C('a'), C('n'), P, C('q'));

        var confirm = _keys.Frames[2];
        Assert.Contains("50 session(s)", confirm);
        Assert.Contains("and 30 more", confirm);
        Assert.Contains("claude:s19", confirm);
        Assert.DoesNotContain("claude:s20", confirm);
        Assert.Contains("0 with warnings", confirm);
        Assert.Contains("0 declined", confirm);
        Assert.Contains("[y/N]", confirm);
        Assert.True(File.Exists(sessions[49].StorePath));
    }

    [Fact]
    public void Transfer_picker_excludes_source_agent()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s]), new FakeAdapter("codex", Full, []), new FakeAdapter("copilot", Full, [])]);
        console.Input.PushKey(ConsoleKey.Enter);   // first offered choice

        RunScreen(svc, console, C('t'), P, C('q'));

        var o = console.Output;
        var picker = o[o.IndexOf("Transfer to which agent", StringComparison.Ordinal)..];
        picker = picker[..picker.IndexOf("Handoff:", StringComparison.Ordinal)];
        Assert.Contains("codex", picker);
        Assert.Contains("copilot", picker);
        Assert.DoesNotContain("claude", picker);
        var run = Assert.Single(_runner.Runs);
        Assert.Equal("codex", run.FileName);
        Assert.Contains("Handoff:", _keys.Frames[1]);   // the result is on screen while waiting for a key
        Assert.True(Directory.GetFiles(Project, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public void Enter_resumes_current_session_via_launch_service()
    {
        var a = Session("claude", "c1", "A", 1);
        var b = Session("codex", "x1", "B", 2);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a]), new FakeAdapter("codex", Full, [b])]);

        RunScreen(svc, console, Key(ConsoleKey.DownArrow), Enter, C('q'));

        var run = Assert.Single(_runner.Runs);
        Assert.Equal("codex", run.FileName);
        Assert.Equal(["resume", "x1"], run.Args);
    }

    [Fact]
    public void Resume_refusal_is_shown_until_a_key_is_pressed_and_loop_continues()
    {
        var s = Session("claude", "c1", "A", 1, project: Path.Combine(_root, "missing"));
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        console.Input.PushTextWithEnter("");   // folder prompt: empty

        var code = RunScreen(svc, console, Enter, P, C('q'));

        Assert.Equal(0, code);
        Assert.Empty(_runner.Runs);
        Assert.Contains("not resuming", _keys.Frames[1]);
    }

    [Fact]
    public void Filter_prompt_applies_valid_input_and_keeps_previous_on_invalid()
    {
        var a = Session("claude", "c1", "Alpha", 1);
        var b = Session("codex", "x1", "Bravo", 2);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a]), new FakeAdapter("codex", Full, [b])]);
        console.Input.PushTextWithEnter("agent:codex");
        console.Input.PushTextWithEnter("older:abc");

        RunScreen(svc, console, C('/'), C('/'), P, C('q'));

        Assert.Contains("filter: agent codex", _keys.Frames[1]);
        Assert.Contains("Invalid filter", _keys.Frames[2]);
        Assert.Contains("older:", _keys.Frames[2]);
        Assert.Contains("filter: agent codex", _keys.Frames[3]);   // previous filter kept
    }

    [Fact]
    public void Repeated_filter_tokens_report_that_only_the_last_value_is_used()
    {
        var a = Session("claude", "c1", "Alpha", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a])]);
        console.Input.PushTextWithEnter("foo bar");

        RunScreen(svc, console, C('/'), P, C('q'));

        Assert.Contains("only the last", _keys.Frames[1]);
        Assert.Contains("project bar", _keys.Frames[2]);
    }

    [Fact]
    public void Long_titles_are_truncated_to_the_console_width_without_breaking_markup()
    {
        var longTitle = Session("claude", "c1", new string('x', 200), 1);
        var brackets = Session("claude", "c2", new string('[', 100), 2);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [longTitle, brackets])], width: 80);

        RunScreen(svc, console, C('q'));

        var rows = _keys.Frames[0].Split('\n').Where(l => l.Contains("claude")).ToList();
        Assert.Equal(2, rows.Count);                    // no row wrapped onto a second line
        Assert.All(rows, r => Assert.Contains("…", r));
        Assert.Contains("[[[", rows[1]);                // literal brackets survived the cut
        var plain = _keys.Frames[0].Replace(ClearScreen, "").Replace("\u001b[3J", "").Replace("\u001b[1;1H", "");
        Assert.All(plain.Split('\n'), l => Assert.True(l.TrimEnd('\r').Length <= 80, l));
    }

    [Fact]
    public void Viewport_keeps_the_cursor_visible_and_pages_and_jumps()
    {
        var sessions = Enumerable.Range(0, 20).Select(i => Session("claude", $"v{i:00}", $"Title{i:00}", 1 + i)).ToArray();
        var (svc, console) = Make([new FakeAdapter("claude", Full, sessions)]);
        console.Profile.Height = 14;   // 5 visible rows

        var keys = Enumerable.Repeat<Ks>(Key(ConsoleKey.DownArrow), 7).ToList();
        keys.AddRange([Key(ConsoleKey.End), Key(ConsoleKey.Home), Key(ConsoleKey.PageDown), Key(ConsoleKey.PageUp), C('q')]);
        RunScreen(svc, console, keys.ToArray());
        var f = _keys.Frames;

        Assert.Contains("(1/20)", f[0]);
        Assert.Contains("Title00", f[0]);
        Assert.DoesNotContain("Title10", f[0]);

        Assert.Contains("(8/20)", f[7]);
        Assert.Contains(">", f[7].Split('\n').Single(l => l.Contains("Title07")));
        Assert.Equal(5, f[7].Split('\n').Count(l => l.Contains("Title")));

        Assert.Contains("(20/20)", f[8]);
        Assert.Contains("Title19", f[8]);
        Assert.DoesNotContain("Title00", f[8]);

        Assert.Contains("(1/20)", f[9]);
        Assert.Contains("Title00", f[9]);

        Assert.Contains("(5/20)", f[10]);
        Assert.Contains("(1/20)", f[11]);
    }

    [Fact]
    public void Untrusted_titles_are_escaped_and_control_chars_stripped()
    {
        var evil = Session("claude", "c1", "[red]x[/] \u001b[31mESC\u0007", 1, project: "C:/p/[bold]proj\u001b[2J");
        var (svc, console) = Make([new FakeAdapter("claude", Full, [evil])]);

        var code = RunScreen(svc, console, C('q'));

        Assert.Equal(0, code);
        Assert.Contains("[red]x[/]", console.Output);
        Assert.Contains("[bold]proj", console.Output);
        // Only the screen's own clear-screen sequence may contain ESC; nothing from the data may.
        var rest = console.Output.Replace("\u001b[2J", "").Replace("\u001b[3J", "").Replace("\u001b[1;1H", "");
        // char overloads: string Contains with control chars is culture-ignorable and would always match.
        Assert.False(rest.Contains('\u001b'));
        Assert.False(console.Output.Contains('\u0007'));
        Assert.Contains("ESC", rest);
    }

    [Fact]
    public void Non_interactive_console_refuses_and_returns_1()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])], interactive: false);

        var code = RunScreen(svc, console, C('d'), C('q'));

        Assert.Equal(1, code);
        Assert.Contains("list", console.Output);
        Assert.Contains("clean", console.Output);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void Quit_returns_0()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(0, RunScreen(svc, console, C('q')));
        var (svc2, console2) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(0, RunScreen(svc2, console2, Key(ConsoleKey.Escape)));
    }

    [Fact]
    public void Empty_list_and_odd_keys_do_not_crash()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);

        var code = RunScreen(svc, console, Enter, P, C(' '), C('a'), P, C('d'), P, C('t'), P, C('x'), Key(ConsoleKey.PageDown),
            Key(ConsoleKey.End), Key(ConsoleKey.Home), Key(ConsoleKey.UpArrow), C('r'), C('q'));

        Assert.Equal(0, code);
    }

    [Fact]
    public void Unreadable_key_source_ends_the_screen_instead_of_looping()
    {
        var (svc, console) = Make([new FakeAdapter("claude", Full, [])]);
        Assert.Equal(1, RunScreen(svc, console /* no keys */));
    }

    [Fact]
    public void Adapter_failure_in_an_action_is_shown_escaped_and_the_loop_stays()
    {
        var s = Session("claude", "c1", "Work", 1);
        var bad = new ThrowingOwnedPaths("claude", Full, [s]);
        var (svc, console) = Make([bad]);

        var code = RunScreen(svc, console, C('a'), P, C('q'));

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.Contains("[boom]", _keys.Frames[1]);
    }

    [Fact]
    public void Tui_n_key_sets_label_and_list_shows_it()
    {
        var a = Session("claude", "c1", "Original title", 1);
        var b = Session("claude", "c2", "Other", 2);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a, b])]);
        console.Input.PushTextWithEnter("[red]Release prep[/]");

        var code = RunScreen(svc, console, C('n'), P, C('q'));

        Assert.Equal(0, code);
        Assert.Contains("Label (empty to clear):", _keys.Frames[1]);
        Assert.Contains("[red]Release prep[/]", _keys.Frames[1]);                // result line, literal
        var list = _keys.Frames[2];                                              // redraw after the pause
        Assert.Contains("[red]Release prep[/]", list);
        Assert.DoesNotContain("Original title", list);
        Assert.Contains("Other", list);
        Assert.Contains("[Enter] resume  [t] transfer to…  [n] label  [a] archive  [d] delete", list);
        Assert.Equal("[red]Release prep[/]", svc.Labels.Load()["claude:c1"]);
        Assert.True(File.Exists(a.StorePath));
    }

    [Fact]
    public void Tui_n_prompt_is_prefilled_and_empty_input_clears()
    {
        var a = Session("claude", "c1", "Original title", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a])]);
        svc.Labels.Set("claude", "c1", "Old name");

        // Enter keeps the prefilled label; backspaces edit it; erasing everything clears it.
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushText("X");
        console.Input.PushKey(ConsoleKey.Enter);
        RunScreen(svc, console, C('n'), P, C('n'), P, C('q'));

        Assert.Contains("Old name", _keys.Frames[1]);
        Assert.Equal("Old nameX", svc.Labels.Load()["claude:c1"]);

        for (var i = 0; i < 9; i++) console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(ConsoleKey.Enter);
        RunScreen(svc, console, C('n'), P, C('q'));

        Assert.Empty(svc.Labels.Load());
        Assert.Contains("Original title", _keys.Frames[2]);
    }

    [Fact]
    public void Labels_survive_reload()
    {
        var a = Session("claude", "c1", "Original title", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a])]);
        console.Input.PushTextWithEnter("Keep me");

        RunScreen(svc, console, C('n'), P, C('r'), C('q'));

        Assert.Contains("Keep me", _keys.Frames[3]);   // after the explicit reload
        // A brand-new screen over the same store (a restart) shows it too.
        var (svc2, console2) = Make([new FakeAdapter("claude", Full, [a])]);
        RunScreen(svc2, console2, C('q'));
        Assert.Contains("Keep me", _keys.Frames[0]);
        Assert.DoesNotContain("Original title", _keys.Frames[0]);
    }

    private sealed class ThrowingOwnedPaths(string name, AdapterCapabilities caps, IEnumerable<SessionInfo> sessions) : ISessionAdapter
    {
        public string Name => name;
        public AdapterCapabilities Capabilities => caps;
        public DiscoveryResult Discover() => new(sessions.ToList(), []);
        public IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s) => [];
        public LaunchSpec BuildResume(SessionInfo s) => new(name, [], ".");
        public LaunchSpec BuildNew(string p, string prompt) => new(name, [], p);
        public IReadOnlyList<string> OwnedPaths(SessionInfo s) => throw new InvalidOperationException("[boom]");
    }
}
