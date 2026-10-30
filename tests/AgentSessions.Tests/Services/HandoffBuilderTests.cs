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
        Assert.Contains("\n- Agent: claude\n", md);
        Assert.Contains("\n- Session id: sess-123\n", md);
        Assert.Contains("\n- Full original session: C:/store/sess-123.jsonl\n", md);
        Assert.Contains("\n- Project: C:/p/app\n", md);
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
        // Entry 10 (neither the request nor in the recent window) is tracked by a weak reference.
        // Midway through the stream we force a GC: a streaming builder has dropped it, a
        // materialising one (ToList/ToArray) still holds it.
        const int Total = 100_000;
        var payload = new string('p', 1500);
        var produced = 0;
        WeakReference? probe = null;
        bool? probeCollectedMidway = null;

        IEnumerable<TranscriptEntry> Gen()
        {
            for (var i = 0; i < Total; i++)
            {
                if (i == Total / 2)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    probeCollectedMidway = !probe!.IsAlive;
                }
                var e = new TranscriptEntry(i % 2 == 1 ? Role.User : Role.Assistant, i == Total - 1 ? "the-end" : payload, null, null, []);
                if (i == 10) probe = new WeakReference(e);
                produced++;
                yield return e;
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var md = HandoffBuilder.Build(Info, Gen());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Total, produced);
        Assert.Contains("the-end", md);
        Assert.True(probeCollectedMidway, "builder kept earlier entries alive (materialised the input)");
        Assert.True(allocated < 30_000_000, $"allocated {allocated}");
    }

    private static string[] Headings(string md) =>
        md.Split('\n').Where(l => l.StartsWith("#")).ToArray();

    private static readonly string[] ExpectedHeadings =
    [
        "# Session handoff", "## Source", "## Original request", "## Recent activity",
        "## Files touched", "## Last commands", "## Where it stopped",
    ];

    private static int FenceLines(string md) => md.Split('\n').Count(l => l.StartsWith("```") || l.StartsWith("~~~"));

    [Fact]
    public void Unclosed_fence_in_prompt_does_not_swallow_later_sections()
    {
        var md = HandoffBuilder.Build(Info, [
            E(Role.User, "```\ncode that never closes " + new string('y', 500)),
            E(Role.Assistant, "```python\nalso open"),
            E(Role.Tool, "git status", "bash")],
            new HandoffOptions(MaxCharsPerEntry: 40));
        Assert.Equal(ExpectedHeadings, Headings(md));
        Assert.Equal(0, FenceLines(md) % 2);
    }

    [Fact]
    public void Heading_lines_in_text_cannot_create_fake_sections()
    {
        var md = HandoffBuilder.Build(Info, [
            E(Role.User, "intro\n## Fake heading\n# Another"),
            E(Role.Assistant, "## Where it stopped\nnope")]);
        Assert.Equal(ExpectedHeadings, Headings(md));
    }

    [Fact]
    public void Last_activity_is_converted_to_utc()
    {
        var s = Info with { LastActivity = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.FromHours(5)) };
        var md = HandoffBuilder.Build(s, []);
        Assert.Contains("- Last activity: 2026-10-30 07:00:00 UTC\n", md);
    }

    [Fact]
    public void Truncation_does_not_split_surrogate_pairs()
    {
        var md = HandoffBuilder.Build(Info, [E(Role.Assistant, new string('a', 4) + string.Concat(Enumerable.Repeat("\U0001F600", 10)))],
            new HandoffOptions(MaxCharsPerEntry: 5));
        for (var i = 0; i < md.Length; i++)
        {
            if (char.IsHighSurrogate(md[i])) { Assert.True(i + 1 < md.Length && char.IsLowSurrogate(md[i + 1])); i++; }
            else Assert.False(char.IsLowSurrogate(md[i]));
        }
        Assert.Contains("[truncated, 20 chars omitted]", md);
    }

    [Fact]
    public void Empty_entry_text_renders_placeholder()
    {
        var md = HandoffBuilder.Build(Info, [E(Role.User, "")]);
        Assert.Contains("> (empty)", md);
    }

    [Fact]
    public void File_paths_with_control_characters_stay_on_one_line()
    {
        var md = HandoffBuilder.Build(Info, [E(Role.Tool, "x", "edit", "a\n## Fake\nb.cs")]);
        Assert.Equal(ExpectedHeadings, Headings(md));
        Assert.Contains("\n- a ## Fake b.cs\n", md);
    }

    [Fact]
    public void Only_injected_context_user_entries_fall_back_to_first_of_them()
    {
        var md = HandoffBuilder.Build(Info, [
            E(Role.User, "<environment_context>one</environment_context>"),
            E(Role.User, "<other>two</other>")]);
        var i = md.IndexOf("## Original request", StringComparison.Ordinal);
        var j = md.IndexOf("## Recent activity", StringComparison.Ordinal);
        Assert.Contains("one", md[i..j]);
        Assert.DoesNotContain("two", md[i..j]);
    }

    [Fact]
    public void System_entries_are_hidden_from_activity_and_stop_point()
    {
        var md = HandoffBuilder.Build(Info, [
            E(Role.User, "do it"),
            E(Role.Assistant, "done"),
            E(Role.System, "secret-system-note")]);
        Assert.DoesNotContain("secret-system-note", md);
        var k = md.IndexOf("## Where it stopped", StringComparison.Ordinal);
        Assert.Contains("**Assistant**", md[k..]);
        Assert.Contains("done", md[k..]);
    }

    [Fact]
    public void Files_list_is_capped_with_and_n_more_note()
    {
        var entries = new[] { new TranscriptEntry(Role.Tool, "x", null, "edit", Enumerable.Range(0, 70).Select(i => $"f{i}.cs").ToList()) };
        var md = HandoffBuilder.Build(Info, entries);
        Assert.Contains("- f49.cs\n", md);
        Assert.DoesNotContain("f50.cs", md);
        Assert.Contains("- ...and 20 more\n", md);
    }
}
