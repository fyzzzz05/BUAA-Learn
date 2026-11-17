using System.Text.Json;
using System.Text.Json.Nodes;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

internal sealed class CourseAutomation(TimeProvider clock, IEnumerable<JobDefinition> jobs, ModelSettingsService? models)
{
    private readonly Dictionary<string, JobDefinition> handlers = jobs.ToDictionary(job => job.Kind, StringComparer.Ordinal);
    private readonly object gate = new();
    private sealed class RuleChanged : Exception;
    public JobDefinition Definition => new("automation", true, TimeSpan.FromMinutes(30), RunAsync);

    internal object Read(AccountStore store)
    {
        lock (gate)
        {
            var reader = new SchoolWorkspaceReader(store);
            var courses = reader.Courses().Concat(store.List("automation").Where(rule => Flag(rule, "enabled"))
                .Select(rule => store.Get("course", Text(rule, "course_id")!)).Where(row => row.ValueKind == JsonValueKind.Object))
                .DistinctBy(row => Text(row, "id"));
            return new {
                model_ready = models?.Get(store).Ready ?? false,
                courses = courses.Select(course =>
                {
                    var id = Text(course, "id")!;
                    var rule = store.Get("automation", id);
                    return new { id, name = Text(course, "name") ?? id, code = Text(course, "code"),
                        enabled = Flag(rule, "enabled"), auto_analyze = Flag(rule, "auto_analyze"), next_run = Text(rule, "next_run"),
                        pending_count = Property(rule, "pending_count") is { ValueKind: JsonValueKind.Number } count ? count.GetInt32() : 0 };
                }).ToArray()
            };
        }
    }

    internal object Save(AccountStore store, IEnumerable<(string Id, bool Enabled, bool Analyze)> changes)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var rows = ScheduleRows(store);
            var writes = new List<RecordWrite>();
            foreach (var (id, enabled, analyze) in changes)
            {
                if (store.Get("course", id).ValueKind != JsonValueKind.Object) throw new ArgumentException("未找到课程。");
                var current = store.Get("automation", id);
                if (analyze && (!enabled || !Flag(current, "auto_analyze") && models?.Get(store).Ready != true))
                    throw new CoreException("请先配置模型，再开启自动笔记。", "automation_model");
                if (Flag(current, "enabled") == enabled && Flag(current, "auto_analyze") == analyze) continue;
                var rule = current.ValueKind == JsonValueKind.Object ? JsonNode.Parse(current.GetRawText())!.AsObject() : new JsonObject();
                rule["course_id"] = id;
                rule["enabled"] = enabled;
                rule["auto_analyze"] = analyze;
                rule["configuration_revision"] = Guid.NewGuid().ToString("N");
                writes.Add(new("automation", id, CoreAutomation.Plan(JsonSerializer.SerializeToElement(rule), rows, now)));
            }
            if (writes.Count != 0) store.PutBatch(writes);
            return Read(store);
        }
    }

    internal string[] Due(AccountStore store, DateTimeOffset now)
    {
        lock (gate)
        {
            var rows = ScheduleRows(store);
            var due = new List<string>();
            foreach (var rule in store.List("automation").Where(rule => Flag(rule, "enabled")))
            {
                var id = Text(rule, "course_id");
                if (id == null || store.Get("course", id).ValueKind != JsonValueKind.Object) continue;
                var planned = CoreAutomation.Plan(rule, rows, now);
                if (!JsonElement.DeepEquals(planned, rule)) store.Put("automation", id, planned);
                if (DateTimeOffset.TryParse(Text(planned, "next_run"), out var next) && next <= now) due.Add(id);
            }
            return due.ToArray();
        }
    }

    private Task RunAsync(JobContext context, CancellationToken cancellation)
    {
        var store = context.Store;
        var course = context.Job.Target;
        var initial = store.Get("automation", course);
        if (!Flag(initial, "enabled")) return Task.CompletedTask;
        var revision = Text(initial, "configuration_revision");
        JsonElement Current()
        {
            var rule = store.Get("automation", course);
            if (!Flag(rule, "enabled") || Text(rule, "configuration_revision") != revision) throw new RuleChanged();
            return rule;
        }
        void Execute(string kind, string id)
        {
            lock (gate) Current();
            if (!handlers.TryGetValue(kind, out var handler)) throw new JobFailureException("请先配置模型，再开启自动笔记。");
            handler.Execute(new JobContext(context.Job with { Target = id }, store, context.Report), cancellation).GetAwaiter().GetResult();
        }
        object? Call(JsonElement request)
        {
            var method = request.GetProperty("method").GetString();
            var id = Text(request, "id");
            switch (method)
            {
                case "load": lock (gate) return Current();
                case "save":
                    lock (gate)
                    {
                        Current();
                        store.Put("automation", course, request.GetProperty("rule"));
                        return null;
                    }
                case "catalogue":
                    Execute("course", course);
                    return new SchoolWorkspaceReader(store).Lessons(course).Select(row => new {
                        id = Text(row, "id"), title = Text(row, "title") ?? "",
                        start_at = row.GetProperty("start_at").GetInt64(), has_recording = Flag(row, "has_recording"),
                        duration_seconds = 0
                    }).ToArray();
                case "materials":
                    if (id == null) throw new ArgumentException("课次无效。");
                    var existing = Availability(store.Get("materials", id));
                    if (existing[0] + existing[1] > 0) return existing;
                    Execute("materials", id);
                    return Availability(store.Get("materials", id));
                case "analyze":
                    if (id == null) throw new ArgumentException("课次无效。");
                    var digest = Text(store.Get("materials", id), "digest");
                    if (digest == null || Text(store.Get("analysis", id), "digest") != digest) Execute("analyze", id);
                    return null;
                default: throw new ArgumentException("自动化命令无效。");
            }
        }
        try { CoreAutomation.Run(course, ScheduleRows(store), clock.GetUtcNow(), Call, cancellation); }
        catch (RuleChanged) { } // A saved draft takes effect immediately for future work.
        return Task.CompletedTask;
    }

    private static int[] Availability(JsonElement material) =>
        [Length(material, "segments"), Length(material, "slides")];
    private static int Length(JsonElement row, string key) =>
        Property(row, key) is { ValueKind: JsonValueKind.Array } list ? list.GetArrayLength() : 0;
    private static JsonElement ScheduleRows(AccountStore store)
    {
        var value = Property(store.Get("meta", "schedule"), "rows");
        if (value.ValueKind != JsonValueKind.Array) return JsonSerializer.SerializeToElement(Array.Empty<object>());
        return JsonSerializer.SerializeToElement(value.EnumerateArray().Select(row => new {
            course_id = Text(row, "course_id"), date = Text(row, "date"), time = Text(row, "time")
        }).Where(row => row.course_id != null && row.date != null && row.time != null).ToArray());
    }
    internal static string? Text(JsonElement row, string key) =>
        Property(row, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    internal static bool Flag(JsonElement row, string key) => Property(row, key).ValueKind == JsonValueKind.True;
    private static JsonElement Property(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) ? value : default;
}
