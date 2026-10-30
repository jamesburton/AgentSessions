using AgentSessions.Adapters;
using AgentSessions.Config;
using AgentSessions.Model;
using AgentSessions.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AgentSessions.Cli;

/// <summary>Composition root shared by the CLI and (later) the TUI.</summary>
public sealed class AppServices
{
    public AppSettings Settings { get; }
    /// <summary>
    /// Problems found when loading settings.json (unreadable file, or values replaced by defaults); each is shown once,
    /// dim, by list and clean.
    /// </summary>
    public IReadOnlyList<string> SettingsWarnings { get; init; } = [];
    public IReadOnlyDictionary<string, ISessionAdapter> Adapters { get; }
    public Func<DateTimeOffset> Now { get; }
    public SessionCatalog Catalog { get; }
    public LabelStore Labels { get; }
    public ArchiveStore Archive { get; }
    public CleanupService Cleanup { get; }
    public LaunchService Launcher { get; }
    public HandoffWriter Writer { get; }
    public HandoffPolisher Polisher { get; }
    public TransferService Transfer { get; }

    public AppServices(IEnumerable<ISessionAdapter> adapters, AppSettings settings, IProcessRunner runner,
        IClipboard clipboard, Func<DateTimeOffset> now, string archiveRoot, LabelStore labels,
        IModelCaller? polishCaller = null, string? homeDir = null)
    {
        Settings = settings;
        Now = now;
        Labels = labels;   // required (no default path here) so tests can never reach the real data directory
        Adapters = adapters.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
        Catalog = new SessionCatalog(Adapters.Values, settings, now, labels);
        Archive = new ArchiveStore(archiveRoot, homeDir);
        Cleanup = new CleanupService(Adapters, Archive, homeDir);
        Launcher = new LaunchService(runner, clipboard);
        Writer = new HandoffWriter(now);
        Polisher = polishCaller is null ? HandoffPolisher.Create(settings) : new HandoffPolisher(settings, polishCaller);
        Transfer = new TransferService(Writer, Polisher, Launcher);
    }

    public static AppServices CreateDefault(string? homeDir = null, string? dataDir = null)
    {
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dataDir ??= SettingsStore.DefaultDataDir();
        var store = new SettingsStore(Path.Combine(dataDir, "settings.json"));
        var settings = store.Load();
        ISessionAdapter[] adapters = [new ClaudeAdapter(homeDir), new CodexAdapter(homeDir), new CopilotAdapter(homeDir)];
        return new AppServices(adapters, settings, new ProcessRunner(), new SystemClipboard(),
            () => DateTimeOffset.UtcNow, settings.ArchiveRoot ?? Path.Combine(dataDir, "archive"),
            new LabelStore(Path.Combine(dataDir, "labels.json")))
        {
            SettingsWarnings = store.Warnings.ToList()
        };
    }
}

internal sealed class ServicesRegistrar(AppServices services, IAnsiConsole console) : ITypeRegistrar
{
    public ITypeResolver Build() => new ServicesResolver(services, console);
    public void Register(Type service, Type implementation) { }
    public void RegisterInstance(Type service, object implementation) { }
    public void RegisterLazy(Type service, Func<object> factory) { }
}

internal sealed class ServicesResolver(AppServices services, IAnsiConsole console) : ITypeResolver
{
    public object? Resolve(Type? type)
    {
        if (type is null) return null;
        // Spectre asks for optional extension points as IEnumerable<T>; we register none.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type == typeof(AppServices)) return services;
        // Commands take the composition root as their constructor arguments (with the console).
        return type.GetConstructor([typeof(AppServices), typeof(IAnsiConsole)]) is not null
            ? Activator.CreateInstance(type, services, console)
            : type.IsAbstract || type.IsInterface || type.IsGenericType ? null : Activator.CreateInstance(type);
    }
}
