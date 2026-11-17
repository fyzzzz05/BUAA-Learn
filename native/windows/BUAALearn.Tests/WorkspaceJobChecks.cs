using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;

internal static class WorkspaceJobChecks
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        ("Workspace account restore, offline state and scoped records", AccountsAsync),
        ("Workspace exclusive lease, corruption and file permissions", LeaseAsync),
        ("Workspace lease also excludes a second process", ProcessLeaseAsync),
        ("Unversioned database backup and atomic account batch", UpgradeAsync),
        ("Serial queue, concurrent deduplication and progress persistence", SerialAsync),
        ("All-account interrupted recovery is idempotent and never replayed", RecoveryAsync),
        ("Offline and expired school jobs require explicit login and retry", LoginAsync),
        ("Failed job preserves data, hides exception secrets and retries with new id", FailureAsync),
        ("Account switch cancels old work and prevents stale-context writes", SwitchAsync),
        ("Uncooperative timeout pauses work, bounds switch/stop and forbids late writes", TimeoutAsync)
    ];
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter<JobStatus>(JsonNamingPolicy.SnakeCaseLower) }
    };
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Workspace/job assertion failed."); }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        var task = action();
        try { await task.WaitAsync(Limit); } catch (T) when (task.IsCompleted) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JobDefinition Definition(string kind, Func<JobContext, CancellationToken, Task> handler, bool school = false, TimeSpan? timeout = null) => new(kind, school, timeout ?? Limit, handler);
    private static async Task AccountsAsync()
    {
        using var folder = new TemporaryDirectory();
        using (var workspace = NativeWorkspace.Open(new(folder.Path)))
        {
            var a = workspace.RegisterAuthenticatedAccount("合成账号甲");
            var b = workspace.RegisterAuthenticatedAccount("合成账号乙");
            a.Put("course", "same-id", new { name = "甲的课程" });
            b.Put("course", "same-id", new { name = "乙的课程" });
            await using var jobs = new JobService(workspace, []);
            await jobs.SelectAccountAsync(a.Owner);
            Check(!jobs.IsSchoolConnected && jobs.CurrentOwner == a.Owner);
            Check(a.Get("course", "same-id").GetProperty("name").GetString() == "甲的课程");
            Check(b.List("course").Single().GetProperty("name").GetString() == "乙的课程");
            Throws<CoreException>(() => workspace.OpenAccount("unknown"));
            Throws<ArgumentException>(() => workspace.RegisterAuthenticatedAccount("bad\0owner"));
        }
        using var reopened = NativeWorkspace.Open(new(folder.Path));
        await using var restored = new JobService(reopened, []);
        Check(restored.CurrentOwner == "合成账号甲" && !restored.IsSchoolConnected);
        Check(reopened.Accounts.Count == 2 && reopened.SchemaVersion == 1);
        await restored.SelectAccountAsync("合成账号乙");
        Check(restored.Jobs.Count == 0 && reopened.LastAccount == "合成账号乙");
    }
    private static Task LeaseAsync()
    {
        using var folder = new TemporaryDirectory();
        var paths = new WorkspacePaths(folder.Path);
        using (var workspace = NativeWorkspace.Open(paths))
        {
            Throws<CoreException>(() => NativeWorkspace.Open(paths));
            if (OperatingSystem.IsWindows())
            {
                var rules = new DirectoryInfo(paths.Root).GetAccessControl();
                Check(rules.AreAccessRulesProtected);
                using var identity = WindowsIdentity.GetCurrent();
                var allowed = new HashSet<string> { identity.User!.Value,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
                foreach (FileSystemAccessRule rule in rules.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    Check(!rule.IsInherited && allowed.Contains(rule.IdentityReference.Value));
            }
            else
            {
                Check((File.GetUnixFileMode(paths.Root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0);
                Check((File.GetUnixFileMode(paths.Database) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0);
            }
        }
        using (var core = new CoreStore(paths.Database)) core.SetSetting("windows.workspace.profile", new { Version = 99, Accounts = Array.Empty<string>(), LastAccount = (string?)null });
        Throws<CoreException>(() => NativeWorkspace.Open(paths));
        using var inspect = new CoreStore(paths.Database);
        Check(inspect.GetSetting("windows.workspace.profile").GetProperty("Version").GetInt32() == 99);
        return Task.CompletedTask;
    }
    private static Task UpgradeAsync()
    {
        using var folder = new TemporaryDirectory();
        var paths = new WorkspacePaths(folder.Path);
        using (var old = new CoreStore(paths.Database)) old.Put("account", "course", "legacy", new { name = "升级前记录" });
        using (var workspace = NativeWorkspace.Open(paths))
        {
            Check(workspace.UpgradeBackup != null && File.Exists(workspace.UpgradeBackup));
            using var backup = new CoreStore(workspace.UpgradeBackup!);
            Check(backup.Get("account", "course", "legacy").GetProperty("name").GetString() == "升级前记录");
            var account = workspace.RegisterAuthenticatedAccount("account");
            Check(account.Get("course", "legacy").GetProperty("name").GetString() == "升级前记录");
            Throws<CoreException>(() => account.PutBatch([new("course", "new", new { x = 1 }), new("unknown-kind", "bad", new { x = 2 })]));
            Check(account.Get("course", "new").ValueKind == JsonValueKind.Null);
            account.PutBatch([new("course", "new", new { x = 1 }), new("meta", "test", new { x = 2 })]);
            Check(account.Get("meta", "test").GetProperty("x").GetInt32() == 2);
        }
        using var reopened = NativeWorkspace.Open(paths);
        Check(reopened.UpgradeBackup == null && Directory.GetFiles(paths.Backups).Length == 1);
        return Task.CompletedTask;
    }
    private static async Task ProcessLeaseAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var host = System.IO.Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo { FileName = host, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(WorkspaceJobChecks).Assembly.Location);
        start.ArgumentList.Add("--workspace-lease-probe"); start.ArgumentList.Add(folder.Path);
        async Task<int> RunProbe()
        {
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Child process failed to start.");
            using var cancellation = new CancellationTokenSource(Limit);
            try { await child.WaitForExitAsync(cancellation.Token); }
            finally { if (!child.HasExited) child.Kill(true); }
            return child.ExitCode;
        }
        Check(await RunProbe() == 7);
        workspace.Dispose();
        Check(await RunProbe() == 0);
    }
    private static async Task SerialAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        var entered = Signal(); var release = Signal(); var executions = 0; var concurrent = 0; var peak = 0;
        JobContext? completedContext = null;
        await using var jobs = new JobService(workspace, [Definition("analyze", async (context, token) =>
        {
            var current = Interlocked.Increment(ref concurrent); peak = Math.Max(peak, current);
            Interlocked.Increment(ref executions);
            try
            {
                if (context.Job.Target == "first") { context.Report("正在读取合成课堂"); entered.TrySetResult(); await release.Task.WaitAsync(token); }
                else completedContext = context;
                context.Report("正在整理合成课堂");
                context.Store.Put("analysis", context.Job.Target, new { ready = true });
            }
            finally { Interlocked.Decrement(ref concurrent); }
        })]);
        await jobs.ConnectAuthenticatedAsync("a");
        var first = jobs.Enqueue("analyze", "first");
        try
        {
            await entered.Task.WaitAsync(Limit);
            var progress = JobService.Decode(workspace.OpenAccount("a").Get("job", first.Id));
            Check(progress.Status == JobStatus.Running && progress.Message == "正在读取合成课堂");
            var duplicates = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => jobs.Enqueue("analyze", "first"))));
            Check(duplicates.All(x => x.Id == first.Id));
            var second = jobs.Enqueue("analyze", "second");
            Check(executions == 1 && second.Id != first.Id);
            release.TrySetResult();
            Check((await jobs.WaitForTerminalAsync(first).WaitAsync(Limit)).Status == JobStatus.Completed);
            Check((await jobs.WaitForTerminalAsync(second).WaitAsync(Limit)).Status == JobStatus.Completed);
            Check(executions == 2 && peak == 1);
            Throws<OperationCanceledException>(() => completedContext!.Store.Put("analysis", "escaped", new { }));
            Throws<OperationCanceledException>(() => completedContext!.Report("迟到的进度"));
            var saved = JobService.Decode(workspace.OpenAccount("a").Get("job", first.Id));
            Check(saved.Status == JobStatus.Completed && saved.Revision >= 3 && saved.FinishedAt != null);
        }
        finally { release.TrySetResult(); }
    }
    private static async Task RecoveryAsync()
    {
        using var folder = new TemporaryDirectory();
        using (var workspace = NativeWorkspace.Open(new(folder.Path)))
        {
            foreach (var owner in new[] { "a", "b" })
            {
                var store = workspace.RegisterAuthenticatedAccount(owner);
                foreach (var state in new[] { JobStatus.Running, JobStatus.Queued, JobStatus.Completed })
                {
                    var row = new JobRecord(state.ToString(), owner, "sync", "", state, "合成历史", DateTimeOffset.UtcNow);
                    store.Put("job", row.Id, JsonSerializer.SerializeToElement(row, Json));
                }
            }
        }
        var count = 0;
        using (var workspace = NativeWorkspace.Open(new(folder.Path)))
        {
            await using var jobs = new JobService(workspace, [Definition("sync", (_, _) => { count++; return Task.CompletedTask; }, true)]);
            foreach (var owner in new[] { "a", "b" })
            {
                await jobs.SelectAccountAsync(owner);
                Check(jobs.Jobs.Count(x => x.Status == JobStatus.Interrupted) == 2);
                Check(jobs.Jobs.Count(x => x.Status == JobStatus.Completed) == 1);
            }
            Check(count == 0 && !jobs.IsSchoolConnected);
        }
        using var reopened = NativeWorkspace.Open(new(folder.Path));
        await using var restored = new JobService(reopened, []);
        Check(JobService.Decode(reopened.OpenAccount("a").Get("job", "Running")).Revision == 1 && count == 0);
    }
    private static async Task LoginAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        workspace.RegisterAuthenticatedAccount("a");
        var entered = Signal(); var release = Signal(); var expired = true; var count = 0;
        await using var jobs = new JobService(workspace, [Definition("sync", async (_, token) =>
        {
            Interlocked.Increment(ref count);
            if (expired) { entered.TrySetResult(); await release.Task.WaitAsync(token); throw new NeedsSchoolLoginException(); }
        }, true)]);
        await jobs.SelectAccountAsync("a");
        var offline = jobs.Enqueue("sync");
        Check(offline.Status == JobStatus.NeedsLogin && count == 0);
        await jobs.ConnectAuthenticatedAsync("a");
        var first = jobs.Retry(offline.Id);
        try
        {
            await entered.Task.WaitAsync(Limit);
            var second = jobs.Enqueue("sync", "different");
            release.TrySetResult();
            Check((await jobs.WaitForTerminalAsync(first).WaitAsync(Limit)).Status == JobStatus.NeedsLogin);
            Check((await jobs.WaitForTerminalAsync(second).WaitAsync(Limit)).Status == JobStatus.NeedsLogin);
            Check(!jobs.IsSchoolConnected && count == 1);
            expired = false;
            await jobs.ConnectAuthenticatedAsync("a");
            var retry = jobs.Retry(first.Id);
            Check(retry.Id != first.Id && retry.RetriedFrom == first.Id);
            Check((await jobs.WaitForTerminalAsync(retry).WaitAsync(Limit)).Status == JobStatus.Completed && count == 2);
        }
        finally { release.TrySetResult(); }
    }
    private static async Task FailureAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        var store = workspace.RegisterAuthenticatedAccount("a");
        store.Put("analysis", "lesson", new { valid = "old" });
        var count = 0;
        await using var jobs = new JobService(workspace, [Definition("analyze", (context, _) =>
        {
            if (Interlocked.Increment(ref count) == 1) throw new InvalidOperationException("SECRET-token-must-not-persist");
            context.Store.Put("analysis", "lesson", new { valid = "new" }); return Task.CompletedTask;
        })]);
        jobs.Changed += _ => throw new InvalidOperationException("observer failure");
        await jobs.SelectAccountAsync("a");
        var failed = await jobs.WaitForTerminalAsync(jobs.Enqueue("analyze", "lesson")).WaitAsync(Limit);
        Check(failed.Status == JobStatus.Failed && !failed.Message.Contains("SECRET"));
        Check(store.Get("analysis", "lesson").GetProperty("valid").GetString() == "old");
        var retry = jobs.Retry(failed.Id);
        Check((await jobs.WaitForTerminalAsync(retry).WaitAsync(Limit)).Status == JobStatus.Completed);
        Check(retry.RetriedFrom == failed.Id && store.Get("analysis", "lesson").GetProperty("valid").GetString() == "new");
        Throws<CoreException>(() => jobs.Retry(retry.Id));
    }
    private static async Task SwitchAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        workspace.RegisterAuthenticatedAccount("b");
        var entered = Signal(); var prevented = false; AccountStore? escaped = null;
        await using var jobs = new JobService(workspace, [Definition("materials", async (context, token) =>
        {
            escaped = context.Store; entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                try { context.Store.Put("materials", "late", new { wrong = true }); }
                catch (OperationCanceledException) { prevented = true; }
            }
        }, true)]);
        await jobs.ConnectAuthenticatedAsync("a");
        var first = jobs.Enqueue("materials", "first");
        await entered.Task.WaitAsync(Limit);
        var queued = jobs.Enqueue("materials", "queued");
        await jobs.SelectAccountAsync("b").WaitAsync(Limit);
        Check(jobs.CurrentOwner == "b" && !jobs.IsSchoolConnected && prevented);
        Check((await jobs.WaitForTerminalAsync(first)).Status == JobStatus.Interrupted);
        Check((await jobs.WaitForTerminalAsync(queued)).Status == JobStatus.Interrupted);
        Throws<OperationCanceledException>(() => escaped!.Put("materials", "late", new { }));
        Check(workspace.OpenAccount("a").Get("materials", "late").ValueKind == JsonValueKind.Null);
        Check(workspace.OpenAccount("b").List("materials").Length == 0 && jobs.Jobs.Count == 0);
        Throws<CoreException>(() => jobs.Retry(first.Id));
    }
    private static async Task TimeoutAsync()
    {
        using var folder = new TemporaryDirectory();
        using var workspace = NativeWorkspace.Open(new(folder.Path));
        workspace.RegisterAuthenticatedAccount("b");
        var entered = Signal(); var release = Signal(); var lateBlocked = false;
        await using var jobs = new JobService(workspace, [Definition("analyze", async (context, _) =>
        {
            entered.TrySetResult(); await release.Task; // Deliberately ignores cancellation.
            try { context.Store.Put("analysis", "late", new { should_not_exist = true }); }
            catch (OperationCanceledException) { lateBlocked = true; }
        }, timeout: TimeSpan.FromMilliseconds(100))], TimeSpan.FromMilliseconds(100));
        await jobs.ConnectAuthenticatedAsync("a");
        var row = jobs.Enqueue("analyze", "slow");
        try
        {
            await entered.Task.WaitAsync(Limit);
            Check((await jobs.WaitForTerminalAsync(row).WaitAsync(Limit)).Status == JobStatus.Interrupted);
            Check(jobs.IsDraining);
            Throws<CoreException>(() => jobs.Enqueue("analyze", "second"));
            await ThrowsAsync<TimeoutException>(() => jobs.SelectAccountAsync("b"));
            Check(jobs.CurrentOwner == "a" && workspace.LastAccount == "a" && !jobs.IsSchoolConnected);
            Check(!await jobs.StopAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally { release.TrySetResult(); await jobs.StopAsync(Limit); }
        Check(lateBlocked && workspace.OpenAccount("a").Get("analysis", "late").ValueKind == JsonValueKind.Null);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "北航 工作区测试 " + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
