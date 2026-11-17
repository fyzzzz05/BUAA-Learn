using System.Text.Json;

namespace BUAALearn.Platform;

public static class ReplayLink
{
    public static Uri ForLesson(JsonElement lesson)
    {
        static string Id(JsonElement row, string name)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("回放编号无效。");
            var id = value.GetString()!;
            if (id.Length is 0 or > 128 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("回放编号无效。");
            return id;
        }
        if (lesson.ValueKind != JsonValueKind.Object || !lesson.TryGetProperty("has_recording", out var ready) || ready.ValueKind != JsonValueKind.True) throw new ArgumentException("这节课尚无可用回放。");
        // Never launch a stored arbitrary source_url, signed link or file path.
        return new("https://classroom.msa.buaa.edu.cn/livingroom?tenant_code=21&course_id=" + Id(lesson, "recording_course_id") + "&sub_id=" + Id(lesson, "id") + "&sub_public=1");
    }
}
