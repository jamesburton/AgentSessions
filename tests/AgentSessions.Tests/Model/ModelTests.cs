using AgentSessions.Model;

namespace AgentSessions.Tests.Model;

public class ModelTests
{
    [Fact]
    public void SessionInfo_is_value_equal_by_fields()
    {
        var t = DateTimeOffset.UnixEpoch;
        var a = new SessionInfo("claude", "1", "/p", "title", t, 10, "/s");
        var b = new SessionInfo("claude", "1", "/p", "title", t, 10, "/s");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Capabilities_flags_combine()
    {
        var c = AdapterCapabilities.List | AdapterCapabilities.Resume;
        Assert.True(c.HasFlag(AdapterCapabilities.List));
        Assert.False(c.HasFlag(AdapterCapabilities.Delete));
    }
}
