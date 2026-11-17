using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

internal static class SchoolSyncChecks
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        ("School sync explicitly selects terms and commits Shanghai calendar atomically", TermsAsync),
        ("Course/material queue crosses real Rust ABI, paginates and reopens offline without secrets", FlowAsync),
        ("Failed semester refresh preserves snapshots; real empty responses archive without deleting", SemesterFailureAsync),
        ("Catalogue ownership conflict rolls back the complete directory", CatalogueConflictAsync),
        ("Empty, failed and tampered material never overwrites previous material/evidence", MaterialFailureAsync),
        ("Recording expiry becomes needs_login without overwriting cached material", RecordingExpiryAsync),
        ("Portal refresh stays in memory; unrecoverable expiry disables school jobs", PortalRefreshAsync),
        ("Account switch cancels real HTTP callback and never retargets stored records", CancelHttpAsync),
        ("Cancellation reaches recording authorizer and prevents directory writes", CancelAuthorizationAsync),
        ("Reconnect reauthorizes cached lessons before fetching material", ReconnectAsync),
        ("Automation drafts save transactionally and retries follow Shanghai time without duplicate fetch", AutomationAsync),
        ("Closing recording authorization preserves portal login and cached directory", ClosedAuthorizationAsync)
    ];
    private const string Term = "2026-20271";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("School sync assertion failed."); }
    private static async Task<JobRecord> Done(SchoolSyncRuntime runtime, JobRecord job, JobStatus expected = JobStatus.Completed)
    {
        var result = await runtime.Jobs.WaitForTerminalAsync(job).WaitAsync(Limit);
        if (result.Status != expected) { Console.WriteLine($"Expected {expected}, got {result.Status}: {result.Message}"); Check(false); }
        return result;
    }
    private static SchoolConnection Connection(MockSchool school, MockAuthorizer authorizer, string owner = "a", string refresh = "") => new(owner, MockSchool.PortalSecret, refresh, "01", authorizer, handler: school);
    private static async Task Prepare(SchoolSyncRuntime runtime) => await Done(runtime, runtime.SyncSemester(Term));
    private static async Task Catalogue(SchoolSyncRuntime runtime) => await Done(runtime, runtime.SyncCourse("c1"));
    private static async Task TermsAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var school = new MockSchool(); var authorizer = new MockAuthorizer();
        await using var runtime = new SchoolSyncRuntime(workspace, new FixedClock());
        await runtime.ConnectAuthenticatedAsync(Connection(school, authorizer));
        await Done(runtime, runtime.RefreshSemesters());
        Check(runtime.Reader!.SelectedSemester == null && runtime.Reader.Courses().Count == 0);
        Check(runtime.Reader.Semesters().Select(x => x.Id).SequenceEqual(new[] { "2025-20262", Term }));
        Check(school.Count("queryKclb") == 0 && school.Count("queryRlData") == 0);
        await Done(runtime, runtime.SyncSemester("unknown"), JobStatus.Failed);
        Check(school.Count("queryKclb") == 0 && runtime.Reader.SelectedSemester == null);
        await Prepare(runtime);
        Check(runtime.Reader.SelectedSemester == Term && runtime.Reader.Courses().Count == 2);
        Check(school.ScheduleWindow == ("2026-10-26", "2026-11-23")); // UTC Nov 1 16:30 is Shanghai Nov 2.
        var schedule = runtime.Reader.Schedule();
        Check(schedule.GetProperty("rows").GetArrayLength() == 2 && schedule.GetProperty("start").GetString() == "2026-10-26");
        Check(schedule.GetProperty("rows")[1].GetProperty("course_id").GetString() == "unmapped");
        Check(authorizer.Count == 0);
    }
    private static async Task FlowAsync()
    {
        using var folder = new TemporaryDirectory();
        using (var workspace = NativeWorkspace.Open(new(folder.Path)))
        {
            var school = new MockSchool(); var authorizer = new MockAuthorizer();
            await using var runtime = new SchoolSyncRuntime(workspace, new FixedClock());
            await runtime.ConnectAuthenticatedAsync(Connection(school, authorizer));
            await Prepare(runtime);
            workspace.OpenAccount("a").MergeSchool([new("course", "c1", new { local_marker = "keep" })]);
            await Catalogue(runtime);
            var reader = runtime.Reader!;
            Check(reader.Courses()[0].GetProperty("id").GetString() == "c1");
            Check(reader.Courses()[0].GetProperty("local_marker").GetString() == "keep");
            Check(reader.Lessons("c1").Select(x => x.GetProperty("id").GetString()).SequenceEqual(new[] { "l3", "l1", "future" }));
            Check(reader.Lesson("future").GetProperty("date").GetString() == "");
            await Done(runtime, runtime.FetchMaterials("future"), JobStatus.Failed);
            Check(school.Count("get-sub-info") == 0);
            await Done(runtime, runtime.FetchMaterials("l1"));
            var material = reader.Materials("l1");
            Check(material.GetProperty("segments").GetArrayLength() == 1 && material.GetProperty("slides").GetArrayLength() == 2);
            Check(material.GetProperty("digest").GetString()!.Length == 64 && school.LastMaterialCourse == "child-recording");
            Check(school.Count("search-ppt") == 2 && authorizer.Count == 1);
            Check(reader.Lesson("l1").GetProperty("slide_count").GetInt32() == 2);
            Check(material.GetProperty("slides")[0].GetProperty("url").GetString() == "https://resource.msa.buaa.edu.cn/first.png");
            foreach (var kind in new[] { "meta", "course", "lesson", "materials", "job" })
                foreach (var row in workspace.OpenAccount("a").List(kind))
                    Check(!row.GetRawText().Contains("SYNTHETIC-"));
            Check(authorizer.LastUri!.Query.Contains(MockSchool.SignatureSecret) && authorizer.LastOwner == "a");
            Check(!authorizer.ChallengeText!.Contains(MockSchool.SignatureSecret));
        }
        using var reopened = NativeWorkspace.Open(new(folder.Path));
        await using var offline = new SchoolSyncRuntime(reopened);
        Check(!offline.Jobs.IsSchoolConnected && offline.Reader!.Courses().Count == 2);
        Check(offline.Reader!.Materials("l1").GetProperty("slides").GetArrayLength() == 2);
        Check(offline.FetchMaterials("l1").Status == JobStatus.NeedsLogin);
    }
    private static async Task SemesterFailureAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var school = new MockSchool(); await using var runtime = new SchoolSyncRuntime(workspace, new FixedClock());
        await runtime.ConnectAuthenticatedAsync(Connection(school, new())); await Prepare(runtime);
        var store = workspace.OpenAccount("a"); var old = store.Get("course", "c1").GetRawText(); var schedule = runtime.Reader!.Schedule().GetRawText();
        school.BadSchedule = true; school.NewCourseName = "未完成的改名";
        await Done(runtime, runtime.SyncSemester(Term), JobStatus.Failed);
        Check(store.Get("course", "c1").GetRawText() == old && runtime.Reader.Schedule().GetRawText() == schedule);
        school.BadSchedule = false; school.EmptyCourses = true;
        await Prepare(runtime);
        Check(runtime.Reader.Courses().Count == 0 && runtime.Reader.Courses(includeArchived: true).Count == 2);
        Check(store.Get("course", "c1").GetRawText() == old);
        school.BadScheduleDate = true;
        var saved = runtime.Reader.Schedule().GetRawText();
        await Done(runtime, runtime.SyncSchedule(), JobStatus.Failed);
        Check(runtime.Reader.Schedule().GetRawText() == saved);
    }
    private static async Task CatalogueConflictAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var school = new MockSchool(); await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(school, new())); await Prepare(runtime);
        var store = workspace.OpenAccount("a");
        store.Put("lesson", "l1", new { id = "l1", course_id = "c2", title = "另一课程的旧记录" });
        var previous = store.Get("course", "c1").GetRawText();
        await Done(runtime, runtime.SyncCourse("c1"), JobStatus.Failed);
        Check(store.Get("course", "c1").GetRawText() == previous && store.Get("lesson", "l3").ValueKind == JsonValueKind.Null);
        Check(store.Get("lesson", "l1").GetProperty("course_id").GetString() == "c2");
    }
    private static async Task MaterialFailureAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var school = new MockSchool(); await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(school, new())); await Prepare(runtime); await Catalogue(runtime);
        await Done(runtime, runtime.FetchMaterials("l1"));
        var store = workspace.OpenAccount("a"); var previous = store.Get("materials", "l1").GetRawText();
        var before = store.Get("lesson", "l1").GetRawText();
        store.Put("analysis", "l1", new { evidence = new { s0 = new { text = "旧证据快照" } }, material_digest = "old" });
        var analysis = store.Get("analysis", "l1").GetRawText();
        school.FailSecondPpt = true;
        await Done(runtime, runtime.FetchMaterials("l1"), JobStatus.Failed);
        Check(store.Get("materials", "l1").GetRawText() == previous && store.Get("lesson", "l1").GetRawText() == before);
        school.FailSecondPpt = false; school.EmptyMaterials = true;
        var pending = await Done(runtime, runtime.FetchMaterials("l1"), JobStatus.Failed);
        Check(pending.Message.Contains("尚未就绪") && store.Get("materials", "l1").GetRawText() == previous);
        school.EmptyMaterials = false; school.TranscriptText = "刷新后的课堂内容";
        await Done(runtime, runtime.FetchMaterials("l1"));
        Check(store.Get("analysis", "l1").GetRawText() == analysis && store.Get("materials", "l1").GetRawText() != previous);
        var tampered = JsonSerializer.SerializeToNode(store.Get("materials", "l1"))!.AsObject(); tampered["digest"] = "invalid";
        var final = store.Get("materials", "l1").GetRawText();
        try { store.SaveMaterials("c1", "l1", JsonSerializer.SerializeToElement(tampered)); throw new InvalidOperationException("Tampered snapshot accepted"); }
        catch (CoreException) { }
        Check(store.Get("materials", "l1").GetRawText() == final);
    }
    private static async Task RecordingExpiryAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var school = new MockSchool(); await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(school, new())); await Prepare(runtime); await Catalogue(runtime);
        await Done(runtime, runtime.FetchMaterials("l1")); var old = runtime.Reader!.Materials("l1").GetRawText();
        school.RecordingExpired = true;
        var expired = await Done(runtime, runtime.FetchMaterials("l1"), JobStatus.NeedsLogin);
        Check(expired.Message.Contains("录播授权") && !runtime.Jobs.IsSchoolConnected && runtime.Reader.Materials("l1").GetRawText() == old);
        var count = school.Total;
        Check(runtime.SyncSchedule().Status == JobStatus.NeedsLogin && school.Total == count);
    }
    private static async Task PortalRefreshAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var school = new MockSchool { NeedRefresh = true }; await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(school, new(), refresh: "SYNTHETIC-REFRESH-SECRET"));
        await Prepare(runtime);
        Check(school.Count("refreshToken") == 1 && runtime.Jobs.IsSchoolConnected);
        Check(!workspace.OpenAccount("a").List("meta").Any(x => x.GetRawText().Contains("SYNTHETIC-")));
        school.PortalExpired = true;
        var previous = runtime.Reader!.Schedule().GetRawText();
        await Done(runtime, runtime.SyncSchedule(), JobStatus.NeedsLogin);
        Check(runtime.Reader.Schedule().GetRawText() == previous && !runtime.Jobs.IsSchoolConnected);
    }
    private static async Task CancelHttpAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        workspace.RegisterAuthenticatedAccount("b");
        var school = new MockSchool { BlockCourses = true }; await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(school, new()));
        var job = runtime.SyncSemester(Term);
        await school.Entered.Task.WaitAsync(Limit);
        var bridge = new WorkspaceBridge(workspace, runtime);
        var active = JsonSerializer.Deserialize<JsonElement>((await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl,
            JsonSerializer.Serialize(new { v = 1, id = "status", method = "workspace", owner = "a", args = new { } })))!);
        Check(active.GetProperty("result").GetProperty("active_job").GetProperty("id").GetString() == job.Id);
        await runtime.SelectAccountAsync("b").WaitAsync(Limit);
        await school.Cancelled.Task.WaitAsync(Limit);
        Check((await runtime.Jobs.WaitForTerminalAsync(job)).Status == JobStatus.Interrupted);
        Check(runtime.Jobs.CurrentOwner == "b" && !runtime.Jobs.IsSchoolConnected && runtime.Reader!.Courses(Term).Count == 0);
        Check(workspace.OpenAccount("a").List("course").Length == 0 && workspace.OpenAccount("b").List("course").Length == 0);
        var other = new MockSchool(); await runtime.ConnectAuthenticatedAsync(Connection(other, new(), "b"));
        await Prepare(runtime); Check(runtime.Reader!.Owner == "b" && runtime.Reader.Courses().Count == 2);
        Check(school.Count("queryKclb") == 1 && workspace.OpenAccount("a").List("course").Length == 0);
    }
    private static async Task CancelAuthorizationAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        workspace.RegisterAuthenticatedAccount("b");
        var school = new MockSchool(); var authorizer = new MockAuthorizer { Block = true };
        await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(school, authorizer)); await Prepare(runtime);
        var job = runtime.SyncCourse("c1"); await authorizer.Entered.Task.WaitAsync(Limit);
        await runtime.SelectAccountAsync("b").WaitAsync(Limit);
        Check((await runtime.Jobs.WaitForTerminalAsync(job)).Status == JobStatus.Interrupted && school.Count("catalogue") == 0);
        Check(workspace.OpenAccount("a").List("lesson").Length == 0 && runtime.Reader!.Lessons("c1").Count == 0);
    }
    private static async Task ReconnectAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(new(), new())); await Prepare(runtime); await Catalogue(runtime);
        await runtime.DisconnectAsync();
        Check(!runtime.Jobs.IsSchoolConnected && runtime.Reader!.Lessons("c1").Count == 3);
        var next = new MockSchool(); var authorizer = new MockAuthorizer();
        await runtime.ConnectAuthenticatedAsync(Connection(next, authorizer));
        await Done(runtime, runtime.FetchMaterials("l1"));
        Check(authorizer.Count == 1 && next.Count("catalogue") == 1 && runtime.Reader!.Materials("l1").ValueKind == JsonValueKind.Object);
    }
    private static async Task ClosedAuthorizationAsync()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var authorizer = new MockAuthorizer(); var school = new MockSchool(); await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.ConnectAuthenticatedAsync(Connection(school, authorizer)); await Prepare(runtime); await Catalogue(runtime);
        var previous = runtime.Reader!.Lessons("c1").Select(x => x.GetRawText()).ToArray();
        authorizer.Close = true; var job = await Done(runtime, runtime.SyncCourse("c1"), JobStatus.Failed);
        Check(job.Message.Contains("已取消") && runtime.Jobs.IsSchoolConnected && school.Count("catalogue") == 1);
        Check(runtime.Reader.Lessons("c1").Select(x => x.GetRawText()).SequenceEqual(previous));
    }

    private static async Task AutomationAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        var school = new MockSchool { EmptyMaterials = true };
        var clock = new FixedClock();
        await using var runtime = new SchoolSyncRuntime(workspace, clock);
        await runtime.ConnectAuthenticatedAsync(Connection(school, new MockAuthorizer()));
        await Prepare(runtime);
        var store = workspace.OpenAccount("a");
        var bridge = new WorkspaceBridge(workspace, runtime);
        async Task<bool> Save(params object[] changes)
        {
            var request = JsonSerializer.Serialize(new { v = 1, id = "auto", owner = "a", method = "save_automation", args = new { changes } });
            using var reply = JsonDocument.Parse((await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, request))!);
            return reply.RootElement.GetProperty("ok").GetBoolean();
        }
        var enable = new { course = "c1", enabled = true, auto_analyze = false };
        Check(!await Save(enable, new { course = "missing", enabled = true, auto_analyze = false }));
        Check(store.List("automation").Length == 0 && await Save(enable));
        var rule = store.Get("automation", "c1");
        Check(rule.GetProperty("tracking_from").GetString() == "2026-11-01");
        await Done(runtime, runtime.Jobs.Enqueue("automation", "c1"), JobStatus.Failed);
        Check(school.Count("get-sub-info") == 1);
        await Done(runtime, runtime.Jobs.Enqueue("automation", "c1"));
        Check(school.Count("get-sub-info") == 1);
        clock.Now = clock.Now.AddHours(2);
        school.EmptyMaterials = false;
        await Done(runtime, runtime.Jobs.Enqueue("automation", "c1"));
        Check(school.Count("get-sub-info") == 2 && store.Get("automation", "c1").GetProperty("pending_count").GetInt32() == 0);
        await Done(runtime, runtime.Jobs.Enqueue("automation", "c1"));
        Check(school.Count("get-sub-info") == 2);
        Check(await Save(new { course = "c1", enabled = false, auto_analyze = false }));
    }

    private sealed class FixedClock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T16:30:00Z", CultureInfo.InvariantCulture); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class MockAuthorizer : IRecordingAuthorizer
    {
        public int Count; public bool Block; public bool Close;
        public Uri? LastUri; public string? LastOwner; public string? ChallengeText;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RecordingAuthorization> AuthorizeAsync(RecordingChallenge challenge, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Count); LastUri = challenge.EntryUri; LastOwner = challenge.Owner; ChallengeText = challenge.ToString();
            Entered.TrySetResult();
            if (Close) throw new RecordingAuthorizationCancelledException();
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return new("root-recording", MockSchool.RecordingSecret);
        }
    }
    private sealed class MockSchool : HttpMessageHandler
    {
        public const string PortalSecret = "SYNTHETIC-PORTAL-SECRET";
        public const string RecordingSecret = "SYNTHETIC-RECORDING-SECRET";
        public const string SignatureSecret = "SYNTHETIC-SIGNED-ENTRY";
        private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);
        public bool BadSchedule, BadScheduleDate, EmptyCourses, FailSecondPpt, EmptyMaterials, RecordingExpired, NeedRefresh, PortalExpired, BlockCourses;
        public string? NewCourseName; public string TranscriptText = "合成课堂原文";
        public (string Start, string End) ScheduleWindow; public string? LastMaterialCourse;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count(string path) => counts.GetValueOrDefault(path);
        public int Total => counts.Values.Sum();
        private static string? Query(Uri uri, string name) => uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).Where(x => x.Length == 2 && x[0] == name).Select(x => Uri.UnescapeDataString(x[1])).FirstOrDefault();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; var path = uri.AbsolutePath.Split('/')[^1]; counts.AddOrUpdate(path, 1, (_, v) => v + 1);
            if (uri.Host == "spoc.buaa.edu.cn")
            {
                Check(request.Headers.Authorization == null && request.Headers.GetValues("RoleCode").Single() == "01");
                var token = request.Headers.GetValues("Token").Single();
                if (path == "refreshToken")
                {
                    if (PortalExpired) return Response(new { code = 401 }, HttpStatusCode.Unauthorized);
                    return Response(new { code = 200, content = new { token = "SYNTHETIC-NEW-PORTAL", refreshToken = "SYNTHETIC-NEW-REFRESH" } });
                }
                if (PortalExpired || (NeedRefresh && token != "Inco-SYNTHETIC-NEW-PORTAL")) return Response(new { code = 401 }, HttpStatusCode.Unauthorized);
                Check(token == (NeedRefresh ? "Inco-SYNTHETIC-NEW-PORTAL" : "Inco-" + PortalSecret));
            }
            else
            {
                Check(!request.Headers.Contains("Token") && !request.Headers.Contains("RoleCode") && request.Headers.Authorization?.ToString() == "Bearer " + RecordingSecret);
                if (RecordingExpired) return Response(new { code = 5002 }, HttpStatusCode.Unauthorized);
            }
            if (path == "queryKclb" && BlockCourses)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            switch (path)
            {
                case "queryXnxq": return Response(new { code = 200, content = new[] { new { xnxq = "2025-20262" }, new { xnxq = Term } } });
                case "queryKclb":
                    return Response(new { code = 200, content = EmptyCourses ? [] : new[] {
                        new { juese = "3", kcid = "c1", kcmc = NewCourseName ?? "合成课程甲", rwh = "task-1", jw_kcdm = "B", skjs = "教师甲" },
                        new { juese = "3", kcid = "c2", kcmc = "合成课程乙", rwh = "task-2", jw_kcdm = "A", skjs = "教师乙" } } });
                case "queryRlData":
                    ScheduleWindow = (Query(uri, "zksrq")!, Query(uri, "zjsrq")!);
                    if (BadSchedule) return Response(new { code = 503 }, HttpStatusCode.ServiceUnavailable);
                    return Response(new { code = 200, content = new[] {
                        new { kcid = "c1", kcmc = "合成课程甲", rq = BadScheduleDate ? "invalid" : "2026-11-01", strkcsj = "08:00-09:35", skdd = "测试教室", jsxm = "教师甲", zjdm = "binding" },
                        new { kcid = "unmapped", kcmc = "未关联课程", rq = "2026-11-02", strkcsj = "10:00-11:35", skdd = "", jsxm = "", zjdm = "other-binding" } } });
                case "queryYhSflx": return Response(new { code = 200, content = "2" });
                case "getHxyTimestampAndSignature2": return Response(new { code = 200, content = new { bind_id = "binding", ktsl_url = "https://portal.msa.buaa.edu.cn/join", signature = SignatureSecret, username = "synthetic-name" } });
                case "catalogue": return Response(new { code = 0, result = new { data = new[] {
                    new { sub_id = "l1", title = "第一节", course_id = "child-recording", start_at = 1793404800L, lecturer_name = "教师甲", content = "{\"save_playback\":{\"contents\":[1],\"contents_duration\":90}}" },
                    new { sub_id = "future", title = "未生成回放", course_id = "", start_at = 0L, lecturer_name = "教师甲", content = "{}" },
                    new { sub_id = "l3", title = "较新课次", course_id = "", start_at = 1793491200L, lecturer_name = "教师甲", content = "{\"save_playback\":{\"contents\":[1],\"contents_duration\":90}}" } } } });
                case "get-sub-info":
                    LastMaterialCourse = Query(uri, "course_id");
                    return Response(new { code = 0, data = new { resource_guid = "guid", duration = 90, video_list = new[] { new { preview_url = "https://resource.msa.buaa.edu.cn/video.mp4?token=SYNTHETIC-VIDEO-SECRET", type = "2", duration = 90 } } } });
                case "search-trans-result": return Response(new { code = 0, list = EmptyMaterials ? [] : new[] { new { all_content = new[] { new { Text = TranscriptText, BeginSec = 0, EndSec = 10 } } } } });
                case "search-ppt":
                    var second = Query(uri, "page") == "2";
                    if (second && FailSecondPpt) return Response(new { code = 503 }, HttpStatusCode.ServiceUnavailable);
                    return Response(new { code = 0, total = EmptyMaterials ? 0 : 2, list = EmptyMaterials ? [] : new[] { new { created_sec = second ? 50 : 0, content = new { pptimgurl = second ? "https://resource.msa.buaa.edu.cn/second.png#SYNTHETIC-IMAGE-SECRET" : "https://resource.msa.buaa.edu.cn/first.png?token=SYNTHETIC-IMAGE-SECRET" } } } });
                default: throw new InvalidOperationException("Unexpected synthetic endpoint");
            }
        }
        private static HttpResponseMessage Response(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
