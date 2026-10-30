namespace AgentSessions.Tui;

/// <summary>Source of single key presses; faked in tests.</summary>
public interface IKeySource
{
    ConsoleKeyInfo ReadKey();

    /// <summary>Drops keys typed ahead (buffered) so they cannot answer a prompt the user has not seen yet.</summary>
    void DiscardPending() { }
}

public sealed class ConsoleKeySource : IKeySource
{
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public void DiscardPending()
    {
        try
        {
            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException) { /* input is redirected: nothing buffered to drop */ }
    }
}
