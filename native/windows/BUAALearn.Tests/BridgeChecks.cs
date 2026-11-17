using System.Text.Json;
using BUAALearn.Platform;

internal static class BridgeChecks
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Bridge assertion failed."); }
    private static string Request(string method, string? owner = "a", object? args = null) => JsonSerializer.Serialize(new { v = 1, id = "r1", method, owner, args = args ?? new { } });
    public static async Task Run()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var a = workspace.RegisterAuthenticatedAccount("a"); workspace.RegisterAuthenticatedAccount("b");
        a.Put("lesson", "l", new { id = "l", course_id = "c" });
        await using var runtime = new SchoolSyncRuntime(workspace); await runtime.SelectAccountAsync("a");
        var bridge = new WorkspaceBridge(workspace, runtime);
        foreach (var source in new[] { "https://spoc.buaa.edu.cn/", "https://workspace.buaa.invalid.evil.test/index.html", "http://workspace.buaa.invalid/index.html", "https://workspace.buaa.invalid/index.html?x=1", "https://workspace.buaa.invalid:444/index.html", "https://user@workspace.buaa.invalid/index.html", "https://workspace.buaa.invalid/remote.html" })
            Check(await bridge.DispatchAsync(source, Request("workspace")) == null);
        Check(WorkspaceBridge.IsWorkspaceDocument(WorkspaceBridge.DocumentUrl + "#/courses"));
        async Task<JsonElement> Call(string body) => JsonSerializer.Deserialize<JsonElement>((await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, body))!);
        Check((await Call(Request("workspace"))).GetProperty("result").GetProperty("owner").GetString() == "a");
        foreach (var body in new[] { "{", Request("get_file", args: new { path = "/etc/passwd" }), Request("lesson", args: new { lesson = "l", owner = "b" }), "{\"v\":1,\"v\":1,\"id\":\"r\",\"owner\":\"a\",\"method\":\"workspace\",\"args\":{}}", new string('x', WorkspaceBridge.MaxRequestBytes + 1) })
            Check((await Call(body)).GetProperty("error").GetString() == "invalid_request");
        Check((await Call(Request("lesson", args: new { lesson = "l" }))).GetProperty("ok").GetBoolean());
        Check((await Call(Request("save_reading", args: new { lesson = "l", tab = "transcript", search = "中文", anchor = (string?)null, offset = 12 }))).GetProperty("ok").GetBoolean());
        Check((await Call(Request("reader", args: new { lesson = "l", tab = "transcript" }))).GetProperty("result").GetProperty("position").GetProperty("search").GetString() == "中文");
        Check((await Call(Request("save_reading", args: new { lesson = "l", tab = "transcript", search = "", anchor = (string?)null, offset = -1 }))).GetProperty("error").GetString() == "invalid_request");
        Check((await Call(Request("reader", args: new { lesson = "l", tab = "file" }))).GetProperty("error").GetString() == "invalid_request");
        var safeReplay = ReplayLink.ForLesson(JsonSerializer.SerializeToElement(new { id = "sub-1", recording_course_id = "recording_1", has_recording = true, source_url = "file:///unsafe" }));
        Check(safeReplay.Host == "classroom.msa.buaa.edu.cn" && safeReplay.Query.Contains("sub_id=sub-1") && !safeReplay.AbsoluteUri.Contains("unsafe"));
        try { ReplayLink.ForLesson(JsonSerializer.SerializeToElement(new { id = "../unsafe", recording_course_id = "r", has_recording = true })); throw new Exception("Expected rejected replay"); } catch (ArgumentException) { }
        Check((await Call(Request("select_account", args: new { account = "b" }))).GetProperty("owner").GetString() == "b");
        Check((await Call(Request("lesson", args: new { lesson = "l" }))).GetProperty("error").GetString() == "stale_account");
        Check((await Call(Request("lesson", "b", new { lesson = "l" }))).GetProperty("error").GetString() == "invalid_request");
        Check(workspace.OpenAccount("b").List("lesson").Length == 0);
        workspace.OpenAccount("b").Put("lesson", "l", new { id = "l", course_id = "b-private" });
        for (var i = 0; i < 50; i++)
        {
            await runtime.SelectAccountAsync("a");
            var read = Task.Run(() => Call(Request("lesson", args: new { lesson = "l" })));
            var change = Task.Run(() => runtime.SelectAccountAsync("b"));
            await Task.WhenAll(read, change);
            var response = await read;
            if (response.GetProperty("ok").GetBoolean())
                Check(response.GetProperty("owner").GetString() == "a" && response.GetProperty("result").GetProperty("lesson").GetProperty("course_id").GetString() == "c");
            else Check(response.GetProperty("error").GetString() == "stale_account");
        }
    }
}
