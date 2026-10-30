using System.Text.Json;

namespace AgentSessions.Adapters;

public static class JsonlReader
{
    /// <summary>Head scans stop after roughly this many characters of file content.</summary>
    public const int HeadScanBytes = 256 * 1024;

    /// <summary>Tail scans only look at this many trailing bytes.</summary>
    public const int TailBytes = 64 * 1024;

    /// <summary>
    /// Streams JSON objects from a JSONL file one line at a time. Blank and unparseable
    /// lines (including a truncated final line) are skipped. The yielded element is only
    /// valid until the next iteration step; Clone() it to keep it. When maxBytes is given,
    /// reading stops once that many characters have been consumed (the line that crosses
    /// the cap is still yielded).
    /// </summary>
    public static IEnumerable<JsonElement> Read(string path, long maxBytes = long.MaxValue)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536, FileOptions.SequentialScan);
        using var reader = new StreamReader(fs);
        string? line;
        long consumed = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            consumed += line.Length + 1;
            if (!string.IsNullOrWhiteSpace(line))
            {
                JsonDocument? doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { doc = null; }
                if (doc is not null)
                    using (doc)
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object) yield return doc.RootElement;
                    }
            }
            if (consumed >= maxBytes) yield break;
        }
    }

    /// <summary>
    /// Newest timestamp (per <paramref name="parseTs"/>) found in the last <see cref="TailBytes"/>
    /// of the file. If the window holds no complete line (e.g. one huge final line) and the file
    /// is larger than the window, falls back to the file's last-write time. Null if nothing found.
    /// </summary>
    public static DateTimeOffset? TailNewest(string path, Func<JsonElement, DateTimeOffset?> parseTs)
    {
        DateTimeOffset? newest = null;
        var any = false;
        foreach (var e in ReadTail(path))
        {
            any = true;
            if (parseTs(e) is { } ts && (newest is null || ts > newest)) newest = ts;
        }
        if (!any && new FileInfo(path).Length > TailBytes)
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return newest;
    }

    private static IEnumerable<JsonElement> ReadTail(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            if (start > 0)
            {
                var nl = text.IndexOf('\n');
                text = nl < 0 ? "" : text[(nl + 1)..]; // drop the partial first line
            }
        }
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument? doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                yield return doc.RootElement;
            }
        }
    }
}
