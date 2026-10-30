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

    public AppSettings Load()
    {
        LastLoadFailed = false;
        try
        {
            if (!File.Exists(settingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastLoadFailed = true;
            return new AppSettings();
        }
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
