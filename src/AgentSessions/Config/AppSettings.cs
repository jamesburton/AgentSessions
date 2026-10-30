namespace AgentSessions.Config;

public record AppSettings(
    int StaleDays = 30,
    string? ArchiveRoot = null,
    bool PolishEnabled = false,
    string? PolishCommand = null,
    int RecentActiveMinutes = 15);
