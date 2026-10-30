namespace AgentSessions.Tests.Fixtures;

public static class SessionFixtures
{
    public static string TempHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentsessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void WriteLines(string path, params string[] lines)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllLines(path, lines);
    }
}
