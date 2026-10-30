namespace AgentSessions.Adapters;

/// <summary>
/// Session ids come from file and folder names and are passed to the agents as a command-line argument
/// (<c>--resume &lt;id&gt;</c>), so a crafted name such as <c>--flag.jsonl</c> must never become a session.
/// </summary>
internal static class SessionIds
{
    public const string UnsafeReason =
        "unsafe session id (only letters, digits, '.', '_' and '-' are allowed, and it must not start with '-'); not listed";

    /// <summary>Non-empty, not starting with '-', and only ASCII letters, digits, '.', '_' and '-'.</summary>
    public static bool IsSafe(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] == '-') return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        return true;
    }
}
