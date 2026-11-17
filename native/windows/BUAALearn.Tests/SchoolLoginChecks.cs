using System.Net;
using System.Text.Json;
using BUAALearn.Platform;

internal static class SchoolLoginChecks
{
    public static readonly (string Name, Func<Task> Run)[] All = [
        ("Login is nonblocking, deduplicated, cancellable and never persists cancelled identity", Cancel),
        ("Portal verification gates registration and releases failed credentials/resources", Verify),
        ("Late login cannot reconnect after reselecting the same offline account", Stale),
        ("Cancellation during queue drain never registers or connects the candidate", Drain),
        ("Browser profiles isolate live surfaces and recover abandoned generated directories", Profiles)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Login assertion failed."); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(SchoolLoginController login)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (login.State.Status == "running") await Task.Delay(10, timeout.Token);
        // The final state notification precedes the worker's cleanup by a few instructions.
        await login.CancelAsync();
    }
    private static SchoolConnection Candidate(string owner, Marker marker, bool fail = false) => new(owner, "SYNTHETIC-PORTAL", "", "01", new Authorizer(), handler: new Portal(fail), sessionResources: marker);
    private static async Task Cancel()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); await using var runtime = new SchoolSyncRuntime(workspace);
        var entered = Signal(); var count = 0;
        await using var login = new SchoolLoginController(runtime, async ct => { Interlocked.Increment(ref count); entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new Exception(); });
        var bridge = new WorkspaceBridge(workspace, runtime, login);
        var request = JsonSerializer.Serialize(new { v = 1, id = "start", owner = (string?)null, method = "connect", args = new { } });
        var response = await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, request).WaitAsync(TimeSpan.FromSeconds(1));
        Check(JsonSerializer.Deserialize<JsonElement>(response!).GetProperty("ok").GetBoolean());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1)); await login.StartAsync(null); Check(count == 1);
        var read = JsonSerializer.Serialize(new { v = 1, id = "read", owner = (string?)null, method = "workspace", args = new { } });
        Check((await bridge.DispatchAsync(WorkspaceBridge.DocumentUrl, read).WaitAsync(TimeSpan.FromSeconds(1)))!.Contains("login_available"));
        await login.CancelAsync(); Check(login.State.Status == "cancelled" && workspace.Accounts.Count == 0 && runtime.Jobs.CurrentOwner == null);
        await using var closed = new SchoolLoginController(runtime, ct => Task.FromCanceled<SchoolConnection>(new CancellationToken(true)));
        await closed.StartAsync(null); await Wait(closed); Check(closed.State.Status == "cancelled");
    }
    private static async Task Verify()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); await using var runtime = new SchoolSyncRuntime(workspace);
        var bad = new Marker(); await using (var login = new SchoolLoginController(runtime, ct => Task.FromResult(Candidate("unverified", bad, true))))
        { await login.StartAsync(null); await Wait(login); Check(login.State.Status == "failed" && !login.State.Message.Contains("SENSITIVE")); }
        Check(bad.Disposals == 1 && workspace.Accounts.Count == 0);
        var good = new Marker(); await using (var login = new SchoolLoginController(runtime, ct => Task.FromResult(Candidate("verified", good))))
        {
            login.Changed += () => throw new Exception("Observer must be isolated");
            await login.StartAsync(null); await Wait(login);
            Check(login.State.Status == "connected" && runtime.Jobs.CurrentOwner == "verified" && runtime.Jobs.IsSchoolConnected);
            var job = runtime.RefreshSemesters(); await runtime.Jobs.WaitForTerminalAsync(job);
            Check(!workspace.OpenAccount("verified").List("meta").Any(x => x.GetRawText().Contains("SYNTHETIC")));
            Check(good.Disposals == 0); await runtime.DisconnectAsync(); Check(good.Disposals == 1);
        }
    }
    private static async Task Stale()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); workspace.RegisterAuthenticatedAccount("a"); await using var runtime = new SchoolSyncRuntime(workspace); await runtime.SelectAccountAsync("a");
        var ready = Signal(); var release = Signal(); var marker = new Marker();
        await using var login = new SchoolLoginController(runtime, async ct => { ready.SetResult(); await release.Task; return Candidate("a", marker); });
        await login.StartAsync("a"); await ready.Task; await runtime.SelectAccountAsync("a"); release.SetResult(); await Wait(login);
        Check(login.State.Status == "cancelled" && !runtime.Jobs.IsSchoolConnected && marker.Disposals == 1);
    }
    private static async Task Drain()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path)); workspace.RegisterAuthenticatedAccount("a");
        var entered = Signal(); var cancelled = Signal(); var release = Signal();
        await using var jobs = new JobService(workspace, [new("sync", false, TimeSpan.FromSeconds(10), async (context, ct) => { using var registration = ct.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task; })]);
        await jobs.SelectAccountAsync("a"); var old = jobs.Enqueue("sync"); await entered.Task;
        using var cancellation = new CancellationTokenSource(); var connect = jobs.ConnectAuthenticatedAsync("b", cancellation.Token);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel(); release.SetResult();
        try { await connect; throw new Exception("Expected cancelled connect"); } catch (OperationCanceledException) { }
        Check(jobs.CurrentOwner == "a" && !jobs.IsSchoolConnected && !workspace.Accounts.Contains("b"));
        Check((await jobs.WaitForTerminalAsync(old)).Status == JobStatus.Interrupted);
    }
    private static Task Profiles()
    {
        using var folder = new TemporaryDirectory(); using var workspace = NativeWorkspace.Open(new(folder.Path));
        using var first = BrowserProfileLease.Create(workspace.Paths, "school"); using var second = BrowserProfileLease.Create(workspace.Paths, "school");
        Check(first.Path != second.Path && Directory.Exists(first.Path));
        var root = Directory.GetParent(first.Path)!.FullName; var abandoned = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(abandoned); File.WriteAllText(Path.Combine(abandoned, "temporary"), "synthetic");
        var unknown = Path.Combine(root, "not-generated"); Directory.CreateDirectory(unknown);
        using var third = BrowserProfileLease.Create(workspace.Paths, "school"); Check(!Directory.Exists(abandoned) && Directory.Exists(unknown) && Directory.Exists(first.Path));
        first.Dispose(); Check(!Directory.Exists(first.Path)); return Task.CompletedTask;
    }
    private sealed class Marker : IDisposable { public int Disposals; public void Dispose() => Disposals++; }
    private sealed class Authorizer : IRecordingAuthorizer { public Task<RecordingAuthorization> AuthorizeAsync(RecordingChallenge challenge, CancellationToken cancellation) => throw new InvalidOperationException(); }
    private sealed class Portal(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fail ? "SENSITIVE invalid response" : "{\"code\":200,\"content\":[{\"xnxq\":\"2026-20271\"}]}") });
        }
    }
}
