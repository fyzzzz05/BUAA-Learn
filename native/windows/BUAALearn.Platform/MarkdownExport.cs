using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BUAALearn.Platform;

internal sealed record MarkdownFile(string FileName, string Path);

internal sealed class MarkdownExport(WorkspacePaths paths, AccountStore store, string lessonId)
{
    private static readonly (string Key, string Label)[] Findings = [
        ("knowledge_points", "知识点"), ("assignments", "作业（待核验）"), ("notices", "重要事项")
    ];

    public MarkdownFile Save()
    {
        var lesson = ReadLesson();
        var analysis = store.Get("analysis", lessonId);
        var transcript = LessonReader.Parse(store.Get("materials", lessonId), false);
        if (analysis.ValueKind != JsonValueKind.Object && transcript.Count == 0)
            throw new ArgumentException("这节课没有可导出的内容。");

        var course = Text(lesson, "course_name") ?? Text(analysis, "course_name");
        if (course == null && Text(lesson, "course_id") is { } courseId)
            course = Text(store.Get("course", courseId), "name");
        var title = Text(lesson, "title") ?? "课堂笔记";
        var name = course == null ? title : course + " · " + title;
        var fileName = FileName(name);

        string? replay = null;
        try { replay = ReplayLink.ForLesson(lesson).AbsoluteUri; }
        catch (ArgumentException) { }

        var output = Render(name, replay, analysis, transcript);
        var directory = paths.LessonExportDirectory(store.Owner, lessonId, create: true);
        var target = Path.Combine(directory, fileName);
        WorkspacePaths.RejectLink(target);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                writer.Write(output);
            }
            WorkspacePaths.SecureFile(temporary);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new(fileName, target);
    }

    public string ExistingDirectory()
    {
        ReadLesson();
        var directory = paths.LessonExportDirectory(store.Owner, lessonId, create: false);
        if (!Directory.Exists(directory)) throw new ArgumentException("请先导出 Markdown。");
        return directory;
    }

    private JsonElement ReadLesson()
    {
        var lesson = store.Get("lesson", lessonId);
        if (lesson.ValueKind != JsonValueKind.Object) throw new ArgumentException("未找到这节课。");
        return lesson;
    }

    private static string FileName(string name)
    {
        var safe = new StringBuilder("课堂-");
        foreach (var rune in name.EnumerateRunes().Take(55))
            safe.Append(Rune.IsControl(rune) || (rune.IsAscii && "<>:\"/\\|?*".Contains((char)rune.Value))
                ? "_" : rune.ToString());
        return safe.ToString().TrimEnd(' ', '.') + ".md";
    }

    private static string Render(string name, string? replay, JsonElement analysis, IReadOnlyList<TranscriptRow> transcript)
    {
        var output = new StringBuilder().Append("# ").AppendLine(Inline(name)).AppendLine();
        if (replay != null) output.Append("[原始回放](").Append(replay).AppendLine(")").AppendLine();
        output.AppendLine("自动识别与模型分析可能错误，作业和重要事项需核对原话。").AppendLine();
        var evidence = LessonReader.Parse(analysis, true).ToDictionary(row => row.Id, StringComparer.Ordinal);
        var quoted = new HashSet<string>(StringComparer.Ordinal);
        if (analysis.ValueKind == JsonValueKind.Object)
        {
            var partial = Text(analysis, "timeline_status") == "partial";
            if (partial)
            {
                output.AppendLine("主题时间轴仍有待整理内容，已完成的学习内容保留。").AppendLine();
                WriteStrings(output, Items(analysis, "timeline_warnings"));
            }
            foreach (var section in Items(analysis, "sections"))
            {
                WriteSection(output, section, "##", Text(section, "title"));
                foreach (var (key, label) in Findings)
                {
                    var rows = Items(section, key).ToArray();
                    if (rows.Length == 0) continue;
                    output.Append("**").Append(label).AppendLine("**").AppendLine();
                    WriteFindings(output, rows, key == "assignments", evidence, quoted);
                }
            }
            if (partial && Items(analysis, "text_summaries").Any())
            {
                output.AppendLine("## 已完成的文本摘要").AppendLine();
                foreach (var summary in Items(analysis, "text_summaries")) WriteSection(output, summary, "###", null);
            }
            foreach (var (key, label) in Findings)
            {
                output.Append("## ").AppendLine(label).AppendLine();
                var rows = Items(analysis, key).ToArray();
                if (rows.Length == 0) output.AppendLine("本次材料未提取到此类内容，识别文本可能不完整。").AppendLine();
                else WriteFindings(output, rows, key == "assignments", evidence, quoted);
            }
            var suggestions = Items(analysis, "review_suggestions").ToArray();
            if (suggestions.Length > 0)
            {
                output.AppendLine("## 建议复习（非老师布置）").AppendLine();
                WriteStrings(output, suggestions);
            }
        }
        if (quoted.Count > 0)
        {
            output.AppendLine("## 笔记引用原文").AppendLine();
            WriteTranscript(output, evidence.Values.Where(row => quoted.Contains(row.Id)));
        }
        if (transcript.Count > 0)
        {
            output.AppendLine("## 原始识别文本").AppendLine();
            WriteTranscript(output, transcript);
        }
        return output.ToString();
    }

    private static void WriteSection(StringBuilder output, JsonElement section, string heading, string? title)
    {
        var range = Times(section);
        output.Append(heading).Append(' ').Append(range ?? "时间未记录");
        if (title != null) output.Append(" · ").Append(Inline(title));
        output.AppendLine().AppendLine();
        if (Text(section, "summary") is { } summary) output.AppendLine(Literal(summary)).AppendLine();
    }

    private static void WriteFindings(StringBuilder output, IEnumerable<JsonElement> rows, bool assignment,
        IReadOnlyDictionary<string, TranscriptRow> evidence, HashSet<string> quoted)
    {
        foreach (var row in rows)
        {
            if (Text(row, "text") is not { } text) continue;
            var range = Property(row, "source_range");
            var refs = Items(row, "evidence_ids").Where(id => id.ValueKind == JsonValueKind.String)
                .Select(id => id.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
            var start = Text(range, "start_id");
            var end = Text(range, "end_id");
            var times = "来源待核验";
            if (start != null && end != null && evidence.TryGetValue(start, out var first) &&
                evidence.TryGetValue(end, out var last) && last.Start >= first.Start)
            {
                times = Interval(first.Start, last.End);
                quoted.UnionWith(evidence.Values.Where(item => item.Start >= first.Start && item.End <= last.End).Select(item => item.Id));
            }
            else if ((range.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) && refs.Length > 0 && refs.All(evidence.ContainsKey))
            {
                times = string.Join("、", refs.Select(id => Clock(evidence[id].Start)));
                quoted.UnionWith(refs);
            }
            output.Append("- ").Append(Inline(text)).Append("（").Append(times).AppendLine("）");
            if (assignment)
                output.Append("  截止：").Append(Inline(Text(row, "deadline") ?? "未说明"))
                    .Append("；对象：").Append(Inline(Text(row, "audience") ?? "未说明"))
                    .Append("；提交：").AppendLine(Inline(Text(row, "submission") ?? "未说明"));
        }
        output.AppendLine();
    }

    private static void WriteStrings(StringBuilder output, IEnumerable<JsonElement> rows)
    {
        foreach (var row in rows)
            if (row.ValueKind == JsonValueKind.String) output.Append("- ").AppendLine(Inline(row.GetString()!));
        output.AppendLine();
    }

    private static void WriteTranscript(StringBuilder output, IEnumerable<TranscriptRow> rows)
    {
        foreach (var row in rows)
            output.Append('[').Append(Interval(row.Start, row.End)).Append("] ").AppendLine(Literal(row.Text)).AppendLine();
    }

    private static JsonElement Property(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) ? value : default;
    private static IEnumerable<JsonElement> Items(JsonElement row, string key) =>
        Property(row, key) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private static string? Text(JsonElement row, string key) =>
        Property(row, key) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    private static string? Times(JsonElement row) =>
        Property(row, "start") is { ValueKind: JsonValueKind.Number } s && s.TryGetDouble(out var start) &&
        Property(row, "end") is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var end) &&
        double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end >= start ? Interval(start, end) : null;
    private static string Interval(double start, double end) => Clock(start) + "–" + Clock(end);
    private static string Clock(double seconds) => string.Create(CultureInfo.InvariantCulture,
        $"{Math.Floor(seconds / 3600):00}:{Math.Floor(seconds / 60) % 60:00}:{Math.Floor(seconds) % 60:00}");
    private static string Inline(string text) => Literal(text.Replace('\r', ' ').Replace('\n', ' '));
    private static string Literal(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
