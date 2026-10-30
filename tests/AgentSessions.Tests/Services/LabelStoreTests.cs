using AgentSessions.Services;
using AgentSessions.Tests.Fixtures;

namespace AgentSessions.Tests.Services;

public class LabelStoreTests : IDisposable
{
    private readonly string _dir = SessionFixtures.TempHome();
    private string LabelsPath => Path.Combine(_dir, "labels.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void LabelStore_roundtrips_and_normalises_key()
    {
        var store = new LabelStore(LabelsPath);
        store.Set("Claude", "Abc-1", "  My label ");

        var loaded = new LabelStore(LabelsPath).Load();
        Assert.Equal("My label", loaded["claude:Abc-1"]);   // agent lowercased, id kept exactly
        Assert.Single(loaded);
        Assert.Equal("claude:Abc-1", LabelStore.KeyOf("CLAUDE", "Abc-1"));
    }

    [Fact]
    public void Set_blank_removes_label()
    {
        var store = new LabelStore(LabelsPath);
        store.Set("claude", "a", "one");
        store.Set("claude", "b", "two");

        store.Set("claude", "a", "   ");
        store.Set("claude", "b", null);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void Sanitize_strips_controls_and_caps_length()
    {
        Assert.Equal("a b c", LabelStore.Sanitize("a\r\nb\t\tc"));
        Assert.Equal("abc", LabelStore.Sanitize("a\u001b\u0007\u007f\u009bbc"));
        Assert.Null(LabelStore.Sanitize(" \t\n\u0001 "));
        Assert.Null(LabelStore.Sanitize(null));
        Assert.Equal(120, LabelStore.Sanitize(new string('x', 500))!.Length);
        // A surrogate pair straddling the cap is dropped whole, never split.
        var capped = LabelStore.Sanitize(new string('x', 119) + "\U0001F600tail")!;
        Assert.Equal(new string('x', 119), capped);
        Assert.Equal(120, LabelStore.Sanitize(new string('x', 121), out var truncated)!.Length);
        Assert.True(truncated);
        LabelStore.Sanitize("short", out truncated);
        Assert.False(truncated);
    }

    [Fact]
    public void Corrupt_file_loads_empty_and_Set_preserves_a_copy_before_writing()
    {
        File.WriteAllText(LabelsPath, "{ not json");
        var store = new LabelStore(LabelsPath);

        Assert.Empty(store.Load());
        store.Set("codex", "x1", "fresh");

        Assert.Equal("fresh", store.Load()["codex:x1"]);
        var copy = Assert.Single(Directory.GetFiles(_dir, "labels.corrupt-*.json"));
        Assert.Equal("{ not json", File.ReadAllText(copy));
        Assert.Matches(@"labels\.corrupt-\d{8}T\d{6}(-\d+)?\.json$", copy);
    }

    [Fact]
    public void Set_never_leaves_a_truncated_file()
    {
        var store = new LabelStore(LabelsPath);
        for (var i = 0; i < 20; i++) store.Set("claude", "id" + i, new string('y', 100));

        // Always the full, parseable file and no stray temp file.
        Assert.Equal(20, store.Load().Count);
        Assert.Equal([LabelsPath], Directory.GetFiles(_dir));
        using (System.Text.Json.JsonDocument.Parse(File.ReadAllText(LabelsPath))) { }
    }

    [WindowsFact]
    public void A_failed_write_keeps_the_previous_file_and_leaves_no_temp()
    {
        var store = new LabelStore(LabelsPath);
        store.Set("claude", "a", "keep me");
        var before = File.ReadAllText(LabelsPath);

        using (new FileStream(LabelsPath, FileMode.Open, FileAccess.Read, FileShare.None))   // blocks the replace
            Assert.ThrowsAny<IOException>(() => store.Set("claude", "b", "lost"));

        Assert.Equal(before, File.ReadAllText(LabelsPath));
        Assert.Equal([LabelsPath], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Write_is_atomic_the_old_file_is_intact_until_the_replace()
    {
        var store = new LabelStore(LabelsPath);
        store.Set("claude", "a", "keep me");
        var before = File.ReadAllBytes(LabelsPath);
        var seen = false;

        // The hook runs after the new content is fully written to the temp file and before the replace.
        var hooked = new LabelStore(LabelsPath)
        {
            BeforeReplace = temp =>
            {
                seen = true;
                Assert.Equal(before, File.ReadAllBytes(LabelsPath));              // target untouched so far
                Assert.Contains("second", File.ReadAllText(temp));                // temp holds the complete new content
                throw new IOException("simulated crash before replace");
            }
        };

        Assert.Throws<IOException>(() => hooked.Set("claude", "b", "second"));

        Assert.True(seen);   // a plain in-place WriteAllText has no such point, so this fails against it
        Assert.Equal(before, File.ReadAllBytes(LabelsPath));
        Assert.Equal([LabelsPath], Directory.GetFiles(_dir));   // temp cleaned up
    }

    [WindowsFact]
    public void An_unreadable_existing_file_aborts_Set_and_is_not_treated_as_corrupt()
    {
        var store = new LabelStore(LabelsPath);
        store.Set("claude", "a", "keep me");
        var before = File.ReadAllBytes(LabelsPath);

        using (new FileStream(LabelsPath, FileMode.Open, FileAccess.Write, FileShare.None))   // read gets an IO error
        {
            Assert.ThrowsAny<IOException>(() => store.Set("claude", "b", "x"));
            Assert.Empty(store.Load());   // Load never throws
        }

        Assert.Equal(before, File.ReadAllBytes(LabelsPath));
        Assert.Equal([LabelsPath], Directory.GetFiles(_dir));   // no corrupt copy, no temp
    }

    [Fact]
    public void Set_reports_the_corrupt_backup_it_made()
    {
        File.WriteAllText(LabelsPath, "[1,2]");   // valid JSON, wrong shape
        var result = new LabelStore(LabelsPath).Set("claude", "a", "x");

        Assert.True(result.Changed);
        Assert.NotNull(result.BackupPath);
        Assert.Equal("[1,2]", File.ReadAllText(result.BackupPath));
        Assert.Null(new LabelStore(LabelsPath).Set("claude", "a", "y").BackupPath);
    }

    [Fact]
    public void Set_that_changes_nothing_does_not_write()
    {
        var store = new LabelStore(LabelsPath);
        Assert.False(store.Set("claude", "a", null).Changed);       // nothing to remove, no file yet
        Assert.False(File.Exists(LabelsPath));

        store.Set("claude", "a", "same");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(LabelsPath, old);
        Assert.False(store.Set("claude", "a", " same ").Changed);   // same after sanitising
        Assert.False(store.Set("claude", "zzz", "").Changed);
        Assert.Equal(old, File.GetLastWriteTimeUtc(LabelsPath));    // never rewritten
    }

    [Fact]
    public void Load_sanitises_hand_edited_values_and_drops_blank_ones()
    {
        File.WriteAllText(LabelsPath, "{\"claude:a\":\"x\\u001b[31my\\n\",\"claude:b\":\"\",\"claude:c\":\" \\t \","
            + "\"claude:d\":\"" + new string('q', 300) + "\"}");

        var loaded = new LabelStore(LabelsPath).Load();

        Assert.Equal("x[31my", loaded["claude:a"]);
        Assert.False(loaded.ContainsKey("claude:b"));
        Assert.False(loaded.ContainsKey("claude:c"));
        Assert.Equal(120, loaded["claude:d"].Length);
    }

    [Fact]
    public void Sanitize_strips_controls_before_collapsing_and_removes_format_and_lone_surrogates()
    {
        Assert.Equal("a b", LabelStore.Sanitize("a\r\u0001\nb"));
        Assert.Equal("ab", LabelStore.Sanitize("a‮​‏⁦⁩b"));   // bidi / zero-width (category Cf)
        Assert.Equal("ab", LabelStore.Sanitize("a\uD800b\uDC00"));                     // lone surrogates
        Assert.Equal("a\U0001F600b", LabelStore.Sanitize("a\U0001F600b"));             // a valid pair is kept
        Assert.Null(LabelStore.Sanitize("​‮"));
    }

    [Fact]
    public void Missing_or_unreadable_path_loads_empty()
    {
        Assert.Empty(new LabelStore(Path.Combine(_dir, "nope", "labels.json")).Load());
        Directory.CreateDirectory(LabelsPath);   // a directory where the file should be
        Assert.Empty(new LabelStore(LabelsPath).Load());
    }
}
