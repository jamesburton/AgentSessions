using AgentSessions.Services;

namespace AgentSessions.Tui;

/// <summary>Selection, marks and filter state of the main list. No I/O; the screen drives it.</summary>
public sealed class MainScreenModel
{
    private readonly Func<DateTimeOffset> _now;
    private IReadOnlyList<ListedSession> _all;
    private readonly HashSet<string> _marked = new(StringComparer.Ordinal);

    public MainScreenModel(IReadOnlyList<ListedSession> all, Func<DateTimeOffset>? now = null)
    {
        _all = all;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Filter = new SessionFilter();
        Rows = Apply();
    }

    public static string KeyOf(ListedSession s) => $"{s.Info.Agent}:{s.Info.Id}";

    public IReadOnlyList<ListedSession> Rows { get; private set; }
    public int Cursor { get; private set; }
    public SessionFilter Filter { get; private set; }
    public IReadOnlyCollection<string> MarkedKeys => _marked;
    public ListedSession? Current => Cursor >= 0 && Cursor < Rows.Count ? Rows[Cursor] : null;

    /// <summary>Marked visible rows (list order), else the current row. Hidden marks are never targeted.</summary>
    public IReadOnlyList<ListedSession> Targets
    {
        get
        {
            var marked = Rows.Where(r => _marked.Contains(KeyOf(r))).ToList();
            if (marked.Count > 0) return marked;
            return Current is { } c ? [c] : [];
        }
    }

    public int StaleCount => Rows.Count(r => r.IsStale);

    public void MoveNext() => MoveBy(1);
    public void MovePrevious() => MoveBy(-1);
    public void MoveFirst() => Cursor = 0;
    public void MoveLast() => Cursor = Math.Max(0, Rows.Count - 1);

    public void MoveBy(int delta) =>
        Cursor = Rows.Count == 0 ? 0 : (int)Math.Clamp((long)Cursor + delta, 0, Rows.Count - 1);

    public void ToggleMark()
    {
        if (Current is not { } c) return;
        var key = KeyOf(c);
        if (!_marked.Remove(key)) _marked.Add(key);
    }

    public void SelectStale()
    {
        foreach (var r in Rows.Where(r => r.IsStale)) _marked.Add(KeyOf(r));
    }

    public void ClearMarks() => _marked.Clear();

    public void SetFilter(SessionFilter filter)
    {
        Filter = filter;
        Rows = Apply();
        Cursor = 0;
        PruneMarks();
    }

    public void Reload(IReadOnlyList<ListedSession> all)
    {
        _all = all;
        Rows = Apply();
        PruneMarks();
        Cursor = Rows.Count == 0 ? 0 : Math.Clamp(Cursor, 0, Rows.Count - 1);
    }

    private List<ListedSession> Apply()
    {
        var at = _now();
        return _all.Where(s => SessionCatalog.Matches(s, Filter, at)).ToList();
    }

    // Marks only ever refer to visible rows, so a destructive action cannot reach a hidden session.
    private void PruneMarks()
    {
        var visible = Rows.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        _marked.RemoveWhere(k => !visible.Contains(k));
    }
}

public static class FilterParser
{
    /// <summary>
    /// Space-separated tokens: <c>agent:NAME</c>, <c>project:TEXT</c>, <c>older:DAYS</c> (0..36500) or a bare word
    /// (project substring). Empty input clears the filter. On failure <paramref name="error"/> says why.
    /// </summary>
    public static bool TryParse(string? input, out SessionFilter filter, out string? error)
    {
        filter = new SessionFilter();
        error = null;
        string? agent = null, project = null;
        TimeSpan? older = null;

        foreach (var token in (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = token.IndexOf(':');
            var key = colon > 0 ? token[..colon].ToLowerInvariant() : "";
            var value = colon > 0 ? token[(colon + 1)..] : token;
            switch (key)
            {
                case "agent":
                    if (value.Length == 0) { error = "agent: needs a name, e.g. agent:claude."; return false; }
                    agent = value;
                    break;
                case "project":
                    if (value.Length == 0) { error = "project: needs text, e.g. project:myapp."; return false; }
                    project = value;
                    break;
                case "older":
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days)
                        || days > CleanupSelection.MaxDays)
                    {
                        error = $"older: needs a whole number of days between 0 and {CleanupSelection.MaxDays}.";
                        return false;
                    }
                    older = TimeSpan.FromDays(days);
                    break;
                default:
                    project = token;   // bare word (or unknown prefix such as a Windows drive path)
                    break;
            }
        }

        filter = new SessionFilter(agent, project, older);
        return true;
    }
}
