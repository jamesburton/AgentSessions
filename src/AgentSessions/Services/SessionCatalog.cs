using AgentSessions.Config;
using AgentSessions.Model;

namespace AgentSessions.Services;

public record ListedSession(SessionInfo Info, bool IsStale, bool IsActive, AdapterCapabilities Capabilities, string? Label = null)
{
    /// <summary>What listings show: the user's label when set, else the agent's own title (untrusted: render via Esc).</summary>
    public string DisplayTitle => Label ?? Info.Title;
}

public record SessionFilter(string? Agent = null, string? ProjectContains = null, TimeSpan? OlderThan = null);

public class SessionCatalog(IEnumerable<ISessionAdapter> adapters, AppSettings settings, Func<DateTimeOffset> now,
    ILabelSource? labels = null)
{
    private readonly IReadOnlyList<ISessionAdapter> _adapters = adapters.ToList();

    public (IReadOnlyList<ListedSession> Sessions, IReadOnlyList<SkippedSession> Skipped) List(SessionFilter? filter = null)
    {
        var at = now();
        var staleAfter = TimeSpan.FromDays(settings.StaleDays);
        var activeWithin = TimeSpan.FromMinutes(settings.RecentActiveMinutes);
        var sessions = new List<ListedSession>();
        var skipped = new List<SkippedSession>();
        IReadOnlyDictionary<string, string> labelMap = new Dictionary<string, string>();
        try { labelMap = labels?.Load() ?? labelMap; }   // once per List(); a failing source just means no labels
        catch (Exception) { }

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
                labelMap.TryGetValue(LabelStore.KeyOf(info.Agent, info.Id), out var label);
                var listed = new ListedSession(info, age > staleAfter, age <= activeWithin, adapter.Capabilities, label);
                if (!Matches(listed, filter, at)) continue;
                sessions.Add(listed);
            }
        }

        var ordered = sessions
            .OrderByDescending(s => s.Info.LastActivity)
            .ThenBy(s => s.Info.Agent, StringComparer.Ordinal)
            .ThenBy(s => s.Info.Id, StringComparer.Ordinal)
            .ToList();
        return (ordered, skipped);
    }

    /// <summary>The one filter predicate, shared by <see cref="List"/> and the TUI model.</summary>
    public static bool Matches(ListedSession session, SessionFilter? f, DateTimeOffset now)
    {
        if (f is null) return true;
        var info = session.Info;
        var age = now - info.LastActivity;
        if (f.Agent is not null && !string.Equals(f.Agent, info.Agent, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.ProjectContains is not null &&
            (info.ProjectPath is null || !info.ProjectPath.Contains(f.ProjectContains, StringComparison.OrdinalIgnoreCase))) return false;
        if (f.OlderThan is { } older && !(age > older)) return false;
        return true;
    }
}
