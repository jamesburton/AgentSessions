using System.Text.Json;

namespace AgentSessions.Config;

public class SettingsStore(string settingsPath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,   // reads accept any key casing; writes stay camelCase
        WriteIndented = true,
    };

    /// <summary>True when the last <see cref="Load"/> found a settings file it could not use (the defaults were applied).</summary>
    public bool LastLoadFailed { get; private set; }

    /// <summary>Everything the last <see cref="Load"/> could not use as written (one line each); shown by list and clean.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = [];

    public const int MinStaleDays = 1, MaxStaleDays = 36500;

    /// <summary>Never throws: a missing file means defaults; an unusable file or value means defaults plus a warning.</summary>
    public AppSettings Load()
    {
        LastLoadFailed = false;
        _warnings.Clear();
        AppSettings loaded;
        try
        {
            if (!File.Exists(settingsPath)) return new AppSettings();
            loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastLoadFailed = true;
            _warnings.Add("settings.json could not be read; using defaults.");
            return new AppSettings();
        }
        return Normalise(loaded, _warnings);
    }

    /// <summary>
    /// Replaces values that would be unsafe with the defaults: staleDays outside 1..36500 (0 or less makes every
    /// session stale), a relative or blank archiveRoot (it would depend on the current directory), and a negative
    /// recentActiveMinutes.
    /// </summary>
    public static AppSettings Normalise(AppSettings s, List<string> warnings)
    {
        var defaults = new AppSettings();
        if (s.StaleDays is < MinStaleDays or > MaxStaleDays)
        {
            warnings.Add($"staleDays must be between {MinStaleDays} and {MaxStaleDays}; using {defaults.StaleDays}.");
            s = s with { StaleDays = defaults.StaleDays };
        }
        if (s.ArchiveRoot is { } root && (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)))
        {
            warnings.Add("archiveRoot must be an absolute path; using the default.");
            s = s with { ArchiveRoot = null };
        }
        if (s.RecentActiveMinutes < 0)
        {
            warnings.Add($"recentActiveMinutes must be 0 or more; using {defaults.RecentActiveMinutes}.");
            s = s with { RecentActiveMinutes = defaults.RecentActiveMinutes };
        }
        return s;
    }

    public void Save(AppSettings settings)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, Options));
    }

    public static string DefaultDataDir()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "AgentSessions");
    }
}
