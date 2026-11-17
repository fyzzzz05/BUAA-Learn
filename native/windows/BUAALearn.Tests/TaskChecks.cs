using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

internal static class TaskChecks
{
    public static async Task ReconcileAndReadAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        var account = workspace.RegisterAuthenticatedAccount("a");
        workspace.RegisterAuthenticatedAccount("b");
        account.Put("course", "c", new { id = "c" });
        account.Put("lesson", "l", new { id = "l", course_id = "c" });
        account.Put("materials", "l", new { digest = "test" });
        account.SaveAnalysis(Analysis("原版课堂原话", "保留", "移除"), "test");

        await using var runtime = new SchoolSyncRuntime(workspace);
        await runtime.SelectAccountAsync("a");
        var bridge = new WorkspaceBridge(workspace, runtime);
        async Task<JsonElement> Call(string method, object args, string owner = "a")
        {
            var request = JsonSerializer.Serialize(new { v = 1, id = "r1", method, owner, args });
            var response = await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, request);
            using var doc = JsonDocument.Parse(response!);
            return doc.RootElement.Clone();
        }
        var first = (await Call("work_items", new { })).GetProperty("result");
        var assignments = first.GetProperty("assignments").EnumerateArray().ToArray();
        var kept = assignments.Single(x => x.GetProperty("text").GetString() == "保留").GetProperty("id").GetString()!;
        var removed = assignments.Single(x => x.GetProperty("text").GetString() == "移除").GetProperty("id").GetString()!;
        var notice = first.GetProperty("notices")[0].GetProperty("id").GetString()!;
        Check((await Call("set_task_done", new { task = kept, done = true })).GetProperty("ok").GetBoolean());
        Check((await Call("set_task_done", new { task = kept, done = "true" })).GetProperty("error").GetString() == "invalid_request");
        account.SaveAnalysis(Analysis("新版课堂原话", "保留", "改写"), "test");

        var reader = new TaskReader(account);
        var list = reader.List();
        Check(list.Assignments.Single(x => x.Id == kept).Done);
        Check(list.Assignments.Single(x => x.Id == removed).Superseded);
        Check(!list.Assignments.Single(x => x.Text == "改写").Done);
        Check(reader.Source("task", kept, "l").Rows.Single().Text == "新版课堂原话");
        Check(reader.Source("task", removed, "l").Rows.Single().Text == "原版课堂原话");
        Check(reader.Source("notice", list.Notices.Single().Id, "l").Rows.Single().Text == "新版课堂原话");
        Check(!(await Call("work_source", new { kind = "notice", id = notice, lesson = "l" })).GetProperty("ok").GetBoolean());
        Check(!(await Call("set_task_done", new { task = removed, done = true })).GetProperty("ok").GetBoolean());
        Check((await Call("set_task_done", new { task = kept, done = false })).GetProperty("ok").GetBoolean());
        Check(!reader.List().Assignments.Single(x => x.Id == kept).Done);
        await runtime.SelectAccountAsync("b");
        Check((await Call("work_items", new { })).GetProperty("error").GetString() == "stale_account");
        Check((await Call("work_items", new { }, "b")).GetProperty("result").GetProperty("assignments").GetArrayLength() == 0);
        Check(!(await Call("work_source", new { kind = "task", id = removed, lesson = "l" }, "b")).GetProperty("ok").GetBoolean());
    }

    private static JsonElement Analysis(string original, params string[] assignments) => JsonSerializer.SerializeToElement(new {
        lesson_id = "l", course_id = "c", course_name = "课程", lesson_title = "课堂", created_at = original, digest = "test",
        sections = Array.Empty<object>(),
        evidence = new { s0 = new { id = "s0", start = 0, end = 5, text = original } },
        assignments = assignments.Select(text => new { text, evidence_ids = new[] { "s0" }, deadline = "周五" }),
        notices = new[] { new { text = "考试事项", evidence_ids = new[] { "s0" } } }
    });

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Task reconciliation or source assertion failed.");
    }
}
