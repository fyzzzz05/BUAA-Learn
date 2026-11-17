using System.Text.Json.Serialization;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

internal sealed class AccountContextChangedException : Exception;
internal sealed record LoginTicket(string? Owner, long Revision);
internal sealed record AccountOperation(AccountStore Store, CancellationToken Cancellation);

public sealed class RecordingChallenge
{
    public string Owner { get; }
    public string CourseId { get; }
    [JsonIgnore] public Uri EntryUri { get; }
    internal RecordingChallenge(string owner, string course, Uri entry) { Owner = owner; CourseId = course; EntryUri = entry; }
    public override string ToString() => "[学校录播授权请求]";
}

public sealed class RecordingAuthorization
{
    public string RecordingCourseId { get; }
    [JsonIgnore] internal string Bearer { get; }
    public RecordingAuthorization(string recordingCourseId, string bearer)
    {
        if (string.IsNullOrWhiteSpace(recordingCourseId) || recordingCourseId.Length > 128 || !recordingCourseId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("录播课程编号无效。");
        if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 16384 || bearer.Any(char.IsControl)) throw new ArgumentException("录播授权信息无效。");
        RecordingCourseId = recordingCourseId; Bearer = bearer;
    }
    public override string ToString() => "[学校录播授权结果]";
}

/// Implementations must dispatch WebView work to the UI and observe cancellation.
/// A synthetic implementation is used by shared tests, not as proof of SSO.
public interface IRecordingAuthorizer
{
    Task<RecordingAuthorization> AuthorizeAsync(RecordingChallenge challenge, CancellationToken cancellation);
}
public sealed class RecordingAuthorizationCancelledException : Exception
{
    public RecordingAuthorizationCancelledException() : base("录播授权未完成或已取消，请重新同步课程。") { }
}

/// In-memory session, immutable owner. The host supplies identity captured from
/// the authenticated school page. This constructor is not an identity verifier.
public sealed class SchoolConnection : IDisposable
{
    public string Owner { get; }
    internal CoreSchool Core { get; }
    internal IRecordingAuthorizer Authorizer { get; }
    internal string? RecordingCourse { get; set; }
    internal string? RecordingId { get; set; }
    private readonly SchoolTransport transport;
    private readonly CancellationTokenSource lifetime;
    private int disposed;
    private readonly IDisposable? sessionResources;
    public SchoolConnection(string owner, string token, string refresh, string role, IRecordingAuthorizer authorizer,
        CancellationToken applicationLifetime = default, HttpMessageHandler? handler = null, IDisposable? sessionResources = null)
    {
        Owner = NativeWorkspace.ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(authorizer);
        if (role != "01") throw new ArgumentException("请使用学校学生角色。");
        Authorizer = authorizer;
        this.sessionResources = sessionResources;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime);
        transport = new SchoolTransport(lifetime.Token, handler);
        try { Core = new CoreSchool(transport, token, refresh, role); }
        catch { transport.Dispose(); lifetime.Dispose(); throw; }
    }
    internal void CheckLive() => ObjectDisposedException.ThrowIf(disposed != 0, this);
    internal void VerifyPortal(CancellationToken cancellation) => Core.Semesters(cancellation);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { lifetime.Cancel(); } catch (AggregateException) { }
        transport.Dispose();
        Core.Dispose();
        lifetime.Dispose();
        sessionResources?.Dispose();
    }
    public override string ToString() => "[学校内存会话]";
}

