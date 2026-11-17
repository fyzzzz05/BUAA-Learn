using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BUAALearn.CoreInterop;
using Microsoft.Win32.SafeHandles;

namespace BUAALearn.Platform;

/// Runs only while holding the standalone native workspace lease.
public static class LegacyMigration
{
    private static readonly HashSet<string> Supported = ["course", "lesson", "materials", "analysis", "task", "chat", "automation", "job", "meta"];
    private static readonly HashSet<string> Mergeable = ["course", "lesson", "meta"];
    private static readonly HashSet<string> Meta = ["semester", "semesters", "schedule", "automation_schedule"];
    private sealed record Pdf(string Lesson, string Source, string Digest, string? Snapshot);
    private sealed record Entry(string Lesson, string Digest);
    private sealed record Journal(string Owner, Entry[] Files);
    public sealed record Conflict(string Kind, string Id, bool Blocking);
    public sealed record Report(int Added, int Identical, int Enriched, int Unsupported, ulong OtherAccountRows,
        int Pdfs, int MissingOrInvalidPdfs, Conflict[] Conflicts, string? BackupDirectory, bool Imported);

    public static Report Run(NativeWorkspace workspace, string source, string owner, bool apply)
    {
        var store = workspace.OpenAccount(owner);
        source = Path.GetFullPath(source);
        WorkspacePaths.RejectLink(source);
        var sourceDirectory = Path.GetDirectoryName(source)!;
        WorkspacePaths.RejectLink(sourceDirectory);
        if (!File.Exists(source)) throw new ArgumentException("未找到旧版数据库。");
        if (SameFile(source, workspace.Paths.Database)) throw new ArgumentException("新旧数据库不能是同一个文件。");
        if (apply) Recover(workspace);
        string? backupDirectory = null;
        var database = source;
        var run = Guid.NewGuid().ToString("N");
        if (apply)
        {
            backupDirectory = Path.Combine(workspace.Paths.Backups, "migration-" + run);
            WorkspacePaths.SecureDirectory(backupDirectory);
            database = Path.Combine(backupDirectory, "legacy.sqlite3");
            try { CoreLegacy.Snapshot(source, database, requireIdle: true); }
            catch (CoreException) { throw new CoreException("无法备份旧库。请关闭旧版程序并检查数据库及磁盘空间。"); }
            WorkspacePaths.SecureFile(database);
            var targetBackup = Path.Combine(backupDirectory, "native-before.sqlite3");
            CoreLegacy.Snapshot(workspace.Paths.Database, targetBackup, requireIdle: false);
            WorkspacePaths.SecureFile(targetBackup);
        }

        var legacy = CoreLegacy.Rows(database, owner);
        var rows = legacy.GetProperty("records").EnumerateArray().ToArray();
        if (rows.Length == 0) throw new ArgumentException("旧库没有与当前学校账号匹配的数据。");
        var files = new List<Pdf>();
        var changes = new List<RecordWrite>();
        var conflicts = new List<Conflict>();
        var added = 0; var identical = 0; var enriched = 0; var unsupported = 0; var missing = 0;
        var materials = rows.Where(row => row.GetProperty("kind").GetString() == "materials")
            .ToDictionary(row => row.GetProperty("id").GetString()!, row => row.GetProperty("body"), StringComparer.Ordinal);
        var lessonIds = rows.Where(row => row.GetProperty("kind").GetString() == "lesson")
            .Select(row => row.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var kind = row.GetProperty("kind").GetString()!;
            var id = row.GetProperty("id").GetString()!;
            var old = row.GetProperty("body");
            if (kind == "slides_pdf")
            {
                var pdf = lessonIds.Contains(id) || store.Get("lesson", id).ValueKind == JsonValueKind.Object
                    ? InspectPdf(sourceDirectory, legacy.GetProperty("legacy_owner").GetString()!, id, old, materials.GetValueOrDefault(id))
                    : null;
                if (pdf == null) { missing++; continue; }
                var present = store.Get("meta", PdfRecord(id));
                if (CourseAutomation.Text(present, "digest") == pdf.Digest &&
                    ImportedFile(workspace.Paths, store, id) is { } imported && File.Exists(imported)) continue;
                if (present.ValueKind == JsonValueKind.Object && CourseAutomation.Text(present, "digest") != pdf.Digest)
                { conflicts.Add(new(kind, id, true)); continue; }
                files.Add(pdf);
                continue;
            }
            if (!Supported.Contains(kind) || kind == "meta" && !Meta.Contains(id) ||
                old.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl))
            { unsupported++; continue; }
            var incoming = Normalize(kind, id, old, owner);
            var current = store.Get(kind, id);
            if (current.ValueKind == JsonValueKind.Null)
            {
                changes.Add(new(kind, id, incoming)); added++; continue;
            }
            if (JsonElement.DeepEquals(incoming, current)) { identical++; continue; }
            if (!Mergeable.Contains(kind) || current.ValueKind != JsonValueKind.Object)
            { conflicts.Add(new(kind, id, true)); continue; }
            if (kind == "lesson" && CourseAutomation.Text(current, "course_id") != CourseAutomation.Text(incoming, "course_id"))
            { conflicts.Add(new(kind, id, true)); continue; }
            var merged = JsonNode.Parse(current.GetRawText())!.AsObject();
            foreach (var field in incoming.EnumerateObject())
                if (!merged.ContainsKey(field.Name)) merged.Add(field.Name, JsonNode.Parse(field.Value.GetRawText()));
            var proposed = JsonSerializer.SerializeToElement(merged);
            if (!JsonElement.DeepEquals(current, proposed)) { changes.Add(new(kind, id, proposed)); enriched++; }
            conflicts.Add(new(kind, id, false));
        }

