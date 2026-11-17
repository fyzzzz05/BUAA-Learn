using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

internal sealed class SlidePdfExport(WorkspacePaths paths, SlideImageCache images)
{
    private const string Header = "%PDF-1.4\n%BUAA-Slides ";
    private const int MaxSlides = 1000;
    private sealed record Slide(string Id, string Url, double Seconds);
    public JobDefinition Definition => new("slides-pdf", false, TimeSpan.FromMinutes(30), RunAsync);

    internal static string PathFor(WorkspacePaths paths, AccountStore store, string lesson, bool create = false)
    {
        if (store.Get("lesson", lesson).ValueKind != JsonValueKind.Object) throw new ArgumentException("未找到这节课。");
        var path = Path.Combine(paths.LessonExportDirectory(store.Owner, lesson, create), "去重课件.pdf");
        WorkspacePaths.RejectLink(path);
        return path;
    }

    internal static string Status(WorkspacePaths paths, AccountStore store, string lesson)
    {
        var path = PathFor(paths, store, lesson);
        if (!File.Exists(path))
        {
            var imported = LegacyMigration.ImportedFile(paths, store, lesson);
            if (imported == null || !File.Exists(imported)) return "missing";
            try
            {
                var snapshot = LegacyMigration.ImportedSnapshot(store, lesson);
                return snapshot != null && snapshot == Snapshot(Read(store, lesson)) ? "ready" : "stale";
            }
            catch (Exception error) when (error is ArgumentException or CoreException or InvalidOperationException or KeyNotFoundException) { return "stale"; }
        }
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[Header.Length + 64];
            file.ReadExactly(header);
            if (!header[..Header.Length].SequenceEqual(Encoding.ASCII.GetBytes(Header))) return "missing";
            Span<byte> end = stackalloc byte[6];
            file.Seek(-end.Length, SeekOrigin.End);
            file.ReadExactly(end);
            if (!end.SequenceEqual("%%EOF\n"u8)) return "missing";
            return Encoding.ASCII.GetString(header[Header.Length..]) == Snapshot(Read(store, lesson)) ? "ready" : "stale";
        }
        catch (IOException) { return "missing"; }
        catch (ArgumentException) { return "stale"; }
        catch (Exception error) when (error is CoreException or InvalidOperationException or KeyNotFoundException) { return "stale"; }
    }

    internal static string ExistingFile(WorkspacePaths paths, AccountStore store, string lesson)
    {
        var path = PathFor(paths, store, lesson);
        if (File.Exists(path)) return path;
        var imported = LegacyMigration.ImportedFile(paths, store, lesson);
        if (imported != null && File.Exists(imported)) return imported;
        throw new ArgumentException("尚未导出 PDF。");
    }

    internal static string MaterialSnapshot(JsonElement materials) => Snapshot(Read(materials));
    private static Slide[] Read(AccountStore store, string lesson) => Read(store.Get("materials", lesson));

    private static Slide[] Read(JsonElement materials)
    {
        if (materials.ValueKind != JsonValueKind.Object || !materials.TryGetProperty("slides", out var slides) ||
            slides.ValueKind != JsonValueKind.Array || slides.GetArrayLength() == 0) return [];
        if (slides.GetArrayLength() > MaxSlides) throw new ArgumentException("单次最多导出 1000 张截图。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return slides.EnumerateArray().Select(row =>
        {
            var id = row.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) throw new ArgumentException("截图索引无效。");
            var url = SlideImageCache.ValidateUrl(row.GetProperty("url").GetString()!).AbsoluteUri;
            var seconds = row.GetProperty("start").GetDouble();
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > 1_000_000_000) throw new ArgumentException("截图时间无效。");
            return new Slide(id, url, seconds);
        }).ToArray();
    }

    private static string Snapshot(Slide[] slides) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(slides)));

    private async Task RunAsync(JobContext context, CancellationToken cancellation)
    {
        var store = context.Store;
        var lesson = context.Job.Target;
        string? temporary = null;
        try
        {
            var slides = Read(store, lesson);
            if (slides.Length == 0) throw new JobFailureException("暂无截图，请先获取课堂材料。");
            var snapshot = Snapshot(slides);
            var target = PathFor(paths, store, lesson, create: true);
            temporary = Path.Combine(Path.GetDirectoryName(target)!, Guid.NewGuid().ToString("N") + ".pdf.tmp");
            using (var pdf = new CoreSlidePdf(temporary, snapshot))
            {
                for (var index = 0; index < slides.Length; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var slide = slides[index];
                    context.Report($"正在处理 PPT 截图 {index + 1} / {slides.Length}");
                    var image = await images.GetAsync(store, lesson, slide.Id, true, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    pdf.Add(image.Bytes ?? throw new JobFailureException("无法读取截图，已有 PDF 保留。"), (ulong)slide.Seconds);
                }
                cancellation.ThrowIfCancellationRequested();
                pdf.Finish();
            }
            WorkspacePaths.SecureFile(temporary);
            if (snapshot != Snapshot(Read(store, lesson))) throw new JobFailureException("截图索引已更新，请重新导出。");
            cancellation.ThrowIfCancellationRequested();
            WorkspacePaths.RejectLink(target);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception error) when (error is CoreException or IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException)
        {
            throw new JobFailureException("PPT PDF 导出失败。请检查截图、大小限制或文件占用，已有 PDF 保留。");
        }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
    }
}
