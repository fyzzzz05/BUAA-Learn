using System.Text.Json;
using BUAALearn.Platform;

internal static class ReaderChecks
{
    public static IEnumerable<(string Name, Action Run)> All => [
        ("Offline reader searches Unicode and resolves immutable evidence without repairing invalid citations", Sources),
        ("Markdown exports partial notes, original evidence and current transcript to isolated UTF-8 files", () => Export().GetAwaiter().GetResult()),
        ("Reading memory survives reopen, isolates account/lesson/tab and resets changed snapshots", Memory)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Reader assertion failed."); }
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static void Seed(AccountStore store, string lesson = "l")
    {
        store.Put("lesson", lesson, new { id = lesson, course_id = "c" });
        store.Put("materials", lesson, Json("""{"segments":[{"id":"s1","start":10,"end":20,"text":"HELLO 中文原话"},{"id":"s0","start":0,"end":8,"text":"旧文本"},{"id":"bad","start":"oops","end":10,"text":"bad"}]}"""));
        store.Put("analysis", lesson, Json("""{"evidence":{"s0":{"start":0,"end":8,"text":"旧文本"},"s1":{"start":10,"end":20,"text":"HELLO 中文原话"},"s2":{"start":30,"end":40,"text":"最后一段"}}}"""));
    }
    private static void Sources()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var store = workspace.RegisterAuthenticatedAccount("a"); Seed(store); var reader = new LessonReader(store, "l");
        Check(reader.Transcript.Count == 2 && reader.Transcript[0].Id == "s0");
        Check(reader.Search("hello").Single().Id == "s1" && reader.Search("中文").Single().Id == "s1" && reader.Search("missing").Count == 0);
        Check(reader.Source(Json("""{"evidence_ids":["s0","s2"]}""")).Count == 3);
        Check(reader.Source(Json("""{"evidence_ids":["s0","fake"]}""")).Count == 0);
        Check(reader.SourceRange("s2", "s0").Count == 0 && reader.SourceRange("fake", "s2").Count == 0);
        store.Put("materials", "l", Json("""{"segments":[{"id":"s0","start":0,"end":8,"text":"刷新后的文本"}]}"""));
        Check(reader.Transcript.Single().Text == "刷新后的文本" && reader.SourceRange("s0", "s1")[0].Text == "旧文本");
    }
    private static async Task Export()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        var store = workspace.RegisterAuthenticatedAccount("a");
        var other = workspace.RegisterAuthenticatedAccount("b");
        var lesson = new { id = "l", course_id = "c", course_name = "中文课程", title = "课堂: 第一节/作业", has_recording = true,
            recording_course_id = "rc", source_url = "https://classroom.msa.buaa.edu.cn/?token=SECRET" };
        store.Put("lesson", "l", lesson);
        other.Put("lesson", "l", lesson);
        store.Put("materials", "l", Json("""{"segments":[{"id":"s0","start":90,"end":95,"text":"当前识别文本"},{"id":"s1","start":3600,"end":3605,"text":"最后一段"}]}"""));
        store.Put("analysis", "l", Json("""
            {"timeline_status":"partial","timeline_warnings":["部分待整理"],
             "sections":[{"start":0,"end":15,"title":"第一段","summary":"主题摘要","knowledge_points":[{"text":"主题知识点","evidence_ids":["s0"]}]}],
             "text_summaries":[{"start":0,"end":15,"summary":"完成的文本摘要"}],
             "assignments":[{"text":"提交练习","deadline":"周五","audience":"全班","submission":"课程平台","source_range":{"start_id":"s0","end_id":"s1","start":999,"end":1000}}],
             "notices":[{"text":"待核验事项","evidence_ids":["missing"]}],"review_suggestions":["复习重点"],
             "evidence":{"s0":{"start":0,"end":5,"text":"分析时的原话"},"s1":{"start":10,"end":15,"text":"第二段原话"}}}
            """));
        await using var runtime = new SchoolSyncRuntime(workspace);
        string? opened = null;
        var bridge = new WorkspaceBridge(workspace, runtime, openExportFolder: path => opened = path);
        async Task<JsonElement> Call(string method, string owner)
        {
            var request = JsonSerializer.Serialize(new { v = 1, id = "export", method, owner, args = new { lesson = "l" } });
            using var reply = JsonDocument.Parse((await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, request))!);
            return reply.RootElement.Clone();
        }
        await runtime.SelectAccountAsync("a");
        var result = (await Call("export_markdown", "a")).GetProperty("result");
        var path = result.GetProperty("path").GetString()!;
        var text = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
        Check(result.GetProperty("file_name").GetString() == "课堂-中文课程 · 课堂_ 第一节_作业.md");
        Check((await Call("open_markdown_folder", "a")).GetProperty("ok").GetBoolean() && opened == Path.GetDirectoryName(path));
        foreach (var expected in new[] { "原始回放", "部分待整理", "主题摘要", "主题知识点", "完成的文本摘要",
            "提交练习（00:00:00–00:00:15）", "截止：周五；对象：全班；提交：课程平台", "待核验事项（来源待核验）",
            "建议复习（非老师布置）", "复习重点", "分析时的原话", "当前识别文本", "[01:00:00–01:00:05] 最后一段" })
            Check(text.Contains(expected, StringComparison.Ordinal));
        Check(!text.Contains("SECRET", StringComparison.Ordinal) && !text.Contains("00:16:39", StringComparison.Ordinal));
        Check((await Call("export_markdown", "a")).GetProperty("result").GetProperty("path").GetString() == path &&
            Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1);
        other.Put("materials", "l", Json("""{"segments":[{"id":"s0","start":0,"end":2,"text":"另一账号的原文"}]}"""));
        await runtime.SelectAccountAsync("b");
        Check((await Call("export_markdown", "a")).GetProperty("error").GetString() == "stale_account");
        var otherPath = (await Call("export_markdown", "b")).GetProperty("result").GetProperty("path").GetString()!;
        Check(otherPath != path && File.ReadAllText(otherPath).Contains("另一账号的原文", StringComparison.Ordinal));
        Check(!File.ReadAllText(otherPath).Contains("笔记引用原文", StringComparison.Ordinal));
    }
    private static void Memory()
    {
        using var folder = new TemporaryDirectory();
        using (var workspace = NativeWorkspace.Open(new(folder.Path)))
        {
            var a = workspace.RegisterAuthenticatedAccount("a"); var b = workspace.RegisterAuthenticatedAccount("b"); Seed(a); Seed(a, "other"); Seed(b);
            var reader = new LessonReader(a, "l");
            reader.SavePosition(ReaderTab.Transcript, "中文", "s1", 123);
            reader.SavePosition(ReaderTab.Timeline, "old", "s0", 55);
            Check(reader.LastTab == ReaderTab.Timeline && reader.RestorePosition(ReaderTab.Transcript).Offset == 123);
            Check(new LessonReader(b, "l").RestorePosition(ReaderTab.Transcript).Offset == 0);
            Check(new LessonReader(a, "other").RestorePosition(ReaderTab.Transcript).Search == "");
            var count = a.List("meta").Length;
            try { reader.SavePosition(ReaderTab.Transcript, "x", null, double.NaN); throw new Exception("Expected rejection"); } catch (ArgumentException) { }
            Check(a.List("meta").Length == count);
        }
        using var reopened = NativeWorkspace.Open(new(folder.Path)); var store = reopened.OpenAccount("a"); var restored = new LessonReader(store, "l");
        Check(restored.RestorePosition(ReaderTab.Transcript) is { Search: "中文", Anchor: "s1", Offset: 123 });
        store.Put("materials", "l", Json("""{"segments":[{"id":"s1","start":10,"end":20,"text":"新快照"}]}"""));
        Check(restored.RestorePosition(ReaderTab.Transcript) is { Search: "中文", Anchor: null, Offset: 0 });
        Check(restored.RestorePosition(ReaderTab.Timeline) is { Anchor: "s0", Offset: 55 });
        restored.SavePosition(ReaderTab.Timeline, "", "missing", 30);
        Check(restored.RestorePosition(ReaderTab.Timeline) is { Anchor: null, Offset: 0 });
    }
}
