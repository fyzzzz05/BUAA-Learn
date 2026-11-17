namespace BUAALearn.Platform;

public sealed record SchoolLoginState(string Status, string Message);

/// Login is a tracked operation outside the bridge dispatch lock. A ticket also
/// invalidates a late browser result after disconnect/reselecting the same owner.
public sealed class SchoolLoginController(SchoolSyncRuntime runtime, Func<CancellationToken, Task<SchoolConnection>> openBrowser) : IAsyncDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? operation;
    private Task? active;
    private bool disposed;
    private SchoolLoginState state = new("idle", "");
    public SchoolLoginState State { get { lock (gate) return state; } }
    public event Action? Changed;
    public async Task<SchoolLoginState> StartAsync(string? expectedOwner, CancellationToken cancellation = default)
    {
        var ticket = await runtime.CaptureLoginAsync(expectedOwner, cancellation).ConfigureAwait(false);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is { IsCompleted: false }) return state;
            operation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var token = operation.Token;
            state = new("running", "请在学校窗口完成登录。");
            active = Task.Run(() => RunAsync(ticket, token));
        }
        Notify(); return State;
    }
    private async Task RunAsync(LoginTicket ticket, CancellationToken cancellation)
    {
        SchoolConnection? candidate = null;
        try
        {
            candidate = await openBrowser(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            candidate.VerifyPortal(cancellation); // Actual school response, not merely an open page.
            var owner = candidate.Owner;
            await runtime.ConnectFromAsync(candidate, ticket, cancellation).ConfigureAwait(false);
            candidate = null; // Runtime now owns both the session and its browser resources.
            await runtime.UseAccountAsync(owner, () => runtime.Jobs.IsSchoolConnected ? runtime.RefreshSemesters() : null, CancellationToken.None).ConfigureAwait(false);
            Set(new("connected", "学校已连接；录播将在进入课程后单独授权。"));
        }
        catch (OperationCanceledException) { Set(new("cancelled", "学校登录已取消或超时。")); }
        catch (AccountContextChangedException) { Set(new("cancelled", "账号状态已改变，请重新连接学校。")); }
        catch { Set(new("failed", "学校连接未完成，请检查网络、学生角色或重新登录。")); }
        finally
        {
            candidate?.Dispose();
            lock (gate) { operation?.Dispose(); operation = null; }
        }
    }
    private void Set(SchoolLoginState value) { lock (gate) state = value; Notify(); }
    private void Notify()
    {
        if (Changed is { } observers) foreach (Action observer in observers.GetInvocationList()) try { observer(); } catch { }
    }
    public async Task CancelAsync()
    {
        Task? task; CancellationTokenSource? source;
        lock (gate) { task = active; source = operation; }
        try { source?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        if (task != null) await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        await CancelAsync().ConfigureAwait(false);
    }
}
