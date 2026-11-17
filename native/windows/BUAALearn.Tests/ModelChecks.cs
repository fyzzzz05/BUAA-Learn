using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

internal static class ModelChecks
{
    public static readonly (string Name, Func<Task> Run)[] All = [
        ("Model endpoints normalize safely and retain provider-specific routes", Endpoints),
        ("Model settings isolate account/service keys and retry failed native cleanup", Settings),
        ("Model configuration and pending credential cleanup survive workspace reopen", Reopen),
        ("Cancelled model-setting commit keeps old settings and tracks uncommitted vault keys", Rollback),
        ("Model transport crosses Rust contracts, uses scoped auth and supports local no-key models", Protocols),
        ("Model transport rejects redirects, error bodies, malformed/oversized output and cancels HTTP", Failures)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Model assertion failed."); }
    private static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T e) { return e; } throw new Exception("Expected " + typeof(T).Name); }
    private const string First = "https://models.example.test/v1";
    private static Task Endpoints()
    {
        var canonical = ModelEndpoint.Parse(" HTTPS://API.DEEPSEEK.COM:443/v1/// ");
        Check(canonical.BaseUrl == "https://api.deepseek.com/v1" && canonical.Provider == "responses" && canonical.RequestUri.AbsolutePath == "/v1/responses");
        Check(ModelEndpoint.Parse("https://api.deepseek.com").RequestUri.AbsolutePath == "/responses");
        Check(ModelEndpoint.Parse("https://api.deepseek.com/proxy").Provider == "chat");
        Check(ModelEndpoint.Parse("https://custom.example.test/Proxy/v1").RequestUri.AbsolutePath == "/Proxy/v1/chat/completions");
        foreach (var url in new[] { "http://localhost:11434/v1", "http://127.0.0.1:8000/v1", "http://[::1]:8000/v1" }) Check(ModelEndpoint.Parse(url).Provider == "chat");
        foreach (var url in new[] { "http://remote.example.test/v1", "file:///tmp/x", "https://user:pass@models.example.test", "https://models.example.test?key=x", "https://models.example.test#x", "https://models.example.test/\nx", "https://models.example.test\\x" }) Throws<CoreException>(() => ModelEndpoint.Parse(url));
        return Task.CompletedTask;
    }
    private static Task Settings()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        var a = workspace.RegisterAuthenticatedAccount("a"); var b = workspace.RegisterAuthenticatedAccount("b"); var vault = new MemoryModelCredentials(); var service = new ModelSettingsService(vault);
        var original = service.Save(a, First, " model-a ", "replace", "SYNTHETIC-OLD-KEY"); Check(original is { KeyState: "saved", Model: "model-a", Ready: true } && vault.Count == 1);
        Check(service.InspectEndpoint(b, First).KeyState == "none");
        Check(service.Save(a, "https://other.example.test/v1", "other", "keep").KeyState == "none" && vault.Count == 1);
        Check(service.Save(a, First + "/", "changed", "keep").KeyState == "saved");
        Check(service.Save(a, First, "changed", "replace", "SYNTHETIC-NEW-KEY").CleanupPending == false && vault.Count == 1);
        Check(!JsonSerializer.Serialize(service.Get(a)).Contains("SYNTHETIC"));
        Check(a.List("meta").All(row => !row.GetRawText().Contains("SYNTHETIC")));
        using (var captured = service.Capture(a)) { Check(!captured.ToString().Contains("SYNTHETIC") && !JsonSerializer.Serialize(captured).Contains("SYNTHETIC")); }
        vault.FailReads = true; Check(service.Get(a) is { KeyState: "unavailable", Ready: false });
        Check(service.Save(a, First, "offline edit", "keep").HasCredentialReference); Throws<ModelCredentialException>(() => service.Capture(a)); vault.FailReads = false;
        vault.FailWrites = true; Throws<ModelCredentialException>(() => service.Save(a, First, "must not save", "replace", "SYNTHETIC-FAILED"));
        Check(service.Get(a).Model == "offline edit" && vault.Count == 1); vault.FailWrites = false;
        vault.FailDeletes = true; var removed = service.Save(a, First, "removed", "remove");
        Check(removed is { KeyState: "none", CleanupPending: true, HasCredentialReference: false } && vault.Count == 1);
        vault.FailDeletes = false; service.Cleanup(a); Check(!service.Get(a).CleanupPending && vault.Count == 0);
        Throws<ArgumentException>(() => service.Save(a, First, "model", "replace", "bad\r\nkey"));
        Throws<ArgumentException>(() => service.Save(a, First, "model", "replace", new string('x', 2561)));
        Check(service.Get(a).Model == "removed");
        service.Save(a, First, "a-key", "replace", "SYNTHETIC-A-KEY");
        var id = "model-credential:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(First)));
        b.Put("meta", "model-config", a.Get("meta", "model-config")); b.Put("meta", id, a.Get("meta", id));
        Check(service.Get(b).KeyState == "missing"); Throws<CoreException>(() => service.Capture(b));
        service.Save(b, First, "b-key", "replace", "SYNTHETIC-B-KEY"); Check(vault.Count == 2);
        service.Save(b, First, "b-key", "remove"); Check(vault.Count == 1 && service.Get(a).KeyState == "saved");
        var activeRef = a.Get("meta", id).GetProperty("reference").GetString()!.ToUpperInvariant();
        a.Put("meta", "model-key-retired", new { entries = new[] { new { base_url = First, reference = activeRef } } });
        Throws<CoreException>(() => service.Cleanup(a)); Check(vault.Count == 1 && service.Get(a).KeyState == "saved");
        Check(!service.RemoveKey(a, First).HasCredentialReference && service.Get(a).Model == "a-key" && vault.Count == 0);
        a.Put("meta", "model-config", new { version = 99, base_url = First, model = "future" });
        Throws<CoreException>(() => service.Save(a, First, "older host", "replace", "SYNTHETIC-MUST-NOT-WRITE"));
        Check(a.Get("meta", "model-config").GetProperty("version").GetInt32() == 99 && vault.Count == 0);
        return Task.CompletedTask;
    }
    private static async Task Reopen()
    {
        using var folder = new TemporaryDirectory(); var vault = new MemoryModelCredentials();
        using (var workspace = NativeWorkspace.Open(new(folder.Path)))
        {
            var store = workspace.RegisterAuthenticatedAccount("a"); await using var runtime = new SchoolSyncRuntime(workspace); await runtime.SelectAccountAsync("a");
            var settings = new ModelSettingsService(vault); settings.Save(store, First, "persisted", "replace", "SYNTHETIC-PERSISTED");
            vault.FailDeletes = true; Check(settings.Save(store, First, "persisted", "remove").CleanupPending);
        }
        using var reopened = NativeWorkspace.Open(new(folder.Path)); await using var offline = new SchoolSyncRuntime(reopened);
        var service = new ModelSettingsService(vault); var account = reopened.OpenAccount("a");
        Check(offline.Jobs.CurrentOwner == "a" && !offline.Jobs.IsSchoolConnected && service.Get(account) is { Model: "persisted", CleanupPending: true, KeyState: "none" });
        vault.FailDeletes = false; service.Cleanup(account); Check(vault.Count == 0 && !service.Get(account).CleanupPending);
    }
    private static async Task Rollback()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); var a = workspace.RegisterAuthenticatedAccount("a"); workspace.RegisterAuthenticatedAccount("b");
        var vault = new MemoryModelCredentials(); var service = new ModelSettingsService(vault); service.Save(a, First, "original", "replace", "SYNTHETIC-ORIGINAL");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vault.FailDeletes = true; vault.AfterWrite = () => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); };
        await using var jobs = new JobService(workspace, [new("analyze", false, TimeSpan.FromSeconds(10), (context, ct) =>
        { using var registration = ct.Register(() => cancelled.TrySetResult()); service.Save(context.Store, First, "candidate", "replace", "SYNTHETIC-CANDIDATE"); return Task.CompletedTask; })]);
        await jobs.SelectAccountAsync("a"); var job = jobs.Enqueue("analyze", "l"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var change = jobs.SelectAccountAsync("b"); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); release.SetResult(); await change;
        Check((await jobs.WaitForTerminalAsync(job)).Status == JobStatus.Interrupted);
        Check(service.Get(a) is { Model: "original", KeyState: "saved", CleanupPending: true } && vault.Count == 2);
        vault.FailDeletes = false; vault.AfterWrite = null; service.Cleanup(a);
        Check(!service.Get(a).CleanupPending && vault.Count == 1 && service.Get(workspace.OpenAccount("b")).BaseUrl == "");
    }
    private static async Task Protocols()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); var a = workspace.RegisterAuthenticatedAccount("a"); var vault = new MemoryModelCredentials(); var service = new ModelSettingsService(vault);
        service.Save(a, "https://api.deepseek.com/v1", "synthetic-model", "replace", "SYNTHETIC-MODEL-KEY");
        var handler = new ModelHandler("auto"); using var transport = new ModelTransport(handler);
        using (var snapshot = service.Capture(a))
        {
            var result = await transport.CompleteAsync(snapshot, "system", "完整合成课堂文本", JsonSerializer.SerializeToElement(new { name = "Result", schema = new { type = "object" } }));
            Check(result.GetProperty("answer").GetString() == "合成回答" && handler.Requests == 1);
        }
        service.Save(a, "http://localhost:11434/v1", "local", "keep");
        using var local = service.Capture(a);
        Check((await transport.CompleteAsync(local, "system", "原文")).GetProperty("answer").GetString() == "合成回答" && handler.Requests == 2);
        local.Dispose();
        try { await transport.CompleteAsync(local, "system", "原文"); throw new Exception("Expected disposed model snapshot"); } catch (ObjectDisposedException) { }
        Check(handler.Requests == 2);
    }
    private static async Task Failures()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); var store = workspace.RegisterAuthenticatedAccount("a"); var service = new ModelSettingsService(new MemoryModelCredentials()); service.Save(store, "http://localhost:11434/v1", "local", "keep");
        foreach (var mode in new[] { "redirect", "auth", "quota", "bad", "large", "incomplete" })
        {
            var handler = new ModelHandler("chat", mode); using var transport = new ModelTransport(handler); using var snapshot = service.Capture(store);
            try { await transport.CompleteAsync(snapshot, "system", "prompt"); throw new Exception("Expected rejected response"); }
            catch (ModelRequestException error) { Check(!error.Message.Contains("SYNTHETIC") && handler.Requests == 1); if (mode == "incomplete") Check(error.Code == "incomplete"); }
        }
        var blocked = new ModelHandler("chat", "block"); using var caller = new ModelTransport(blocked); using var settings = service.Capture(store); using var cancellation = new CancellationTokenSource();
        var pending = caller.CompleteAsync(settings, "system", "prompt", cancellation: cancellation.Token); await blocked.Entered.Task; cancellation.Cancel();
        try { await pending; throw new Exception("Expected cancelled HTTP"); } catch (OperationCanceledException) { }
    }
    public static Task WindowsCredentialRoundTrip()
    {
        var vault = new WindowsModelCredentialStore(); var target = "BUAALearn/model/test-" + Guid.NewGuid().ToString("N"); var bytes = Encoding.ASCII.GetBytes("SYNTHETIC-WINDOWS-KEY");
        try { vault.Write(target, bytes); var read = vault.Read(target)!; try { Check(read.SequenceEqual(bytes)); } finally { CryptographicOperations.ZeroMemory(read); } vault.Delete(target); Check(vault.Read(target) == null); }
        finally { vault.Delete(target); CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }
    private sealed class ModelHandler(string provider, string mode = "ok") : HttpMessageHandler
    {
        public int Requests; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Token") && !request.Headers.Contains("RoleCode"));
            var current = provider == "auto" ? request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) ? "responses" : "chat" : provider;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Check(request.Method == HttpMethod.Post);
            if (current == "responses")
            {
                Check(request.RequestUri!.AbsolutePath == "/v1/responses" && request.Headers.Authorization?.ToString() == "Bearer SYNTHETIC-MODEL-KEY");
                Check(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString() == "json_schema");
            }
            else Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions" && request.Headers.Authorization == null && body.RootElement.TryGetProperty("messages", out _));
            Entered.TrySetResult(); if (mode == "block") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var status = mode switch { "redirect" => HttpStatusCode.TemporaryRedirect, "auth" => HttpStatusCode.Unauthorized, "quota" => HttpStatusCode.TooManyRequests, _ => HttpStatusCode.OK };
            object response = current == "responses" ? new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "{\"answer\":\"合成回答\"}" } } } } } : (object)new { choices = new[] { new { finish_reason = mode == "incomplete" ? "length" : "stop", message = new { content = "{\"answer\":\"合成回答\"}" } } } };
            var content = new StringContent(mode is "bad" or "auth" or "quota" or "redirect" ? "SYNTHETIC-SECRET-ERROR-BODY" : JsonSerializer.Serialize(response));
            if (mode == "large") content.Headers.ContentLength = ModelTransport.MaxResponseBytes + 1;
            return new(status) { Content = content };
        }
    }
}
internal sealed class MemoryModelCredentials : IModelCredentialStore
{
    private readonly Dictionary<string, byte[]> values = [];
    public bool FailReads, FailWrites, FailDeletes; public Action? AfterWrite;
    public int Count => values.Count;
    public byte[]? Read(string target) { if (FailReads) throw new ModelCredentialException(); return values.TryGetValue(target, out var value) ? (byte[])value.Clone() : null; }
    public void Write(string target, ReadOnlySpan<byte> value) { if (FailWrites) throw new ModelCredentialException(); values[target] = value.ToArray(); AfterWrite?.Invoke(); }
    public void Delete(string target) { if (FailDeletes) throw new ModelCredentialException(); if (values.Remove(target, out var bytes)) CryptographicOperations.ZeroMemory(bytes); }
}
