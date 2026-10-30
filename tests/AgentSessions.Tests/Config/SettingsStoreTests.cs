using AgentSessions.Config;
using AgentSessions.Tests.Fixtures;

namespace AgentSessions.Tests.Config;

public class SettingsStoreTests
{
    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var store = new SettingsStore(Path.Combine(SessionFixtures.TempHome(), "settings.json"));
        var s = store.Load();
        Assert.Equal(30, s.StaleDays);
        Assert.False(s.PolishEnabled);
    }

    [Fact]
    public void Save_then_Load_roundtrips()
    {
        var path = Path.Combine(SessionFixtures.TempHome(), "sub", "settings.json");
        var store = new SettingsStore(path);
        var saved = new AppSettings(StaleDays: 7, ArchiveRoot: "C:/arch", PolishEnabled: true, PolishCommand: "cmd", RecentActiveMinutes: 5);
        store.Save(saved);
        Assert.Equal(saved, store.Load());
    }

    [Fact]
    public void Load_with_corrupt_json_returns_defaults()
    {
        var path = Path.Combine(SessionFixtures.TempHome(), "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppSettings(), new SettingsStore(path).Load());
    }

    [Theory]
    [InlineData("{ \"StaleDays\": 7, \"ArchiveRoot\": \"C:/a\", \"PolishEnabled\": true, \"PolishCommand\": \"c\", \"RecentActiveMinutes\": 5 }")]
    [InlineData("{ \"staleDays\": 7, \"archiveRoot\": \"C:/a\", \"polishEnabled\": true, \"polishCommand\": \"c\", \"recentActiveMinutes\": 5 }")]
    [InlineData("{ \"STALEDAYS\": 7, \"archiveroot\": \"C:/a\", \"PolishEnabled\": true, \"polishCOMMAND\": \"c\", \"recentActiveminutes\": 5 }")]
    public void Load_accepts_any_key_casing(string json)
    {
        var path = Path.Combine(SessionFixtures.TempHome(), "settings.json");
        File.WriteAllText(path, json);
        var store = new SettingsStore(path);
        Assert.Equal(new AppSettings(7, "C:/a", true, "c", 5), store.Load());
        Assert.False(store.LastLoadFailed);
    }

    [Fact]
    public void Save_writes_camelCase_keys()
    {
        var path = Path.Combine(SessionFixtures.TempHome(), "settings.json");
        new SettingsStore(path).Save(new AppSettings());
        var text = File.ReadAllText(path);
        Assert.Contains("\"staleDays\"", text);
        Assert.DoesNotContain("\"StaleDays\"", text);
    }

    [Fact]
    public void LastLoadFailed_is_true_for_corrupt_file_only()
    {
        var dir = SessionFixtures.TempHome();
        var missing = new SettingsStore(Path.Combine(dir, "none.json"));
        missing.Load();
        Assert.False(missing.LastLoadFailed);

        var bad = Path.Combine(dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        var store = new SettingsStore(bad);
        store.Load();
        Assert.True(store.LastLoadFailed);
    }
}
