using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public sealed record ModelSettingsView(string BaseUrl, string Model, bool Ready, string KeyState, bool HasCredentialReference, bool CleanupPending);
public sealed class ModelCallSettings : IDisposable
{
    public string Owner { get; }
    public ModelEndpoint Endpoint { get; }
    public string Model { get; }
    [JsonIgnore] internal ModelSecret? Key { get; }
    private int disposed;
    internal ModelCallSettings(string owner, ModelEndpoint endpoint, string model, ModelSecret? key) { Owner = owner; Endpoint = endpoint; Model = model; Key = key; }
    internal void CheckLive() => ObjectDisposedException.ThrowIf(disposed != 0, this);
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Key?.Dispose(); }
    public override string ToString() => "[模型调用配置]";
}

/// Non-secret settings and versioned vault references commit in one SQLite batch.
/// Superseded keys remain tracked for cleanup if the native vault is unavailable.
public sealed class ModelSettingsService(IModelCredentialStore credentials)
{
    private readonly object gate = new();
    private sealed record Config(int Version, string BaseUrl, string Model);
    private sealed record CredentialRef(string BaseUrl, string? Reference);
    private sealed record Retired(string BaseUrl, string Reference);
    private sealed record Garbage(Retired[] Entries);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true };
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RefId(string endpoint) => "model-credential:" + Hash(endpoint);
    private static string Target(string owner, string endpoint, string reference) => "BUAALearn/model/" + Hash(JsonSerializer.Serialize(new[] { owner, endpoint })) + "/" + (ValidRef(reference) ?? throw new CoreException("模型凭据引用无效。"));
    private static T? Read<T>(AccountStore store, string id) where T : class
    {
        var raw = store.Get("meta", id); if (raw.ValueKind == JsonValueKind.Null) return null;
        try { return raw.ValueKind == JsonValueKind.Object ? raw.Deserialize<T>(Json) ?? throw new JsonException() : throw new JsonException(); }
        catch (JsonException) { throw new CoreException("本地模型配置无效，请保留数据后检查。"); }
    }
    private static JsonElement Body<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    private static void EnsureVersion(AccountStore store)
    {
        if (Read<Config>(store, "model-config") is { Version: not 1 }) throw new CoreException("模型配置版本不受支持。");
    }
    private static CredentialRef Ref(AccountStore store, string endpoint) => Read<CredentialRef>(store, RefId(endpoint)) ?? new(endpoint, null);
    private static Garbage RetiredKeys(AccountStore store)
    {
        var saved = Read<Garbage>(store, "model-key-retired") ?? new([]);
        if (saved.Entries == null || saved.Entries.Length > 1000 || saved.Entries.Any(x => x == null || string.IsNullOrEmpty(x.BaseUrl) || string.IsNullOrEmpty(x.Reference)))
            throw new CoreException("模型凭据清理记录无效，请保留数据后检查。");
        return saved;
    }
    private static string? ValidRef(string? value)
    {
        if (value == null) return null;
        if (!Guid.TryParseExact(value, "N", out var id)) throw new CoreException("模型凭据引用无效，请保留数据后检查。");
        return id.ToString("N");
    }
    private string KeyState(AccountStore store, string endpoint, out bool hasReference)
    {
        var reference = Ref(store, endpoint); hasReference = reference.Reference != null;
        if (reference.BaseUrl != endpoint) throw new CoreException("模型凭据地址不一致。");
        if (reference.Reference == null) return "none";
        ValidRef(reference.Reference);
        try
        {
            var bytes = credentials.Read(Target(store.Owner, endpoint, reference.Reference));
            if (bytes == null) return "missing";
            try { return ValidKey(bytes) ? "saved" : "unavailable"; } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (ModelCredentialException) { return "unavailable"; }
    }
    private static bool ValidKey(ReadOnlySpan<byte> value)
    {
        if (value.Length is 0 or > WindowsModelCredentialStore.MaxBytes) return false;
        foreach (var b in value) if (b is < 33 or > 126) return false;
        return true;
    }
    public ModelSettingsView Get(AccountStore store)
    {
        lock (gate)
        {
            var config = Read<Config>(store, "model-config");
            if (config == null) return new("", "", false, "none", false, RetiredKeys(store).Entries.Length > 0);
            if (config.Version != 1) throw new CoreException("模型配置版本不受支持。");
            var endpoint = ModelEndpoint.Parse(config.BaseUrl); var model = ModelEndpoint.Model(config.Model);
            var state = KeyState(store, endpoint.BaseUrl, out var has);
            return new(endpoint.BaseUrl, model, state is "none" or "saved", state, has, RetiredKeys(store).Entries.Length > 0);
        }
    }
    public ModelSettingsView InspectEndpoint(AccountStore store, string baseUrl)
    {
        lock (gate)
        {
            var endpoint = ModelEndpoint.Parse(baseUrl); var state = KeyState(store, endpoint.BaseUrl, out var has);
            return new(endpoint.BaseUrl, "", false, state, has, RetiredKeys(store).Entries.Length > 0);
        }
    }
    public ModelSettingsView Save(AccountStore store, string baseUrl, string model, string keyAction, string? key = null)
    {
        var endpoint = ModelEndpoint.Parse(baseUrl); model = ModelEndpoint.Model(model);
        if (keyAction is not ("keep" or "replace" or "remove") || (keyAction == "replace" ? key == null : key != null)) throw new ArgumentException("请选择有效的密钥操作。");
        byte[]? bytes = key == null ? null : Encoding.UTF8.GetBytes(key);
        try
        {
            if (bytes != null && !ValidKey(bytes)) throw new ArgumentException("密钥不可包含空白或控制字符，且最多为 2560 字节。");
            lock (gate)
            {
                EnsureVersion(store);
                var previous = Ref(store, endpoint.BaseUrl); if (previous.BaseUrl != endpoint.BaseUrl) throw new CoreException("模型凭据地址不一致。");
                ValidRef(previous.Reference); var originalRetired = RetiredKeys(store).Entries; var retired = originalRetired.ToList();
                var reference = keyAction == "replace" ? Guid.NewGuid().ToString("N") : keyAction == "remove" ? null : previous.Reference;
                // Persist an unreachable candidate reference before writing the
                // vault. A failed DB commit/deletion can never orphan an untracked key.
                if (bytes != null) store.Put("meta", "model-key-retired", Body(new Garbage([.. retired, new(endpoint.BaseUrl, reference!)])));
                if (previous.Reference != null && previous.Reference != reference) retired.Add(new(endpoint.BaseUrl, previous.Reference));
                try
                {
                    if (bytes != null) credentials.Write(Target(store.Owner, endpoint.BaseUrl, reference!), bytes);
                    store.PutBatch([new("meta", "model-config", Body(new Config(1, endpoint.BaseUrl, model))),
                        new("meta", RefId(endpoint.BaseUrl), Body(new CredentialRef(endpoint.BaseUrl, reference))),
                        new("meta", "model-key-retired", Body(new Garbage(retired.Distinct().ToArray())))]);
                }
                catch
                {
                    if (bytes != null)
                    {
                        try
                        {
                            credentials.Delete(Target(store.Owner, endpoint.BaseUrl, reference!));
                            // Preserve the preparation record if SQLite is unavailable.
                            store.Put("meta", "model-key-retired", Body(new Garbage(originalRetired)));
                        }
                        catch (ModelCredentialException) { }
                        catch (CoreException) { }
                        catch (OperationCanceledException) { }
                    }
                    throw;
                }
                try { Cleanup(store); } catch (CoreException) { /* Active config committed; keep cleanup references visible. */ }
                return Get(store);
            }
        }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Cleanup(AccountStore store)
    {
        lock (gate)
        {
            EnsureVersion(store);
            var saved = RetiredKeys(store).Entries; var remaining = new List<Retired>();
            foreach (var item in saved)
            {
                ValidRef(item.Reference);
                var endpoint = ModelEndpoint.Parse(item.BaseUrl).BaseUrl;
                // Never delete a reference that remains active, even after malformed import.
                if (ValidRef(Ref(store, endpoint).Reference) == ValidRef(item.Reference)) throw new CoreException("模型凭据清理记录不一致。");
                try { credentials.Delete(Target(store.Owner, endpoint, item.Reference)); } catch (ModelCredentialException) { remaining.Add(item); }
            }
            if (remaining.Count != saved.Length) store.Put("meta", "model-key-retired", Body(new Garbage(remaining.ToArray())));
        }
    }
    public ModelSettingsView RemoveKey(AccountStore store, string baseUrl)
    {
        var endpoint = ModelEndpoint.Parse(baseUrl);
        lock (gate)
        {
            EnsureVersion(store);
            var previous = Ref(store, endpoint.BaseUrl);
            if (previous.BaseUrl != endpoint.BaseUrl) throw new CoreException("模型凭据地址不一致。");
            var retired = RetiredKeys(store).Entries.ToList();
            if (ValidRef(previous.Reference) is { } reference) retired.Add(new(endpoint.BaseUrl, reference));
            // Key removal is independent of unsaved model/address form changes.
            store.PutBatch([new("meta", RefId(endpoint.BaseUrl), Body(new CredentialRef(endpoint.BaseUrl, null))),
                new("meta", "model-key-retired", Body(new Garbage(retired.Distinct().ToArray())))]);
            try { Cleanup(store); } catch (CoreException) { }
            return InspectEndpoint(store, endpoint.BaseUrl);
        }
    }
    public ModelCallSettings Capture(AccountStore store)
    {
        lock (gate)
        {
            var config = Read<Config>(store, "model-config") ?? throw new CoreException("请先配置模型服务和模型名称。");
            if (config.Version != 1) throw new CoreException("模型配置版本不受支持。");
            var endpoint = ModelEndpoint.Parse(config.BaseUrl); var model = ModelEndpoint.Model(config.Model); var reference = Ref(store, endpoint.BaseUrl);
            if (reference.BaseUrl != endpoint.BaseUrl) throw new CoreException("模型凭据地址不一致。");
            byte[]? bytes = null;
            if (ValidRef(reference.Reference) is { } id)
            {
                bytes = credentials.Read(Target(store.Owner, endpoint.BaseUrl, id));
                if (bytes == null) throw new CoreException("已保存的模型密钥不可用，请重新保存或移除。");
                if (!ValidKey(bytes)) { CryptographicOperations.ZeroMemory(bytes); throw new ModelCredentialException(); }
            }
            return new(store.Owner, endpoint, model, bytes == null ? null : new ModelSecret(bytes));
        }
    }
}