        var report = new Report(added, identical, enriched, unsupported, legacy.GetProperty("other_account_records").GetUInt64(),
            files.Count, missing, conflicts.ToArray(), backupDirectory, false);
        if (!apply) return report;
        var reportPath = Path.Combine(backupDirectory!, "report.json");
        WriteJson(reportPath, report);
        if (conflicts.Any(conflict => conflict.Blocking)) return report;
        if (changes.Count == 0 && files.Count == 0) return report with { Imported = true };

        var journalPath = Path.Combine(backupDirectory!, "pending.json");
        var journal = new Journal(owner, files.Select(file => new Entry(file.Lesson, file.Digest)).ToArray());
        WriteJson(journalPath, journal);
        try
        {
            // Stage and verify every file before publishing any names.
            for (var index = 0; index < files.Count; index++)
            {
                var item = files[index];
                var temp = Stage(backupDirectory!, index);
                using (var input = File.OpenRead(item.Source))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(flushToDisk: true); }
                WorkspacePaths.SecureFile(temp);
                if (FileDigest(temp) != item.Digest) throw new IOException("旧 PDF 在迁移中变化，请重试。");
            }
            for (var index = 0; index < files.Count; index++)
            {
                var item = files[index];
                var final = ImportedPath(workspace.Paths, owner, item.Lesson, run, item.Digest, create: true);
                File.Move(Stage(backupDirectory!, index), final);
                changes.Add(new("meta", PdfRecord(item.Lesson), new { run, digest = item.Digest, snapshot = item.Snapshot }));
            }
            changes.Add(new("meta", "migration:" + run, new { complete = true }));
            store.PutBatch(changes);
        }
        catch
        {
            Recover(workspace);
            throw;
        }
        File.Delete(journalPath);
        report = report with { Imported = true };
        WriteJson(reportPath, report, replace: true);
        return report;
    }

    public static void Recover(NativeWorkspace workspace)
    {
        foreach (var directory in Directory.EnumerateDirectories(workspace.Paths.Backups, "migration-*"))
        {
            var name = Path.GetFileName(directory);
            var run = name["migration-".Length..];
            if (!Hex(run, 32)) continue;
            WorkspacePaths.RejectLink(directory);
            var path = Path.Combine(directory, "pending.json");
            WorkspacePaths.RejectLink(path);
            if (!File.Exists(path)) continue;
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path)) ?? throw new IOException("迁移清单无效。");
            var store = workspace.OpenAccount(journal.Owner);
            var committed = CourseAutomation.Flag(store.Get("meta", "migration:" + run), "complete");
            for (var index = 0; index < journal.Files.Length; index++)
            {
                var item = journal.Files[index];
                var final = ImportedPath(workspace.Paths, journal.Owner, item.Lesson, run, item.Digest, create: false);
                if (!committed && File.Exists(final)) File.Delete(final);
                var staged = Stage(directory, index);
                WorkspacePaths.RejectLink(staged);
                if (File.Exists(staged)) File.Delete(staged);
            }
            File.Delete(path);
        }
    }

    internal static string? ImportedFile(WorkspacePaths paths, AccountStore store, string lesson)
    {
        var record = store.Get("meta", PdfRecord(lesson));
        var run = CourseAutomation.Text(record, "run");
        var digest = CourseAutomation.Text(record, "digest");
        return Hex(run, 32) && Hex(digest, 64) ? ImportedPath(paths, store.Owner, lesson, run!, digest!, false) : null;
    }
    internal static string? ImportedSnapshot(AccountStore store, string lesson) =>
        CourseAutomation.Text(store.Get("meta", PdfRecord(lesson)), "snapshot");

    private static string PdfRecord(string lesson) => "migration-pdf:" + lesson;
    private static string Stage(string folder, int index) => Path.Combine(folder, $"pdf-{index}.staged");
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigit);
    private static string ImportedPath(WorkspacePaths paths, string owner, string lesson, string run, string digest, bool create)
    {
        if (!Hex(run, 32) || !Hex(digest, 64)) throw new ArgumentException("PDF 迁移标识无效。");
        var path = Path.Combine(paths.LessonExportDirectory(owner, lesson, create), $"legacy-{run}-{digest[..12]}.pdf");
        WorkspacePaths.RejectLink(path);
        return path;
    }

    private static Pdf? InspectPdf(string root, string legacyOwner, string lesson, JsonElement record, JsonElement material)
    {
        var folder = Path.Combine(root, "exports", Key(legacyOwner));
        var path = Path.Combine(folder, $"slides-{Key(lesson)}.pdf");
        WorkspacePaths.RejectLink(Path.GetDirectoryName(folder)!);
        WorkspacePaths.RejectLink(folder);
        WorkspacePaths.RejectLink(path);
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        if (input.Length is < 20 or > 512L * 1024 * 1024) return null;
        Span<byte> header = stackalloc byte[5];
        input.ReadExactly(header);
        if (!header.SequenceEqual("%PDF-"u8)) return null;
        var tail = new byte[Math.Min(1024, (int)input.Length)];
        input.Seek(-tail.Length, SeekOrigin.End);
        input.ReadExactly(tail);
        if (!Encoding.ASCII.GetString(tail).TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal)) return null;
        input.Position = 0;
        var digest = Convert.ToHexStringLower(SHA256.HashData(input));
        string? snapshot = null;
        if (material.ValueKind == JsonValueKind.Object && material.TryGetProperty("slides", out var slides) &&
            slides.ValueKind == JsonValueKind.Array && CourseAutomation.Text(record, "digest") == Hash(PythonJson(slides)))
        {
            try { snapshot = SlidePdfExport.MaterialSnapshot(material); }
            catch (Exception error) when (error is ArgumentException or CoreException or InvalidOperationException or KeyNotFoundException) { }
        }
        return new(lesson, path, digest, snapshot);
    }

    private static string PythonJson(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Object => "{" + string.Join(", ", value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => PythonString(x.Name) + ": " + PythonJson(x.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(PythonJson)) + "]",
        JsonValueKind.String => PythonString(value.GetString()!),
        _ => value.GetRawText()
    };
    private static string PythonString(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var c in value) result.Append(c switch {
            '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
            _ when c < ' ' || c > '~' => $"\\u{(int)c:x4}", _ => c.ToString()
        });
        return result.Append('"').ToString();
    }

    private static JsonElement Normalize(string kind, string id, JsonElement old, string owner)
    {
        var row = JsonNode.Parse(old.GetRawText())!.AsObject();
        if (kind == "lesson" && !row.ContainsKey("start_at"))
            row["start_at"] = DateTimeOffset.TryParse(CourseAutomation.Text(old, "date"), out var date) ? date.ToUnixTimeSeconds() : 0;
        if (kind == "job")
        {
            row["id"] = id; row["owner"] = owner;
            if (CourseAutomation.Text(old, "status") is "queued" or "running")
            {
                row["migration_original_status"] = CourseAutomation.Text(old, "status");
                row["status"] = "interrupted";
                row["message"] = "旧版未完成的任务已中断。";
            }
        }
        if (kind == "automation")
        {
            row["course_id"] = id;
            row["migration_original_enabled"] = CourseAutomation.Flag(old, "enabled");
            row["migration_original_auto_analyze"] = CourseAutomation.Flag(old, "auto_analyze");
            row["enabled"] = false; row["auto_analyze"] = false; row["next_run"] = null;
        }
        return JsonSerializer.SerializeToElement(row);
    }

    private static void WriteJson<T>(string path, T value, bool replace = false)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(flushToDisk: true); }
        WorkspacePaths.SecureFile(temp);
        File.Move(temp, path, overwrite: replace);
    }
    private static string FileDigest(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static string Key(string value) => Hash(value)[..24];
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool SameFile(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
        OperatingSystem.IsWindows() && SameWindowsFile(a, b);

    [SupportedOSPlatform("windows")]
    private static bool SameWindowsFile(string a, string b)
    {
        using var left = File.OpenHandle(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var right = File.OpenHandle(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(left, out var l) || !GetFileInformationByHandle(right, out var r))
            throw new IOException("无法确认新旧数据库的文件身份。");
        return (l.Volume, l.High, l.Low) == (r.Volume, r.High, r.Low);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Identity
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, High, Low;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Identity identity);
}
