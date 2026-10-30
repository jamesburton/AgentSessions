using System.Text.Json;
using AgentSessions.Model;

namespace AgentSessions.Services;

/// <summary>One path recorded in an archive manifest. Kind is "file", "directory" or "missing".</summary>
public record ManifestEntry(string OriginalPath, string? ArchivedPath, string Kind);

public record ArchiveManifest(string Agent, string Id, DateTimeOffset ArchivedAt, IReadOnlyList<ManifestEntry> Entries);

/// <summary>
/// Moves session files into <c>root/&lt;agent&gt;/&lt;id&gt;/files/&lt;volume&gt;/&lt;path under volume&gt;</c> so that
/// <see cref="Restore"/> can put them back exactly. Nothing is ever overwritten.
/// </summary>
public class ArchiveStore(string root, string? homeDir = null)
{
    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    /// <summary>
    /// Archives <paramref name="paths"/> (existing ones; missing ones are recorded as missing) and returns the archive directory.
    /// Throws, leaving the originals intact, if the archive directory already exists or any move fails
    /// (already moved paths are rolled back).
    /// </summary>
    public string Archive(SessionInfo s, IReadOnlyList<string> paths)
    {
        foreach (var p in paths) PathGuard.Validate(p, homeDir);
        var dir = Path.Combine(Root, Safe(s.Agent), Safe(s.Id));
        if (Directory.Exists(dir) || File.Exists(dir))
            throw new IOException($"Archive already exists: '{dir}'. Refusing to overwrite.");

        // Normalise, de-duplicate, and drop paths nested inside another listed path (they move with it).
        var unique = new List<string>();
        foreach (var p in paths.Select(PathGuard.Normalize))
            if (!unique.Any(u => string.Equals(u, p, PathGuard.Comparison))) unique.Add(p);
        unique = unique.Where(p => !unique.Any(o => !string.Equals(o, p, PathGuard.Comparison) && PathGuard.IsSameOrUnder(p, o))).ToList();

        var entries = new List<ManifestEntry>();
        var moves = new List<(string Src, string Dst)>();
        foreach (var p in unique)
        {
            var isDir = Directory.Exists(p);
            if (!isDir && !File.Exists(p) && !PathGuard.IsReparsePoint(p)) { entries.Add(new ManifestEntry(p, null, "missing")); continue; }
            if (PathGuard.IsSameOrUnder(dir, p))
                throw new IOException($"Archive directory '{dir}' is inside the path being archived: '{p}'.");
            var rel = ArchivedRelative(p);
            entries.Add(new ManifestEntry(p, rel.Replace('\\', '/'), isDir ? "directory" : "file"));
            moves.Add((p, Path.Combine(dir, rel)));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        Directory.CreateDirectory(dir);
        // Manifest first: if the process dies mid-move the archive still says where everything came from.
        File.WriteAllText(Path.Combine(dir, ManifestName),
            JsonSerializer.Serialize(new ArchiveManifest(s.Agent, s.Id, DateTimeOffset.UtcNow, entries), Json));

        var done = new List<(string Src, string Dst)>();
        try
        {
            foreach (var m in moves) { MovePath(m.Src, m.Dst); done.Add(m); }
        }
        catch (Exception ex)
        {
            var rollbackErrors = Rollback(done);
            if (rollbackErrors.Count == 0)
            {
                try { File.Delete(Path.Combine(dir, ManifestName)); TreeOps.RemoveEmptyDirs(dir); } catch { /* leftover empty dirs are harmless */ }
                throw new IOException($"Archive failed and was rolled back; originals are intact: {ex.Message}", ex);
            }
            throw new IOException($"Archive failed ({ex.Message}) and rollback was incomplete ({string.Join("; ", rollbackErrors)}). " +
                                  $"Nothing was deleted; see '{dir}' (manifest.json lists the original locations).", ex);
        }
        return dir;
    }

    /// <summary>Puts an archive back at the exact original locations. Throws <see cref="IOException"/> and changes nothing if any destination exists.</summary>
    public void Restore(string archivedDir)
    {
        var manifestPath = Path.Combine(archivedDir, ManifestName);
        if (!File.Exists(manifestPath)) throw new IOException($"No manifest.json in '{archivedDir}'.");
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllText(manifestPath), Json)
                       ?? throw new IOException("Unreadable manifest.");

        var moves = new List<(string Src, string Dst)>();
        foreach (var e in manifest.Entries.Where(e => e.Kind != "missing"))
        {
            if (e.ArchivedPath is null) throw new IOException($"Manifest entry for '{e.OriginalPath}' has no archived path.");
            var src = Path.GetFullPath(Path.Combine(archivedDir, e.ArchivedPath));
            if (string.Equals(PathGuard.Normalize(src), PathGuard.Normalize(archivedDir), PathGuard.Comparison)
                || !PathGuard.IsSameOrUnder(src, archivedDir))
                throw new IOException($"Manifest entry escapes the archive: '{e.ArchivedPath}'.");
            PathGuard.Validate(e.OriginalPath, homeDir);
            if (!Directory.Exists(src) && !File.Exists(src) && !PathGuard.IsReparsePoint(src))
                throw new IOException($"Archived item is missing: '{src}'.");
            // Exists() is false for broken links, so check the link itself too.
            if (Directory.Exists(e.OriginalPath) || File.Exists(e.OriginalPath) || PathGuard.IsReparsePoint(e.OriginalPath))
                throw new IOException($"Restore conflict: destination already exists: '{e.OriginalPath}'. Nothing was changed.");
            moves.Add((src, e.OriginalPath));
        }

        var done = new List<(string Src, string Dst)>();
        try
        {
            foreach (var m in moves) { MovePath(m.Src, m.Dst); done.Add(m); }
        }
        catch (Exception ex)
        {
            var errs = Rollback(done);
            throw new IOException(errs.Count == 0
                ? $"Restore failed and was rolled back; the archive is intact: {ex.Message}"
                : $"Restore failed ({ex.Message}) and rollback was incomplete ({string.Join("; ", errs)}).", ex);
        }

        File.Delete(manifestPath);
        TreeOps.RemoveEmptyDirs(archivedDir);   // removes the archive dir once empty; never deletes content
    }

