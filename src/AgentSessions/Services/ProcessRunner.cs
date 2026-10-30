using System.Diagnostics;
using System.Text;
using AgentSessions.Model;

namespace AgentSessions.Services;

/// <summary>Runs the child with the console inherited and waits for it.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public int Run(LaunchSpec spec)
    {
        var psi = BuildStartInfo(spec, Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{spec.FileName}'.");
        p.WaitForExit();
        return p.ExitCode;
    }

    internal static ProcessStartInfo BuildStartInfo(LaunchSpec spec, string pathEnv, string pathExtEnv)
    {
        var found = Resolve(spec.FileName, pathEnv, pathExtEnv)
            ?? throw new InvalidOperationException($"Could not find '{spec.FileName}' on PATH.");
        // Absolute, so a relative PATH entry (e.g. ".") can never resolve against the child's working directory.
        var resolved = Path.GetFullPath(found);

        var psi = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = spec.WorkingDirectory };
        var ext = Path.GetExtension(resolved);
        if (OperatingSystem.IsWindows()
            && (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var arg in spec.Args.Append(resolved))
                if (arg.IndexOfAny(['%', '^', '!', '"', '\n', '\r']) >= 0)
                    throw new InvalidOperationException(
                        $"Refusing to launch '{spec.FileName}' via a .cmd shim: an argument contains characters " +
                        "(% ^ ! \" or a newline) that cmd.exe would interpret (arguments or the shim path).");

            var sb = new StringBuilder("/d /s /c \"\"").Append(resolved).Append('"');
            foreach (var arg in spec.Args)
            {
                var trailing = arg.Length - arg.TrimEnd('\\').Length;
                sb.Append(" \"").Append(arg).Append('\\', trailing).Append('"');
            }
            sb.Append('"');
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            psi.FileName = string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec;
            psi.Arguments = sb.ToString();
        }
        else
        {
            psi.FileName = resolved;
            foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    private static string? Resolve(string fileName, string pathEnv, string pathExtEnv)
    {
        // exe/com first, then other runnable extensions in PATHEXT order; .ps1 is never used.
        var exts = pathExtEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => !e.Equals(".ps1", StringComparison.OrdinalIgnoreCase)).ToList();
        exts = exts.Where(IsDirect).Concat(exts.Where(e => !IsDirect(e))).ToList();

        var dirs = Path.IsPathRooted(fileName) || fileName.Contains('/') || fileName.Contains('\\')
            ? new[] { "" }
            : pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in dirs)
        {
            var baseName = dir.Length == 0 ? fileName : Path.Combine(dir, fileName);
            var ownExt = Path.GetExtension(fileName);
            if (ownExt.Length > 0 && !ownExt.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                && (!OperatingSystem.IsWindows() || exts.Contains(ownExt, StringComparer.OrdinalIgnoreCase))
                && File.Exists(baseName))
                return baseName;
            foreach (var e in exts)
                if (File.Exists(baseName + e)) return baseName + e;
            if (!OperatingSystem.IsWindows() && File.Exists(baseName)) return baseName;
        }
        return null;
    }

    private static bool IsDirect(string ext) =>
        ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".com", StringComparison.OrdinalIgnoreCase);
}
