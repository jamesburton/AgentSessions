using System.Text.Json;

namespace AgentSessions.Adapters;

public static class JsonlReader
{
    /// <summary>
    /// Streams JSON objects from a JSONL file one line at a time. Blank and unparseable
    /// lines (including a truncated final line) are skipped. The yielded element is only
    /// valid until the next iteration step; Clone() it to keep it.
    /// </summary>
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
