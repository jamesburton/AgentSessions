using System.ComponentModel;
using AgentSessions.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using static AgentSessions.Cli.CliSupport;

namespace AgentSessions.Cli;

/// <summary>Sets or clears a session label. Writes only the tool's own labels.json, never an agent's store.</summary>
public sealed class RenameCommand(AppServices services, IAnsiConsole console) : Command<RenameCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<AGENT>")] [Description("claude, codex or copilot.")]
        public string Agent { get; init; } = "";

        [CommandArgument(1, "<ID>")] [Description("Session id (a unique prefix is enough).")]
        public string Id { get; init; } = "";

        [CommandArgument(2, "[LABEL]")] [Description("The label (several words are joined). Omit, or use --clear, to remove it.")]
        public string[] Label { get; init; } = [];

        [CommandOption("--clear")] [Description("Remove the label.")]
        public bool Clear { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        if (Find(services, console, s.Agent, s.Id) is not var (_, session)) return Error;

        string? label = null;
        var truncated = false;
        if (!s.Clear && s.Label.Length > 0)
            label = LabelStore.Sanitize(string.Join(' ', s.Label), out truncated);

        try
        {
            services.Labels.Set(session.Agent, session.Id, label);
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Could not save the label: {Esc(ex.Message)}[/]");
            return Error;
        }

        var name = $"{Esc(session.Agent)}:{Esc(session.Id)}";
        if (label is null)
        {
            var blank = !s.Clear && s.Label.Length > 0;
            console.MarkupLine(blank
                ? $"Label cleared for {name} (the given label was blank after removing control characters)."
                : $"Label cleared for {name}.");
            return Ok;
        }
        console.MarkupLine($"Label for {name}: {Esc(label)}");
        if (truncated) console.MarkupLine($"[yellow]The label was truncated to {LabelStore.MaxLength} characters.[/]");
        return Ok;
    }
}
