using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

internal static class MigrationChecks
{
    public static void Run()
    {
        using var folder = new TemporaryDirectory();
        var legacyPath = Path.Combine(folder.Path, "旧数据库.sqlite3");
        var owner = "学生甲";
        var legacyOwner = Hash(owner)[..24];
        using var old = new CoreStore(legacyPath);
        old.Put(legacyOwner, "course", "c", new { id = "c", name = "旧课", semester = "term", old_field = true });
        old.Put(legacyOwner, "lesson", "l", new { id = "l", course_id = "c", title = "旧课堂", date = "2026-11-01T08:00:00+08:00" });
        var slides = new[] { new { id = "s", start = 1, url = "https://resource.msa.buaa.edu.cn/test.png" } };
        old.Put(legacyOwner, "materials", "l", new { lesson_id = "l", slides });
        var legacyDigest = Hash("""[{"id": "s", "start": 1, "url": "https://resource.msa.buaa.edu.cn/test.png"}]""");
        old.Put(legacyOwner, "slides_pdf", "l", new { digest = legacyDigest });
        old.Put(legacyOwner, "automation", "c", new { enabled = true, auto_analyze = true });
        old.Put(legacyOwner, "job", "j", new { kind = "analyze", target = "l", status = "running", message = "running", created_at = "2026-11-01T00:00:00Z" });
        old.Put("unrelated", "course", "other", new { id = "other" });
        old.SetSetting("model", new { api_key = "SYNTHETIC-SECRET" });
        var oldExports = Path.Combine(folder.Path, "exports", Hash(legacyOwner)[..24]);
        Directory.CreateDirectory(oldExports);
        var legacyPdf = Path.Combine(oldExports, "slides-" + Hash("l")[..24] + ".pdf");
        using (var pdf = new CoreSlidePdf(legacyPdf, new string('a', 64)))
        {
            pdf.Add(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "slide.png")), 1);
            pdf.Finish();
        }
        using var workspace = NativeWorkspace.Open(new(Path.Combine(folder.Path, "目标 数据")));
        var target = workspace.RegisterAuthenticatedAccount(owner);
        target.Put("course", "c", new { id = "c", name = "保留的名字", semester = "term" });
        var preview = LegacyMigration.Run(workspace, legacyPath, owner, false);
        Check(preview is { Added: 4, Enriched: 1, OtherAccountRows: 1, Pdfs: 1, Imported: false });
        Check(!Directory.EnumerateFileSystemEntries(workspace.Paths.Backups).Any() && target.List("lesson").Length == 0);
        var result = LegacyMigration.Run(workspace, legacyPath, owner, true);
        Check(result.Imported && File.Exists(Path.Combine(result.BackupDirectory!, "native-before.sqlite3")));
        Check(target.Get("course", "c").GetProperty("name").GetString() == "保留的名字" && target.List("course").Length == 1);
        Check(target.Get("lesson", "l").GetProperty("start_at").GetInt64() > 0);
        Check(!target.Get("automation", "c").GetProperty("enabled").GetBoolean() && JobService.Decode(target.Get("job", "j")).Status == JobStatus.Interrupted);
        var copied = Directory.GetFiles(workspace.Paths.Exports, "*.pdf", SearchOption.AllDirectories).Single();
        Check(File.ReadAllBytes(copied).SequenceEqual(File.ReadAllBytes(legacyPdf)));
        Check(LegacyMigration.Run(workspace, legacyPath, owner, true) is { Added: 0, Pdfs: 0, Imported: true });
        old.Put(legacyOwner, "materials", "l", new { changed = true });
        old.Put(legacyOwner, "chat", "new", new { id = "new" });
        var conflict = LegacyMigration.Run(workspace, legacyPath, owner, true);
        Check(!conflict.Imported && conflict.Conflicts.Any(item => item is { Kind: "materials", Blocking: true }) && target.List("chat").Length == 0);
        Check(old.Get(legacyOwner, "job", "j").GetProperty("status").GetString() == "running");

        var journalId = new string('c', 32);
        var journal = Path.Combine(workspace.Paths.Backups, "migration-" + journalId);
        Directory.CreateDirectory(journal);
        var digest = new string('d', 64);
        var uncommitted = Path.Combine(Path.GetDirectoryName(copied)!, $"legacy-{journalId}-{digest[..12]}.pdf");
        File.Copy(legacyPdf, uncommitted);
        File.WriteAllText(Path.Combine(journal, "pending.json"), JsonSerializer.Serialize(new {
            Owner = owner, Files = new[] { new { Lesson = "l", Digest = digest } }
        }));
        LegacyMigration.Recover(workspace);
        Check(!File.Exists(uncommitted) && File.Exists(copied));
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Migration assertion failed."); }
}
