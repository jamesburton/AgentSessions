using AgentSessions.Model;

namespace AgentSessions.Services;

public enum CleanupAction { Archive, Delete }

public record CleanupPlanItem(ListedSession Session, CleanupAction Action, IReadOnlyList<string> Paths, string? Warning, string? Declined);

public record CleanupFailure(CleanupPlanItem Item, string Error);

public record CleanupResult(IReadOnlyList<CleanupPlanItem> Done, IReadOnlyList<CleanupFailure> Failed);

public class CleanupService(IReadOnlyDictionary<string, ISessionAdapter> adapters, ArchiveStore archive, string? homeDir = null)
{
    /// <summary>Dry run: no side effects. Declines (never throws) when the adapter lacks the capability or is unknown.</summary>
    public IReadOnlyList<CleanupPlanItem> Plan(IEnumerable<ListedSession> sessions, CleanupAction action)
    {
        var items = new List<CleanupPlanItem>();
        var need = Needed(action);
        foreach (var s in sessions)
        {
            if (!adapters.TryGetValue(s.Info.Agent, out var adapter))
            {
                items.Add(new CleanupPlanItem(s, action, [], null, $"No adapter for agent '{s.Info.Agent}'."));
                continue;
            }
            if (!adapter.Capabilities.HasFlag(need))
            {
                items.Add(new CleanupPlanItem(s, action, [], null,
                    $"The {s.Info.Agent} adapter does not support {action.ToString().ToLowerInvariant()}."));
                continue;
            }
            IReadOnlyList<string> paths;
            try { paths = adapter.OwnedPaths(s.Info); }
            catch (Exception ex)
            {
                items.Add(new CleanupPlanItem(s, action, [], null, $"Could not determine the files to {action.ToString().ToLowerInvariant()}: {ex.Message}"));
                continue;
            }
            var warning = s.IsActive ? "Session looks active (recent activity); it may still be running. Confirm before proceeding." : null;
            items.Add(new CleanupPlanItem(s, action, paths, warning, null));
        }
        return items;
    }

    /// <summary>
    /// Applies items independently; one failure never stops the others. Declined items are neither Done nor Failed:
    /// they are simply not applied. <c>AfterRemove</c> runs only after the files were archived/deleted.
    /// </summary>
    public CleanupResult Apply(IEnumerable<CleanupPlanItem> items)
    {
        var done = new List<CleanupPlanItem>();
        var failed = new List<CleanupFailure>();
        foreach (var item in items)
        {
            if (item.Declined is not null) continue;
            try
            {
                if (!adapters.TryGetValue(item.Session.Info.Agent, out var adapter))
                    throw new InvalidOperationException($"No adapter for agent '{item.Session.Info.Agent}'.");
                if (!adapter.Capabilities.HasFlag(Needed(item.Action)))
                    throw new InvalidOperationException($"The {adapter.Name} adapter does not support {item.Action}.");

                if (item.Action == CleanupAction.Archive) archive.Archive(item.Session.Info, item.Paths);
                else DeletePaths(item.Paths);

                try { adapter.AfterRemove(item.Session.Info); }
                catch (Exception ex)
                {
                    var verb = item.Action == CleanupAction.Archive ? "archived" : "deleted";
                    failed.Add(new CleanupFailure(item, $"The files were already {verb}, but updating the agent's index failed: {ex.Message}"));
                    continue;
                }
                done.Add(item);
            }
            catch (Exception ex)
            {
                failed.Add(new CleanupFailure(item, ex.Message));
            }
        }
        return new CleanupResult(done, failed);
    }

    private static AdapterCapabilities Needed(CleanupAction a) =>
        a == CleanupAction.Archive ? AdapterCapabilities.Archive : AdapterCapabilities.Delete;

    private void DeletePaths(IReadOnlyList<string> paths)
    {
        // Validate everything first so one unsafe path means nothing at all is deleted.
        foreach (var p in paths)
        {
            PathGuard.Validate(p, homeDir);
            if (PathGuard.IsSameOrUnder(archive.Root, p))
                throw new InvalidOperationException($"Refusing to delete '{p}': it contains the archive.");
        }
        foreach (var p in paths)
        {
            if (!Directory.Exists(p) && !File.Exists(p) && !PathGuard.IsReparsePoint(p)) continue;
            TreeOps.DeleteSafely(p);
        }
    }
}
