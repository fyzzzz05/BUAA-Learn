using System.Runtime.InteropServices;
using System.Text.Json;

namespace BUAALearn.CoreInterop;

public sealed class CoreException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

public static class CoreBridge
{
    public const uint ExpectedAbi = 1;
    public static void CheckAbi()
    {
        if (NativeMethods.buaa_core_abi_version() != ExpectedAbi)
            throw new CoreException("业务内核版本不匹配，请重新安装。", "abi_mismatch");
    }
    internal static string Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("输入不可包含 NUL。", nameof(value));
        return value;
    }
    internal static JsonElement Decode(nint pointer)
    {
        if (pointer == 0) throw new CoreException("业务内核未返回数据。");
        try
        {
            var text = Marshal.PtrToStringUTF8(pointer) ?? throw new CoreException("内核数据无法读取。");
            using var document = JsonDocument.Parse(text);
            var envelope = document.RootElement;
            if (!envelope.GetProperty("ok").GetBoolean())
                throw new CoreException(envelope.GetProperty("error").GetString() ?? "业务操作失败。",
                    envelope.TryGetProperty("error_code", out var code) ? code.GetString() : null);
            return envelope.GetProperty("data").Clone();
        }
        finally { NativeMethods.buaa_core_string_free(pointer); }
    }
    public static JsonElement ParseSemesters(string body)
    {
        CheckAbi();
        return Decode(NativeMethods.buaa_core_parse_semesters(Text(body)));
    }
    public static JsonElement InspectSlideImage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is 0 or > 12 * 1024 * 1024) throw new ArgumentException("PPT 图片大小无效。");
        CheckAbi(); return Decode(NativeMethods.buaa_core_inspect_slide_image(bytes, (uint)bytes.Length));
    }
    public static JsonElement ModelRequest(string provider, string model, string system, string prompt, JsonElement? schema = null)
    {
        CheckAbi(); return Decode(NativeMethods.buaa_core_model_request(Text(provider), Text(model), Text(system), Text(prompt), schema?.GetRawText() ?? "null"));
    }
    public static JsonElement ModelParse(string provider, string body)
    {
        CheckAbi(); return Decode(NativeMethods.buaa_core_model_parse(Text(provider), Text(body)));
    }
}

public sealed class CoreStore : IDisposable
{
    private readonly StoreHandle handle;
    private readonly object gate = new();
    public uint? SchemaVersion { get; private init; }
    public bool BackupCreated { get; private init; }
    private CoreStore(StoreHandle handle) => this.handle = handle;
    public static CoreStore OpenVersioned(string path, string backup)
    {
        CoreBridge.CheckAbi();
        nint pointer = 0;
        try
        {
            var result = CoreBridge.Decode(NativeMethods.buaa_store_open_versioned(CoreBridge.Text(Path.GetFullPath(path)), CoreBridge.Text(Path.GetFullPath(backup)), out pointer));
            if (pointer == 0) throw new CoreException("内核未返回数据库句柄。");
            var version = result.GetProperty("schema_version").GetUInt32();
            var backedUp = result.GetProperty("backup_created").GetBoolean();
            var owned = new StoreHandle(pointer);
            pointer = 0; // Ownership has transferred; error paths must not free it again.
            return new CoreStore(owned) { SchemaVersion = version, BackupCreated = backedUp };
        }
        catch { if (pointer != 0) NativeMethods.buaa_store_close(pointer); throw; }
    }
    public CoreStore(string path)
    {
        CoreBridge.CheckAbi();
        handle = new StoreHandle(NativeMethods.buaa_store_open(CoreBridge.Text(Path.GetFullPath(path))));
        if (handle.IsInvalid) { handle.Dispose(); throw new CoreException("无法打开本地数据库。"); }
    }
    public void Put(string owner, string kind, string id, object body)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            CoreBridge.Decode(NativeMethods.buaa_store_put(handle, CoreBridge.Text(owner), CoreBridge.Text(kind), CoreBridge.Text(id), JsonSerializer.Serialize(body)));
        }
    }
    public JsonElement Get(string owner, string kind, string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            return CoreBridge.Decode(NativeMethods.buaa_store_get(handle, CoreBridge.Text(owner), CoreBridge.Text(kind), CoreBridge.Text(id)));
        }
    }
    public void Dispose() { lock (gate) handle.Dispose(); }
    public JsonElement List(string owner, string kind)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); return CoreBridge.Decode(NativeMethods.buaa_store_list(handle, CoreBridge.Text(owner), CoreBridge.Text(kind))); }
    }
    public JsonElement GetSetting(string key)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); return CoreBridge.Decode(NativeMethods.buaa_store_setting_get(handle, CoreBridge.Text(key))); }
    }
    public void SetSetting(string key, object value)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); CoreBridge.Decode(NativeMethods.buaa_store_setting_put(handle, CoreBridge.Text(key), JsonSerializer.Serialize(value))); }
    }
    public void PutBatch(string owner, object rows)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); CoreBridge.Decode(NativeMethods.buaa_store_put_batch(handle, CoreBridge.Text(owner), JsonSerializer.Serialize(rows))); }
    }
    public void MergeSchool(string owner, object rows)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); CoreBridge.Decode(NativeMethods.buaa_store_merge_school(handle, CoreBridge.Text(owner), JsonSerializer.Serialize(rows))); }
    }
    public JsonElement ListNoticeGroups(string owner)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); return CoreBridge.Decode(NativeMethods.buaa_store_list_notice_groups(handle, CoreBridge.Text(owner))); }
    }
    public JsonElement SetTaskDone(string owner, string id, bool done)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); return CoreBridge.Decode(NativeMethods.buaa_store_set_task_done(handle, CoreBridge.Text(owner), CoreBridge.Text(id), done ? 1u : 0u)); }
    }
    public void SaveAnalysis(string owner, JsonElement analysis, string digest)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); CoreBridge.Decode(NativeMethods.buaa_store_save_analysis_checked(handle, CoreBridge.Text(owner), analysis.GetRawText(), CoreBridge.Text(digest))); }
    }
    public void SaveMaterials(string owner, string course, string lesson, JsonElement body)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); CoreBridge.Decode(NativeMethods.buaa_store_save_materials(handle, CoreBridge.Text(owner), CoreBridge.Text(course), CoreBridge.Text(lesson), body.GetRawText())); }
    }
}
