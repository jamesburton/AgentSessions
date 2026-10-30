using System.Runtime.InteropServices;

namespace AgentSessions.Services;

/// <summary>Guards shared by archive and delete: refuse paths that must never be removed.</summary>
internal static class PathGuard
{
    public static StringComparison Comparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Full path without trailing separators (except for a root, which is kept intact).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= root.Length) return full;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsSameOrUnder(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        if (string.Equals(c, p, Comparison)) return true;
        var prefix = p.EndsWith(Path.DirectorySeparatorChar) || p.EndsWith(Path.AltDirectorySeparatorChar)
            ? p : p + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, Comparison);
    }

    /// <summary>Throws if the path is empty, relative, a filesystem root, or the home directory (or one of its ancestors).</summary>
    public static void Validate(string? path, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Refusing to touch an empty path.");
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"Refusing to touch a relative path: '{path}'.");
        var full = Normalize(path);
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= root.Length)
            throw new InvalidOperationException($"Refusing to touch a filesystem root: '{path}'.");
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home) && IsSameOrUnder(home, full))
            throw new InvalidOperationException($"Refusing to touch the home directory (or a parent of it): '{path}'.");
    }

    public static bool IsReparsePoint(string path)
    {
        // File.GetAttributes throws for missing paths (FileInfo.Attributes would return -1, i.e. every flag set).
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

internal static class TreeOps
{
    /// <summary>Deletes a file, link or directory tree. Links are removed, never followed.</summary>
    public static void DeleteSafely(string path)
    {
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReparsePoint))
        {
            // Remove the link itself, not its target.
            if (attrs.HasFlag(FileAttributes.Directory)) Directory.Delete(path, false);
            else File.Delete(path);
            return;
        }
        if (!attrs.HasFlag(FileAttributes.Directory))
        {
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList())
            DeleteSafely(child);
        Directory.Delete(path, false);
    }

    /// <summary>Counts files, directories and bytes in a tree; throws if it contains a link.</summary>
    public static (long Files, long Dirs, long Bytes) Measure(string dir)
    {
        long files = 0, dirs = 0, bytes = 0;
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Cannot copy across volumes: '{e.FullName}' is a link.");
            if (e is FileInfo f) { files++; bytes += f.Length; } else dirs++;
        }
        return (files, dirs, bytes);
    }

    public static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Removes empty directories bottom-up; anything non-empty is left alone.</summary>
    public static void RemoveEmptyDirs(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir).ToList())
            if (!PathGuard.IsReparsePoint(d)) RemoveEmptyDirs(d);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false);
    }
}
