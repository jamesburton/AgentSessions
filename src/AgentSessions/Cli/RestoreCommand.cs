using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using static AgentSessions.Cli.CliSupport;

namespace AgentSessions.Cli;

public sealed class RestoreCommand(AppServices services, IAnsiConsole console) : Command<RestoreCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ARCHIVED_DIR>")] [Description("An archive folder (contains manifest.json).")]
        public string Dir { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        try
        {
            services.Archive.Restore(Path.GetFullPath(s.Dir));
            console.MarkupLine($"[green]Restored[/] {Esc(s.Dir)}");
            return Ok;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]{Esc(ex.Message)}[/]");
            return Error;
        }
    }
}
