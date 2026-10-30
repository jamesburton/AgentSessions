using System.Globalization;
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
public sealed record LabelWrite(bool Changed, string? BackupPath);

public sealed partial class LabelStore(string path) : ILabelSource
{
    public const int MaxLength = 120;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string KeyOf(string agent, string id) => agent.ToLowerInvariant() + ":" + id;

    /// <summary>
    /// Never throws: a missing, unreadable or corrupt file is an empty set of labels. Values are sanitised (the file
    /// may be hand-edited) and entries that end up blank are dropped.
    /// </summary>
    public IReadOnlyDictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (k, v) in Read() ?? [])
                if (Sanitize(v) is { } clean) result[k] = clean;
        }
        catch (Exception) { result.Clear(); }
        return result;
    }

    /// <summary>
    /// Raw entries; null when the file does not exist. Throws <see cref="JsonException"/> when the content is not a
    /// string-to-string object (corrupt), and IO/permission exceptions when it cannot be read (NOT corruption).
    /// </summary>
    private Dictionary<string, string>? Read()
    {
        if (!File.Exists(path)) return null;
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Options)
            ?? throw new JsonException("labels.json is not an object.");
        return new Dictionary<string, string>(d, StringComparer.Ordinal);
    }

    /// <summary>Test seam: called with the temp path once it is fully written and before it replaces labels.json.</summary>
    internal Action<string>? BeforeReplace { get; init; }

    /// <summary>
    /// Sets (or, when blank, removes) a label and writes nothing when that changes nothing. Throws if labels.json
    /// cannot be read or safely rewritten; an unreadable (IO error) file is left exactly as it was.
    /// </summary>
    public LabelWrite Set(string agent, string id, string? label)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;

        Dictionary<string, string> labels;
        var corrupt = false;
        try
        {
            labels = Read() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            corrupt = true;   // only unparseable content counts as corruption; IO errors propagate
            labels = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var key = KeyOf(agent, id);
        var clean = Sanitize(label);
        var had = labels.TryGetValue(key, out var existing);
        if (!corrupt && (clean is null ? !had : had && existing == clean)) return new LabelWrite(false, null);

        Directory.CreateDirectory(dir);
        string? backup = null;
        if (corrupt)
            backup = PreserveCorrupt(full);   // if even the copy fails the exception propagates; original untouched

        if (clean is not null) labels[key] = clean;
        else labels.Remove(key);

        var temp = Path.Combine(dir, $"labels.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(labels, Options), new UTF8Encoding(false));
            BeforeReplace?.Invoke(temp);
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
            return new LabelWrite(true, backup);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string PreserveCorrupt(string full)
    {
        var dir = Path.GetDirectoryName(full)!;
        var stamp = DateTime.Now.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var n = 0; ; n++)
        {
            var copy = Path.Combine(dir, $"labels.corrupt-{stamp}{(n == 0 ? "" : "-" + n)}.json");
            if (File.Exists(copy)) continue;
            File.Copy(full, copy, overwrite: false);
            return copy;
        }
    }

    public static string? Sanitize(string? label) => Sanitize(label, out _);

    /// <summary>Control characters removed, newlines/tabs become one space, trimmed, capped; blank => null.</summary>
    public static string? Sanitize(string? label, out bool truncated)
    {
        truncated = false;
        if (label is null) return null;
        // Strip controls, format/bidi characters and lone surrogates FIRST (keeping CR, LF, TAB), then collapse newline/tab runs.
        var sb = new StringBuilder(label.Length);
        for (var i = 0; i < label.Length; i++)
        {
            var c = label[i];
            if (char.IsHighSurrogate(c) && i + 1 < label.Length && char.IsLowSurrogate(label[i + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(label, i) != UnicodeCategory.Format) sb.Append(c).Append(label[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c)) { }
            else if (c is '\r' or '\n' or '\t') sb.Append(c);
            else if (!char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format) sb.Append(c);
        }
        var s = Whitespace().Replace(sb.ToString(), " ").Trim();
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
