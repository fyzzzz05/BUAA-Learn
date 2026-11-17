using System.Net;
using System.Text;
using System.Text.Json;
using BUAALearn.Platform;

/// Loopback QA adapter for the exact native bridge and temporary synthetic DB.
/// Not referenced by the shipped App. Never accepts arbitrary paths or owners.
internal static class WorkspacePreview
{
    public static async Task<int> Run(string assets, int port)
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var store = workspace.RegisterAuthenticatedAccount("合成账号甲"); workspace.RegisterAuthenticatedAccount("合成账号乙");
        store.Put("course", "c1", new { id = "c1", name = "计算机网络", code = "CS301", semester = "2026-20271", teachers = "合成教师", recording_count = 2, lesson_count = 3, lesson_ids = new[] { "l1", "l2", "future" } });
        store.Put("course", "c2", new { id = "c2", name = "线性代数", code = "MA201", semester = "2026-20271", teachers = "合成教师", recording_count = 0 });
        store.Put("meta", "semesters", new { ids = new[] { "2026-20271" } });
        store.Put("meta", "semester", new { id = "2026-20271" });
        store.Put("meta", "semester:2026-20271", new { id = "2026-20271", course_ids = new[] { "c1", "c2" } });
        store.Put("meta", "schedule", new { rows = new[] { new { date = "2026-11-02", course_id = "c1", course_name = "计算机网络", start_time = "08:00" } } });
        foreach (var id in new[] { "l1", "l2", "future" }) store.Put("lesson", id, new { id, course_id = "c1", title = id == "l1" ? "可靠传输与流量控制" : id == "l2" ? "网络层与路由" : "待生成回放", date = id == "future" ? "" : "2026-11-01T08:00:00+08:00", start_at = id == "future" ? 0 : 1793491200, has_recording = id != "future", materials_at = id == "l1" ? "2026-11-01" : null, analysis_at = id == "l1" ? "2026-11-01" : null });
        var rows = Enumerable.Range(0, 250).Select(i => new { id = "s" + i, start = i * 20, end = i * 20 + 15, text = i == 0 ? "可靠传输通过确认与重传保证数据到达。" : i == 1 ? "Sliding Window 控制发送方的在途数据量。" : "合成课堂文本第 " + i + " 段。" }).ToArray();
        store.Put("materials", "l1", new { lesson_id = "l1", course_id = "c1", digest = "preview-materials", segments = rows, slides = new[] { new { id = "p1", start = 0, url = "https://resource.msa.buaa.edu.cn/synthetic.png" }, new { id = "p2", start = 120, url = "https://resource.msa.buaa.edu.cn/synthetic-second.png" } }, duration = 5000, coverage = new { ratio = 0.75, note = "时间区间覆盖率不代表识别准确率；空白可能是静音、练习或漏识别。", gaps = Array.Empty<object>() }, limitations = new[] { "自动识别存在错词与漏字。" } });
        JsonElement PreviewAnalysis(string date, params string[] assignments) => JsonSerializer.SerializeToElement(new {
            lesson_id = "l1", course_id = "c1", course_name = "计算机网络", lesson_title = "可靠传输与流量控制",
            created_at = date, digest = "preview-materials", evidence = rows.ToDictionary(x => x.id),
            assignments = assignments.Select(text => new { text, deadline = "下周五", submission = "课程平台", evidence_ids = new[] { "s0" } }),
            notices = new[] { new { text = "复习滑动窗口机制", evidence_ids = new[] { "s1" } } },
            sections = new[] { new { kind = "chapter", title = "可靠传输", summary = "确认、重传与窗口机制。", start = 0, end = 35, start_id = "s0", end_id = "s1" }, new { kind = "unclassified", title = "尚未归类的原文", summary = "其余课堂原文保留待整理。", start = 40, end = 4995, start_id = "s2", end_id = "s249" } }
        });
        store.SaveAnalysis(PreviewAnalysis("2026-11-01", "完成可靠传输练习", "提交旧版练习"), "preview-materials");
        store.SaveAnalysis(PreviewAnalysis("2026-11-02", "完成可靠传输练习"), "preview-materials");
        var models = new ModelSettingsService(new MemoryModelCredentials());
        using var modelTransport = new ModelTransport(new LearningChecks.SyntheticModel { Mode = "partial" });
        await using var images = new SlideImageCache(workspace.Paths, new SlideCacheChecks.Images());
        await using var runtime = new SchoolSyncRuntime(workspace, models: models, modelTransport: modelTransport, images: images); await runtime.SelectAccountAsync("合成账号甲");
        // Synthetic pending browser: lets QA exercise login/cancel controls without
        // impersonating a successful school login or opening a real school page.
        await using var login = new SchoolLoginController(runtime, async cancellation =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException();
        });
        var bridge = new WorkspaceBridge(workspace, runtime, login, images: images, models: models);
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        Console.WriteLine($"Synthetic workspace preview: http://127.0.0.1:{port}/index.html");
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();
                try
                {
                    var request = context.Request; var response = context.Response;
                    response.Headers["Cache-Control"] = "no-store";
                    if (request.Url?.AbsolutePath == "/bridge" && request.HttpMethod == "POST" && request.Headers["Origin"] == $"http://127.0.0.1:{port}")
                    {
                        using var input = new StreamReader(request.InputStream, Encoding.UTF8);
                        var buffer = new char[WorkspaceBridge.MaxRequestBytes + 1]; var length = 0;
                        while (length < buffer.Length) { var read = await input.ReadAsync(buffer.AsMemory(length)); if (read == 0) break; length += read; }
                        var reply = await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, new string(buffer, 0, length));
                        response.ContentType = "application/json"; await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(reply ?? "null"));
                    }
                    else if (request.HttpMethod == "GET" && new[] { "/index.html", "/app.js", "/app.css", "/main.js" }.Contains(request.Url?.AbsolutePath))
                    {
                        var filename = request.Url!.AbsolutePath[1..]; response.ContentType = filename.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : filename.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html";
                        await response.OutputStream.WriteAsync(await File.ReadAllBytesAsync(Path.Combine(assets, filename)));
                    }
                    else response.StatusCode = 404;
                    response.Close();
                }
                catch { context.Response.StatusCode = 500; context.Response.Close(); }
            }
        }
        catch (HttpListenerException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        return 0;
    }
}
