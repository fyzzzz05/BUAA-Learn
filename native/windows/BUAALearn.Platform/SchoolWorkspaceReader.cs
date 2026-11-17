using System.Text.Json;

namespace BUAALearn.Platform;

public sealed record SemesterOption(string Id, bool Available, bool Cached);

/// Reads one immutable account namespace without school/network access.
public sealed class SchoolWorkspaceReader(AccountStore store)
{
    public string Owner => store.Owner;
    public string? SelectedSemester => store.Get("meta", "semester") is var value && value.ValueKind == JsonValueKind.Object ? value.GetProperty("id").GetString() : null;
    public IReadOnlyList<SemesterOption> Semesters()
    {
        var raw = store.Get("meta", "semesters");
        var available = raw.ValueKind == JsonValueKind.Object ? raw.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
        var cached = store.List("meta").Where(x => x.TryGetProperty("course_ids", out _) && x.TryGetProperty("id", out _)).Select(x => x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        return available.Concat(cached.Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Select(x => new SemesterOption(x, available.Contains(x, StringComparer.Ordinal), cached.Contains(x))).ToArray();
    }
    public IReadOnlyList<JsonElement> Courses(string? semester = null, bool includeArchived = false)
    {
        semester ??= SelectedSemester;
        if (semester == null) return [];
        var snapshot = store.Get("meta", "semester:" + semester);
        var ids = snapshot.ValueKind == JsonValueKind.Object ? snapshot.GetProperty("course_ids").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal) : null;
        return store.List("course").Where(x => x.GetProperty("semester").GetString() == semester && (includeArchived || ids == null || ids.Contains(x.GetProperty("id").GetString())))
            .OrderByDescending(x => x.TryGetProperty("recording_count", out var count) ? count.GetInt32() : -1)
            .ThenBy(x => x.GetProperty("code").GetString(), StringComparer.Ordinal).ThenBy(x => x.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
    }
    public IReadOnlyList<JsonElement> Lessons(string courseId, bool includeArchived = false)
    {
        var course = store.Get("course", courseId);
        if (course.ValueKind != JsonValueKind.Object) return [];
        var ids = course.TryGetProperty("lesson_ids", out var snapshot) ? snapshot.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal) : null;
        return store.List("lesson").Where(x => x.GetProperty("course_id").GetString() == courseId && (includeArchived || ids == null || ids.Contains(x.GetProperty("id").GetString())))
            .OrderByDescending(x => x.GetProperty("start_at").GetInt64()).ThenBy(x => x.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
    }
    public JsonElement Lesson(string id) => store.Get("lesson", id);
    public JsonElement Materials(string id) => store.Get("materials", id);
    public JsonElement Schedule() => store.Get("meta", "schedule");
}
