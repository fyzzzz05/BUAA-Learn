using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

if (args is ["--workspace-preview", var assets, var previewPort])
    return WorkspacePreview.Run(Path.GetFullPath(assets), int.Parse(previewPort, System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();

if (args is ["--workspace-lease-probe", var probePath])
{
    try { using var workspace = NativeWorkspace.Open(new(probePath)); return 0; }
    catch (CoreException error) when (error.Message == "学习空间正在使用中，请关闭另一个实例后重试。") { return 7; }
    catch { return 8; }
}

var tests = new List<(string Name, Action Run)>
{
    ("ABI and repeated owned UTF-8 results", () =>
    {
        CoreBridge.CheckAbi();
        for (var i = 0; i < 2000; i++)
            Check(CoreBridge.ParseSemesters(Fixture("semesters"))[0].GetString() == "2026-20271");
        Throws<CoreException>(() => CoreBridge.ParseSemesters("{}"));
        Throws<ArgumentException>(() => CoreBridge.ParseSemesters("{}\0ignored"));
    }),
    ("Unicode database, account isolation, reopen and disposed handle", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "北航 Windows 测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "学习.sqlite3");
            var store = new CoreStore(path);
            using (store)
            {
                store.Put("合成账号甲", "course", "1", new { name = "课程 α 中文", count = 3 });
                Check(store.Get("合成账号乙", "course", "1").ValueKind == JsonValueKind.Null);
                Check(store.Get("合成账号甲", "course", "1").GetProperty("name").GetString() == "课程 α 中文");
                Throws<CoreException>(() => store.Put("a", "unsupported", "1", new { }));
            }
            Throws<ObjectDisposedException>(() => store.Get("a", "course", "1"));
            using var reopened = new CoreStore(path);
            Check(reopened.Get("合成账号甲", "course", "1").GetProperty("count").GetInt32() == 3);
        }
        finally { Directory.Delete(directory, true); }
    }),
    ("Native to managed callback survives GC and retains explicit term", () =>
    {
        var transport = new FakeTransport();
        using var school = new CoreSchool(transport, "synthetic-token", "", "01");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(school.Semesters().GetArrayLength() == 2);
        var courses = school.Courses("2025-20262");
        Check(courses[0].GetProperty("name").GetString() == "合成测试课程");
        Check(transport.TermSeen);
        Throws<ArgumentException>(() => school.Courses("x\0y"));
    }),
    ("Recording signature, catalogue and material ABI end to end", () =>
    {
        using var school = new CoreSchool(new RecordingTransport(), "synthetic-token", "", "01");
        var entry = LoginProtocol.RecordingEntry(school.RecordingEntry("course", "task"));
        Check(entry.Host == "portal.msa.buaa.edu.cn");
        Throws<CoreException>(() => school.Catalogue("recording-course"));
        school.SetRecording("synthetic-recording-token");
        var rows = school.Catalogue("recording-course");
        Check(rows.GetArrayLength() == 1 && rows[0].GetProperty("has_recording").GetBoolean());
        var material = school.Materials("recording-course", "lesson", 90);
        Check(material.GetProperty("segments")[0].GetProperty("text").GetString() == "这是一段合成课堂文本");
        Check(material.GetProperty("slides").GetArrayLength() == 0);
        Check(material.GetProperty("digest").GetString()!.Length == 64);
        Throws<CoreException>(() => school.Materials("recording-course", "lesson", double.NaN));
    }),
    ("Callback root released on handle disposal", () =>
    {
        var (school, weak) = CreateWeakTransport();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Check(weak.IsAlive);
        school.Semesters(); school.Dispose(); school.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Check(!weak.IsAlive);
        Throws<ObjectDisposedException>(() => school.Semesters());
    }),
    ("Managed callback failure becomes native error envelope", () =>
    {
        using var school = new CoreSchool(new ThrowingTransport(), "synthetic-token", "", "01");
        var error = Throws<CoreException>(() => school.Semesters());
        Check(error.Code == "school_transport" && !error.Message.Contains("SENSITIVE"));
    }),
    ("Network host and credential isolation", () =>
    {
        foreach (var url in new[] { "http://spoc.buaa.edu.cn/x", "https://spoc.buaa.edu.cn.evil.test/x", "https://user:pass@spoc.buaa.edu.cn/x", "https://spoc.buaa.edu.cn:444/x", "https://spoc.buaa.edu.cn/x#fragment" })
            Throws<CoreException>(() => SchoolTransport.ValidateRequest(new Uri(url), "GET", new Dictionary<string, string>()));
        Throws<CoreException>(() => SchoolTransport.ValidateRequest(new Uri("https://classroom.msa.buaa.edu.cn/x"), "GET", new Dictionary<string, string> { ["tOkEn"] = "secret" }));
        Throws<CoreException>(() => SchoolTransport.ValidateRequest(new Uri("https://spoc.buaa.edu.cn/x"), "GET", new Dictionary<string, string> { ["Authorization"] = "secret" }));
        Throws<CoreException>(() => SchoolTransport.ValidateRequest(new Uri("https://spoc.buaa.edu.cn/x"), "GET", new Dictionary<string, string> { ["Token"] = "a\r\nb" }));
    }),
    ("HTTP redirect and excessive response rejected", () =>
    {
        using var redirect = new SchoolTransport(CancellationToken.None, new Handler(_ => new HttpResponseMessage(HttpStatusCode.Found)));
        Throws<CoreException>(() => redirect.Send(Request()));
        using var oversized = new SchoolTransport(CancellationToken.None, new Handler(_ =>
        {
            var content = new ByteArrayContent([]); content.Headers.ContentLength = SchoolTransport.MaxResponseBytes + 1L;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        Throws<CoreException>(() => oversized.Send(Request()));
    }),
    ("HTTP callback cancellation and Unicode body", () =>
    {
        using var transport = new SchoolTransport(CancellationToken.None, new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("中文响应", Encoding.UTF8) }));
        using var body = JsonDocument.Parse(transport.Send(Request()));
        Check(body.RootElement.GetProperty("body").GetString() == "中文响应");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var cancelled = new SchoolTransport(cancellation.Token, new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Throws<OperationCanceledException>(() => cancelled.Send(Request()));
    }),
    ("Browser navigation and signed HTTP upgrade policy", () =>
    {
        Check(LoginProtocol.IsAllowed(new Uri("https://sso.buaa.edu.cn/login")));
        Check(!LoginProtocol.IsAllowed(new Uri("https://spoc.buaa.edu.cn.evil.test/")));
        Check(!LoginProtocol.IsAllowed(new Uri("https://unknown.buaa.edu.cn/")));
        Check(!LoginProtocol.CanCapture(new Uri("https://sso.buaa.edu.cn/login")));
        Check(LoginProtocol.UpgradeRecording(new Uri("http://yjapi.msa.buaa.edu.cn/x?signature=synthetic"))?.Scheme == "https");
        Check(LoginProtocol.UpgradeRecording(new Uri("http://other.buaa.edu.cn/x")) == null);
        Check(LoginProtocol.CourseId(new Uri("https://classroom.msa.buaa.edu.cn/x?course_id=123")) == "123");
        Check(LoginProtocol.CourseId(new Uri("https://sso.buaa.edu.cn/x?course_id=123")) == null);
    }),
    ("Recording cookie domain, encoding and entry isolation", () =>
    {
        const string cookie = "a:1:{s:6:\"_token\";i:0;s:9:\"synthetic\";}";
        Check(LoginProtocol.RecordingBearer("_token", ".msa.buaa.edu.cn", Uri.EscapeDataString(cookie)) == "synthetic");
        Check(LoginProtocol.RecordingBearer("_token", "evil.test", cookie) == null);
        Check(LoginProtocol.RecordingBearer("_token", "msa.buaa.edu.cn.evil.test", cookie) == null);
        using var signature = JsonDocument.Parse("{\"ktsl_url\":\"https://portal.msa.buaa.edu.cn/join\",\"realname\":\"合成 名字\",\"signature\":\"a&b\"}");
        var uri = LoginProtocol.RecordingEntry(signature.RootElement);
        Check(uri.Query.Contains("signature=a%26b"));
        using var invalid = JsonDocument.Parse("{\"ktsl_url\":\"https://evil.test\"}");
        Throws<CoreException>(() => LoginProtocol.RecordingEntry(invalid.RootElement));
    })
};
tests.AddRange(WorkspaceJobChecks.All.Select(x => (x.Name, (Action)(() => x.Run().GetAwaiter().GetResult()))));
tests.AddRange(SchoolSyncChecks.All.Select(x => (x.Name, (Action)(() => x.Run().GetAwaiter().GetResult()))));
tests.AddRange(ReaderChecks.All);
tests.Add(("Legacy migration backs up WAL, isolates accounts and rolls back conflicts and pending PDF moves", MigrationChecks.Run));
tests.Add(("Work list preserves completion, archived evidence and account isolation across reanalysis", () => TaskChecks.ReconcileAndReadAsync().GetAwaiter().GetResult()));
tests.AddRange(LearningChecks.All.Select(x => (x.Name, (Action)(() => x.Run().GetAwaiter().GetResult()))));
tests.AddRange(ModelChecks.All.Select(x => (x.Name, (Action)(() => x.Run().GetAwaiter().GetResult()))));
if (OperatingSystem.IsWindows()) tests.Add(("Windows Credential Manager native write/read/delete round trip", () => ModelChecks.WindowsCredentialRoundTrip().GetAwaiter().GetResult()));
else Console.WriteLine("SKIP Windows Credential Manager native runtime (requires Windows).");
tests.AddRange(SlideCacheChecks.All.Select(x => (x.Name, (Action)(() => x.Run().GetAwaiter().GetResult()))));
tests.AddRange(SchoolLoginChecks.All.Select(x => (x.Name, (Action)(() => x.Run().GetAwaiter().GetResult()))));
tests.Add(("Workspace bridge rejects remote sources, arbitrary commands, malformed requests and stale accounts", () => BridgeChecks.Run().GetAwaiter().GetResult()));
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}\n{error.StackTrace}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} checks passed (synthetic data, no school login).");
return failed == 0 ? 0 : 1;

