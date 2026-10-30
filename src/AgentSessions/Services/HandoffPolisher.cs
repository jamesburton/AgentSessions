using System.Diagnostics;
using System.Text;
using AgentSessions.Config;
using AgentSessions.Model;

namespace AgentSessions.Services;

public interface IModelCaller
{
    string Polish(string handoffMarkdown);
}

/// <summary>Opt-in polish of an extracted handoff; any failure falls back to the extracted text with a note.</summary>
public sealed class HandoffPolisher(AppSettings settings, IModelCaller? caller)
{
    internal IModelCaller? Caller => caller;

    public static HandoffPolisher Create(AppSettings settings) =>
        new(settings, string.IsNullOrWhiteSpace(settings.PolishCommand)
            ? null
            : new CommandModelCaller(settings.PolishCommand, CommandModelCaller.DefaultTimeout));

    public (string Content, string? Note) Apply(string extracted)
    {
        if (!settings.PolishEnabled) return (extracted, null);
        if (caller is null)
            return (extracted, "Polish is enabled but no polish command is configured; using the unpolished handoff.");

        string polished;
        try { polished = caller.Polish(extracted); }
        catch (Exception ex)
        {
            return (extracted, $"Polish failed ({ex.GetType().Name}: {ex.Message}); using the unpolished handoff.");
        }

        if (string.IsNullOrWhiteSpace(polished))
            return (extracted, "Polish returned no text; using the unpolished handoff.");

        if (SourceLines(extracted).Any(l => !polished.Contains(l, StringComparison.Ordinal)))
            return (extracted, "Polished text lost the source reference (the ## Source lines); using the unpolished handoff.");

        return (polished, null);
    }

    private static IEnumerable<string> SourceLines(string md)
    {
        var inSource = false;
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSource = line.Equals("## Source", StringComparison.Ordinal);
                continue;
            }
            if (inSource && line.Length > 0) yield return line;
        }
    }
}

/// <summary>Runs a user-configured command: handoff Markdown on stdin, polished Markdown on stdout.</summary>
public sealed class CommandModelCaller(string? command, TimeSpan timeout) : IModelCaller
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public string Polish(string handoffMarkdown)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("no polish command configured");
        var parts = SplitCommandLine(command);
        if (parts.Count == 0) throw new InvalidOperationException("no polish command configured");

        var psi = ProcessRunner.BuildStartInfo(
            new LaunchSpec(parts[0], parts.Skip(1).ToList(), Environment.CurrentDirectory),
            Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD");
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{parts[0]}'.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            p.StandardInput.Write(handoffMarkdown);
            p.StandardInput.Close();
        }
        catch (IOException) { /* child exited without reading all input; exit code decides */ }

        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
            throw new TimeoutException($"polish command timed out after {timeout.TotalSeconds:0} seconds");
        }
        p.WaitForExit(); // flush async readers
        if (p.ExitCode != 0)
        {
            var err = stderr.Result.Trim();
            if (err.Length > 200) err = err[..200];
            throw new InvalidOperationException($"polish command exited with code {p.ExitCode}: {err}");
        }
        return stdout.Result;
    }

    /// <summary>Splits on whitespace; double quotes group (and are removed); no shell expansion.</summary>
    internal static List<string> SplitCommandLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var has = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; has = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (has) { result.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else { sb.Append(c); has = true; }
        }
        if (has) result.Add(sb.ToString());
        return result;
    }
}
