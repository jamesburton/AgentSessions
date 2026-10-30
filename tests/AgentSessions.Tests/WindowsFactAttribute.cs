using Xunit;

namespace AgentSessions.Tests;

/// <summary>A fact that is reported as skipped (not passed) on non-Windows OSes.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-only";
    }
}
