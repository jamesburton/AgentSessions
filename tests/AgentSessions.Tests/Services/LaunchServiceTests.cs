using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;
using Xunit;

namespace AgentSessions.Tests.Services;

public sealed class FakeRunner : IProcessRunner
{
    public List<LaunchSpec> Runs { get; } = [];
    public int ExitCode { get; set; }
    public Exception? Throws { get; set; }

    public int Run(LaunchSpec spec)
    {
        if (Throws is not null) throw Throws;
        Runs.Add(spec);
        return ExitCode;
    }
}

public sealed class FakeClipboard : IClipboard
{
    public List<string> Copied { get; } = [];
    public Exception? Throws { get; set; }

    public void Set(string text)
    {
        if (Throws is not null) throw Throws;
        Copied.Add(text);
    }
}

public sealed class LaunchServiceTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private const AdapterCapabilities Full = AdapterCapabilities.List | AdapterCapabilities.Resume | AdapterCapabilities.LaunchWithPrompt;
    private const AdapterCapabilities NoPrompt = AdapterCapabilities.List | AdapterCapabilities.Resume;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "as-launch-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly FakeClipboard _clip = new();

    public LaunchServiceTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private LaunchService Svc() => new(_runner, _clip);
    private static FakeAdapter Adapter(AdapterCapabilities c = Full) => new("claude", c, []);
    private static SessionInfo Session(string? project) => new("claude", "s1", project, "t", T, 1, "store");

    [Fact]
    public void Resume_runs_adapter_resume_spec()
    {
        var r = Svc().Resume(Adapter(), Session(_tmp), () => throw new Exception("must not ask"));
        Assert.True(r.Launched);
        Assert.Null(r.Message);
        var spec = Assert.Single(_runner.Runs);
        Assert.Equal("claude", spec.FileName);
        Assert.Equal(new[] { "resume", "s1" }, spec.Args);
        Assert.Equal(_tmp, spec.WorkingDirectory);
    }

    [Fact]
    public void Resume_reports_nonzero_exit_code_but_still_launched()
    {
        _runner.ExitCode = 3;
        var r = Svc().Resume(Adapter(), Session(_tmp), () => null);
        Assert.True(r.Launched);
        Assert.Contains("3", r.Message);
    }

    [Fact]
    public void Resume_with_missing_project_asks_for_folder_then_runs_there()
    {
        var gone = Path.Combine(_tmp, "gone");
        var r = Svc().Resume(Adapter(), Session(gone), () => _tmp);
        Assert.True(r.Launched);
        Assert.Equal(_tmp, Assert.Single(_runner.Runs).WorkingDirectory);
    }

    [Fact]
    public void Resume_with_null_project_asks_for_folder()
    {
        var r = Svc().Resume(Adapter(), Session(null), () => _tmp);
        Assert.True(r.Launched);
        Assert.Equal(_tmp, Assert.Single(_runner.Runs).WorkingDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resume_with_missing_project_and_declined_prompt_refuses_and_does_not_run(string? answer)
    {
        var r = Svc().Resume(Adapter(), Session(Path.Combine(_tmp, "gone")), () => answer);
        Assert.False(r.Launched);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Resume_with_nonexistent_folder_refuses()
    {
        var r = Svc().Resume(Adapter(), Session(null), () => Path.Combine(_tmp, "nope"));
        Assert.False(r.Launched);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Resume_turns_runner_InvalidOperationException_into_refusal()
    {
        _runner.Throws = new InvalidOperationException("nope shim");
        var r = Svc().Resume(Adapter(), Session(_tmp), () => null);
        Assert.False(r.Launched);
        Assert.Contains("nope shim", r.Message);
    }

    [Fact]
    public void StartWithHandoff_runs_BuildNew_with_the_opening_prompt()
    {
        var hp = Path.Combine(_tmp, "handoff.md");
        var r = Svc().StartWithHandoff(Adapter(), _tmp, hp);
        Assert.True(r.Launched);
        var spec = Assert.Single(_runner.Runs);
        Assert.Equal(_tmp, spec.WorkingDirectory);
        Assert.Equal($"Read {hp} and continue the work described there.", Assert.Single(spec.Args));
        Assert.Empty(_clip.Copied);
    }

    [Fact]
    public void StartWithHandoff_falls_back_to_clipboard_when_adapter_lacks_LaunchWithPrompt()
    {
        var hp = Path.Combine(_tmp, "handoff.md");
        var prompt = $"Read {hp} and continue the work described there.";
        var r = Svc().StartWithHandoff(Adapter(NoPrompt), _tmp, hp);
        Assert.True(r.Launched);
        Assert.Equal(prompt, Assert.Single(_clip.Copied));
        Assert.Contains("clipboard", r.Message);
        Assert.Contains(prompt, r.Message);
        // empty-string args stripped
        Assert.Empty(Assert.Single(_runner.Runs).Args);
    }

    [Fact]
    public void StartWithHandoff_clipboard_failure_still_launches_with_prompt_in_message()
    {
        _clip.Throws = new InvalidOperationException("no clip tool");
        var hp = Path.Combine(_tmp, "handoff.md");
        var r = Svc().StartWithHandoff(Adapter(NoPrompt), _tmp, hp);
        Assert.True(r.Launched);
        Assert.Single(_runner.Runs);
        Assert.Contains($"Read {hp} and continue the work described there.", r.Message);
        Assert.Contains("copy", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartWithHandoff_with_nonexistent_project_refuses()
    {
        var r = Svc().StartWithHandoff(Adapter(), Path.Combine(_tmp, "gone"), "h.md");
        Assert.False(r.Launched);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void StartWithHandoff_turns_runner_InvalidOperationException_into_refusal()
    {
        _runner.Throws = new InvalidOperationException("bad arg");
        var r = Svc().StartWithHandoff(Adapter(), _tmp, "h.md");
        Assert.False(r.Launched);
        Assert.Contains("bad arg", r.Message);
    }

    [Fact]
    public void Project_path_with_spaces_and_non_ascii_is_passed_intact()
    {
        var odd = Path.Combine(_tmp, "my proj \u00e9\u4e2d\u6587 caf\u00e9");
        Directory.CreateDirectory(odd);
        var hp = Path.Combine(odd, "h\u00e9.md");
        Svc().StartWithHandoff(Adapter(), odd, hp);
        Svc().Resume(Adapter(), Session(odd), () => null);
        Assert.Equal(odd, _runner.Runs[0].WorkingDirectory);
        Assert.Equal($"Read {hp} and continue the work described there.", Assert.Single(_runner.Runs[0].Args));
        Assert.Equal(odd, _runner.Runs[1].WorkingDirectory);
    }
}

public sealed class ProcessRunnerStartInfoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "as-path-" + Guid.NewGuid().ToString("N"));
    private const string Ext = ".COM;.EXE;.BAT;.CMD;.PS1";

    public ProcessRunnerStartInfoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "");
    private static LaunchSpec Spec(string file, params string[] args) => new(file, args, Path.GetTempPath());

    [Fact]
    public void Prefers_exe_over_cmd()
    {
        Touch("tool.cmd"); Touch("tool.exe");
        var psi = ProcessRunner.BuildStartInfo(Spec("tool", "a b"), _dir, ".CMD;.EXE");
        Assert.Equal(Path.Combine(_dir, "tool.exe"), psi.FileName, ignoreCase: true);
        Assert.Equal(new[] { "a b" }, psi.ArgumentList);
    }

    [Fact]
    public void Exe_args_with_spaces_and_non_ascii_are_passed_intact()
    {
        Touch("tool.exe");
        var arg = "Read C:\\my proj\\caf\u00e9 \u4e2d\u6587\\h.md %x% ^ ! \"q\"";
        var psi = ProcessRunner.BuildStartInfo(Spec("tool", arg), _dir, Ext);
        Assert.Equal(new[] { arg }, psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.False(psi.RedirectStandardInput);
    }

    [Fact]
    public void Cmd_shim_runs_via_cmd_exe_with_quoted_args()
    {
        Touch("codex.cmd");
        var psi = ProcessRunner.BuildStartInfo(Spec("codex", "resume", "a b", "caf\u00e9", ""), _dir, Ext);
        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Empty(psi.ArgumentList);
        var shim = Path.Combine(_dir, "codex.cmd");
        Assert.Equal($"/d /s /c \"\"{shim}\" \"resume\" \"a b\" \"caf\u00e9\" \"\"\"", psi.Arguments, ignoreCase: true);
    }

    [Fact]
    public void Cmd_shim_doubles_trailing_backslashes()
    {
        Touch("codex.bat");
        var psi = ProcessRunner.BuildStartInfo(Spec("codex", "C:\\dir\\"), _dir, Ext);
        Assert.Contains("\"C:\\dir\\\\\"", psi.Arguments);
    }

    [Theory]
    [InlineData("100%")]
    [InlineData("a^b")]
    [InlineData("hi!")]
    [InlineData("say \"hi\"")]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    public void Cmd_shim_rejects_metacharacters(string arg)
    {
        Touch("codex.cmd");
        Assert.Throws<InvalidOperationException>(() => ProcessRunner.BuildStartInfo(Spec("codex", arg), _dir, Ext));
    }

    [Fact]
    public void Ps1_is_ignored()
    {
        Touch("only.ps1");
        var ex = Assert.Throws<InvalidOperationException>(() => ProcessRunner.BuildStartInfo(Spec("only"), _dir, Ext));
        Assert.Contains("only", ex.Message);
    }

    [Fact]
    public void Ps1_ignored_in_favour_of_cmd()
    {
        Touch("t.ps1"); Touch("t.cmd");
        var psi = ProcessRunner.BuildStartInfo(Spec("t"), _dir, ".PS1;.CMD");
        Assert.Equal("cmd.exe", psi.FileName);
    }

    [Fact]
    public void Working_directory_is_set()
    {
        Touch("tool.exe");
        var psi = ProcessRunner.BuildStartInfo(Spec("tool"), _dir, Ext);
        Assert.Equal(Path.GetTempPath(), psi.WorkingDirectory);
    }
}
