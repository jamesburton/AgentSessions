namespace AgentSessions.Services;

public record CleanupSelectionResult(IReadOnlyList<ListedSession> Selected, IReadOnlyList<string> UnknownIds);

/// <summary>Decides which sessions a cleanup targets; shared by the CLI and the TUI.</summary>
public static class CleanupSelection
{
    /// <summary>Largest accepted --older-than, in days (about 100 years); keeps TimeSpan.FromDays far from overflow.</summary>
    public const int MaxDays = 36500;

    /// <summary>Splits a comma-separated list of <c>agent:id</c> pairs, dropping blanks.</summary>
    public static string[] ParseIds(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// <paramref name="ids"/> given (even empty): exactly those sessions (<c>agent:id</c>, exact id); any unknown id
    /// is reported in <c>UnknownIds</c> and the caller must not act. Otherwise: sessions inactive for more than
    /// <paramref name="olderThanDays"/> days, or, without it, those already flagged stale by the settings.
    /// </summary>
    public static CleanupSelectionResult Select(IReadOnlyList<ListedSession> all, string[]? ids, int? olderThanDays, DateTimeOffset now)
    {
        if (ids is not null)
        {
            var selected = new List<ListedSession>();
            var unknown = new List<string>();
            foreach (var raw in ids)
            {
                var parts = raw.Split(':', 2);
                var hit = parts.Length == 2
                    ? all.FirstOrDefault(x => string.Equals(x.Info.Agent, parts[0], StringComparison.OrdinalIgnoreCase) && x.Info.Id == parts[1])
                    : null;
                if (hit is null) unknown.Add(raw);
                else if (!selected.Contains(hit)) selected.Add(hit);
            }
            return new CleanupSelectionResult(selected, unknown);
        }

        var threshold = olderThanDays is { } d ? TimeSpan.FromDays(Math.Clamp(d, 0, MaxDays)) : (TimeSpan?)null;
        var picked = all.Where(x => threshold is { } t ? now - x.Info.LastActivity > t : x.IsStale).ToList();
        return new CleanupSelectionResult(picked, []);
    }
}
