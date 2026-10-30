namespace AgentSessions.Tui;

/// <summary>Source of single key presses; faked in tests.</summary>
public interface IKeySource
{
    ConsoleKeyInfo ReadKey();
}

public sealed class ConsoleKeySource : IKeySource
{
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);
}
