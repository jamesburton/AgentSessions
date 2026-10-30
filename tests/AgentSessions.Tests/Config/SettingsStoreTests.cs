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
}
