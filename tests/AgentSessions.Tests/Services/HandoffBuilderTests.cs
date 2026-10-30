using AgentSessions.Model;
using AgentSessions.Services;
using Xunit;

namespace AgentSessions.Tests.Services;

public class HandoffBuilderTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionInfo Info = new("claude", "sess-123", "C:/p/app", "title", T, 10, "C:/store/sess-123.jsonl");

    private static TranscriptEntry E(Role r, string text, string? tool = null, params string[] files) =>
        new(r, text, null, tool, files);

    [Fact]
    public void Includes_original_request_from_first_user_entry()
    {
        var md = HandoffBuilder.Build(Info, [
            E(Role.User, "<environment_context>cwd</environment_context>"),
            E(Role.User, "Please fix the login bug"),
            E(Role.User, "second prompt")]);
        var i = md.IndexOf("## Original request", StringComparison.Ordinal);
        var j = md.IndexOf("## Recent activity", StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i);
        var section = md[i..j];
        Assert.Contains("Please fix the login bug", section);
        Assert.DoesNotContain("environment_context", section);
        Assert.DoesNotContain("second prompt", section);
    }

    [Fact]
    public void Always_contains_source_agent_id_and_store_path()
    {
        var md = HandoffBuilder.Build(Info, [E(Role.User, "hi")]);
        Assert.Contains("## Source", md);
        Assert.Contains("claude", md);
        Assert.Contains("sess-123", md);
        Assert.Contains("C:/store/sess-123.jsonl", md);
        Assert.Contains("C:/p/app", md);
    }

    [Fact]
    public void Caps_recent_entries_and_truncates_long_entries()
    {
        var entries = Enumerable.Range(0, 50).Select(i => E(Role.Assistant, $"msg-{i:D3} " + new string('x', 5000))).ToList();
        var input = entries.Sum(e => e.Text.Length);
        var md = HandoffBuilder.Build(Info, entries, new HandoffOptions(MaxRecentEntries: 5, MaxCharsPerEntry: 100));
        Assert.True(md.Length < input);
        Assert.Contains("truncated", md);
        Assert.Contains("msg-049", md);
        Assert.Contains("msg-045", md);
        Assert.DoesNotContain("msg-044", md);
    }

    [Fact]
    public void Lists_distinct_files_touched()
    {
        var md = HandoffBuilder.Build(Info, [
            E(Role.Tool, "x", "edit", "a.cs", "b.cs"),
            E(Role.Tool, "y", "edit", "a.cs", "c.cs")]);
        var i = md.IndexOf("## Files touched", StringComparison.Ordinal);
        var section = md[i..];
        Assert.Equal(1, section.Split("a.cs").Length - 1);
        Assert.Contains("b.cs", section);
        Assert.Contains("c.cs", section);
    }

    [Fact]
    public void Lists_last_commands_from_tool_entries_with_shell_names()
    {
        var md = HandoffBuilder.Build(Info, [
            E(Role.Tool, "git status", "Bash"),
            E(Role.Tool, "ignored-edit", "edit"),
            E(Role.Tool, "dotnet test", "shell_command"),
            E(Role.Tool, "ls -la", "PowerShell")],
            new HandoffOptions(MaxCommands: 2));
        var i = md.IndexOf("## Last commands", StringComparison.Ordinal);
        var j = md.IndexOf("## Where it stopped", StringComparison.Ordinal);
        var section = md[i..j];
        Assert.DoesNotContain("git status", section);
        Assert.DoesNotContain("ignored-edit", section);
        Assert.Contains("dotnet test", section);
        Assert.Contains("ls -la", section);
    }

    [Fact]
    public void Empty_transcript_still_produces_source_section_and_placeholder()
    {
        var md = HandoffBuilder.Build(Info, []);
        Assert.Contains("## Source", md);
        Assert.Contains("sess-123", md);
        Assert.Contains("C:/store/sess-123.jsonl", md);
        Assert.Contains("## Original request", md);
        Assert.Contains("## Where it stopped", md);
        Assert.Contains("_(none", md);
        Assert.DoesNotContain("\r", md);
    }

    [Fact]
    public void Streams_input_without_materialising_more_than_needed()
    {
        static IEnumerable<TranscriptEntry> Gen()
        {
            for (var i = 0; i < 100_000; i++)
                yield return new TranscriptEntry(i % 2 == 0 ? Role.User : Role.Assistant, "entry " + i, null, null, []);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var md = HandoffBuilder.Build(Info, Gen());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("entry 99999", md);
        Assert.True(allocated < 100_000_000, $"allocated {allocated}");
    }
}
