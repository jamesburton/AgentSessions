using AgentSessions;

namespace AgentSessions.Tests;

public class SmokeTests
{
    [Fact]
    public void Main_with_version_flag_returns_zero()
    {
        Assert.Equal(0, Program.Main(["--version"]));
    }
}
