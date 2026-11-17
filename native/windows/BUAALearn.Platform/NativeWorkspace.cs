using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public sealed class WorkspacePaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "learning.sqlite3");
    public string Backups => Path.Combine(Root, "backups");
    public string Exports => Path.Combine(Root, "exports");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public WorkspacePaths(string root) => Root = Path.GetFullPath(root);
    public static WorkspacePaths Default()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("非 Windows 测试须显式指定临时数据目录。");
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BUAA Learn Native"));
    }
    internal string LessonExportDirectory(string owner, string lesson, bool create)
    {
        static string Key(string value) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
        RejectLink(Exports);
        var account = Path.Combine(Exports, Key(owner));
        var target = Path.Combine(account, Key(lesson));
        if (create) { SecureDirectory(account); SecureDirectory(target); }
        else { RejectLink(account); RejectLink(target); }
        return target;
    }
    internal void Prepare()
    {
        foreach (var path in new[] { Root, Backups, Exports, Cache, Logs })
        {
            RejectLink(path);
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) SecureWindowsDirectory(path);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        foreach (var path in new[] { Database, Database + "-wal", Database + "-shm", Path.Combine(Root, "workspace.lock") }) RejectLink(path);
    }
    internal static void RejectLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new CoreException("数据目录或文件不能是符号链接或重解析点。");
    }
    internal static void SecureFile(string path)
    {
        if (!File.Exists(path)) return;
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in WindowsOwners()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    internal static void SecureDirectory(string path)
    {
        RejectLink(path);
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) SecureWindowsDirectory(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] WindowsOwners()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return [identity.User ?? throw new CoreException("无法读取当前 Windows 用户。"), new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)];
    }
    [SupportedOSPlatform("windows")]
    private static void SecureWindowsDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in WindowsOwners()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}

/// Owns one database and exclusive workspace lease. No passwords, tokens or keys
/// belong in the profile. Login success is established separately by the host.
public sealed class NativeWorkspace : IDisposable
{
    private sealed record Profile(int Version, string[] Accounts, string? LastAccount);
    private const string ProfileKey = "windows.workspace.profile";
    private readonly CoreStore core;
    private readonly FileStream lease;
    private readonly object gate = new();
    private Profile profile;
    private bool disposed;
    public WorkspacePaths Paths { get; }
    public string? UpgradeBackup { get; }
    public uint SchemaVersion => core.SchemaVersion!.Value;
    public IReadOnlyList<string> Accounts { get { lock (gate) return Array.AsReadOnly((string[])profile.Accounts.Clone()); } }
    public string? LastAccount { get { lock (gate) return profile.LastAccount; } }

    private NativeWorkspace(WorkspacePaths paths, CoreStore core, FileStream lease, Profile profile, string? backup)
    { Paths = paths; this.core = core; this.lease = lease; this.profile = profile; UpgradeBackup = backup; }

