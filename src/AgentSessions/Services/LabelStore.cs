using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSessions.Services;

public interface ILabelSource
{
    IReadOnlyDictionary<string, string> Load();
}

/// <summary>
/// Tool-owned session labels in <c>labels.json</c>, keyed by <c>agent:id</c>. This file is the ONLY thing the label
/// feature writes; no agent session store is ever touched.
/// </summary>
public sealed partial class LabelStore(string path) : ILabelSource
{
    public const int MaxLength = 120;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string KeyOf(string agent, string id) => agent.ToLowerInvariant() + ":" + id;

    /// <summary>Never throws: a missing, unreadable or corrupt file is an empty set of labels.</summary>
    public IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            return Read() ?? new Dictionary<string, string>();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Null when the file does not exist. Throws when it exists but cannot be read or parsed.</summary>
    private Dictionary<string, string>? Read()
    {
        if (!File.Exists(path)) return null;
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Options)
            ?? throw new JsonException("labels.json is not an object.");
        return new Dictionary<string, string>(d, StringComparer.Ordinal);
    }

    /// <summary>Sets (or, when blank, removes) a label. Throws if labels.json cannot be safely rewritten.</summary>
    public void Set(string agent, string id, string? label)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);

        Dictionary<string, string> labels;
        try
        {
            labels = Read() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Never discard what we cannot understand: keep a copy first. If even that fails, the exception
            // propagates and the original file is left exactly as it was.
            PreserveCorrupt(full);
            labels = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var key = KeyOf(agent, id);
        if (Sanitize(label) is { } clean) labels[key] = clean;
        else labels.Remove(key);

        var temp = Path.Combine(dir, $"labels.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(labels, Options), new UTF8Encoding(false));
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void PreserveCorrupt(string full)
    {
        var dir = Path.GetDirectoryName(full)!;
        var stamp = DateTime.Now.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var n = 0; ; n++)
        {
            var copy = Path.Combine(dir, $"labels.corrupt-{stamp}{(n == 0 ? "" : "-" + n)}.json");
            if (File.Exists(copy)) continue;
            File.Copy(full, copy, overwrite: false);
            return;
        }
    }

    public static string? Sanitize(string? label) => Sanitize(label, out _);

    /// <summary>Control characters removed, newlines/tabs become one space, trimmed, capped; blank => null.</summary>
    public static string? Sanitize(string? label, out bool truncated)
    {
        truncated = false;
        if (label is null) return null;
        var s = Whitespace().Replace(label, " ");
        s = string.Concat(s.Where(c => !char.IsControl(c))).Trim();
        if (s.Length > MaxLength)
        {
            truncated = true;
            var cut = MaxLength;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;   // never split a surrogate pair
            s = s[..cut].TrimEnd();
        }
        return s.Length == 0 ? null : s;
    }

    [GeneratedRegex(@"[\r\n\t]+")]
    private static partial Regex Whitespace();
}
