using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using static AgentSessions.Cli.CliSupport;

namespace AgentSessions.Cli;

public sealed class TransferCommand(AppServices services, IAnsiConsole console) : Command<TransferCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<AGENT>")] [Description("Source agent.")]
        public string Agent { get; init; } = "";

        [CommandArgument(1, "<ID>")] [Description("Source session id (a unique prefix is enough).")]
        public string Id { get; init; } = "";

        [CommandOption("--to <AGENT>")] [Description("Target agent to start on the handoff.")]
        public string? To { get; init; }

        [CommandOption("--polish")] [Description("Ask the configured model to polish the handoff (opt-in in settings).")]
        public bool Polish { get; init; }

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(To) ? ValidationResult.Error("--to <AGENT> is required.") : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        if (Find(services, console, s.Agent, s.Id) is not var (source, session)) return Error;
        if (!services.Adapters.TryGetValue(s.To!, out var target))
        {
            console.MarkupLine($"[red]Unknown target agent '{Esc(s.To)}'. Known agents: {Esc(string.Join(", ", services.Adapters.Keys))}.[/]");
            return Error;
        }

        if (string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase))
        {
            console.MarkupLine($"[red]Source and target are both {Esc(source.Name)}. To continue a session in the same agent use: agentsessions resume {Esc(source.Name)} {Esc(session.Id)}[/]");
            return Error;
        }

        var r = services.Transfer.Transfer(source, session, target, s.Polish, FolderPrompt(console));
        if (r.HandoffPath.Length > 0) console.MarkupLine($"Handoff: {Esc(r.HandoffPath)}");
        if (r.BackupPath is not null) console.MarkupLine($"Previous handoff backed up to: {Esc(r.BackupPath)}");
        if (s.Polish && !services.Settings.PolishEnabled)
            console.MarkupLine("[yellow]--polish ignored: polish is not enabled in settings.[/]");
        if (r.Note is not null)
            console.MarkupLine(r.Launched ? $"[yellow]{Esc(r.Note)}[/]" : $"[red]{Esc(r.Note)}[/]");
        return r.Launched ? Ok : Error;
    }
}
