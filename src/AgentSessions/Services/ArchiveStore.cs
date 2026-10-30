using System.Security.Cryptography;
using System.Text.Json;
using AgentSessions.Model;

namespace AgentSessions.Services;

/// <summary>One path recorded in an archive manifest. Kind is "file", "directory" or "missing".</summary>
public record ManifestEntry(string OriginalPath, string? ArchivedPath, string Kind);

public record ArchiveManifest(string Agent, string Id, DateTimeOffset ArchivedAt, IReadOnlyList<ManifestEntry> Entries);

/// <summary>
/// Moves session files into <c>root/&lt;agent&gt;/&lt;id&gt;/files/&lt;volume&gt;/&lt;path under volume&gt;</c> so that
/// <see cref="Restore"/> can put them back exactly. Nothing is ever overwritten.
/// The layout is volume-relative (drive letter, <c>UNC_server_share</c> or <c>_root</c>), NOT home-relative:
/// do not assume archived paths are relative to the home directory; always use manifest.json, which records each
/// original absolute path and its archived location.
/// <para>
/// Moves use the native rename. Only when the OS reports ERROR_NOT_SAME_DEVICE (or the roots differ) does it fall back to
/// copy, verify (size and SHA-256 of every file), then delete originals. Any other failure is a failed item and the original
/// is left untouched. If deleting originals fails part-way, the missing files are copied back (never overwriting) and verified;
/// if even that fails the archive payload and manifest.json are kept and the error says so.
/// </para>
/// </summary>
public class ArchiveStore(string root, string? homeDir = null)
{
    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    // Test seams (internal): force the cross-device fallback, and inject failures into its delete / copy-back steps.
    internal Func<string, string, bool>? ForceNotSameDevice { get; set; }
    internal Action<string>? BeforeDeleteOriginal { get; set; }
    internal Action<string>? BeforeCopyBack { get; set; }
    internal Action<string>? BeforeCopy { get; set; }
    internal Action<string, string>? NativeMove { get; set; }   // replaces the native rename when set

