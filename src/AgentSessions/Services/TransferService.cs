using AgentSessions.Model;

namespace AgentSessions.Services;

public record TransferResult(bool Launched, string HandoffPath, string? BackupPath, string? Note);

/// <summary>
/// Builds a handoff from a source session, writes it into the project (backing up any existing one) and
/// launches the target agent on it. The Note (polish and launch messages) is for local display only and is
/// never written into the handoff file.
/// </summary>
public class TransferService(HandoffWriter writer, HandoffPolisher polisher, LaunchService launcher)
{
    public TransferResult Transfer(ISessionAdapter sourceAdapter, SessionInfo source, ISessionAdapter target,
        bool polish, Func<string?> askForFolder)
    {
        var project = source.ProjectPath;
        if (string.IsNullOrEmpty(project) || !Directory.Exists(project))
        {
            project = askForFolder();
            if (string.IsNullOrEmpty(project))
                return Fail("Project folder is unknown and none was chosen; nothing was written or launched.");
            if (!Directory.Exists(project))
                return Fail($"Folder does not exist: {project}; nothing was written or launched.");
        }

        string content;
        string? polishNote = null;
        try
        {
            content = HandoffBuilder.Build(source, sourceAdapter.ReadTranscript(source));
            if (polish) (content, polishNote) = polisher.Apply(content);
        }
        catch (Exception ex)
        {
            return Fail($"Could not build the handoff: {ex.Message}; nothing was written or launched.");
        }

        HandoffWriteResult written;
        try { written = writer.Write(project, content, source); }
        catch (Exception ex)
        {
            return Fail($"Could not write the handoff: {ex.Message}; nothing was launched.");
        }

        var launch = launcher.StartWithHandoff(target, project, written.HandoffPath);
        var note = string.Join(" ", new[] { polishNote, launch.Message }.Where(n => !string.IsNullOrEmpty(n)));
        return new TransferResult(launch.Launched, written.HandoffPath, written.BackupPath, note.Length == 0 ? null : note);
    }

    private static TransferResult Fail(string note) => new(false, "", null, note);
}
