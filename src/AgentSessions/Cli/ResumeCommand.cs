using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using static AgentSessions.Cli.CliSupport;

namespace AgentSessions.Cli;

public sealed class ResumeCommand(AppServices services, IAnsiConsole console) : Command<ResumeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<AGENT>")] [Description("claude, codex or copilot.")]
        public string Agent { get; init; } = "";

        [CommandArgument(1, "<ID>")] [Description("Session id (a unique prefix is enough).")]
        public string Id { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        if (Find(services, console, s.Agent, s.Id) is not var (adapter, session)) return Error;
        var result = services.Launcher.Resume(adapter, session, FolderPrompt(console, services.ConsoleEnvironment));
        if (result.Message is not null)
            console.MarkupLine(result.Launched ? $"[yellow]{Esc(result.Message)}[/]" : $"[red]{Esc(result.Message)}[/]");
        return result.Launched ? Ok : Error;
    }
}
