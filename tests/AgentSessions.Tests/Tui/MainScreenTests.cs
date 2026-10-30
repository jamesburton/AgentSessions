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
    private readonly string _root = SessionFixtures.TempHome();
    private readonly FakeRunner _runner = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<LaunchSpec> Runs { get; } = [];
        public int Run(LaunchSpec spec) { Runs.Add(spec); return 0; }
    }

    private sealed class FakeClipboard : IClipboard { public void Set(string t) { } }

    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public ConsoleKeyInfo ReadKey() =>
            _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("script exhausted");
    }

    private static ConsoleKeyInfo C(char c) =>
        new(c, c == ' ' ? ConsoleKey.Spacebar : c == '/' ? ConsoleKey.Oem2 : (ConsoleKey)char.ToUpperInvariant(c), false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Enter => new('\r', ConsoleKey.Enter, false, false, false);

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
        AppSettings? settings = null)
    {
        Directory.CreateDirectory(Project);
        var console = new TestConsole().Width(200);
        if (interactive) console.Interactive();
        var svc = new AppServices(adapters, settings ?? new AppSettings(), _runner, new FakeClipboard(), () => Now,
            Path.Combine(_root, "archive"), null, _root);
        return (svc, console);
    }

    private static int RunScreen(AppServices svc, TestConsole console, params ConsoleKeyInfo[] keys) =>
        new MainScreen(console, svc, new ScriptedKeys(keys)).Run();

    [Fact]
    public void Renders_header_columns_and_stale_tag()
    {
        var fresh = Session("claude", "c1", "Brainstorm session-manager tool", 0);
        var stale = Session("codex", "x1", "Old work", 90);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [fresh]), new FakeAdapter("codex", Full, [stale])]);

        var code = RunScreen(svc, console, C('s'), C('q'));

        Assert.Equal(0, code);
        var o = console.Output;
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
        console.Input.PushTextWithEnter("");   // default answer

        var code = RunScreen(svc, console, C('a'), C('q'));

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));
        Assert.Contains("claude:c1", console.Output);
        Assert.Contains("Cancelled", console.Output);
    }

    [Fact]
    public void Archive_without_an_answer_source_is_refused_and_changes_nothing()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        // interactive console but no input queued: the prompt throws, which must count as No.

        var code = RunScreen(svc, console, C('a'), C('q'));

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void Archive_confirmed_moves_the_session_to_the_archive()
    {
        var s = Session("claude", "c1", "Work", 1);
        var keep = Session("claude", "c2", "Keep", 2);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s, keep])]);
        console.Input.PushTextWithEnter("y");

        RunScreen(svc, console, C('a'), C('q'));

        Assert.False(File.Exists(s.StorePath));
        Assert.True(File.Exists(keep.StorePath));
        Assert.True(Directory.Exists(Path.Combine(_root, "archive", "claude", "c1")));
        Assert.Contains("Done", console.Output);
    }

    [Fact]
    public void Archive_acts_on_marked_rows_only()
    {
        var a = Session("claude", "c1", "A", 1);
        var b = Session("claude", "c2", "B", 2);
        var c = Session("claude", "c3", "C", 3);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a, b, c])]);
        console.Input.PushTextWithEnter("y");

        RunScreen(svc, console, Key(ConsoleKey.DownArrow), C(' '), Key(ConsoleKey.DownArrow), C(' '), C('a'), C('q'));

        Assert.True(File.Exists(a.StorePath));
        Assert.False(File.Exists(b.StorePath));
        Assert.False(File.Exists(c.StorePath));
    }

    [Fact]
    public void Delete_confirmation_says_permanently_and_default_is_no()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        console.Input.PushTextWithEnter("");

        RunScreen(svc, console, C('d'), C('q'));

        Assert.Contains("PERMANENTLY delete", console.Output);
        Assert.Contains("[y/N]", console.Output);
        Assert.True(File.Exists(s.StorePath));
    }

    [Fact]
    public void Delete_confirmed_deletes()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        console.Input.PushTextWithEnter("y");

        RunScreen(svc, console, C('d'), C('q'));

        Assert.False(File.Exists(s.StorePath));
    }

    [Fact]
    public void Declined_agent_is_shown_and_not_applied()
    {
        var cop = Session("copilot", "p1", "Cop", 1);
        var cl = Session("claude", "c1", "Cl", 2);
        var noArchive = Full & ~AdapterCapabilities.Archive;
        var (svc, console) = Make([new FakeAdapter("copilot", noArchive, [cop]), new FakeAdapter("claude", Full, [cl])]);
        console.Input.PushTextWithEnter("y");

        RunScreen(svc, console, C('s'), C('a'), C('q'));    // nothing stale: current row (copilot) is the target
        Assert.Contains("Declined", console.Output);
        Assert.True(File.Exists(cop.StorePath));
        Assert.Contains("Nothing to do", console.Output);

        // mixed: mark both, only claude is applied
        console.Input.PushTextWithEnter("y");
        RunScreen(svc, console, C(' '), Key(ConsoleKey.DownArrow), C(' '), C('a'), C('q'));
        Assert.True(File.Exists(cop.StorePath));
        Assert.False(File.Exists(cl.StorePath));
    }

    [Fact]
    public void Active_session_warning_is_shown_in_the_plan()
    {
        var s = Session("claude", "c1", "Live", 0);
        var live = s with { LastActivity = Now.AddMinutes(-1) };
        var (svc, console) = Make([new FakeAdapter("claude", Full, [live])]);
        console.Input.PushTextWithEnter("");

        RunScreen(svc, console, C('a'), C('q'));

        Assert.Contains("Warning", console.Output);
        Assert.Contains("active", console.Output);
    }

    [Fact]
    public void Transfer_picker_excludes_source_agent()
    {
        var s = Session("claude", "c1", "Work", 1);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s]), new FakeAdapter("codex", Full, []), new FakeAdapter("copilot", Full, [])]);
        console.Input.PushKey(ConsoleKey.Enter);   // first offered choice

        RunScreen(svc, console, C('t'), C('q'));

        var o = console.Output;
        var picker = o[o.IndexOf("Transfer to which agent", StringComparison.Ordinal)..];
        picker = picker[..picker.IndexOf("AGENT", StringComparison.Ordinal)];   // up to the next list redraw
        Assert.Contains("codex", picker);
        Assert.Contains("copilot", picker);
        Assert.DoesNotContain("claude", picker);
        var run = Assert.Single(_runner.Runs);
        Assert.Equal("codex", run.FileName);
        Assert.Contains("Handoff:", o);
        Assert.True(File.Exists(Path.Combine(Project, "HANDOFF.md")) || Directory.GetFiles(Project, "*", SearchOption.AllDirectories).Length > 0);
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
    public void Resume_refusal_is_shown_and_loop_continues()
    {
        var s = Session("claude", "c1", "A", 1, project: Path.Combine(_root, "missing"));
        var (svc, console) = Make([new FakeAdapter("claude", Full, [s])]);
        console.Input.PushTextWithEnter("");   // folder prompt: empty

        var code = RunScreen(svc, console, Enter, C('q'));

        Assert.Equal(0, code);
        Assert.Empty(_runner.Runs);
        Assert.Contains("not resuming", console.Output);
    }

    [Fact]
    public void Filter_prompt_applies_valid_input_and_keeps_previous_on_invalid()
    {
        var a = Session("claude", "c1", "Alpha", 1);
        var b = Session("codex", "x1", "Bravo", 2);
        var (svc, console) = Make([new FakeAdapter("claude", Full, [a]), new FakeAdapter("codex", Full, [b])]);
        console.Input.PushTextWithEnter("agent:codex");
        console.Input.PushTextWithEnter("older:abc");

        RunScreen(svc, console, C('/'), C('/'), C('q'));

        var o = console.Output;
        Assert.Contains("filter: agent codex", o);
        Assert.Contains("older:", o);   // error message
        Assert.DoesNotContain("filter: all agents · all projects · any age\n", o[o.LastIndexOf("older:", StringComparison.Ordinal)..]);
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

        var code = RunScreen(svc, console, Enter, C(' '), C('a'), C('d'), C('t'), C('x'), Key(ConsoleKey.PageDown),
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
        console.Input.PushTextWithEnter("y");

        var code = RunScreen(svc, console, C('a'), C('q'));

        Assert.Equal(0, code);
        Assert.True(File.Exists(s.StorePath));
        Assert.Contains("[boom]", console.Output);
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
