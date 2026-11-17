using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BUAALearn.Platform;

public sealed record WorkItem(string Id, string Kind, string CourseId, string CourseName, string LessonId,
    string LessonTitle, string Text, string Deadline, string Audience, string Submission, bool Done, bool Superseded);
public sealed record WorkList(WorkItem[] Assignments, WorkItem[] Notices);
public sealed record WorkSource(string LessonId, string LessonTitle, bool Superseded, IReadOnlyList<TranscriptRow> Rows);

/// Account-scoped projection. No model request and no school session required.
/// Task sources are immutable snapshots; notices address an exact analysis version.
public sealed class TaskReader(AccountStore store)
{
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
    private static string Version(JsonElement analysis) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(analysis.GetRawText())));
    private static WorkItem Item(JsonElement value, string id, string kind, JsonElement? analysis = null)
    {
        var parent = analysis ?? value;
        return new(id, kind, Text(parent, "course_id"), Text(parent, "course_name"), Text(parent, "lesson_id"), Text(parent, "lesson_title"), Text(value, "text"), Text(value, "deadline"), Text(value, "audience"), Text(value, "submission"), Flag(value, "done"), Flag(value, "superseded"));
    }
    public WorkList List()
    {
        var tasks = store.List("task").Where(x => x.ValueKind == JsonValueKind.Object && Text(x, "id") != "" && Text(x, "text") != "").Select(x => Item(x, Text(x, "id"), "task")).ToArray();
        var notices = new List<WorkItem>();
        foreach (var analysis in store.ListNoticeGroups().EnumerateArray())
        {
            if (analysis.ValueKind != JsonValueKind.Object || !analysis.TryGetProperty("notices", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            var version = Text(analysis, "source_version"); var index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (Text(row, "text") != "") notices.Add(Item(row, $"notice:{version}:{index}", "notice", analysis));
                index++;
            }
        }
        return new(tasks, notices.ToArray());
    }
    public void SetDone(string id, bool done) => store.SetTaskDone(id, done);
    public WorkSource Source(string kind, string id, string lessonId)
    {
        JsonElement finding, document; bool superseded = false; string title;
        if (kind == "task")
        {
            finding = store.Get("task", id);
            if (finding.ValueKind != JsonValueKind.Object || Text(finding, "lesson_id") != lessonId || Text(finding, "text") == "") throw new ArgumentException("未找到对应课堂的作业。");
            superseded = Flag(finding, "superseded"); title = Text(finding, "lesson_title");
            var snapshot = Text(finding, "source_snapshot");
            if (snapshot.StartsWith("task-source:", StringComparison.Ordinal) && snapshot.Length == 76 && snapshot[12..].All(char.IsAsciiHexDigit)) document = store.Get("meta", snapshot);
            else
            {
                // Legacy active tasks can use the current analysis only when
                // the exact assignment still exists there. Never attach a new
                // analysis' matching segment IDs to a superseded old task.
                document = store.Get("analysis", lessonId);
                if (snapshot != "" || superseded || document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array ||
                    !assignments.EnumerateArray().Any(x => Text(x, "text") == Text(finding, "text") && x.TryGetProperty("evidence_ids", out var refs) && finding.TryGetProperty("evidence_ids", out var saved) && JsonElement.DeepEquals(refs, saved)))
                    return new(lessonId, title, superseded, []);
            }
        }
        else if (kind == "notice")
        {
            document = store.Get("analysis", lessonId); var parts = id.Split(':');
            if (document.ValueKind != JsonValueKind.Object || parts.Length != 3 || parts[0] != "notice" || parts[1] != Version(document) || !int.TryParse(parts[2], out var index) || index < 0 ||
                !document.TryGetProperty("notices", out var notices) || notices.ValueKind != JsonValueKind.Array || index >= notices.GetArrayLength()) throw new ArgumentException("事项已变化，请刷新列表。");
            finding = notices[index]; title = Text(document, "lesson_title");
        }
        else throw new ArgumentException("来源类型无效。");
        if (Text(document, "lesson_id") != lessonId) return new(lessonId, title, superseded, []);
        return new(lessonId, title, superseded, LessonReader.SourceFrom(document, finding));
    }
}