static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static T Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T error) { return error; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json"));
static string Request() => JsonSerializer.Serialize(new { url = "https://spoc.buaa.edu.cn/test", method = "GET", headers = new { }, body = (string?)null });
[MethodImpl(MethodImplOptions.NoInlining)]
static (CoreSchool, WeakReference) CreateWeakTransport()
{
    var transport = new FakeTransport();
    return (new CoreSchool(transport, "synthetic-token", "", "01"), new WeakReference(transport));
}

sealed class FakeTransport : ISchoolTransport
{
    public bool TermSeen { get; private set; }
    public string Send(string request)
    {
        using var document = JsonDocument.Parse(request);
        var url = document.RootElement.GetProperty("url").GetString()!;
        var courses = url.Contains("queryKclb");
        TermSeen |= courses && url.Contains("2025-20262");
        var name = courses ? "courses" : "semesters";
        return JsonSerializer.Serialize(new { status = 200, body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")) });
    }
}
sealed class ThrowingTransport : ISchoolTransport
{
    public string Send(string request) => throw new Exception("SENSITIVE failure must not escape callback");
}
sealed class RecordingTransport : ISchoolTransport
{
    public string Send(string request)
    {
        using var document = JsonDocument.Parse(request);
        var data = document.RootElement;
        var uri = new Uri(data.GetProperty("url").GetString()!);
        var headers = data.GetProperty("headers").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!);
        SchoolTransport.ValidateRequest(uri, data.GetProperty("method").GetString()!, headers);
        object body = uri.AbsolutePath switch
        {
            "/spocnewht/kc/queryYhSflx" => new { code = 200, content = "2" },
            "/spocnewht/yun/getHxyTimestampAndSignature2" => new { code = 200, content = new { bind_id = "synthetic", ktsl_url = "https://portal.msa.buaa.edu.cn/join", signature = "synthetic" } },
            "/courseapi/v2/course/catalogue" => new { code = 0, result = new { data = new[] { new { sub_id = "lesson", title = "合成课次", content = "{\"save_playback\":{\"contents\":[1],\"contents_duration\":90}}" } } } },
            "/courseapi/v3/portal-home-setting/get-sub-info" => new { code = 0, data = new { resource_guid = "synthetic", duration = 90 } },
            "/courseapi/v3/web-socket/search-trans-result" => new { code = 0, list = new[] { new { all_content = new[] { new { Text = "这是一段合成课堂文本", BeginSec = 0, EndSec = 10 } } } } },
            "/pptnote/v1/schedule/search-ppt" => new { code = 0, list = Array.Empty<object>(), total = 0 },
            _ => throw new InvalidOperationException("Unexpected synthetic endpoint")
        };
        return JsonSerializer.Serialize(new { status = 200, body = JsonSerializer.Serialize(body) });
    }
}
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(action(request)); }
}