    public static NativeWorkspace Open(WorkspacePaths? paths = null)
    {
        paths ??= WorkspacePaths.Default();
        paths.Prepare();
        var lockPath = Path.Combine(paths.Root, "workspace.lock");
        FileStream lease;
        try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new CoreException("学习空间正在使用中，请关闭另一个实例后重试。"); }
        CoreStore? core = null;
        try
        {
            WorkspacePaths.SecureFile(lockPath);
            var backup = Path.Combine(paths.Backups, "schema-before-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            core = CoreStore.OpenVersioned(paths.Database, backup);
            foreach (var file in new[] { paths.Database, paths.Database + "-wal", paths.Database + "-shm", backup }) WorkspacePaths.SecureFile(file);
            var saved = core.GetSetting(ProfileKey);
            var profile = saved.ValueKind == JsonValueKind.Null ? new Profile(1, [], null) : saved.Deserialize<Profile>() ?? throw new CoreException("本地账号配置无效。");
            if (profile.Version != 1 || profile.Accounts == null || profile.Accounts.Distinct(StringComparer.Ordinal).Count() != profile.Accounts.Length)
                throw new CoreException("本地账号配置版本或内容无效。");
            foreach (var owner in profile.Accounts) ValidateOwner(owner);
            if (profile.LastAccount != null && !profile.Accounts.Contains(profile.LastAccount, StringComparer.Ordinal)) throw new CoreException("本地账号配置不一致。");
            return new NativeWorkspace(paths, core, lease, profile, core.BackupCreated ? backup : null);
        }
        catch { core?.Dispose(); lease.Dispose(); throw; }
    }
    internal static string ValidateOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256 || owner != owner.Trim() || owner.Any(char.IsControl)) throw new ArgumentException("学校账号标识无效。", nameof(owner));
        return owner;
    }
    public AccountStore RegisterAuthenticatedAccount(string owner)
    {
        owner = ValidateOwner(owner);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!profile.Accounts.Contains(owner, StringComparer.Ordinal)) Save(profile with { Accounts = [.. profile.Accounts, owner] });
            return new(core, owner, CancellationToken.None);
        }
    }
    public AccountStore OpenAccount(string owner)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!profile.Accounts.Contains(owner, StringComparer.Ordinal)) throw new CoreException("未找到该账号的本地学习空间。");
            return new(core, owner, CancellationToken.None);
        }
    }
    // Queue coordinator calls this only after old work is cancelled and drained.
    internal void RememberAccount(string owner)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!profile.Accounts.Contains(owner, StringComparer.Ordinal)) throw new CoreException("未找到该账号的本地学习空间。");
            Save(profile with { LastAccount = owner });
        }
    }
    private void Save(Profile updated) { core.SetSetting(ProfileKey, updated); profile = updated; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { core.Dispose(); } finally { lease.Dispose(); }
        }
    }
}

public sealed record RecordWrite(string Kind, string Id, object Body);

/// Owner is immutable: old contexts can never retarget writes to a newly selected account.
public sealed class AccountStore
{
    private readonly CoreStore core;
    private readonly CancellationToken cancellation;
    public string Owner { get; }
    internal AccountStore(CoreStore core, string owner, CancellationToken cancellation) { this.core = core; Owner = owner; this.cancellation = cancellation; }
    internal AccountStore BoundTo(CancellationToken token) => new(core, Owner, token);
    public JsonElement Get(string kind, string id) { cancellation.ThrowIfCancellationRequested(); return core.Get(Owner, kind, id); }
    public JsonElement[] List(string kind) { cancellation.ThrowIfCancellationRequested(); return core.List(Owner, kind).EnumerateArray().Select(x => x.Clone()).ToArray(); }
    public void Put(string kind, string id, object body) { cancellation.ThrowIfCancellationRequested(); core.Put(Owner, kind, id, body); }
    public void PutBatch(IEnumerable<RecordWrite> rows)
    {
        cancellation.ThrowIfCancellationRequested();
        core.PutBatch(Owner, rows.Select(x => new { kind = x.Kind, id = x.Id, body = x.Body }).ToArray());
    }
    public void MergeSchool(IEnumerable<RecordWrite> rows)
    {
        cancellation.ThrowIfCancellationRequested();
        core.MergeSchool(Owner, rows.Select(x => new { kind = x.Kind, id = x.Id, body = x.Body }).ToArray());
    }
    internal JsonElement ListNoticeGroups()
    {
        cancellation.ThrowIfCancellationRequested(); return core.ListNoticeGroups(Owner);
    }
    public JsonElement SetTaskDone(string id, bool done)
    {
        cancellation.ThrowIfCancellationRequested(); return core.SetTaskDone(Owner, id, done);
    }
    public void SaveAnalysis(JsonElement analysis, string digest)
    {
        cancellation.ThrowIfCancellationRequested(); core.SaveAnalysis(Owner, analysis, digest);
    }
    public void SaveMaterials(string course, string lesson, JsonElement body)
    {
        cancellation.ThrowIfCancellationRequested();
        core.SaveMaterials(Owner, course, lesson, body);
    }
}