    private static List<string> Rollback(List<(string Src, string Dst)> done)
    {
        var errors = new List<string>();
        for (var i = done.Count - 1; i >= 0; i--)
        {
            try { MovePath(done[i].Dst, done[i].Src); }
            catch (Exception e) { errors.Add($"'{done[i].Dst}' -> '{done[i].Src}': {e.Message}"); }
        }
        return errors;
    }

    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (s.Length == 0) throw new ArgumentException($"Unusable agent/id for an archive folder: '{name}'.");
        return s;
    }

    private static string ArchivedRelative(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)!;
        var rest = fullPath[root.Length..];
        string key;
        if (root.StartsWith("\\\\") || root.StartsWith("//"))
            key = "UNC_" + Safe(root.Trim('\\', '/').Replace('\\', '_').Replace('/', '_'));
        else if (root.Length >= 2 && root[1] == ':')
            key = char.ToUpperInvariant(root[0]).ToString();
        else
            key = "_root";
        return Path.Combine("files", key, rest);
    }

    /// <summary>Moves a file or directory (links are moved as links). Falls back to copy + verify + delete across volumes.</summary>
    private static void MovePath(string src, string dst)
    {
        var isDir = Directory.Exists(src);
        if (Directory.Exists(dst) || File.Exists(dst) || PathGuard.IsReparsePoint(dst))
            throw new IOException($"Destination already exists: '{dst}'.");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var sameRoot = string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase);

        if (!isDir)
        {
            if (sameRoot) File.Move(src, dst); else CopyVerifyDeleteFile(src, dst);
            return;
        }
        if (sameRoot)
        {
            try { Directory.Move(src, dst); return; }
            catch (IOException) when (Directory.Exists(src) && !Directory.Exists(dst)) { /* e.g. mount point: fall through */ }
        }
        CopyVerifyDeleteDir(src, dst);
    }

    private static void CopyVerifyDeleteFile(string src, string dst)
    {
        if (PathGuard.IsReparsePoint(src)) throw new IOException($"Cannot move link across volumes: '{src}'.");
        File.Copy(src, dst);
        try
        {
            if (new FileInfo(src).Length != new FileInfo(dst).Length)
                throw new IOException($"Copy verification failed for '{src}'.");
            File.Delete(src);
        }
        catch
        {
            if (File.Exists(src)) { try { File.Delete(dst); } catch { /* best effort; original intact */ } }
            throw;
        }
    }

    private static void CopyVerifyDeleteDir(string src, string dst)
    {
        if (PathGuard.IsReparsePoint(src)) throw new IOException($"Cannot move link across volumes: '{src}'.");
        var before = TreeOps.Measure(src);
        try
        {
            TreeOps.CopyTree(src, dst);
            if (TreeOps.Measure(dst) != before) throw new IOException($"Copy verification failed for '{src}'.");
        }
        catch
        {
            if (Directory.Exists(dst)) { try { TreeOps.DeleteSafely(dst); } catch { /* best effort; original intact */ } }
            throw;
        }
        try { TreeOps.DeleteSafely(src); }
        catch (Exception ex)
        {
            throw new IOException($"Copied '{src}' to '{dst}' and verified it, but could not remove the original completely: {ex.Message}. The copy is intact.", ex);
        }
    }
}
