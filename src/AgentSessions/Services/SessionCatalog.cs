using AgentSessions.Config;
using AgentSessions.Model;

namespace AgentSessions.Services;

public record ListedSession(SessionInfo Info, bool IsStale, bool IsActive, AdapterCapabilities Capabilities);

public record SessionFilter(string? Agent = null, string? ProjectContains = null, TimeSpan? OlderThan = null);

public class SessionCatalog(IEnumerable<ISessionAdapter> adapters, AppSettings settings, Func<DateTimeOffset> now)
{
    private readonly IReadOnlyList<ISessionAdapter> _adapters = adapters.ToList();

    public (IReadOnlyList<ListedSession> Sessions, IReadOnlyList<SkippedSession> Skipped) List(SessionFilter? filter = null)
    {
        var at = now();
        var staleAfter = TimeSpan.FromDays(settings.StaleDays);
        var activeWithin = TimeSpan.FromMinutes(settings.RecentActiveMinutes);
        var sessions = new List<ListedSession>();
        var skipped = new List<SkippedSession>();

        foreach (var adapter in _adapters)
        {
            DiscoveryResult result;
            try
            {
                result = adapter.Discover();
            }
            catch (Exception ex)
            {
                skipped.Add(new SkippedSession(adapter.Name, ex.Message));
                continue;
            }

            skipped.AddRange(result.Skipped);
            foreach (var info in result.Sessions)
            {
                var age = at - info.LastActivity;
                if (!Matches(filter, info, age)) continue;
                sessions.Add(new ListedSession(info, age > staleAfter, age <= activeWithin, adapter.Capabilities));
            }
        }

        var ordered = sessions
            .OrderByDescending(s => s.Info.LastActivity)
            .ThenBy(s => s.Info.Agent, StringComparer.Ordinal)
            .ThenBy(s => s.Info.Id, StringComparer.Ordinal)
            .ToList();
        return (ordered, skipped);
    }

    private static bool Matches(SessionFilter? f, SessionInfo info, TimeSpan age)
    {
        if (f is null) return true;
        if (f.Agent is not null && !string.Equals(f.Agent, info.Agent, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.ProjectContains is not null &&
            (info.ProjectPath is null || !info.ProjectPath.Contains(f.ProjectContains, StringComparison.OrdinalIgnoreCase))) return false;
        if (f.OlderThan is { } older && !(age > older)) return false;
        return true;
    }
}
