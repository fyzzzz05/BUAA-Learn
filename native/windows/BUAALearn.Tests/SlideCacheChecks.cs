using System.Net;
using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

internal static class SlideCacheChecks
{
    public static readonly (string Name, Func<Task> Run)[] All = [
        ("Original slide image crosses Rust ABI, caches per account and reopens without network", Offline),
        ("Invalid/redirected/oversized images never enter cache and damaged files can be repaired", Failures),
        ("Account switch cancels actual image HTTP while the workspace bridge remains responsive", Switch),
        ("Queued PDF export preserves a complete old file when refreshed material fails", Pdf),
        ("Image cache shutdown drains HTTP before workspace disposal and rejects new reads", Shutdown)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Slide cache assertion failed."); }
    private static byte[] Image => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "slide.png"));
    private static AccountStore Seed(NativeWorkspace workspace, string owner)
    {
        var store = workspace.RegisterAuthenticatedAccount(owner);
        store.Put("lesson", "l", new { id = "l", course_id = "c" });
        store.Put("materials", "l", new { slides = new[] { new { id = "p", start = 0, url = "https://resource.msa.buaa.edu.cn/synthetic.png" } } }); return store;
    }
    private static async Task Offline()
    {
        using var folder = new TemporaryDirectory();
        using (var workspace = NativeWorkspace.Open(new(folder.Path)))
        {
            var a = Seed(workspace, "a"); var b = Seed(workspace, "b"); var handler = new Images(); await using var cache = new SlideImageCache(workspace.Paths, handler);
            Check((await cache.GetAsync(a, "l", "p", false)).Status == "missing" && handler.Requests == 0);
            var result = await cache.GetAsync(a, "l", "p", true);
            Check(result is { Status: "cached", Mime: "image/png", Width: 640, Height: 360 } && result.Bytes!.SequenceEqual(Image));
            Check((await cache.GetAsync(a, "l", "p", true)).Bytes!.SequenceEqual(Image) && handler.Requests == 1);
            Check((await cache.GetAsync(b, "l", "p", false)).Status == "missing");
            Check(a.List("meta").All(x => !x.GetRawText().Contains("https://")));
        }
        using var reopened = NativeWorkspace.Open(new(folder.Path)); var offline = new Images { Mode = "offline" }; await using var reader = new SlideImageCache(reopened.Paths, offline);
        Check((await reader.GetAsync(reopened.OpenAccount("a"), "l", "p", false)).Bytes!.SequenceEqual(Image) && offline.Requests == 0);
        Check((await reader.GetAsync(reopened.OpenAccount("b"), "l", "p", false)).Status == "missing");
    }
    private static async Task Failures()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); var store = Seed(workspace, "a");
        foreach (var address in new[] { "http://resource.msa.buaa.edu.cn/x", "https://resource.msa.buaa.edu.cn.evil.test/x", "https://resource.msa.buaa.edu.cn/x?token=x", "https://resource.msa.buaa.edu.cn/x#x", "https://u:p@resource.msa.buaa.edu.cn/x", "https://spoc.buaa.edu.cn/x" })
        { try { SlideImageCache.ValidateUrl(address); throw new Exception("Expected blocked URL"); } catch (CoreException) { } }
        foreach (var mode in new[] { "bad", "redirect", "declared-large", "stream-large" })
        {
            await using var cache = new SlideImageCache(workspace.Paths, new Images { Mode = mode });
            try { await cache.GetAsync(store, "l", "p", true); throw new Exception("Expected invalid image"); } catch (CoreException) { }
            Check(store.List("meta").Length == 0 && !Directory.EnumerateFiles(workspace.Paths.Cache, "*.bin", SearchOption.AllDirectories).Any());
        }
        var handler = new Images(); await using var good = new SlideImageCache(workspace.Paths, handler); await good.GetAsync(store, "l", "p", true);
        var file = Directory.EnumerateFiles(workspace.Paths.Cache, "*.bin", SearchOption.AllDirectories).Single();
        var damaged = Image; damaged[damaged.Length / 2] ^= 255; await File.WriteAllBytesAsync(file, damaged);
        Check((await good.GetAsync(store, "l", "p", false)).Status == "corrupt");
        Check((await good.GetAsync(store, "l", "p", true)).Status == "cached" && handler.Requests == 2 && File.ReadAllBytes(file).SequenceEqual(Image));
        store.Put("materials", "l", new { slides = new[] { new { id = "p", start = 0, url = "https://resource.msa.buaa.edu.cn/new.png" } } });
        Check((await good.GetAsync(store, "l", "p", false)).Status == "missing");
    }
    private static async Task Switch()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); Seed(workspace, "a"); Seed(workspace, "b");
        await using var runtime = new SchoolSyncRuntime(workspace); await runtime.SelectAccountAsync("a"); var handler = new Images { Mode = "block" }; await using var cache = new SlideImageCache(workspace.Paths, handler);
        var bridge = new WorkspaceBridge(workspace, runtime, images: cache);
        string Request(string method, object args) => JsonSerializer.Serialize(new { v = 1, id = "image", owner = "a", method, args });
        var pending = bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, Request("slide", new { lesson = "l", slide = "p", download = true }));
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var read = await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, Request("workspace", new { })).WaitAsync(TimeSpan.FromSeconds(1)); Check(read!.Contains("accounts"));
        await runtime.SelectAccountAsync("b");
        var result = JsonSerializer.Deserialize<JsonElement>((await pending.WaitAsync(TimeSpan.FromSeconds(2)))!);
        Check(result.GetProperty("error").GetString() == "cancelled" && workspace.OpenAccount("a").List("meta").Length == 0 && workspace.OpenAccount("b").List("meta").Length == 0);
        Check(!Directory.EnumerateFiles(workspace.Paths.Cache, "*.bin", SearchOption.AllDirectories).Any());
    }
    private static async Task Pdf()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        var store = Seed(workspace, "a");
        var handler = new Images();
        await using var cache = new SlideImageCache(workspace.Paths, handler);
        await using var runtime = new SchoolSyncRuntime(workspace, images: cache);
        await runtime.SelectAccountAsync("a");
        var bridge = new WorkspaceBridge(workspace, runtime, images: cache);
        async Task<JsonElement> Call(string method)
        {
            var request = JsonSerializer.Serialize(new { v = 1, id = "pdf", method, owner = "a", args = new { lesson = "l" } });
            using var reply = JsonDocument.Parse((await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, request))!);
            return reply.RootElement.GetProperty("result").Clone();
        }
        Check((await Call("pdf_status")).GetString() == "missing");
        await Call("export_pdf");
        var job = runtime.Jobs.Enqueue("slides-pdf", "l");
        Check((await runtime.Jobs.WaitForTerminalAsync(job).WaitAsync(TimeSpan.FromSeconds(5))).Status == JobStatus.Completed);
        var path = Directory.EnumerateFiles(workspace.Paths.Exports, "*.pdf", SearchOption.AllDirectories).Single();
        var original = File.ReadAllBytes(path);
        Check(original.AsSpan().StartsWith("%PDF-1.4"u8) && (await Call("pdf_status")).GetString() == "ready");
        store.Put("materials", "l", new { slides = new[] { new { id = "p", start = 0, url = "https://resource.msa.buaa.edu.cn/bad.png" } } });
        handler.Mode = "bad";
        Check((await Call("pdf_status")).GetString() == "stale");
        var failed = await runtime.Jobs.WaitForTerminalAsync(runtime.Jobs.Enqueue("slides-pdf", "l")).WaitAsync(TimeSpan.FromSeconds(5));
        Check(failed.Status == JobStatus.Failed && File.ReadAllBytes(path).SequenceEqual(original));
        Check(!Directory.EnumerateFiles(workspace.Paths.Exports, "*.tmp", SearchOption.AllDirectories).Any());
    }
    private static async Task Shutdown()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); var store = Seed(workspace, "a");
        var handler = new Images { Mode = "block" }; var cache = new SlideImageCache(workspace.Paths, handler);
        var pending = cache.GetAsync(store, "l", "p", true); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        try { await pending; throw new Exception("Expected stopped request"); } catch (OperationCanceledException) { }
        Check(store.List("meta").Length == 0);
        try { await cache.GetAsync(store, "l", "p", false); throw new Exception("Expected disposed cache"); } catch (ObjectDisposedException) { }
        await cache.DisposeAsync();
    }
    internal sealed class Images : HttpMessageHandler
    {
        public string Mode = "ok"; public int Requests; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; Check(request.RequestUri!.Host == "resource.msa.buaa.edu.cn" && !request.Headers.Any() && request.Content == null && request.Method == HttpMethod.Get);
            Entered.TrySetResult();
            if (Mode == "block") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Mode == "offline") throw new HttpRequestException();
            var response = new HttpResponseMessage(Mode == "redirect" ? HttpStatusCode.Found : HttpStatusCode.OK);
            response.Content = Mode == "stream-large" ? new UnknownLengthContent(new byte[SlideImageCache.MaxImageBytes + 1]) : new ByteArrayContent(Mode == "bad" ? "<svg/>"u8.ToArray() : Image);
            if (Mode == "declared-large") response.Content.Headers.ContentLength = SlideImageCache.MaxImageBytes + 1;
            if (Mode == "stream-large") Check(response.Content.Headers.ContentLength == null);
            return response;
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, false));
    }
}
