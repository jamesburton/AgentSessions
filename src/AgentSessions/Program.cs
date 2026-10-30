using System.Reflection;
using AgentSessions.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AgentSessions;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            var asm = typeof(Program).Assembly;
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "unknown";
            Console.WriteLine(version);
            return 0;
        }

        return Run(args, AnsiConsole.Console, AppServices.CreateDefault());
    }

    internal static int Run(string[] args, IAnsiConsole console, AppServices services)
    {
        // TODO(Task 14): with no arguments, start the TUI here instead of printing help.
        if (args.Length == 0) args = ["--help"];

        var app = new CommandApp(new ServicesRegistrar(services, console));
        app.Configure(c =>
        {
            c.SetApplicationName("agentsessions");
            c.Settings.Console = console;
            c.Settings.ExceptionHandler = (ex, _) =>
            {
                var usage = ex is CommandParseException or CommandRuntimeException;
                console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return usage ? 2 : 1;
            };
            c.AddCommand<ListCommand>("list").WithDescription("List sessions across agents.");
            c.AddCommand<CleanCommand>("clean").WithDescription("Archive or delete stale sessions (dry run unless --apply).");
            c.AddCommand<ResumeCommand>("resume").WithDescription("Resume a session in its agent.");
            c.AddCommand<TransferCommand>("transfer").WithDescription("Hand a session over to another agent.");
            c.AddCommand<RestoreCommand>("restore").WithDescription("Restore an archived session.");
        });
        return app.Run(args);
    }
}
