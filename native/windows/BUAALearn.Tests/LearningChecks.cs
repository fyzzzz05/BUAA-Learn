using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

internal static class LearningChecks
{
    public static readonly (string Name, Func<Task> Run)[] All = [
        ("Course question ABI uses grounded source and skips the model without text", QuestionAbi),
        ("Course question bridge scopes sources, persists history and cancels model HTTP", QuestionBridge),
        ("Whole lesson analysis crosses managed/Rust/HTTP boundaries and saves offline", Success),
        ("Long analysis sends all chunks and retains complete evidence", Long),
        ("Model JSON/evidence repair is bounded and failures preserve notes/tasks", Invalid),
        ("Late transport failures abort optional repair and preserve notes", Fatal),
        ("Changed material snapshot rejects analysis commit atomically", Changed),
        ("Account transition cancels actual model HTTP and prevents stale writes", Cancel),
        ("Invalid topic plans retain grounded findings with explicit partial timeline", Partial),
        ("Missing model/text never sends model HTTP; bridge requires cost acknowledgement", Preconditions)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Learning assertion failed."); }
    private sealed class Setup : IAsyncDisposable
    {
        public readonly TemporaryDirectory Folder = new();
        public readonly NativeWorkspace Workspace;
        public readonly AccountStore Store;
        public readonly ModelSettingsService Models = new(new MemoryModelCredentials());
        public readonly SyntheticModel Handler = new();
        public readonly ModelTransport Transport;
        public readonly SchoolSyncRuntime Runtime;
        public Setup(bool longText = false, bool configure = true)
        {
            Workspace = NativeWorkspace.Open(new(Folder.Path)); Store = Workspace.RegisterAuthenticatedAccount("a"); Workspace.RegisterAuthenticatedAccount("b");
            Store.Put("course", "c", new { id = "c", name = "合成课程" });
            Store.Put("lesson", "l", new { id = "l", course_id = "c", title = "合成课堂", start_at = 0 });
            var segments = Enumerable.Range(0, longText ? 2 : 1).Select(i => new { id = "s" + i, start = i * 60, end = (i + 1) * 60, text = longText ? "FIRST-" + new string(i == 0 ? '甲' : '乙', 11000) + "-LAST-" + i : "合成原文" }).ToArray();
            Store.Put("materials", "l", new { lesson_id = "l", course_id = "c", digest = "initial", duration = segments.Length * 60, segments });
            Store.Put("analysis", "l", new { marker = "old" }); Store.Put("task", "old", new { marker = "old-task", done = true });
            if (configure) Models.Save(Store, "http://localhost:11434/v1", "synthetic-model", "keep");
            Transport = new(Handler); Runtime = new(Workspace, models: Models, modelTransport: Transport);
        }
        public async Task<JobRecord> Run()
        {
            await Runtime.SelectAccountAsync("a");
            return await Runtime.Jobs.WaitForTerminalAsync(Runtime.AnalyzeLesson("l")).WaitAsync(TimeSpan.FromSeconds(10));
        }
        public void Old() { Check(Store.Get("analysis", "l").GetProperty("marker").GetString() == "old"); Check(Store.Get("task", "old").GetProperty("done").GetBoolean()); }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Transport.Dispose(); Workspace.Dispose(); Folder.Dispose(); }
    }
    private static Task QuestionAbi()
    {
        var model = new QuestionModel();
        var empty = JsonSerializer.SerializeToElement(new { question = "矩阵", materials = Array.Empty<object>(), lessons = Array.Empty<object>(), created_at = "test" });
        Check(CoreLearning.Ask(model, empty).GetProperty("sources").GetArrayLength() == 0 && model.Calls == 0);
        var input = JsonSerializer.SerializeToElement(new {
            question = "矩阵", created_at = "test",
            lessons = new[] { new { id = "l", title = "合成课堂", date = "", source_url = "" } },
            materials = new[] { new { lesson_id = "l", segments = new[] { new { id = "s0", start = 0, end = 5, text = "矩阵的课堂原话" } } } }
        });
        var result = CoreLearning.Ask(model, input);
        Check(model.Calls == 2 && result.GetProperty("sources")[0].GetProperty("text").GetString() == "矩阵的课堂原话");
        return Task.CompletedTask;
    }
    private static async Task QuestionBridge()
    {
        await using var s = new Setup();
        await s.Runtime.SelectAccountAsync("a");
        s.Store.Put("course", "other", new { id = "other" });
        s.Store.Put("lesson", "foreign", new { id = "foreign", course_id = "other", title = "其他课程", start_at = 0 });
        s.Store.Put("lesson", "empty", new { id = "empty", course_id = "c", title = "缺材料", start_at = 0 });
        s.Store.Put("lesson", "second", new { id = "second", course_id = "c", title = "第二节", start_at = 1 });
        s.Store.Put("materials", "second", new { segments = new[] { new { id = "s2", start = 1, end = 2, text = "第二节合成原文" } } });
        var bridge = new WorkspaceBridge(s.Workspace, s.Runtime);
        async Task<JsonElement> Call(string method, object args, string owner = "a")
        {
            var payload = JsonSerializer.Serialize(new { v = 1, id = "request", method, owner, args });
            using var reply = JsonDocument.Parse((await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, payload))!);
            return reply.RootElement.Clone();
        }
        Task<JsonElement> Ask(string? lesson, string request = "question") =>
            Call("ask_course", new { request, course = "c", lesson, question = "合成原文", acknowledge_cost = true });
        Check((await Ask("foreign")).GetProperty("error").GetString() == "qa_scope");
        Check((await Ask("empty")).GetProperty("error").GetString() == "qa_no_material" && s.Handler.Calls == 0);
        var answer = (await Ask("l")).GetProperty("result");
        Check(answer.GetProperty("sources")[0].GetProperty("lesson_id").GetString() == "l");
        Check(!s.Handler.Prompts.Last().Contains("第二节合成原文", StringComparison.Ordinal));
        Check((await Ask(null)).GetProperty("ok").GetBoolean() && s.Handler.Prompts.Last().Contains("第二节合成原文", StringComparison.Ordinal));
        Check((await Call("course_chat", new { course = "c" })).GetProperty("result").GetArrayLength() == 2);
        Check((await Call("course_chat", new { course = "other" })).GetProperty("result").GetArrayLength() == 0);
        await s.Runtime.SelectAccountAsync("b");
        Check((await Call("course_chat", new { course = "c" }, "b")).GetProperty("error").GetString() == "qa_scope");
        await s.Runtime.SelectAccountAsync("a");
        s.Handler.Mode = "auth";
        Check((await Ask("l")).GetProperty("error").GetString() == "qa_authentication");
        s.Handler.Mode = "block";
        var previousCalls = s.Handler.Calls;
        var pending = Ask("l", "cancel-me");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref s.Handler.Calls) == previousCalls) await Task.Delay(10, timeout.Token);
        await Call("cancel_question", new { request = "cancel-me" });
        Check((await pending.WaitAsync(timeout.Token)).GetProperty("error").GetString() == "cancelled");
        Check(s.Handler.Cancelled && s.Store.List("chat").Length == 2);
    }
    private sealed class QuestionModel : ICoreModel
    {
        public int Calls { get; private set; }
        public JsonElement Complete(JsonElement request, CancellationToken cancellation)
        {
            Calls++;
            return request.GetProperty("schema").GetProperty("name").GetString() switch {
                "Keywords" => JsonSerializer.SerializeToElement(new { keywords = new[] { "矩阵" } }),
                "Answer" => JsonSerializer.SerializeToElement(new { answer = "本课讨论矩阵 [c0]", evidence_ids = new[] { "c0" } }),
                _ => throw new InvalidOperationException("Unexpected question model schema.")
            };
        }
    }
    private static async Task Success()
    {
        await using var s = new Setup(); Check((await s.Run()).Status == JobStatus.Completed); Check(!s.Runtime.Jobs.IsSchoolConnected && s.Handler.Calls == 1);
        var analysis = s.Store.Get("analysis", "l"); Check(analysis.GetProperty("timeline_status").GetString() == "complete");
        Check(analysis.GetProperty("evidence").GetProperty("s0").GetProperty("text").GetString() == "合成原文");
        Check(s.Store.Get("lesson", "l").GetProperty("analysis_at").GetString()!.Length > 0);
        var reader = new LessonReader(s.Store, "l"); Check(reader.SourceRange("s0", "s0").Count == 1);
        s.Store.Put("materials", "l", new { digest = "new", segments = Array.Empty<object>() });
        Check(reader.SourceRange("s0", "s0")[0].Text == "合成原文");
    }
    private static async Task Long()
    {
        await using var s = new Setup(true); Check((await s.Run()).Status == JobStatus.Completed); Check(s.Handler.Calls == 3);
        Check(s.Handler.Prompts.Any(x => x.Contains("-LAST-0")) && s.Handler.Prompts.Any(x => x.Contains("-LAST-1")));
        var evidence = s.Store.Get("analysis", "l").GetProperty("evidence"); Check(evidence.EnumerateObject().Count() == 2 && evidence.GetProperty("s1").GetProperty("text").GetString()!.EndsWith("-LAST-1"));
    }
    private static async Task Invalid()
    {
        foreach (var mode in new[] { "json", "evidence" })
        { await using var s = new Setup(); s.Handler.Mode = mode; Check((await s.Run()).Status == JobStatus.Failed); Check(s.Handler.Calls == 3); s.Old(); }
        await using var repaired = new Setup(); repaired.Handler.Mode = "repair"; Check((await repaired.Run()).Status == JobStatus.Completed && repaired.Handler.Calls == 2);
    }
    private static async Task Fatal()
    {
        foreach (var mode in new[] { "auth", "quota", "late-auth", "late-network" })
        {
            await using var s = new Setup(); s.Handler.Mode = mode;
            var result = await s.Run(); Check(result.Status == JobStatus.Failed && !result.Message.Contains("SECRET"));
            Check(s.Handler.Calls == (mode.StartsWith("late-") ? 2 : 1)); s.Old();
        }
    }
    private static async Task Changed()
    {
        await using var s = new Setup(); s.Handler.BeforeReply = () => s.Store.Put("materials", "l", new { digest = "changed" });
        Check((await s.Run()).Status == JobStatus.Failed); s.Old();
    }
    private static async Task Cancel()
    {
        await using var s = new Setup(); s.Handler.Mode = "block"; await s.Runtime.SelectAccountAsync("a");
        var job = s.Runtime.AnalyzeLesson("l"); await s.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A setting change must never retarget the running request/snapshot.
        s.Models.Save(s.Store, "http://localhost:1234/v1", "other-model", "keep");
        await s.Runtime.SelectAccountAsync("b"); Check((await s.Runtime.Jobs.WaitForTerminalAsync(job)).Status == JobStatus.Interrupted);
        Check(s.Handler.Cancelled && s.Handler.Calls == 1); s.Old(); Check(s.Workspace.OpenAccount("b").Get("analysis", "l").ValueKind == JsonValueKind.Null);
    }
    private static async Task Partial()
    {
        await using var s = new Setup(); s.Handler.Mode = "partial"; Check((await s.Run()).Status == JobStatus.Completed && s.Handler.Calls == 3);
        var analysis = s.Store.Get("analysis", "l"); Check(analysis.GetProperty("timeline_status").GetString() == "partial");
        Check(analysis.GetProperty("sections")[0].GetProperty("kind").GetString() == "unclassified" && analysis.GetProperty("knowledge_points").GetArrayLength() == 1);
    }
    private static async Task Preconditions()
    {
        await using (var s = new Setup(configure: false)) { Check((await s.Run()).Status == JobStatus.Failed && s.Handler.Calls == 0); s.Old(); }
        await using (var s = new Setup())
        {
            s.Store.Put("materials", "l", new { digest = "empty", segments = Array.Empty<object>() });
            Check((await s.Run()).Status == JobStatus.Failed && s.Handler.Calls == 0); s.Old();
            var bridge = new WorkspaceBridge(s.Workspace, s.Runtime, models: s.Models);
            var response = await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, """{"v":1,"id":"r1","method":"analyze_lesson","owner":"a","args":{"lesson":"l","acknowledge_cost":false}}""");
            using var doc = JsonDocument.Parse(response!); Check(!doc.RootElement.GetProperty("ok").GetBoolean());
        }
    }
    internal sealed class SyntheticModel : HttpMessageHandler
    {
        public string Mode = "ok"; public int Calls; public bool Cancelled; public Action? BeforeReply;
        public readonly List<string> Prompts = [];
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Check(request.RequestUri!.Host == "localhost" && request.RequestUri.Port == 11434 && request.Headers.Authorization == null);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var prompt = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!; Prompts.Add(prompt); Entered.TrySetResult();
            if (Mode == "block") try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); } catch (OperationCanceledException) { Cancelled = true; throw; }
            if (Mode == "auth" || Mode == "late-auth" && Calls > 1) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("SECRET") };
            if (Mode == "quota") return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("SECRET") };
            if (Mode == "late-network" && Calls > 1) throw new HttpRequestException("SECRET");
            var matches = Regex.Matches(prompt, @"\[(s\d+) \d{2}:");
            var ids = matches.Select(m => m.Groups[1].Value).Distinct().ToArray();
            var first = ids.FirstOrDefault() ?? "s0"; var last = ids.LastOrDefault() ?? "s0";
            var partial = Mode is "partial" or "late-auth" or "late-network";
            var topic = new { topics = new[] { new { title = "合成主题", summary = "合成摘要", start_id = partial ? "missing" : "t0" } }, covered_until_id = "t1" };
            object output = matches.Count == 0 ? topic : new { summary = "合成摘要", topics = new[] { new { title = "合成主题", summary = "合成摘要", start_id = partial ? "missing" : first } }, covered_until_id = last,
                knowledge_points = new[] { new { text = "合成知识", evidence_ids = new[] { Mode == "evidence" ? "missing" : first } } }, assignments = Array.Empty<object>(), notices = Array.Empty<object>(), review_suggestions = Array.Empty<string>() };
            var system = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            if (system.StartsWith("将学生问题转成", StringComparison.Ordinal))
                output = new { keywords = new[] { prompt } };
            else if (system.StartsWith("你是课程学习助手", StringComparison.Ordinal))
                output = new { answer = "来自课堂 [c0]", evidence_ids = new[] { "c0" } };
            var text = Mode == "json" || Mode == "repair" && Calls == 1 ? "INVALID JSON" : JsonSerializer.Serialize(output);
            BeforeReply?.Invoke();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } })) };
        }
    }
}
