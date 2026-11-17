using System.Text.Json;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

/// Each job owns one account, material snapshot, endpoint and credential copy.
/// No school login is needed to analyze already downloaded text.
public sealed class LearningService(ModelSettingsService models, ModelTransport transport, TimeProvider? clock = null)
{
    internal static JsonElement[] History(AccountStore store, string course)
    {
        RequireCourse(store, course);
        return store.List("chat")
            .Where(row => row.TryGetProperty("course_id", out var id) && id.GetString() == course)
            .OrderByDescending(row => row.GetProperty("created_at").GetString(), StringComparer.Ordinal)
            .Take(20).ToArray();
    }

    internal object Ask(AccountStore store, string course, string? lesson, string question, CancellationToken cancellation)
    {
        RequireCourse(store, course);
        var lessons = new SchoolWorkspaceReader(store).Lessons(course);
        if (lesson != null && !lessons.Any(row => row.GetProperty("id").GetString() == lesson))
            throw new CoreException("课次不属于当前课程。", "qa_scope");

        var materials = new List<object>();
        var scope = new List<object>();
        foreach (var row in lessons)
        {
            cancellation.ThrowIfCancellationRequested();
            var id = row.GetProperty("id").GetString()!;
            if (lesson != null && id != lesson) continue;
            var cached = store.Get("materials", id);
            if (cached.ValueKind != JsonValueKind.Object || !cached.TryGetProperty("segments", out var segments) ||
                segments.ValueKind != JsonValueKind.Array) continue;
            var text = segments.EnumerateArray().Where(segment => segment.TryGetProperty("text", out var value) &&
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())).ToArray();
            if (text.Length == 0) continue;
            materials.Add(new { lesson_id = id, segments = text });
            scope.Add(new {
                id, title = row.GetProperty("title").GetString(),
                date = row.TryGetProperty("date", out var date) ? date.GetString() ?? "" : "",
                source_url = ""
            });
        }
        if (materials.Count == 0) throw new CoreException("请先获取课堂识别文本。", "qa_no_material");

        var created = (clock ?? TimeProvider.System).GetUtcNow().ToString("O");
        using var settings = CaptureQuestionSettings(store);
        var input = JsonSerializer.SerializeToElement(new { question, materials, lessons = scope, created_at = created });
        var result = CoreLearning.Ask(new Adapter(transport, settings), input, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var chat = new {
            id = Guid.NewGuid().ToString("N"), course_id = course, lesson_id = lesson, question,
            answer = result.GetProperty("answer"), sources = result.GetProperty("sources"), created_at = created
        };
        store.Put("chat", chat.id, chat);
        return chat;
    }

    private ModelCallSettings CaptureQuestionSettings(AccountStore store)
    {
        try { return models.Capture(store); }
        catch (CoreException) { throw new CoreException("请检查模型设置。", "qa_configuration"); }
    }

    private static void RequireCourse(AccountStore store, string course)
    {
        if (store.Get("course", course).ValueKind != JsonValueKind.Object)
            throw new CoreException("课程不属于当前账号。", "qa_scope");
    }

    public JobDefinition Analysis => new("analyze", false, TimeSpan.FromMinutes(30), AnalyzeAsync);
    private sealed class Adapter(ModelTransport transport, ModelCallSettings settings) : ICoreModel
    {
        public JsonElement Complete(JsonElement request, CancellationToken cancellation)
        {
            try { return transport.CompleteAsync(settings, request.GetProperty("system").GetString()!, request.GetProperty("prompt").GetString()!, request.GetProperty("schema"), cancellation).GetAwaiter().GetResult(); }
            catch (ModelRequestException error) { throw new CoreModelException(error.Code, error.Message); }
        }
    }
    private Task AnalyzeAsync(JobContext context, CancellationToken cancellation)
    {
        var store = context.Store; var id = context.Job.Target;
        var lesson = store.Get("lesson", id); var materials = store.Get("materials", id);
        if (lesson.ValueKind != JsonValueKind.Object || materials.ValueKind != JsonValueKind.Object ||
            !materials.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() == 0 ||
            !segments.EnumerateArray().Any(x => x.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())))
            throw new JobFailureException("这节课没有可分析的识别文本，请先获取材料。");
        var courseId = lesson.GetProperty("course_id").GetString()!;
        var course = store.Get("course", courseId);
        if (course.ValueKind != JsonValueKind.Object || !materials.TryGetProperty("digest", out var rawDigest) || rawDigest.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(rawDigest.GetString()))
            throw new JobFailureException("课程或材料记录不完整，请重新获取材料。");
        ModelCallSettings settings;
        try { settings = models.Capture(store); }
        catch (Exception error) when (error is CoreException or ModelCredentialException) { throw new JobFailureException("模型配置或密钥不可用，请在设置中检查后重试。"); }
        using (settings)
        {
            var input = JsonSerializer.SerializeToElement(new {
                lesson = new { id, course_id = courseId, course_name = course.GetProperty("name").GetString(), title = lesson.GetProperty("title").GetString(), duration = materials.TryGetProperty("duration", out var duration) ? duration.GetDouble() : 0 },
                materials, model = settings.Model, created_at = (clock ?? TimeProvider.System).GetUtcNow().ToString("O")
            });
            JsonElement analysis;
            try { analysis = CoreLearning.Analyze(new Adapter(transport, settings), input, cancellation, context.Report); }
            catch (CoreModelException error) { throw new JobFailureException(Failure(error.Code)); }
            catch (CoreException) { throw new JobFailureException("模型结果未通过完整性或引用校验，原有笔记已保留，请重试。"); }
            cancellation.ThrowIfCancellationRequested();
            // Rust checks the digest and course relationship inside the same
            // transaction that updates notes, task reconciliation and status.
            try { store.SaveAnalysis(analysis, rawDigest.GetString()!); }
            catch (CoreException) { throw new JobFailureException("材料已变化或笔记保存失败，原有笔记已保留，请重试。"); }
        }
        return Task.CompletedTask;
    }
    private static string Failure(string code) => code switch {
        "authentication" => "模型鉴权失败，请在设置中检查密钥后重试。",
        "quota" => "模型服务限流或额度不足，原有笔记已保留，请稍后重试。",
        "timeout" => "模型请求超时，原有笔记已保留，请稍后重试。",
        "network" => "无法连接模型服务，原有笔记已保留，请检查网络。",
        "refused" => "模型拒绝了请求，原有笔记已保留。",
        "incomplete" => "模型输出未完成，原有笔记已保留，请重试。",
        _ => "模型请求未完成，原有笔记已保留，请检查服务后重试。"
    };
}
