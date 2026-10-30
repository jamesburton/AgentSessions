using System.Reflection;

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

        return 0;
    }
}
