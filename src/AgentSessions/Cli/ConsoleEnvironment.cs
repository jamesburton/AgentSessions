using System.Text;

namespace AgentSessions.Cli;

/// <summary>
/// Whether the REAL process console is redirected. Interactivity is not left to the console library alone: the TUI and
/// every prompt also require that neither stdin nor stdout is redirected, so piped input can never answer a prompt.
/// </summary>
public interface IConsoleEnvironment
{
    bool InputRedirected { get; }
    bool OutputRedirected { get; }
}

/// <summary>The process console (<see cref="Console.IsInputRedirected"/> / <see cref="Console.IsOutputRedirected"/>).</summary>
public sealed class SystemConsoleEnvironment : IConsoleEnvironment
{
    public static SystemConsoleEnvironment Instance { get; } = new();
    public bool InputRedirected => Console.IsInputRedirected;
    public bool OutputRedirected => Console.IsOutputRedirected;
}

/// <summary>Fixed answers (tests, and AppServices built without <see cref="AppServices.CreateDefault"/>).</summary>
public sealed record FixedConsoleEnvironment(bool InputRedirected = false, bool OutputRedirected = false) : IConsoleEnvironment;

/// <summary>Console encoding setup done once at startup.</summary>
public static class ConsoleSetup
{
    /// <summary>
    /// Switches a terminal console to UTF-8 (no BOM) for output and input so titles, the ellipsis and the separator dots
    /// render instead of '?' under the default Windows code page. Does nothing when output is redirected (a pipe or file
    /// keeps the platform default). Never throws: each setter that the console refuses is skipped. True only if both
    /// encodings were set.
    /// </summary>
    public static bool UseUtf8(bool outputRedirected, Action<Encoding> setOut, Action<Encoding> setIn)
    {
        if (outputRedirected) return false;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return TrySet(setOut, utf8) & TrySet(setIn, utf8);   // non-short-circuit: try both

        static bool TrySet(Action<Encoding> set, Encoding e)
        {
            try { set(e); return true; }
            catch (Exception) { return false; }   // e.g. no console attached, or the host refuses the code page
        }
    }
}
