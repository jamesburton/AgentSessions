using AgentSessions.Model;
using AgentSessions.Services;
using Spectre.Console;

namespace AgentSessions.Cli;

internal static class CliSupport
{
    public const int Ok = 0, Error = 1, Usage = 2;

    /// <summary>Every untrusted string (titles, paths, messages) must go through this before being printed as markup.</summary>
    public static string Esc(string? s) => Markup.Escape(s ?? "");

    public static string Age(DateTimeOffset now, DateTimeOffset then)
    {
        var d = now - then;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h ago";
        if (d < TimeSpan.FromDays(30)) return $"{(int)d.TotalDays}d ago";
        if (d < TimeSpan.FromDays(365)) return $"{(int)(d.TotalDays / 30)}mo ago";
        return $"{(int)(d.TotalDays / 365)}y ago";
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.0} {units[i]}";
    }

    public static bool IsInteractive(IAnsiConsole console) => console.Profile.Capabilities.Interactive;

    /// <summary>Default No; non-interactive input or any read failure means No.</summary>
    public static bool Confirm(IAnsiConsole console, string question)
    {
        if (!IsInteractive(console))
        {
            console.MarkupLine("[yellow]Input is not interactive; not proceeding (pass --yes to skip the prompt).[/]");
            return false;
        }
        try
        {
            var answer = console.Prompt(new TextPrompt<string>(Markup.Escape(question) + " ").AllowEmpty());
            return answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                   || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Folder prompt for resume/transfer; only asks when interactive, otherwise returns null (refuse).</summary>
    public static Func<string?> FolderPrompt(IAnsiConsole console) => () =>
    {
        if (!IsInteractive(console)) return null;
        try
        {
            var answer = console.Prompt(new TextPrompt<string>("Project folder for this session:").AllowEmpty());
            return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
        }
        catch (Exception) { return null; }
    };

    /// <summary>Finds a session by agent + id (exact, else unique prefix). On failure prints why and returns null.</summary>
    public static (ISessionAdapter Adapter, SessionInfo Session)? Find(AppServices services, IAnsiConsole console,
        string agent, string id)
    {
        if (!services.Adapters.TryGetValue(agent, out var adapter))
        {
            console.MarkupLine($"[red]Unknown agent '{Esc(agent)}'. Known agents: {Esc(string.Join(", ", services.Adapters.Keys))}.[/]");
            return null;
        }
        var (sessions, _) = services.Catalog.List(new SessionFilter(Agent: adapter.Name));
        var exact = sessions.Where(s => s.Info.Id == id).ToList();
        var matches = exact.Count > 0
            ? exact
            : sessions.Where(s => id.Length > 0 && s.Info.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            console.MarkupLine($"[red]No {Esc(adapter.Name)} session matches id '{Esc(id)}'.[/]");
            return null;
        }
        if (matches.Count > 1)
        {
            console.MarkupLine($"[red]Id '{Esc(id)}' is ambiguous; it matches {matches.Count} {Esc(adapter.Name)} sessions:[/]");
            foreach (var m in matches.Take(10)) console.MarkupLine($"  {Esc(m.Info.Id)}  {Esc(m.Info.Title)}");
            return null;
        }
        return (adapter, matches[0].Info);
    }
}
