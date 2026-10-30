using System.ComponentModel;
using AgentSessions.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using static AgentSessions.Cli.CliSupport;

namespace AgentSessions.Cli;

public sealed class ListCommand(AppServices services, IAnsiConsole console) : Command<ListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--agent <AGENT>")] [Description("Only this agent (claude, codex, copilot).")]
        public string? Agent { get; init; }

        [CommandOption("--project <TEXT>")] [Description("Only sessions whose project path contains this text.")]
        public string? Project { get; init; }

        [CommandOption("--older-than <DAYS>")] [Description("Only sessions inactive for more than this many days.")]
        public int? OlderThan { get; init; }

        [CommandOption("--stale")] [Description("Only stale sessions (older than the stale-days setting).")]
        public bool Stale { get; init; }

        public override ValidationResult Validate() =>
            OlderThan is < 0 or > CleanupSelection.MaxDays
                ? ValidationResult.Error($"--older-than must be between 0 and {CleanupSelection.MaxDays} days.")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        PrintSettingsWarnings(services, console);
        if (s.Agent is not null && !services.Adapters.ContainsKey(s.Agent))
        {
            console.MarkupLine($"[red]Unknown agent '{Esc(s.Agent)}'. Known agents: {Esc(string.Join(", ", services.Adapters.Keys))}.[/]");
            return Usage;
        }
        TimeSpan? older = s.OlderThan is { } d ? TimeSpan.FromDays(d) : null;
        var (sessions, skipped) = services.Catalog.List(new SessionFilter(s.Agent, s.Project, older));
        var rows = s.Stale ? sessions.Where(x => x.IsStale).ToList() : sessions.ToList();
        var now = services.Now();

        var table = new Table();
        foreach (var c in new[] { "AGENT", "ID", "PROJECT", "LAST ACTIVE", "SIZE", "TITLE" }) table.AddColumn(c);
        foreach (var r in rows)
        {
            var title = Esc(r.DisplayTitle) + (r.IsStale ? " " + Esc("[stale]") : "");
            table.AddRow(Esc(r.Info.Agent), Esc(r.Info.Id), Esc(r.Info.ProjectPath ?? "(unknown)"),
                Age(now, r.Info.LastActivity), Size(r.Info.SizeBytes), title);
        }
        console.Write(table);
        console.MarkupLine($"{rows.Count} session(s).");
        if (skipped.Count > 0)
            console.MarkupLine($"[yellow]{skipped.Count} session(s) skipped (unreadable).[/]");
        return Ok;
    }
}
