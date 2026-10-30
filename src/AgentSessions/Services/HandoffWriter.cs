using System.Text;
using AgentSessions.Model;

namespace AgentSessions.Services;

public record HandoffWriteResult(string HandoffPath, string? BackupPath);

/// <summary>
/// Writes <c>&lt;project&gt;/.docs/handoff.md</c> without ever losing an existing one: the old file is first
/// copied to <c>handoff.&lt;yyyy-MM-ddTHHmm&gt;.md</c> (timestamp in UTC; <c>-2</c>, <c>-3</c>... before <c>.md</c>
/// on collision), the copy is verified byte-for-byte, and only then is the new file moved over the original
/// atomically (temp file in the same folder). Backups are never deleted or overwritten.
/// </summary>
public class HandoffWriter(Func<DateTimeOffset> now)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // Test seam: copies source to a NEW destination (must not overwrite an existing file).
    internal Action<string, string> CopyFile { get; init; } = (s, d) => File.Copy(s, d, overwrite: false);

    public HandoffWriteResult Write(string projectPath, string content, SessionInfo source)
    {
        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Project folder does not exist: {projectPath}");

        var docs = Path.Combine(projectPath, ".docs");
        var handoff = Path.Combine(docs, "handoff.md");
        if (Directory.Exists(handoff))
            throw new IOException($"A directory occupies the handoff path: {handoff}");
        Directory.CreateDirectory(docs);

        string? backup = null;
        if (File.Exists(handoff))
        {
            if ((File.GetAttributes(handoff) & FileAttributes.ReadOnly) != 0)
                throw new IOException($"Existing handoff is read-only; nothing written: {handoff}");
            // Probe for a lock (throws IOException) before creating any backup.
            using (new FileStream(handoff, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            backup = CreateVerifiedBackup(docs, handoff);
        }

        var text = Header(source, backup is null ? null : Path.GetFileName(backup)) + content;
        var temp = Path.Combine(docs, $".handoff.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, Utf8NoBom.GetBytes(text));
            File.Move(temp, handoff, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return new HandoffWriteResult(handoff, backup);
    }

    private string CreateVerifiedBackup(string docs, string handoff)
    {
        var stamp = now().UtcDateTime.ToString("yyyy-MM-ddTHHmm");
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(docs, n == 1 ? $"handoff.{stamp}.md" : $"handoff.{stamp}-{n}.md");
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;

            CopyFile(handoff, candidate); // throws on failure; the original is untouched
            if (!File.ReadAllBytes(handoff).AsSpan().SequenceEqual(File.ReadAllBytes(candidate)))
            {
                // Only ever delete the copy we just created and found bad, never anything else.
                try { File.Delete(candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw new IOException($"Backup verification failed; original left untouched: {handoff}");
            }
            return candidate;
        }
    }

    private static string Header(SessionInfo s, string? backupName)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- Handoff written by AgentSessions\n");
        sb.Append($"     Source session: agent={s.Agent} id={s.Id}\n");
        sb.Append($"     Source store: {s.StorePath}\n");
        if (backupName is not null) sb.Append($"     Replaced previous handoff: {backupName}\n");
        sb.Append("-->\n\n");
        return sb.ToString();
    }
}
