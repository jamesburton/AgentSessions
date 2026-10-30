using AgentSessions.Config;
using AgentSessions.Model;
using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;
using Xunit;

namespace AgentSessions.Tests.Services;

public class SessionCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo S(string agent, string id, TimeSpan age, string? project = "C:/p/app") =>
        new(agent, id, project, "t", Now - age, 1, "/store/" + id);

    private static SessionCatalog Catalog(AppSettings? settings, params ISessionAdapter[] adapters) =>
        new(adapters, settings ?? new AppSettings(), () => Now);

    private static FakeAdapter Fake(string name, params SessionInfo[] s) =>
        new(name, AdapterCapabilities.List | AdapterCapabilities.Resume, s);

    [Fact]
    public void List_merges_agents_newest_first()
    {
        var c = Catalog(null,
            Fake("claude", S("claude", "a", TimeSpan.FromDays(3)), S("claude", "b", TimeSpan.FromDays(1))),
            Fake("codex", S("codex", "c", TimeSpan.FromDays(2))));
        var (sessions, skipped) = c.List();
        Assert.Equal(["b", "c", "a"], sessions.Select(x => x.Info.Id));
        Assert.Empty(skipped);
        Assert.Equal(AdapterCapabilities.List | AdapterCapabilities.Resume, sessions[0].Capabilities);
    }

    [Fact]
    public void Session_older_than_StaleDays_is_stale()
    {
        var c = Catalog(null, Fake("claude",
            S("claude", "old", TimeSpan.FromDays(31)),
            S("claude", "edge", TimeSpan.FromDays(30)),
            S("claude", "new", TimeSpan.FromDays(29))));
        var byId = c.List().Sessions.ToDictionary(x => x.Info.Id);
        Assert.True(byId["old"].IsStale);
        Assert.False(byId["edge"].IsStale);
        Assert.False(byId["new"].IsStale);
    }

    [Fact]
    public void StaleDays_setting_is_respected()
    {
        var c = Catalog(new AppSettings(StaleDays: 7), Fake("claude",
            S("claude", "a", TimeSpan.FromDays(8)), S("claude", "b", TimeSpan.FromDays(6))));
        var byId = c.List().Sessions.ToDictionary(x => x.Info.Id);
        Assert.True(byId["a"].IsStale);
        Assert.False(byId["b"].IsStale);
    }

    [Fact]
    public void Session_within_RecentActiveMinutes_is_active()
    {
        var c = Catalog(null, Fake("claude",
            S("claude", "hot", TimeSpan.FromMinutes(5)),
            S("claude", "edge", TimeSpan.FromMinutes(15)),
            S("claude", "cold", TimeSpan.FromMinutes(16))));
        var byId = c.List().Sessions.ToDictionary(x => x.Info.Id);
        Assert.True(byId["hot"].IsActive);
        Assert.True(byId["edge"].IsActive);
        Assert.False(byId["cold"].IsActive);
    }

    [Fact]
    public void Filter_by_agent_and_project_and_age()
    {
        var c = Catalog(null,
            Fake("claude",
                S("claude", "a", TimeSpan.FromDays(10), "C:/Work/Alpha"),
                S("claude", "b", TimeSpan.FromDays(1), "C:/Work/Alpha"),
                S("claude", "n", TimeSpan.FromDays(10), null)),
            Fake("codex", S("codex", "c", TimeSpan.FromDays(10), "C:/Work/Alpha")));

        Assert.Equal(["c"], c.List(new SessionFilter(Agent: "CODEX")).Sessions.Select(x => x.Info.Id));
        Assert.Equal(["a", "b", "c"], c.List(new SessionFilter(ProjectContains: "alpha")).Sessions.Select(x => x.Info.Id).Order());
        Assert.Equal(["a", "c", "n"], c.List(new SessionFilter(OlderThan: TimeSpan.FromDays(5))).Sessions.Select(x => x.Info.Id).Order());
        Assert.Equal(["a"], c.List(new SessionFilter("claude", "alpha", TimeSpan.FromDays(5))).Sessions.Select(x => x.Info.Id));
    }

    [Fact]
    public void One_adapter_throwing_does_not_break_list_and_is_reported_as_skipped()
    {
        var c = Catalog(null,
            FakeAdapter.Throwing("codex", new InvalidOperationException("boom")),
            Fake("claude", S("claude", "a", TimeSpan.FromDays(1))),
            new FakeAdapter("copilot", AdapterCapabilities.List, [], [new SkippedSession("/x", "bad")]));
        var (sessions, skipped) = c.List();
        Assert.Equal(["a"], sessions.Select(x => x.Info.Id));
        Assert.Contains(new SkippedSession("codex", "boom"), skipped);
        Assert.Contains(new SkippedSession("/x", "bad"), skipped);
        Assert.Equal(2, skipped.Count);
    }

    private sealed class Labels(Dictionary<string, string> d) : ILabelSource
    {
        public int Loads;
        public IReadOnlyDictionary<string, string> Load() { Loads++; return d; }
    }

    [Fact]
    public void Catalog_applies_label_and_keeps_original_title()
    {
        var a = new SessionInfo("Claude", "id1", "C:/p", "Original", Now, 1, "/s/1");
        var b = new SessionInfo("Claude", "id2", "C:/p", "Other", Now.AddHours(-1), 1, "/s/2");
        var labels = new Labels(new() { ["claude:id1"] = "Mine" });
        var c = new SessionCatalog([new FakeAdapter("Claude", AdapterCapabilities.List, [a, b])], new AppSettings(), () => Now, labels);

        var (sessions, _) = c.List();

        Assert.Equal("Mine", sessions[0].Label);
        Assert.Equal("Original", sessions[0].Info.Title);
        Assert.Null(sessions[1].Label);
        Assert.Equal(1, labels.Loads);   // once per List()
    }
}
