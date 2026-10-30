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

    /// <summary>
    /// Returns the polished handoff, or the extracted one plus a Note when polish is off-path or fails.
    /// The Note is for local display only (it may contain a stderr excerpt) and must never be written into the handoff file or logs.
    /// </summary>
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

        var polishedLines = polished.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (SourceLines(extracted).Any(l => !polishedLines.Contains(l)))
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
    internal Action<Process>? OnStarted { get; init; }
    internal int MaxStdoutChars { get; init; } = 4_000_000;
    internal int MaxStderrChars { get; init; } = 64_000;

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
        var tasks = new List<Task>();
        try
        {
            OnStarted?.Invoke(p);
            var overflow = new string?[1];
            // Everything below runs concurrently, so a child that never reads stdin cannot block the deadline.
            var stdin = Task.Run(() =>
            {
                try { p.StandardInput.Write(handoffMarkdown); p.StandardInput.Close(); }
                catch (IOException) { /* child exited without reading all input; exit code decides */ }
                catch (ObjectDisposedException) { }
            });
            var stdout = Task.Run(() => ReadCapped(p, p.StandardOutput, MaxStdoutChars, "stdout", overflow));
            var stderr = Task.Run(() => ReadCapped(p, p.StandardError, MaxStderrChars, "stderr", overflow));
            tasks.AddRange([stdin, stdout, stderr]);

            if (!p.WaitForExit(timeout))
            {
                KillTree(p);
                throw new TimeoutException($"polish command timed out after {timeout.TotalSeconds:0} seconds");
            }
            // Process exited; give the pipe readers a bounded time (a grandchild could hold the pipes open).
            if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(10)))
            {
                KillTree(p);
                throw new TimeoutException("polish command output did not close after the process exited");
            }
            if (overflow[0] is { } which)
                throw new InvalidOperationException($"polish command {which} exceeded the size limit");
            if (p.ExitCode != 0)
            {
                var err = stderr.Result.Trim();
                if (err.Length > 200) err = err[..200];
                throw new InvalidOperationException($"polish command exited with code {p.ExitCode}; stderr: {err}");
            }
            return stdout.Result;
        }
        finally
        {
            KillTree(p);
            try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5)); } catch { /* already reported or irrelevant */ }
        }
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone or not killable; nothing more to do */ }
    }

    private static string ReadCapped(Process p, StreamReader reader, int max, string name, string?[] overflow)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            if (sb.Length + n > max)
            {
                overflow[0] = name;
                KillTree(p);
                return sb.ToString();
            }
            sb.Append(buf, 0, n);
        }
        return sb.ToString();
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
