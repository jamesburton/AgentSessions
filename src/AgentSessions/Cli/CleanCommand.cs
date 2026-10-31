using System.ComponentModel;
using AgentSessions.Config;
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
            if (OlderThan is < 0 or > CleanupSelection.MaxDays)
                return ValidationResult.Error($"--older-than must be between 0 and {CleanupSelection.MaxDays} days.");
            if (Ids is not null && CleanupSelection.ParseIds(Ids).Length == 0)
                return ValidationResult.Error("--ids needs at least one agent:id pair.");
            if (Ids is not null && OlderThan is not null)
                return ValidationResult.Error("--ids and --older-than cannot be combined; use one or the other.");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        PrintSettingsWarnings(services, console);
        var action = s.Delete ? CleanupAction.Delete : CleanupAction.Archive;

        // Belt and braces for a scripted delete: settings.json is normalised on load (staleDays >= 1), but settings can
        // also be supplied directly. A sub-day threshold the user did not type would make every session "stale".
        if (action == CleanupAction.Delete && s.Apply && s.Ids is null && s.OlderThan is null
            && services.Settings.StaleDays < SettingsStore.MinStaleDays)
        {
            console.MarkupLine($"[red]Refused: the stale threshold ({services.Settings.StaleDays} days) is below 1 day, so every session would be selected for deletion. Pass --older-than DAYS explicitly. Nothing was changed.[/]");
            return Error;
        }

        var all = services.Catalog.List().Sessions;
        var selection = CleanupSelection.Select(all, s.Ids is null ? null : CleanupSelection.ParseIds(s.Ids),
            s.OlderThan, services.Now());
        if (selection.UnknownIds.Count > 0)
        {
            console.MarkupLine($"[red]No session found for: {Esc(string.Join(", ", selection.UnknownIds))} (expected agent:id, exact id). Nothing was changed.[/]");
            return Error;
        }

        var plan = services.Cleanup.Plan(selection.Selected, action);
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

        if (!s.Yes)
        {
            var warned = actionable.Count(p => p.Warning is not null);
            var what = action == CleanupAction.Delete ? "PERMANENTLY delete" : "archive";
            var question = $"About to {what} {actionable.Count} session(s)"
                + (action == CleanupAction.Delete ? " (their session files only; prompt history and agent backups may remain)" : "")
                + (warned > 0 ? $", {warned} with active-session warnings" : "")
                + ". Proceed? [y/N]";
            switch (Confirm(console, services.ConsoleEnvironment, question))
            {
                case ConfirmResult.Refused:
                    console.MarkupLine("[red]Refused: no interactive confirmation is possible here, so nothing was changed. Pass --yes to proceed without a prompt.[/]");
                    return Error;
                case ConfirmResult.No:
                    console.MarkupLine("Cancelled; nothing was changed.");
                    return Ok;
            }
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
            console.MarkupLine($"  {Esc(i.Agent)}:{Esc(i.Id)}  {Esc(p.Session.DisplayTitle)}  ({Size(i.SizeBytes)})");
            foreach (var path in p.Paths) console.MarkupLine($"      {Esc(path)}");
            if (p.Warning is not null) console.MarkupLine($"      [yellow]Warning:[/] {Esc(p.Warning)}");
            if (p.Declined is not null) console.MarkupLine($"      [red]Declined:[/] {Esc(p.Declined)}");
        }
    }
}
