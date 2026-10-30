using AgentSessions.Model;

namespace AgentSessions.Services;

public interface IProcessRunner { int Run(LaunchSpec spec); }

public interface IClipboard { void Set(string text); }

public record LaunchResult(bool Launched, string? Message);

public class LaunchService(IProcessRunner runner, IClipboard clipboard)
{
    public LaunchResult Resume(ISessionAdapter a, SessionInfo s, Func<string?> askForFolder)
    {
        var folder = s.ProjectPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            folder = askForFolder();
            if (string.IsNullOrEmpty(folder))
                return new(false, "Project folder is missing and none was chosen; not resuming.");
            if (!Directory.Exists(folder))
                return new(false, $"Folder does not exist: {folder}");
        }

        try
        {
            var code = runner.Run(a.BuildResume(s with { ProjectPath = folder }));
            return new(true, code == 0 ? null : $"{a.Name} exited with code {code}.");
        }
        catch (InvalidOperationException ex)
        {
            return new(false, ex.Message);
        }
    }

    public LaunchResult StartWithHandoff(ISessionAdapter target, string projectPath, string handoffPath)
    {
        if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
            return new(false, $"Project folder does not exist: {projectPath}");

        var prompt = $"Read {handoffPath} and continue the work described there.";
        var withPrompt = target.Capabilities.HasFlag(AdapterCapabilities.LaunchWithPrompt);
        var spec = target.BuildNew(projectPath, withPrompt ? prompt : "");
        if (!withPrompt)
            spec = spec with { Args = spec.Args.Where(x => x.Length > 0).ToList() };

        string? note = null;
        if (!withPrompt)
        {
            try
            {
                clipboard.Set(prompt);
                note = $"{target.Name} cannot take an opening prompt; it was copied to the clipboard (paste it once the agent starts): {prompt}";
            }
            catch (Exception ex)
            {
                note = $"{target.Name} cannot take an opening prompt and copying to the clipboard failed ({ex.Message}); type this once the agent starts: {prompt}";
            }
        }

        try
        {
            var code = runner.Run(spec);
            if (code != 0) note = (note is null ? "" : note + " ") + $"{target.Name} exited with code {code}.";
            return new(true, note);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, ex.Message);
        }
    }
}
