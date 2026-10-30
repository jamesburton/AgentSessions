using System.Text.Json;

namespace AgentSessions.Adapters;

public static class JsonlReader
{
    /// <summary>
    /// Streams JSON objects from a JSONL file one line at a time. Blank and unparseable
    /// lines (including a truncated final line) are skipped. The yielded element is only
    /// valid until the next iteration step; Clone() it to keep it.
    /// </summary>
    public static IEnumerable<JsonElement> ReadTail(string path, int tailBytes = 64 * 1024)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = Math.Max(0, fs.Length - tailBytes);
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

    public static IEnumerable<JsonElement> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536, FileOptions.SequentialScan);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
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