    /// <summary>The original is partially removed and could not be restored; the full copy was kept.</summary>
    private sealed class CompromisedException(string message) : IOException(message);

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
            foreach (var m in moves) { MovePath(m.Src, m.Dst, "Archive"); done.Add(m); }
        }
        catch (CompromisedException ex)
        {
            // Do not roll back, do not delete anything: the manifest and payload are the only complete copy.
            throw new IOException($"Archive failed and data may be incomplete: {ex.Message} Items archived before this one stay archived. " +
                                  $"Nothing else was deleted; the archive and manifest.json are kept at '{dir}'.", ex);
        }
        catch (Exception ex)
        {
            // MovePath only throws a non-Compromised exception when the failing item's original is intact.
            var rollbackErrors = Rollback(done, "Rollback");
            if (rollbackErrors.Count > 0)
                throw new IOException($"Archive failed ({ex.Message}) and rollback was incomplete ({string.Join("; ", rollbackErrors)}). " +
                                      $"Nothing was deleted; the archive and manifest.json are kept at '{dir}' and list the original locations.", ex);
            if (TryRemoveEmptyArchive(dir))
                throw new IOException($"Archive failed and was rolled back; originals are intact: {ex.Message}", ex);
            throw new IOException($"Archive failed and was rolled back; originals are intact, but leftover archive data could not be " +
                                  $"cleaned and is kept (with manifest.json) at '{dir}': {ex.Message}", ex);
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
            foreach (var m in moves) { MovePath(m.Src, m.Dst, "Restore"); done.Add(m); }
        }
        catch (CompromisedException ex)
        {
            throw new IOException($"Restore failed and data may be incomplete: {ex.Message} Items restored before this one stay restored. " +
                                  $"The archive and manifest.json were kept at '{archivedDir}'.", ex);
        }
        catch (Exception ex)
        {
            var errs = Rollback(done, "Rollback");
            throw new IOException(errs.Count == 0
                ? $"Restore failed and was rolled back; the archive is intact (manifest.json kept): {ex.Message}"
                : $"Restore failed ({ex.Message}) and rollback was incomplete ({string.Join("; ", errs)}). The archive and manifest.json were kept at '{archivedDir}'.", ex);
        }

        File.Delete(manifestPath);
        TreeOps.RemoveEmptyDirs(archivedDir);   // removes the archive dir once empty; never deletes content
    }

    private List<string> Rollback(List<(string Src, string Dst)> done, string op)
    {
        var errors = new List<string>();
        for (var i = done.Count - 1; i >= 0; i--)
        {
            try { MovePath(done[i].Dst, done[i].Src, op); }
            catch (Exception e) { errors.Add($"'{done[i].Dst}' -> '{done[i].Src}': {e.Message}"); }
        }
        return errors;
    }

    /// <summary>Removes the archive dir only if nothing but empty folders and manifest.json remain. Never deletes payload.</summary>
    private static bool TryRemoveEmptyArchive(string dir)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir).ToList())
                if (!PathGuard.IsReparsePoint(d)) TreeOps.RemoveEmptyDirs(d);
            var left = Directory.GetFileSystemEntries(dir);
            if (left.Length != 1 || !string.Equals(Path.GetFileName(left[0]), ManifestName, StringComparison.Ordinal)) return false;
            File.Delete(left[0]);
            Directory.Delete(dir, false);
            return true;
        }
        catch { return false; }
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

    private static bool IsNotSameDevice(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is IOException && (unchecked((uint)ex.HResult) == 0x80070011u || (!OperatingSystem.IsWindows() && ex.HResult == 18)))
                return true;
        return false;
    }

    /// <summary>
    /// Moves a file or directory (links are moved as links). Throws a non-Compromised exception only if the source is intact.
    /// Falls back to copy + verify + delete ONLY for ERROR_NOT_SAME_DEVICE or differing volume roots; any other failure propagates.
    /// </summary>
    private void MovePath(string src, string dst, string op)
    {
        var isDir = Directory.Exists(src);
        if (Directory.Exists(dst) || File.Exists(dst) || PathGuard.IsReparsePoint(dst))
            throw new IOException($"Destination already exists: '{dst}'.");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        var forced = ForceNotSameDevice?.Invoke(src, dst) == true;
        var sameRoot = string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase);
        if (!forced && sameRoot)
        {
            try
            {
                if (NativeMove is not null) NativeMove(src, dst);
                else if (isDir) Directory.Move(src, dst);
                else File.Move(src, dst);
                return;
            }
            catch (IOException ex) when (IsNotSameDevice(ex)) { /* fall back below */ }
        }
        BeforeCopy?.Invoke(src);
        if (isDir) CopyVerifyDeleteDir(src, dst, op); else CopyVerifyDeleteFile(src, dst);
    }

    private static string Hash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private void CopyVerifyDeleteFile(string src, string dst)
    {
        if (PathGuard.IsReparsePoint(src)) throw new IOException($"Cannot move link across volumes: '{src}'. Original untouched.");
        var info = new FileInfo(src);
        var (len, mtime) = (info.Length, info.LastWriteTimeUtc);
        var log = new TreeOps.CopyLog();
        try
        {
            TreeOps.CopyFileNew(src, dst, log);
            if (new FileInfo(dst).Length != len || Hash(src) != Hash(dst))
                throw new IOException($"Copy verification failed for '{src}'; original untouched.");
            var now = new FileInfo(src);
            if (now.Length != len || now.LastWriteTimeUtc != mtime)
                throw new IOException($"'{src}' changed while it was being copied; original untouched.");
        }
        catch { log.Cleanup(); throw; }

        try
        {
            BeforeDeleteOriginal?.Invoke(src);
            if (File.GetAttributes(src).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(src, FileAttributes.Normal);
            File.Delete(src);
        }
        catch (Exception ex)
        {
            // A single-file delete is atomic. If the source is already gone, the file is verified in the destination: success.
            if (!File.Exists(src)) return;
            log.Cleanup();   // original is still whole, so drop only the copy this call created
            throw new IOException($"Could not remove the original '{src}' after copying; original left in place: {ex.Message}", ex);
        }
    }

    private sealed record Entry(string Rel, bool IsDir, long Length, DateTime MtimeUtc);

    private static List<Entry> Snapshot(string dir)
    {
        var list = new List<Entry>();
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Cannot copy across volumes: '{e.FullName}' is a link. Original untouched.");
            list.Add(e is FileInfo f
                ? new Entry(Path.GetRelativePath(dir, e.FullName), false, f.Length, f.LastWriteTimeUtc)
                : new Entry(Path.GetRelativePath(dir, e.FullName), true, 0, default));
        }
        return list;
    }

    private void CopyVerifyDeleteDir(string src, string dst, string op)
    {
        if (PathGuard.IsReparsePoint(src)) throw new IOException($"Cannot move link across volumes: '{src}'. Original untouched.");
        List<Entry> snap;
        var log = new TreeOps.CopyLog();
        try
        {
            snap = Snapshot(src);
            TreeOps.CopyTree(src, dst, log);
            if (Snapshot(dst).Count != snap.Count) throw new IOException($"Copy verification failed for '{src}' (entry count); original untouched.");
            foreach (var e in snap)
            {
                var d = Path.Combine(dst, e.Rel);
                if (e.IsDir ? !Directory.Exists(d) : new FileInfo(d).Length != e.Length || Hash(Path.Combine(src, e.Rel)) != Hash(d))
                    throw new IOException($"Copy verification failed for '{Path.Combine(src, e.Rel)}'; original untouched.");
            }
        }
        catch { log.Cleanup(); throw; }

        try { DeleteOriginals(src, snap); return; }
        catch (Exception deleteError) { RestoreOriginal(src, dst, snap, deleteError, log, op); }
    }

    private void DeleteOriginals(string src, List<Entry> snap)
    {
        foreach (var e in snap.Where(e => !e.IsDir).OrderByDescending(e => e.Rel.Length))
        {
            var p = Path.Combine(src, e.Rel);
            BeforeDeleteOriginal?.Invoke(p);
            var fi = new FileInfo(p);
            if (fi.Length != e.Length || fi.LastWriteTimeUtc != e.MtimeUtc)
                throw new IOException($"'{p}' changed after it was copied; not deleting it.");
            if (fi.Attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(p, FileAttributes.Normal);
            File.Delete(p);
        }
        foreach (var e in snap.Where(e => e.IsDir).OrderByDescending(e => e.Rel.Length))
            Directory.Delete(Path.Combine(src, e.Rel), false);
        Directory.Delete(src, false);
    }

    /// <summary>Delete phase failed: copy back what is missing (never overwriting), verify, then drop the copy. Always throws.</summary>
    private void RestoreOriginal(string src, string dst, List<Entry> snap, Exception deleteError, TreeOps.CopyLog log, string op)
    {
        try
        {
            Directory.CreateDirectory(src);
            foreach (var e in snap.Where(e => e.IsDir)) Directory.CreateDirectory(Path.Combine(src, e.Rel));
            foreach (var e in snap.Where(e => !e.IsDir))
            {
                var o = Path.Combine(src, e.Rel);
                if (File.Exists(o)) continue;
                BeforeCopyBack?.Invoke(o);
                File.Copy(Path.Combine(dst, e.Rel), o, false);
            }
            foreach (var e in snap.Where(e => !e.IsDir))
                if (Hash(Path.Combine(src, e.Rel)) != Hash(Path.Combine(dst, e.Rel)))
                    throw new IOException($"'{Path.Combine(src, e.Rel)}' does not match the copy");
        }
        catch (Exception copyBackError)
        {
            throw new CompromisedException($"The original '{src}' is partially removed (delete error: {deleteError.Message}) and could not be restored " +
                                           $"({copyBackError.Message}). The full copy is kept at '{dst}'.");
        }
        // Remove only the entries this copy created (never a recursive delete of a path someone else may have populated).
        if (!log.Cleanup())
            throw new IOException($"{op} failed; originals restored and verified whole at '{src}' ({deleteError.Message}), " +
                                  $"but some copied files under '{dst}' could not be removed; they are kept.", deleteError);
        throw new IOException($"{op} failed; originals restored and verified whole at '{src}': {deleteError.Message}", deleteError);
    }
}
