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

        return RunMain(args, AnsiConsole.Console, () => AppServices.CreateDefault());
    }

    internal static int RunMain(string[] args, IAnsiConsole console, Func<AppServices> create)
    {
        AppServices services;
        try { services = create(); }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]AgentSessions could not start: {CliSupport.Esc(ex.Message)}[/]");
            return 1;
        }
        return Run(args, console, services);
    }

    internal static int Run(string[] args, IAnsiConsole console, AppServices services)
    {
        if (args.Length == 0)
        {
            // The TUI is the primary interface; without a terminal fall back to the command help.
            if (CliSupport.IsInteractive(console))
                return new Tui.MainScreen(console, services, new Tui.ConsoleKeySource()).Run();
            args = ["--help"];
        }

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
            c.AddCommand<RenameCommand>("rename").WithDescription("Label a session (stored by this tool only; the agent's own data is untouched).");
            c.AddCommand<CleanCommand>("clean").WithDescription("Archive or delete stale sessions (dry run unless --apply).");
            c.AddCommand<ResumeCommand>("resume").WithDescription("Resume a session in its agent.");
            c.AddCommand<TransferCommand>("transfer").WithDescription("Hand a session over to another agent.");
            c.AddCommand<RestoreCommand>("restore").WithDescription("Restore an archived session.");
        });
        return app.Run(args);
    }
}