/// Owns the session lifecycle alongside the queue. Callers may enqueue/read Jobs;
/// account/session transitions must go through this runtime, not Jobs directly.
public sealed class SchoolSyncRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim binding = new(1, 1);
    private readonly NativeWorkspace workspace;
    private SchoolConnection? connection;
    private bool disposed;
    private long accountRevision;
    private CancellationTokenSource accountLifetime = new();
    public JobService Jobs { get; }
    internal LearningService? Learning { get; }
    internal CourseAutomation Automation { get; }
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource scheduleLifetime = new();
    private readonly Task? scheduler;
    private readonly Dictionary<string, DateTimeOffset> scheduleAttempts = new(StringComparer.Ordinal);
    public long AccountRevision => Interlocked.Read(ref accountRevision);
    public SchoolWorkspaceReader? Reader => Jobs.CurrentOwner is { } owner ? new(workspace.OpenAccount(owner)) : null;
    public SchoolSyncRuntime(NativeWorkspace workspace, TimeProvider? clock = null, ModelSettingsService? models = null, ModelTransport? modelTransport = null, SlideImageCache? images = null, bool scheduleAutomation = false)
    {
        this.workspace = workspace;
        this.clock = clock ?? TimeProvider.System;
        var sync = new SchoolSyncService(ResolveAsync, this.clock);
        if ((models == null) != (modelTransport == null)) throw new ArgumentException("模型设置与传输必须同时提供。");
        Learning = models == null ? null : new LearningService(models, modelTransport!, clock);
        var definitions = Learning == null ? sync.Definitions : sync.Definitions.Append(Learning.Analysis);
        if (images != null) definitions = definitions.Append(new SlidePdfExport(workspace.Paths, images).Definition);
        Automation = new CourseAutomation(this.clock, definitions, models);
        Jobs = new JobService(workspace, definitions.Append(Automation.Definition));
        if (scheduleAutomation) scheduler = Task.Run(() => ScheduleAutomationAsync(scheduleLifetime.Token));
    }
    private async Task<SchoolConnection> ResolveAsync(string owner, CancellationToken cancellation)
    {
        await binding.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (disposed || connection == null || connection.Owner != owner) throw new NeedsSchoolLoginException();
            connection.CheckLive();
            return connection;
        }
        finally { binding.Release(); }
    }
    // Local bridge reads and enqueue operations share the same transition lock
    // as native login/account changes. Keep this synchronous and network-free.
    internal async Task<T> UseAccountAsync<T>(string? expected, Func<T> action, CancellationToken cancellation)
    {
        await binding.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Jobs.CurrentOwner != expected) throw new AccountContextChangedException();
            return action();
        }
        finally { binding.Release(); }
    }
    internal Task<LoginTicket> CaptureLoginAsync(string? expected, CancellationToken cancellation) =>
        UseAccountAsync(expected, () => new LoginTicket(expected, accountRevision), cancellation);
    internal Task<AccountOperation> CaptureAccountAsync(string? expected, CancellationToken cancellation) =>
        UseAccountAsync(expected, () => expected == null ? throw new ArgumentException("请先选择本地账号。") : new AccountOperation(workspace.OpenAccount(expected).BoundTo(accountLifetime.Token), accountLifetime.Token), cancellation);
    private void RotateAccountLifetime()
    {
        try { accountLifetime.Cancel(); } catch (AggregateException) { }
        accountLifetime.Dispose(); accountLifetime = new();
    }
    /// On success takes ownership of a newly verified session; on failure the
    /// candidate is disposed, while an old lingering session remains untouched.
    public Task ConnectAuthenticatedAsync(SchoolConnection candidate) => ConnectCoreAsync(candidate, null, CancellationToken.None);
    internal Task ConnectFromAsync(SchoolConnection candidate, LoginTicket ticket, CancellationToken cancellation) => ConnectCoreAsync(candidate, ticket, cancellation);
    private async Task ConnectCoreAsync(SchoolConnection candidate, LoginTicket? ticket, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        SchoolConnection? previous = null;
        try { await binding.WaitAsync(cancellation).ConfigureAwait(false); }
        catch { candidate.Dispose(); throw; }
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.ThrowIfCancellationRequested();
            if (ticket != null && (Jobs.CurrentOwner != ticket.Owner || accountRevision != ticket.Revision)) throw new AccountContextChangedException();
            if (ReferenceEquals(connection, candidate)) throw new InvalidOperationException("重新连接须提供新会话。");
            candidate.CheckLive();
            await Jobs.ConnectAuthenticatedAsync(candidate.Owner, cancellation).ConfigureAwait(false);
            previous = connection; connection = candidate;
            accountRevision++;
            RotateAccountLifetime();
        }
        catch { if (!ReferenceEquals(connection, candidate)) candidate.Dispose(); throw; }
        finally { binding.Release(); }
        previous?.Dispose();
    }
    public Task SelectAccountAsync(string owner) => SelectAccountCoreAsync(owner, false, null);
    internal Task SelectAccountFromAsync(string owner, string? expected) => SelectAccountCoreAsync(owner, true, expected);
    private async Task SelectAccountCoreAsync(string owner, bool enforceExpected, string? expected)
    {
        SchoolConnection? previous = null;
        await binding.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enforceExpected && Jobs.CurrentOwner != expected) throw new AccountContextChangedException();
            await Jobs.SelectAccountAsync(owner).ConfigureAwait(false);
            previous = connection; connection = null;
            accountRevision++;
            RotateAccountLifetime();
        }
        finally { binding.Release(); }
        previous?.Dispose();
    }
    public Task DisconnectAsync() => DisconnectCoreAsync(false, null);
    internal Task DisconnectFromAsync(string? expected) => DisconnectCoreAsync(true, expected);
    private async Task DisconnectCoreAsync(bool enforceExpected, string? expected)
    {
        SchoolConnection? previous = null;
        await binding.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enforceExpected && Jobs.CurrentOwner != expected) throw new AccountContextChangedException();
            await Jobs.DisconnectAsync().ConfigureAwait(false);
            previous = connection; connection = null;
            accountRevision++;
            RotateAccountLifetime();
        }
        finally { binding.Release(); }
        previous?.Dispose();
    }
    private async Task ScheduleAutomationAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            {
                var owner = Jobs.CurrentOwner;
                if (owner == null || !Jobs.IsSchoolConnected || Jobs.IsDraining) continue;
                try
                {
                    await UseAccountAsync(owner, () =>
                    {
                        if (!Jobs.IsSchoolConnected) return false;
                        var store = workspace.OpenAccount(owner);
                        if (!store.List("automation").Any(rule => CourseAutomation.Flag(rule, "enabled"))) return false;
                        var now = clock.GetUtcNow();
                        var day = now.ToOffset(TimeSpan.FromHours(8)).Date;
                        var last = CourseAutomation.Text(store.Get("meta", "schedule"), "synced_at");
                        if ((!DateTimeOffset.TryParse(last, out var synced) || synced.ToOffset(TimeSpan.FromHours(8)).Date != day) &&
                            (!scheduleAttempts.TryGetValue(owner, out var attempt) || now - attempt >= TimeSpan.FromHours(2)))
                        {
                            SyncSchedule();
                            scheduleAttempts[owner] = now;
                            return true;
                        }
                        foreach (var course in Automation.Due(store, now)) Jobs.Enqueue("automation", course);
                        return true;
                    }, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (error is CoreException or AccountContextChangedException) { }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public JobRecord RefreshSemesters() => Jobs.Enqueue("sync");
    public JobRecord SyncSemester(string semester)
    {
        if (string.IsNullOrWhiteSpace(semester)) throw new ArgumentException("请选择学期。", nameof(semester));
        return Jobs.Enqueue("sync", semester);
    }
    public JobRecord SyncSchedule() => Jobs.Enqueue("schedule");
    public JobRecord SyncCourse(string course) => Jobs.Enqueue("course", course);
    public JobRecord AnalyzeLesson(string lesson) => Jobs.Enqueue("analyze", lesson);
    public JobRecord FetchMaterials(string lesson) => Jobs.Enqueue("materials", lesson);
    public async ValueTask DisposeAsync()
    {
        if (scheduler != null && !scheduler.IsCompleted) { scheduleLifetime.Cancel(); await scheduler.ConfigureAwait(false); }
        await binding.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            await Jobs.DisposeAsync().ConfigureAwait(false);
            disposed = true; connection?.Dispose(); connection = null;
            try { accountLifetime.Cancel(); } catch (AggregateException) { }
            accountLifetime.Dispose();
            scheduleLifetime.Dispose();
        }
        finally { binding.Release(); }
    }
}
