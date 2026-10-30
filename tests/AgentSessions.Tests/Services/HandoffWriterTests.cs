using System.Text;
using AgentSessions.Model;
using AgentSessions.Services;
using Xunit;

namespace AgentSessions.Tests.Services;

public class HandoffWriterTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 10, 30, 12, 34, 10, TimeSpan.Zero);
    private static readonly SessionInfo Src = new("codex", "sess-42", "C:/p/app", "t", T, 5, "C:/store/sess-42.jsonl");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hw " + Guid.NewGuid().ToString("N"));

    public HandoffWriterTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Handoff(string? project = null) => Path.Combine(project ?? _root, ".docs", "handoff.md");

    private string SeedHandoff(string text, string? project = null)
    {
        var p = Handoff(project);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Creates_docs_folder_and_file_when_none_exists()
    {
        var r = new HandoffWriter(() => T).Write(_root, "BODY\n", Src);
        Assert.Null(r.BackupPath);
        Assert.Equal(Handoff(), r.HandoffPath);
        Assert.Contains("BODY\n", File.ReadAllText(r.HandoffPath));
    }

    [Fact]
    public void Existing_handoff_is_backed_up_unchanged_before_write()
    {
        var p = SeedHandoff("my hand-written notes\r\nline2\n");
        var original = File.ReadAllBytes(p);
        var r = new HandoffWriter(() => T).Write(_root, "NEW\n", Src);
        Assert.NotNull(r.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(r.BackupPath!));
        Assert.Equal("handoff.2026-10-30T1234.md", Path.GetFileName(r.BackupPath));
        Assert.Contains("NEW\n", File.ReadAllText(p));
        Assert.DoesNotContain("hand-written", File.ReadAllText(p));
    }

    [Fact]
    public void New_handoff_links_backup_and_source_session()
    {
        SeedHandoff("old");
        var r = new HandoffWriter(() => T).Write(_root, "NEW\n", Src);
        var text = File.ReadAllText(r.HandoffPath);
        Assert.Contains("Replaced previous handoff: handoff.2026-10-30T1234.md", text);
        Assert.Contains("codex", text);
        Assert.Contains("sess-42", text);
        Assert.Contains("C:/store/sess-42.jsonl", text);
        Assert.DoesNotContain(_root, text.Replace(Src.StorePath, ""));
    }

    [Fact]
    public void Two_writes_in_the_same_minute_do_not_overwrite_the_first_backup()
    {
        var p = SeedHandoff("v0\n");
        var w = new HandoffWriter(() => T);
        var b1 = w.Write(_root, "v1\n", Src).BackupPath!;
        var b2 = w.Write(_root, "v2\n", Src).BackupPath!;
        var b3 = w.Write(_root, "v3\n", Src).BackupPath!;
        Assert.Equal(3, new[] { b1, b2, b3 }.Distinct().Count());
        Assert.Equal("v0\n", File.ReadAllText(b1));
        Assert.Contains("v1\n", File.ReadAllText(b2));
        Assert.Contains("v2\n", File.ReadAllText(b3));
        Assert.Contains("v3\n", File.ReadAllText(p));
        Assert.EndsWith("-2.md", b2);
        Assert.EndsWith("-3.md", b3);
    }

    [Fact]
    public void Missing_project_path_throws_and_creates_nothing()
    {
        var missing = Path.Combine(_root, "nope");
        Assert.Throws<DirectoryNotFoundException>(() => new HandoffWriter(() => T).Write(missing, "x", Src));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Backup_copy_failure_leaves_original_and_writes_nothing()
    {
        var p = SeedHandoff("precious\n");
        var original = File.ReadAllBytes(p);
        var w = new HandoffWriter(() => T) { CopyFile = (_, _) => throw new IOException("disk full") };
        Assert.Throws<IOException>(() => w.Write(_root, "NEW\n", Src));
        Assert.Equal(original, File.ReadAllBytes(p));
        Assert.Equal(["handoff.md"], Directory.GetFiles(Path.GetDirectoryName(p)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Corrupt_backup_fails_verification_and_leaves_original()
    {
        var p = SeedHandoff("precious\n");
        var original = File.ReadAllBytes(p);
        var w = new HandoffWriter(() => T) { CopyFile = (_, d) => File.WriteAllText(d, "trunc") };
        Assert.Throws<IOException>(() => w.Write(_root, "NEW\n", Src));
        Assert.Equal(original, File.ReadAllBytes(p));
        Assert.Equal(["handoff.md"], Directory.GetFiles(Path.GetDirectoryName(p)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Backup_name_occupied_by_directory_is_skipped_not_overwritten()
    {
        var p = SeedHandoff("precious\n");
        Directory.CreateDirectory(Path.Combine(_root, ".docs", "handoff.2026-10-30T1234.md"));
        var r = new HandoffWriter(() => T).Write(_root, "NEW\n", Src);
        Assert.EndsWith("-2.md", r.BackupPath);
        Assert.Equal("precious\n", File.ReadAllText(r.BackupPath!));
    }

    [Fact]
    public void Read_only_existing_handoff_throws_without_writing()
    {
        var p = SeedHandoff("precious\n");
        File.SetAttributes(p, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<IOException>(() => new HandoffWriter(() => T).Write(_root, "NEW\n", Src));
            Assert.Equal("precious\n", File.ReadAllText(p));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(p)!));
        }
        finally { File.SetAttributes(p, FileAttributes.Normal); }
    }

    [Fact]
    public void Locked_existing_handoff_throws_without_writing()
    {
        var p = SeedHandoff("precious\n");
        using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => new HandoffWriter(() => T).Write(_root, "NEW\n", Src));
        Assert.Equal("precious\n", File.ReadAllText(p));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(p)!));
    }

    [Fact]
    public void Path_with_spaces_and_non_ascii_works_utf8_no_bom_lf_preserved()
    {
        var proj = Path.Combine(_root, "my projekt é日本");
        Directory.CreateDirectory(proj);
        SeedHandoff("old", proj);
        var r = new HandoffWriter(() => T).Write(proj, "héllo 日本\nline\n", Src);
        var bytes = File.ReadAllBytes(r.HandoffPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith("héllo 日本\nline\n", text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void No_temp_files_left_after_success()
    {
        SeedHandoff("old");
        var r = new HandoffWriter(() => T).Write(_root, "NEW\n", Src);
        var names = Directory.GetFiles(Path.GetDirectoryName(r.HandoffPath)!).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["handoff.2026-10-30T1234.md", "handoff.md"], names);
    }
}
