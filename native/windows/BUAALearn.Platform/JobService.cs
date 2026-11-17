using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public enum JobStatus { Queued, Running, Completed, Failed, NeedsLogin, Interrupted }
public sealed record JobRecord(string Id, string Owner, string Kind, string Target, JobStatus Status,
    string Message, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null, string? RetriedFrom = null, int Revision = 0);
public sealed record JobDefinition(string Kind, bool RequiresSchool, TimeSpan Timeout, Func<JobContext, CancellationToken, Task> Execute);
public sealed class NeedsSchoolLoginException(bool recording = false) : Exception(recording ? "录播授权未完成或已过期，请重新连接学校。" : "学校登录已过期，请重新登录。");
public sealed class JobFailureException : Exception
{
    // Only host services construct these with fixed, safe user-facing text.
    internal JobFailureException(string message) : base(message) { }
}

public sealed class JobContext
{
    public JobRecord Job { get; }
    public AccountStore Store { get; }
    private readonly Action<string> progress;
    internal JobContext(JobRecord job, AccountStore store, Action<string> progress) { Job = job; Store = store; this.progress = progress; }
    public void Report(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 512 || message.Any(char.IsControl)) throw new ArgumentException("任务进度文案无效。", nameof(message));
        progress(message);
    }
}

/// One serial worker per exclusively leased workspace. Handlers and credentials
/// are never persisted. Event consumers dispatch to their UI, filter Owner and
/// ignore lower Revisions (notifications from different threads can cross).
public sealed class JobService : IAsyncDisposable
{
    private sealed class Entry(JobRecord record)
    {
        public JobRecord Record = record;
        public readonly TaskCompletionSource<JobRecord> Terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Execution
    {
        public Entry Entry { get; }
        public CancellationTokenSource Cancellation { get; }
        private readonly CancellationTokenSource contextLifetime;
        public CancellationToken ContextToken { get; }
        public bool IsLingering;
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Execution(Entry entry, CancellationTokenSource cancellation)
        {
            Entry = entry; Cancellation = cancellation;
            contextLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            ContextToken = contextLifetime.Token;
        }
        public void Cancel() { try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }
        public void RevokeContext() { try { contextLifetime.Cancel(); } catch (AggregateException) { } }
        public void Dispose() { contextLifetime.Dispose(); Cancellation.Dispose(); }
    }
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<JobStatus>(JsonNamingPolicy.SnakeCaseLower, false) }
    };
    private static readonly HashSet<string> Kinds = ["sync", "schedule", "course", "materials", "analyze", "slides-pdf", "automation"];
    private readonly NativeWorkspace workspace;
    private readonly Dictionary<string, JobDefinition> definitions;
    private readonly Dictionary<(string Owner, string Id), Entry> entries = [];
    private readonly Channel<Entry> queue = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim transition = new(1, 1);
    private readonly object gate = new();
    private readonly TimeSpan drainTimeout;
    private readonly Task worker;
    private Execution? active;
    private string? owner;
    private bool online;
    private bool accepting = true;
    private bool stopping;
    private int disposed;
    public event Action<JobRecord>? Changed;
    public string? Fault { get; private set; }
    public string? CurrentOwner { get { lock (gate) return owner; } }
    public bool IsSchoolConnected { get { lock (gate) return online; } }
    public bool IsDraining { get { lock (gate) return active?.IsLingering == true; } }

    public JobService(NativeWorkspace workspace, IEnumerable<JobDefinition> definitions, TimeSpan? drainTimeout = null)
    {
        this.workspace = workspace;
        this.definitions = definitions.ToDictionary(x => x.Kind, StringComparer.Ordinal);
        foreach (var definition in this.definitions.Values)
            if (!Kinds.Contains(definition.Kind) || definition.Timeout <= TimeSpan.Zero || definition.Timeout > TimeSpan.FromHours(1) || definition.Execute == null)
                throw new ArgumentException("任务类型、处理器或超时无效。", nameof(definitions));
        this.drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        if (this.drainTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        Recover();
        owner = workspace.LastAccount; // Always offline after process restart.
        worker = Task.Run(WorkAsync);
    }
    private static bool IsTerminal(JobStatus state) => state is not (JobStatus.Queued or JobStatus.Running);
    private static JsonElement Encode(JobRecord record) => JsonSerializer.SerializeToElement(record, Json);
    public static JobRecord Decode(JsonElement value) => value.Deserialize<JobRecord>(Json) ?? throw new CoreException("任务记录格式异常。");
    private void Recover()
    {
        foreach (var account in workspace.Accounts)
        {
            var store = workspace.OpenAccount(account);
            var recovered = new List<RecordWrite>();
            foreach (var value in store.List("job"))
            {
                var record = Decode(value);
                if (record.Owner != account || string.IsNullOrWhiteSpace(record.Id) || !Kinds.Contains(record.Kind) || record.CreatedAt == default || record.Revision < 0 || record.Revision == int.MaxValue || !Enum.IsDefined(record.Status))
                    throw new CoreException("任务记录归属或格式异常，请保留数据库后检查。");
                if (!IsTerminal(record.Status))
                {
                    record = record with { Status = JobStatus.Interrupted, Message = "应用已重启，请重新运行此任务。", FinishedAt = DateTimeOffset.UtcNow, Revision = record.Revision + 1 };
                    recovered.Add(new("job", record.Id, Encode(record)));
                }
                var entry = new Entry(record);
                entry.Terminal.TrySetResult(record);
                entries.Add((record.Owner, record.Id), entry);
            }
            if (recovered.Count > 0) store.PutBatch(recovered);
        }
    }
    public IReadOnlyList<JobRecord> Jobs
    {
        get { lock (gate) return entries.Values.Select(x => x.Record).Where(x => x.Owner == owner).OrderByDescending(x => x.CreatedAt).ToArray(); }
    }
    public JobRecord Enqueue(string kind, string target = "") => Enqueue(kind, target, null, null);
    private JobRecord Enqueue(string kind, string target, string? retriedFrom, string? expectedOwner)
    {
        JobRecord record;
        lock (gate)
        {
            EnsureAvailable();
            if (!accepting || active?.IsLingering == true) throw new CoreException("旧任务仍在退出，请稍后重试。");
            if (owner == null || (expectedOwner != null && expectedOwner != owner)) throw new CoreException("请先打开对应账号的本地学习空间。");
            if (!definitions.TryGetValue(kind, out var definition)) throw new CoreException("该任务尚未接入处理器。");
            if (target == null || target.Length > 512 || target.Any(char.IsControl)) throw new ArgumentException("任务目标无效。", nameof(target));
            var duplicate = entries.Values.FirstOrDefault(x => x.Record.Owner == owner && x.Record.Kind == kind && x.Record.Target == target && !IsTerminal(x.Record.Status));
            if (duplicate != null) return duplicate.Record;
            var state = definition.RequiresSchool && !online ? JobStatus.NeedsLogin : JobStatus.Queued;
            record = new(Guid.NewGuid().ToString("N"), owner, kind, target, state,
                state == JobStatus.NeedsLogin ? "请先连接学校后重试。" : "等待处理", DateTimeOffset.UtcNow,
                FinishedAt: state == JobStatus.NeedsLogin ? DateTimeOffset.UtcNow : null, RetriedFrom: retriedFrom);
            workspace.OpenAccount(owner).Put("job", record.Id, Encode(record));
            var entry = new Entry(record); entries.Add((owner, record.Id), entry);
            if (IsTerminal(state)) entry.Terminal.TrySetResult(record);
            else if (!queue.Writer.TryWrite(entry)) throw new CoreException("任务队列已停止。");
        }
        Notify(record);
        return record;
    }
    public JobRecord Retry(string id)
    {
        JobRecord previous;
        lock (gate)
        {
            EnsureAvailable();
            if (owner == null || !entries.TryGetValue((owner, id), out var entry)) throw new CoreException("未找到当前账号的任务。");
            previous = entry.Record;
            if (previous.Status is not (JobStatus.Failed or JobStatus.NeedsLogin or JobStatus.Interrupted)) throw new CoreException("只有失败、需登录或中断的任务可以重试。");
        }
        return Enqueue(previous.Kind, previous.Target, previous.Id, previous.Owner);
    }
    public Task<JobRecord> WaitForTerminalAsync(JobRecord record, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (!entries.TryGetValue((record.Owner, record.Id), out var entry)) throw new CoreException("未找到任务。");
            return entry.Terminal.Task.WaitAsync(cancellation);
        }
    }
    public Task SelectAccountAsync(string account) => SwitchAsync(account, false, false);
    /// The caller must have independently verified the school login. This does
    /// not accept/store a token and is not an authentication verifier.
    public Task ConnectAuthenticatedAsync(string account, CancellationToken cancellation = default) => SwitchAsync(account, true, true, cancellation: cancellation);
    public Task DisconnectAsync() => CurrentOwner is { } current ? SwitchAsync(current, false, false, current) : Task.CompletedTask;
    private async Task SwitchAsync(string account, bool connected, bool register, string? expectedCurrent = null, CancellationToken cancellation = default)
    {
        account = NativeWorkspace.ValidateOwner(account);
        if (!await transition.WaitAsync(drainTimeout, cancellation).ConfigureAwait(false)) throw new TimeoutException("另一个账号操作仍在退出，请稍后重试。");
        try
        {
            Execution? running; List<JobRecord> cancelled;
            lock (gate)
            {
                EnsureAvailable();
                cancellation.ThrowIfCancellationRequested();
                if (expectedCurrent != null && expectedCurrent != owner) return;
                if (!register) workspace.OpenAccount(account);
                accepting = false; online = false;
                cancelled = InterruptQueued("账号连接已变更，请重新运行任务。");
                running = active;
            }
            running?.Cancel();
            foreach (var record in cancelled) Notify(record);
            if (running != null) await running.Drained.Task.WaitAsync(drainTimeout).ConfigureAwait(false);
            lock (gate)
            {
                EnsureAvailable();
                if (cancellation.IsCancellationRequested) { accepting = true; cancellation.ThrowIfCancellationRequested(); }
                if (register) workspace.RegisterAuthenticatedAccount(account);
                workspace.RememberAccount(account);
                owner = account; online = connected; accepting = true;
            }
        }
        finally { transition.Release(); }
    }
    // Called under gate. Persist the entire per-account interruption before publishing.
    private List<JobRecord> InterruptQueued(string message)
    {
        var changed = entries.Values.Where(x => x.Record.Status == JobStatus.Queued).Select(x => (Entry: x, Record: x.Record with
        { Status = JobStatus.Interrupted, Message = message, FinishedAt = DateTimeOffset.UtcNow, Revision = x.Record.Revision + 1 })).ToArray();
        foreach (var group in changed.GroupBy(x => x.Record.Owner))
            workspace.OpenAccount(group.Key).PutBatch(group.Select(x => new RecordWrite("job", x.Record.Id, Encode(x.Record))));
        foreach (var item in changed) { item.Entry.Record = item.Record; item.Entry.Terminal.TrySetResult(item.Record); }
        return changed.Select(x => x.Record).ToList();
    }
    private void Save(Entry entry, JobRecord record)
    {
        workspace.OpenAccount(record.Owner).Put("job", record.Id, Encode(record));
        entry.Record = record;
        if (IsTerminal(record.Status)) entry.Terminal.TrySetResult(record);
    }
    private void Report(Execution execution, string message)
    {
        JobRecord updated;
        lock (gate)
        {
            execution.ContextToken.ThrowIfCancellationRequested();
            if (active != execution || execution.Entry.Record.Status != JobStatus.Running) return;
            updated = execution.Entry.Record with { Message = message, Revision = execution.Entry.Record.Revision + 1 };
            Save(execution.Entry, updated);
        }
        Notify(updated);
    }
    private async Task WorkAsync()
    {
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                Execution? execution = null; JobRecord started; JobDefinition definition;
                lock (gate)
                {
                    if (entry.Record.Status != JobStatus.Queued || stopping) continue;
                    definition = definitions[entry.Record.Kind];
                    var available = entry.Record.Owner == owner && accepting && (!definition.RequiresSchool || online);
                    started = entry.Record with { Status = available ? JobStatus.Running : JobStatus.NeedsLogin,
                        Message = available ? "正在处理" : "学校连接已失效，请重新登录。", StartedAt = available ? DateTimeOffset.UtcNow : null,
                        FinishedAt = available ? null : DateTimeOffset.UtcNow, Revision = entry.Record.Revision + 1 };
                    Save(entry, started);
                    if (available)
                    {
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        cancellation.CancelAfter(definition.Timeout);
                        active = execution = new Execution(entry, cancellation);
                    }
                }
                Notify(started);
                if (execution == null) continue;
                await ExecuteAsync(execution, definition).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch
        {
            lock (gate)
            {
                Fault = "任务状态无法保存，队列已暂停。请检查本地存储后重启。";
                accepting = false; online = false;
                foreach (var entry in entries.Values.Where(x => !IsTerminal(x.Record.Status))) entry.Terminal.TrySetException(new CoreException(Fault));
            }
            stop.Cancel();
        }
    }
    private async Task ExecuteAsync(Execution execution, JobDefinition definition)
    {
        var entry = execution.Entry;
        var token = execution.Cancellation.Token;
        var context = new JobContext(entry.Record, workspace.OpenAccount(entry.Record.Owner).BoundTo(execution.ContextToken), message => Report(execution, message));
        var task = Task.Run(() => definition.Execute(context, token));
        JobStatus state; string message;
        try { await task.WaitAsync(token).ConfigureAwait(false); state = JobStatus.Completed; message = "处理完成"; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { state = JobStatus.Interrupted; message = "任务已停止或超时，请重试。"; }
        catch (NeedsSchoolLoginException error) { state = JobStatus.NeedsLogin; message = error.Message; }
        catch (JobFailureException error) { state = JobStatus.Failed; message = error.Message; }
        catch { state = JobStatus.Failed; message = "任务未完成，原有记录已保留，请重试。"; }
        execution.RevokeContext(); // Revoke late writes before completing the terminal waiter.
        try
        {
            JobRecord terminal;
            lock (gate)
            {
                if (token.IsCancellationRequested) { state = JobStatus.Interrupted; message = "任务已停止或超时，请重试。"; }
                if (state == JobStatus.NeedsLogin) online = false;
                execution.IsLingering = !task.IsCompleted;
                terminal = entry.Record with { Status = state, Message = message, FinishedAt = DateTimeOffset.UtcNow, Revision = entry.Record.Revision + 1 };
                Save(entry, terminal);
            }
            execution.Cancel(); // Also invalidates escaped contexts after success.
            Notify(terminal);
        }
        finally
        {
            execution.Cancel();
            // Never start another handler alongside one that ignores cancellation.
            // Switch/stop have bounded waits and leave this workspace paused meanwhile.
            try { await task.ConfigureAwait(false); } catch { }
            lock (gate) { if (active == execution) active = null; }
            execution.Drained.TrySetResult();
            execution.Dispose();
        }
    }
    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (stopping || Fault != null) throw new CoreException(Fault ?? "任务队列已停止。");
    }
    private void Notify(JobRecord record)
    {
        foreach (var observer in Changed?.GetInvocationList() ?? [])
            try { ((Action<JobRecord>)observer)(record); } catch { /* Observers cannot stop the worker. */ }
    }
    public async Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? drainTimeout;
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!await transition.WaitAsync(limit).ConfigureAwait(false)) return false;
        try
        {
            Execution? running; List<JobRecord> cancelled;
            lock (gate)
            {
                if (disposed != 0) return true;
                stopping = true; accepting = false; online = false;
                cancelled = InterruptQueued("应用正在退出，任务已中断。"); running = active;
                queue.Writer.TryComplete();
            }
            running?.Cancel(); stop.Cancel();
            foreach (var record in cancelled) Notify(record);
            try { await worker.WaitAsync(limit).ConfigureAwait(false); return true; }
            catch (TimeoutException) { return false; }
        }
        finally { transition.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (!await StopAsync().ConfigureAwait(false)) throw new TimeoutException("任务仍在退出，暂不能释放工作区。");
        if (Interlocked.Exchange(ref disposed, 1) == 0) stop.Dispose();
    }
}
