using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tui;

namespace AgentSessions.Tests.Tui;

public class MainScreenModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static ListedSession L(string agent, string id, int ageDays, bool stale = false, string project = "C:/p/app") =>
        new(new SessionInfo(agent, id, project, "t" + id, Now.AddDays(-ageDays), 1, "/s/" + id), stale, false, AdapterCapabilities.List);

    private static MainScreenModel Model(params ListedSession[] s) => new(s, () => Now);

    [Fact]
    public void SelectStale_marks_only_stale_sessions()
    {
        var m = Model(L("claude", "a", 1), L("claude", "b", 90, stale: true), L("codex", "c", 100, stale: true));
        m.SetFilter(new SessionFilter(Agent: "claude"));
        m.SelectStale();
        Assert.Equal(["claude:b"], m.MarkedKeys);   // codex:c is stale but not visible
    }

    [Fact]
    public void ToggleMark_toggles_current_row()
    {
        var m = Model(L("claude", "a", 1), L("claude", "b", 2));
        m.MoveNext();
        m.ToggleMark();
        Assert.Equal(["claude:b"], m.MarkedKeys);
        m.ToggleMark();
        Assert.Empty(m.MarkedKeys);
    }

    [Fact]
    public void Navigation_clamps_at_ends()
    {
        var m = Model(L("claude", "a", 1), L("claude", "b", 2));
        m.MovePrevious();
        Assert.Equal(0, m.Cursor);
        m.MoveNext(); m.MoveNext(); m.MoveNext();
        Assert.Equal(1, m.Cursor);

        var empty = Model();
        empty.MoveNext(); empty.MovePrevious(); empty.ToggleMark(); empty.SelectStale();
        Assert.Equal(0, empty.Cursor);
        Assert.Null(empty.Current);
        Assert.Empty(empty.Targets);
        Assert.Empty(empty.MarkedKeys);
    }

    [Fact]
    public void Filter_narrows_rows()
    {
        var m = Model(L("claude", "a", 1, project: "C:/p/Foo"), L("codex", "b", 40), L("claude", "c", 50, project: "C:/p/bar"));
        m.SetFilter(new SessionFilter(Agent: "claude"));
        Assert.Equal(["a", "c"], m.Rows.Select(r => r.Info.Id));
        m.SetFilter(new SessionFilter(ProjectContains: "foo"));
        Assert.Equal(["a"], m.Rows.Select(r => r.Info.Id));
        m.SetFilter(new SessionFilter(OlderThan: TimeSpan.FromDays(30)));
        Assert.Equal(["b", "c"], m.Rows.Select(r => r.Info.Id));
        m.SetFilter(new SessionFilter());
        Assert.Equal(3, m.Rows.Count);
    }

    [Fact]
    public void Targets_are_marked_rows_else_current()
    {
        var m = Model(L("claude", "a", 1), L("claude", "b", 2), L("claude", "c", 3));
        m.MoveNext();
        Assert.Equal(["b"], m.Targets.Select(t => t.Info.Id));
        m.ToggleMark(); m.MoveNext(); m.ToggleMark();
        Assert.Equal(["b", "c"], m.Targets.Select(t => t.Info.Id));
    }

    [Fact]
    public void Reload_keeps_marks_and_clamps_cursor()
    {
        var m = Model(L("claude", "a", 1), L("claude", "b", 2), L("claude", "c", 3));
        m.ToggleMark(); m.MoveNext(); m.MoveNext();
        m.Reload([L("claude", "a", 1)]);
        Assert.Equal(0, m.Cursor);
        Assert.Equal(["claude:a"], m.MarkedKeys);
        m.Reload([L("claude", "x", 1)]);
        Assert.Empty(m.MarkedKeys);
        m.Reload([]);
        Assert.Equal(0, m.Cursor);
    }

    [Fact]
    public void Marks_hidden_by_a_filter_are_dropped_so_they_can_never_be_targeted()
    {
        var m = Model(L("claude", "a", 1), L("codex", "b", 2));
        m.SelectStale();
        m.ToggleMark();
        m.SetFilter(new SessionFilter(Agent: "codex"));
        Assert.Empty(m.MarkedKeys);
        Assert.Equal(["b"], m.Targets.Select(t => t.Info.Id));
    }

    [Theory]
    [InlineData("agent:claude project:foo older:30", "claude", "foo", 30)]
    [InlineData("bar", null, "bar", null)]
    [InlineData("older:0", null, null, 0)]
    [InlineData("", null, null, null)]
    public void FilterParser_parses_tokens(string input, string? agent, string? project, int? days)
    {
        Assert.True(FilterParser.TryParse(input, out var f, out _));
        Assert.Equal(agent, f.Agent);
        Assert.Equal(project, f.ProjectContains);
        Assert.Equal(days is { } d ? TimeSpan.FromDays(d) : null, f.OlderThan);
    }

    [Fact]
    public void FilterParser_reports_repeated_tokens_and_uses_the_last()
    {
        Assert.True(FilterParser.TryParse("agent:claude agent:codex older:1 older:2 foo bar", out var f, out _, out var note));
        Assert.Equal("codex", f.Agent);
        Assert.Equal(TimeSpan.FromDays(2), f.OlderThan);
        Assert.Equal("bar", f.ProjectContains);
        Assert.Contains("only the last", note);
        Assert.Contains("agent", note);

        Assert.True(FilterParser.TryParse("agent:claude foo", out _, out _, out var none));
        Assert.Null(none);
    }

    [Theory]
    [InlineData("older:abc")]
    [InlineData("older:-1")]
    [InlineData("older:36501")]
    [InlineData("older:99999999999999999999")]
    [InlineData("agent:")]
    public void FilterParser_rejects_invalid(string input)
    {
        Assert.False(FilterParser.TryParse(input, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }
}
