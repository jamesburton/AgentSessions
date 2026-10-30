using AgentSessions.Config;
using AgentSessions.Services;
using Xunit;

namespace AgentSessions.Tests.Services;

public sealed class HandoffPolisherTests
{
    private const string Extracted =
        "# Session handoff\n\n## Source\n\n- Agent: claude\n- Session id: abc\n- Full original session: C:\\s\\abc.jsonl\n- Project: C:\\p\n\n## Task\n\nDo the thing.\n";

    private sealed class FakeCaller : IModelCaller
    {
        public int Calls { get; private set; }
        public Func<string, string> Impl { get; init; } = s => s;
        public string Polish(string handoffMarkdown) { Calls++; return Impl(handoffMarkdown); }
    }

    private static AppSettings On => new(PolishEnabled: true);

    [Fact]
    public void Disabled_returns_extracted_unchanged_and_never_calls_model()
    {
        var fake = new FakeCaller { Impl = _ => "polished" };
        var (content, note) = new HandoffPolisher(new AppSettings(), fake).Apply(Extracted);
        Assert.Equal(Extracted, content);
        Assert.Null(note);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void Enabled_returns_polished_output()
    {
        var polished = Extracted + "\nTidied.\n";
        var fake = new FakeCaller { Impl = _ => polished };
        var (content, note) = new HandoffPolisher(On, fake).Apply(Extracted);
        Assert.Equal(polished, content);
        Assert.Null(note);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void Enabled_but_model_throws_returns_extracted_with_note()
    {
        var fake = new FakeCaller { Impl = _ => throw new TimeoutException("too slow") };
        var (content, note) = new HandoffPolisher(On, fake).Apply(Extracted);
        Assert.Equal(Extracted, content);
        Assert.NotNull(note);
        Assert.Contains("too slow", note);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\t ")]
    public void Enabled_but_model_returns_empty_returns_extracted_with_note(string output)
    {
        var fake = new FakeCaller { Impl = _ => output };
        var (content, note) = new HandoffPolisher(On, fake).Apply(Extracted);
        Assert.Equal(Extracted, content);
        Assert.NotNull(note);
    }

    [Fact]
    public void Polished_output_missing_source_lines_falls_back_with_note()
    {
        var fake = new FakeCaller { Impl = _ => "# Summary\n\n- Agent: claude\n\nDo the thing, shorter." };
        var (content, note) = new HandoffPolisher(On, fake).Apply(Extracted);
        Assert.Equal(Extracted, content);
        Assert.NotNull(note);
        Assert.Contains("source", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Polished_output_keeping_source_lines_is_accepted()
    {
        var polished = "# Polished\n\n## Source\n\n- Agent: claude\n- Session id: abc\n- Full original session: C:\\s\\abc.jsonl\n- Project: C:\\p\n\nShort.";
        var (content, note) = new HandoffPolisher(On, new FakeCaller { Impl = _ => polished }).Apply(Extracted);
        Assert.Equal(polished, content);
        Assert.Null(note);
    }

    [Fact]
    public void Null_caller_with_polish_enabled_falls_back_with_note()
    {
        var (content, note) = new HandoffPolisher(On, null).Apply(Extracted);
        Assert.Equal(Extracted, content);
        Assert.NotNull(note);
        Assert.Contains("command", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_returns_null_caller_without_command_and_a_caller_with_one()
    {
        Assert.Null(HandoffPolisher.Create(new AppSettings(PolishEnabled: true)).Caller);
        Assert.NotNull(HandoffPolisher.Create(new AppSettings(PolishEnabled: true, PolishCommand: "x")).Caller);
    }

    [Fact]
    public void SplitCommandLine_handles_quotes_and_whitespace()
    {
        Assert.Equal(["a", "b c", "d"], CommandModelCaller.SplitCommandLine("  a \"b c\"   d "));
        Assert.Equal(["C:\\Program Files\\x.exe", "-p"], CommandModelCaller.SplitCommandLine("\"C:\\Program Files\\x.exe\" -p"));
        Assert.Equal(["a", "", "b"], CommandModelCaller.SplitCommandLine("a \"\" b"));
        Assert.Empty(CommandModelCaller.SplitCommandLine("   "));
    }

    [Fact]
    public void Blank_command_throws_InvalidOperation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new CommandModelCaller("  ", TimeSpan.FromSeconds(5)).Polish("x"));
        Assert.Contains("no polish command configured", ex.Message);
    }

    [WindowsFact]
    public void Real_process_echoes_stdin_to_stdout()
    {
        var input = "hello \u00e9 world\nline2";
        var output = new CommandModelCaller("cmd.exe /c more", TimeSpan.FromSeconds(30)).Polish(input);
        Assert.Contains("hello", output);
        Assert.Contains("line2", output);
    }

    [WindowsFact]
    public void Real_process_nonzero_exit_throws_with_code()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new CommandModelCaller("cmd.exe /c exit 3", TimeSpan.FromSeconds(30)).Polish("x"));
        Assert.Contains("code 3", ex.Message);
    }

    [WindowsFact]
    public void Real_process_timeout_kills_and_throws()
    {
        var caller = new CommandModelCaller("powershell -NoProfile -Command Start-Sleep -Seconds 60", TimeSpan.FromSeconds(2));
        Assert.Throws<TimeoutException>(() => caller.Polish("x"));
    }

    private const string PsEcho =
        "powershell -NoProfile -Command \"[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::Out.Write([Console]::In.ReadToEnd())\"";

    [WindowsFact]
    public void Child_that_never_reads_stdin_with_huge_input_times_out_and_is_killed()
    {
        var pid = 0;
        var caller = new CommandModelCaller("powershell -NoProfile -Command Start-Sleep -Seconds 30", TimeSpan.FromSeconds(2))
        { OnStarted = p => pid = p.Id };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => caller.Polish(new string('x', 2_000_000)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.NotEqual(0, pid);
        Assert.True(IsGone(pid), "child still running");
    }

    private static bool IsGone(int pid)
    {
        for (var i = 0; i < 20; i++)
        {
            try { using var q = System.Diagnostics.Process.GetProcessById(pid); if (q.HasExited) return true; }
            catch (ArgumentException) { return true; }
            Thread.Sleep(100);
        }
        return false;
    }

    [WindowsFact]
    public void Large_input_round_trips_through_reading_child()
    {
        var input = string.Concat(Enumerable.Repeat("line of handoff text\n", 50_000)); // ~1.1 MB
        var output = new CommandModelCaller(PsEcho, TimeSpan.FromSeconds(60)).Polish(input);
        Assert.Equal(input, output);
    }

    [WindowsFact]
    public void Non_ascii_round_trips_exactly()
    {
        var input = "caf\u00e9 \u4e2d\u6587 done";
        Assert.Equal(input, new CommandModelCaller(PsEcho, TimeSpan.FromSeconds(60)).Polish(input));
    }

    [WindowsFact]
    public void Stdout_over_cap_kills_child_and_throws()
    {
        var caller = new CommandModelCaller("powershell -NoProfile -Command \"[Console]::Out.Write('x' * 50000); Start-Sleep -Seconds 30\"", TimeSpan.FromSeconds(30))
        { MaxStdoutChars = 1000 };
        var ex = Assert.Throws<InvalidOperationException>(() => caller.Polish("x"));
        Assert.Contains("stdout", ex.Message);
    }

    [WindowsFact]
    public void Stderr_over_cap_kills_child_and_throws()
    {
        var caller = new CommandModelCaller("powershell -NoProfile -Command \"[Console]::Error.Write('x' * 50000); Start-Sleep -Seconds 30\"", TimeSpan.FromSeconds(30))
        { MaxStderrChars = 100 };
        var ex = Assert.Throws<InvalidOperationException>(() => caller.Polish("x"));
        Assert.Contains("stderr", ex.Message);
    }

    [WindowsFact]
    public void Nonzero_exit_message_labels_stderr_excerpt_and_truncates()
    {
        var caller = new CommandModelCaller("powershell -NoProfile -Command \"[Console]::Error.Write('e' * 500); exit 2\"", TimeSpan.FromSeconds(30));
        var ex = Assert.Throws<InvalidOperationException>(() => caller.Polish("x"));
        Assert.Contains("code 2", ex.Message);
        Assert.Contains("stderr: eeee", ex.Message);
        Assert.DoesNotContain(new string('e', 201), ex.Message);
    }

    [Fact]
    public void Source_line_near_miss_is_not_accepted()
    {
        var polished = "# P\n\n- Agent: claude\n- Session id: abcdef\n- Full original session: C:\\s\\abc.jsonl\n- Project: C:\\p\n";
        var (content, note) = new HandoffPolisher(On, new FakeCaller { Impl = _ => polished }).Apply(Extracted);
        Assert.Equal(Extracted, content);
        Assert.NotNull(note);
    }
}
