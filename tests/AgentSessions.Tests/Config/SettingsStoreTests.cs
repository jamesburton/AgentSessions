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

    private static (AppSettings Settings, SettingsStore Store) LoadJson(string json)
    {
        var path = Path.Combine(SessionFixtures.TempHome(), "settings.json");
        File.WriteAllText(path, json);
        var store = new SettingsStore(path);
        return (store.Load(), store);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(36501)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_staleDays_falls_back_to_30_with_a_warning(int days)
    {
        var (s, store) = LoadJson($"{{ \"staleDays\": {days}, \"recentActiveMinutes\": 5 }}");
        Assert.Equal(30, s.StaleDays);
        Assert.Equal(5, s.RecentActiveMinutes);
        Assert.Equal(["staleDays must be between 1 and 36500; using 30."], store.Warnings);
        Assert.False(store.LastLoadFailed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(36500)]
    public void Boundary_staleDays_are_accepted(int days)
    {
        var (s, store) = LoadJson($"{{ \"staleDays\": {days} }}");
        Assert.Equal(days, s.StaleDays);
        Assert.Empty(store.Warnings);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("..\\\\elsewhere")]
    [InlineData("   ")]
    [InlineData("C:relative")]
    public void Relative_or_blank_archiveRoot_is_ignored_with_a_warning(string root)
    {
        var (s, store) = LoadJson($"{{ \"archiveRoot\": \"{root}\" }}");
        Assert.Null(s.ArchiveRoot);
        Assert.Equal(["archiveRoot must be an absolute path; using the default."], store.Warnings);
    }

    [Fact]
    public void Negative_recentActiveMinutes_falls_back_with_a_warning()
    {
        var (s, store) = LoadJson("{ \"recentActiveMinutes\": -1 }");
        Assert.Equal(15, s.RecentActiveMinutes);
        Assert.Equal(["recentActiveMinutes must be 0 or more; using 15."], store.Warnings);
    }

    [Fact]
    public void Several_problems_give_one_warning_each()
    {
        var (s, store) = LoadJson("{ \"staleDays\": 0, \"archiveRoot\": \"rel\", \"recentActiveMinutes\": -3, \"polishEnabled\": true }");
        Assert.Equal(new AppSettings(PolishEnabled: true), s);
        Assert.Equal(3, store.Warnings.Count);
    }

    [Fact]
    public void Corrupt_file_is_a_warning_and_valid_or_missing_files_have_none()
    {
        var (_, bad) = LoadJson("{ not json");
        Assert.Equal(["settings.json could not be read; using defaults."], bad.Warnings);
        var (_, good) = LoadJson("{ \"staleDays\": 7, \"archiveRoot\": \"C:\\\\a\" }");
        Assert.Empty(good.Warnings);
        var missing = new SettingsStore(Path.Combine(SessionFixtures.TempHome(), "none.json"));
        missing.Load();
        Assert.Empty(missing.Warnings);
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
