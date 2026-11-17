using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

internal sealed class SchoolSyncService(Func<string, CancellationToken, Task<SchoolConnection>> resolve, TimeProvider clock)
{
    public IEnumerable<JobDefinition> Definitions => new[]
    {
        Define("sync", SyncSemesterAsync), Define("schedule", SyncScheduleAsync),
        Define("course", SyncCourseAsync), Define("materials", FetchMaterialsAsync)
    };
    private JobDefinition Define(string kind, Func<JobContext, SchoolConnection, CancellationToken, Task> action) =>
        new(kind, true, TimeSpan.FromMinutes(5), async (context, cancellation) =>
        {
            var session = await resolve(context.Store.Owner, cancellation).ConfigureAwait(false);
            try { await action(context, session, cancellation).ConfigureAwait(false); }
            catch (RecordingAuthorizationCancelledException)
            { throw new JobFailureException("录播授权未完成或已取消，请重新同步课程。"); }
            catch (CoreException error) when (error.Code is "login_required" or "recording_auth")
            {
                session.RecordingCourse = null; session.RecordingId = null;
                throw new NeedsSchoolLoginException(error.Code == "recording_auth");
            }
            catch (CoreException error) when (error.Code == "school_transport")
            { throw new JobFailureException("学校网络暂时无法连接，请稍后重试。"); }
            catch (CoreException error) when (error.Code == "school_error")
            { throw new JobFailureException("学校返回数据异常，已有记录保留，请稍后重试。"); }
        });
    private string Now => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    private (string Start, string End) Window()
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
        return (today.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.AddDays(21).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
    private object Schedule(SchoolConnection session, CancellationToken cancellation)
    {
        var window = Window();
        var rows = session.Core.Schedule(window.Start, window.End, cancellation).EnumerateArray().Select(x => x.Clone()).ToArray();
        foreach (var row in rows)
        {
            if (!DateOnly.TryParseExact(row.GetProperty("date").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new JobFailureException("学校返回课表日期异常，原课表已保留。");
        }
        var filtered = rows.Where(x => string.CompareOrdinal(x.GetProperty("date").GetString(), window.Start) >= 0 && string.CompareOrdinal(x.GetProperty("date").GetString(), window.End) <= 0).ToArray();
        return new { rows = filtered, start = window.Start, end = window.End, synced_at = Now };
    }
    private Task SyncSemesterAsync(JobContext context, SchoolConnection session, CancellationToken cancellation)
    {
        context.Report("正在读取学校学期");
        var terms = session.Core.Semesters(cancellation).EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (terms.Length == 0) throw new JobFailureException("学校未返回可用学期，已有记录保留。");
        var rows = new List<RecordWrite> { new("meta", "semesters", new { ids = terms, synced_at = Now }) };
        if (context.Job.Target.Length == 0) { context.Store.MergeSchool(rows); return Task.CompletedTask; }
        var term = context.Job.Target;
        if (!terms.Contains(term, StringComparer.Ordinal)) throw new JobFailureException("学校未返回所选学期，请刷新学期后重新选择。");
        context.Report("正在同步课程");
        var courses = session.Core.Courses(term, cancellation).EnumerateArray().ToArray();
        foreach (var course in courses)
        {
            var body = JsonNode.Parse(course.GetRawText())!.AsObject(); body["synced_at"] = Now;
            rows.Add(new("course", course.GetProperty("id").GetString()!, body));
        }
        context.Report("正在同步真实课表");
        var schedule = Schedule(session, cancellation);
        rows.Add(new("meta", "semester", new { id = term, synced_at = Now }));
        rows.Add(new("meta", "semester:" + term, new { id = term, course_ids = courses.Select(x => x.GetProperty("id").GetString()).ToArray(), synced_at = Now }));
        rows.Add(new("meta", "schedule", schedule));
        context.Store.MergeSchool(rows); // One transaction, only after all network reads succeed.
        return Task.CompletedTask;
    }
    private Task SyncScheduleAsync(JobContext context, SchoolConnection session, CancellationToken cancellation)
    {
        context.Report("正在同步真实课表");
        var schedule = Schedule(session, cancellation);
        context.Store.MergeSchool([new("meta", "schedule", schedule)]);
        return Task.CompletedTask;
    }
    private static JsonElement Course(AccountStore store, string id)
    {
        var course = store.Get("course", id);
        if (course.ValueKind != JsonValueKind.Object || course.GetProperty("id").GetString() != id) throw new JobFailureException("未找到这门课程，请先同步课程。");
        return course;
    }
    private async Task SyncCourseAsync(JobContext context, SchoolConnection session, CancellationToken cancellation)
    {
        var course = Course(context.Store, context.Job.Target);
        session.RecordingCourse = null; session.RecordingId = null;
        context.Report("正在获取学校录播入口");
        var id = course.GetProperty("id").GetString()!;
        var signature = session.Core.RecordingEntry(id, course.GetProperty("task_id").GetString()!, cancellation);
        var challenge = new RecordingChallenge(session.Owner, id, LoginProtocol.RecordingEntry(signature));
        context.Report("正在等待录播授权");
        var authorization = await session.Authorizer.AuthorizeAsync(challenge, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        session.Core.SetRecording(authorization.Bearer, cancellation);
        context.Report("正在读取录播目录");
        var lessons = session.Core.Catalogue(authorization.RecordingCourseId, cancellation).EnumerateArray().ToArray();
        var records = new List<RecordWrite>();
        foreach (var lesson in lessons)
        {
            var recordingId = lesson.GetProperty("recording_course_id").GetString();
            if (string.IsNullOrEmpty(recordingId)) recordingId = authorization.RecordingCourseId;
            var lessonId = lesson.GetProperty("id").GetString()!;
            var start = lesson.GetProperty("start_at").GetInt64();
            string date;
            try { date = start == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(start).ToOffset(TimeSpan.FromHours(8)).ToString("O", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { throw new JobFailureException("学校返回课次时间异常，原目录已保留。"); }
            var source = "https://classroom.msa.buaa.edu.cn/livingroom?tenant_code=21&course_id=" + Uri.EscapeDataString(recordingId) + "&sub_id=" + Uri.EscapeDataString(lessonId) + "&sub_public=1";
            records.Add(new("lesson", lessonId, new
            {
                id = lessonId, course_id = id, course_name = course.GetProperty("name").GetString(),
                recording_course_id = recordingId, title = lesson.GetProperty("title").GetString(),
                teachers = lesson.GetProperty("teachers").GetString(), start_at = start, date,
                duration = lesson.GetProperty("duration_seconds").GetDouble(), has_recording = lesson.GetProperty("has_recording").GetBoolean(), source_url = source
            }));
        }
        records.Add(new("course", id, new
        {
            recording_id = authorization.RecordingCourseId, checked_at = Now, sync_error = "", lesson_count = lessons.Length,
            recording_count = lessons.Count(x => x.GetProperty("has_recording").GetBoolean()), lesson_ids = lessons.Select(x => x.GetProperty("id").GetString()).ToArray()
        }));
        context.Store.MergeSchool(records);
        session.RecordingCourse = id; session.RecordingId = authorization.RecordingCourseId;
    }
    private async Task FetchMaterialsAsync(JobContext context, SchoolConnection session, CancellationToken cancellation)
    {
        var lessonId = context.Job.Target;
        var lesson = context.Store.Get("lesson", lessonId);
        if (lesson.ValueKind != JsonValueKind.Object || lesson.GetProperty("id").GetString() != lessonId) throw new JobFailureException("未找到这节课，请先同步课程目录。");
        var courseId = lesson.GetProperty("course_id").GetString()!;
        var course = Course(context.Store, courseId);
        if (!lesson.GetProperty("has_recording").GetBoolean()) throw new JobFailureException("这节课尚未生成回放。");
        if (session.RecordingCourse != courseId || !course.TryGetProperty("recording_id", out var expected) || expected.GetString() != session.RecordingId)
        {
            // Reauthorize an old cached course after restart/reconnect before material requests.
            var catalogueContext = new JobContext(context.Job with { Target = courseId }, context.Store, context.Report);
            await SyncCourseAsync(catalogueContext, session, cancellation).ConfigureAwait(false);
            lesson = context.Store.Get("lesson", lessonId);
            course = Course(context.Store, courseId);
        }
        if (!course.GetProperty("lesson_ids").EnumerateArray().Any(x => x.GetString() == lessonId) || !lesson.GetProperty("has_recording").GetBoolean())
            throw new JobFailureException("最新目录中这节课暂无可用回放，旧材料保留。");
        context.Report("正在获取课堂文本和截图索引");
        var material = session.Core.Materials(lesson.GetProperty("recording_course_id").GetString()!, lessonId, lesson.GetProperty("duration").GetDouble(), cancellation);
        if (material.GetProperty("segments").GetArrayLength() == 0 && material.GetProperty("slides").GetArrayLength() == 0)
            throw new JobFailureException("课堂材料尚未就绪，旧材料已保留，请稍后重试。");
        var body = JsonNode.Parse(material.GetRawText())!.AsObject();
        body["course_id"] = courseId; body["recording_course_id"] = lesson.GetProperty("recording_course_id").GetString();
        context.Store.SaveMaterials(courseId, lessonId, JsonSerializer.SerializeToElement(body));
    }
}
