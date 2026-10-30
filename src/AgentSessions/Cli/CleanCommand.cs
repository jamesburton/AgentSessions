using System.ComponentModel;
using AgentSessions.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using static AgentSessions.Cli.CliSupport;

namespace AgentSessions.Cli;

public sealed class CleanCommand(AppServices services, IAnsiConsole console) : Command<CleanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--older-than <DAYS>")] [Description("Stale threshold in days (default: the stale-days setting).")]
        public int? OlderThan { get; init; }

        [CommandOption("--archive")] [Description("Move sessions to the archive (default).")]
        public bool Archive { get; init; }

        [CommandOption("--delete")] [Description("Permanently delete sessions (needs --apply; never the default).")]
        public bool Delete { get; init; }

        [CommandOption("--apply")] [Description("Actually do it. Without this only the plan is printed.")]
        public bool Apply { get; init; }

        [CommandOption("--yes")] [Description("Skip the confirmation prompt (with --apply).")]
        public bool Yes { get; init; }

        [CommandOption("--ids <IDS>")] [Description("Comma-separated agent:id pairs instead of the stale sessions.")]
        public string? Ids { get; init; }

        public override ValidationResult Validate()
        {
            if (Archive && Delete) return ValidationResult.Error("Choose either --archive or --delete, not both.");
            if (OlderThan is < 0) return ValidationResult.Error("--older-than must not be negative.");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        var action = s.Delete ? CleanupAction.Delete : CleanupAction.Archive;

        List<ListedSession> selected;
        if (s.Ids is not null)
        {
            var all = services.Catalog.List().Sessions;
            selected = [];
            var bad = new List<string>();
            foreach (var raw in s.Ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = raw.Split(':', 2);
                var hit = parts.Length == 2
                    ? all.FirstOrDefault(x => string.Equals(x.Info.Agent, parts[0], StringComparison.OrdinalIgnoreCase) && x.Info.Id == parts[1])
                    : null;
                if (hit is null) bad.Add(raw);
                else if (!selected.Contains(hit)) selected.Add(hit);
            }
            if (bad.Count > 0)
            {
                console.MarkupLine($"[red]No session found for: {Esc(string.Join(", ", bad))} (expected agent:id, exact id). Nothing was changed.[/]");
                return Error;
            }
        }
        else if (s.OlderThan is { } days)
        {
            selected = services.Catalog.List(new SessionFilter(OlderThan: TimeSpan.FromDays(days))).Sessions.ToList();
        }
        else
        {
            selected = services.Catalog.List().Sessions.Where(x => x.IsStale).ToList();
        }

        var plan = services.Cleanup.Plan(selected, action);
        var verb = action == CleanupAction.Archive ? "archive" : "delete";
        PrintPlan(plan, verb);

        if (!s.Apply)
        {
            console.MarkupLine($"[yellow]Dry run: nothing was changed. Add --apply to {verb} the listed sessions.[/]");
            return Ok;
        }

        var actionable = plan.Where(p => p.Declined is null).ToList();
        if (actionable.Count == 0)
        {
            console.MarkupLine("Nothing to do.");
            return plan.Count > 0 ? Error : Ok;
        }

        if (!s.Yes && !Confirm(console, $"Proceed? [y/N] ({verb} {actionable.Count} session(s))"))
        {
            console.MarkupLine("Cancelled; nothing was changed.");
            return Ok;
        }

        var result = services.Cleanup.Apply(actionable);
        foreach (var d in result.Done)
            console.MarkupLine($"[green]Done[/] {Esc(d.Session.Info.Agent)}:{Esc(d.Session.Info.Id)}");
        foreach (var f in result.Failed)
            console.MarkupLine($"[red]Failed[/] {Esc(f.Item.Session.Info.Agent)}:{Esc(f.Item.Session.Info.Id)}: {Esc(f.Error)}");
        console.MarkupLine($"{result.Done.Count} done, {result.Failed.Count} failed, {plan.Count - actionable.Count} declined.");
        return result.Failed.Count > 0 ? Error : Ok;
    }

    private void PrintPlan(IReadOnlyList<CleanupPlanItem> plan, string verb)
    {
        console.MarkupLine($"Plan: {verb} {plan.Count} session(s).");
        foreach (var p in plan)
        {
            var i = p.Session.Info;
            console.MarkupLine($"  {Esc(i.Agent)}:{Esc(i.Id)}  {Esc(i.Title)}  ({Size(i.SizeBytes)})");
            foreach (var path in p.Paths) console.MarkupLine($"      {Esc(path)}");
            if (p.Warning is not null) console.MarkupLine($"      [yellow]Warning:[/] {Esc(p.Warning)}");
            if (p.Declined is not null) console.MarkupLine($"      [red]Declined:[/] {Esc(p.Declined)}");
        }
    }
}
