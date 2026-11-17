using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public sealed record SlideImageResult(string Status, string? Mime = null, byte[]? Bytes = null, int Width = 0, int Height = 0);

/// One bounded image at a time. Cache metadata lives in the existing owner-scoped
/// SQLite; original content-addressed files live under the private cache directory.
public sealed class SlideImageCache : IAsyncDisposable
{
    public const int MaxImageBytes = 12 * 1024 * 1024;
    private readonly WorkspacePaths paths;
    private readonly HttpClient client;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private int active;
    private bool stopping;
    private bool disposed;
    private TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Metadata(string Digest, int Size, string Mime, int Width, int Height);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true };
    public SlideImageCache(WorkspacePaths paths, HttpMessageHandler? handler = null)
    {
        this.paths = paths;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        { if (buffer.Length + read > MaxImageBytes) throw new CoreException("单张 PPT 图片超过大小限制。"); buffer.Write(chunk, 0, read); }
        return buffer.ToArray();
    }
    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "resource.msa.buaa.edu.cn" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new CoreException("PPT 图片地址不受支持，请刷新课堂材料。");
        return uri;
    }
    private static Uri Resolve(AccountStore store, string lesson, string slide)
    {
        if (store.Get("lesson", lesson).ValueKind != JsonValueKind.Object) throw new ArgumentException("未找到这节课。");
        var material = store.Get("materials", lesson);
        if (material.ValueKind != JsonValueKind.Object || !material.TryGetProperty("slides", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new ArgumentException("未找到截图索引。");
        var matches = rows.EnumerateArray().Where(x => x.TryGetProperty("id", out var id) && id.GetString() == slide).ToArray();
        if (matches.Length != 1) throw new ArgumentException("未找到这张截图。");
        return ValidateUrl(matches[0].GetProperty("url").GetString()!);
    }
    public async Task<SlideImageResult> GetAsync(AccountStore store, string lesson, string slide, bool download, CancellationToken cancellation = default)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(stopping, this); active++; }
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            operation.CancelAfter(TimeSpan.FromSeconds(30));
            var token = operation.Token;
            await serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var uri = Resolve(store, lesson, slide); var key = Hash(uri.AbsoluteUri);
                var directory = Path.Combine(paths.Cache, "slides", Hash(store.Owner));
                WorkspacePaths.SecureDirectory(Path.Combine(paths.Cache, "slides")); WorkspacePaths.SecureDirectory(directory);
                var metadata = store.Get("meta", "slide-cache:" + key);
                Metadata? saved = null;
                try { if (metadata.ValueKind == JsonValueKind.Object) saved = metadata.Deserialize<Metadata>(Json); } catch (JsonException) { }
                var corrupt = metadata.ValueKind != JsonValueKind.Null;
                if (saved != null && saved.Digest is { Length: 64 } && saved.Digest.All(char.IsAsciiHexDigit) && saved.Size is > 0 and <= MaxImageBytes)
                {
                    var file = Path.Combine(directory, key + "." + saved.Digest.ToLowerInvariant() + ".bin"); WorkspacePaths.RejectLink(file);
                    if (File.Exists(file) && new FileInfo(file).Length == saved.Size)
                    {
                        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var bytes = await ReadBoundedAsync(input, token).ConfigureAwait(false);
                        try
                        {
                            var info = CoreBridge.InspectSlideImage(bytes);
                            if (info.GetProperty("digest").GetString() == saved.Digest && info.GetProperty("mime").GetString() == saved.Mime && info.GetProperty("width").GetInt32() == saved.Width && info.GetProperty("height").GetInt32() == saved.Height)
                            { token.ThrowIfCancellationRequested(); return new("cached", saved.Mime, bytes, saved.Width, saved.Height); }
                        }
                        catch (CoreException) { }
                    }
                }
                if (!download) return new(corrupt ? "corrupt" : "missing");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri); // No cookies, credentials or default auth headers.
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK) throw new CoreException("PPT 图片读取失败，请稍后重试。");
                if (response.Content.Headers.ContentLength > MaxImageBytes) throw new CoreException("单张 PPT 图片超过大小限制。");
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var original = await ReadBoundedAsync(stream, token).ConfigureAwait(false); var inspected = CoreBridge.InspectSlideImage(original);
                token.ThrowIfCancellationRequested();
                if (Resolve(store, lesson, slide) != uri) throw new CoreException("课堂截图索引已变化，请重新打开。");
                var entry = new Metadata(inspected.GetProperty("digest").GetString()!, original.Length, inspected.GetProperty("mime").GetString()!, inspected.GetProperty("width").GetInt32(), inspected.GetProperty("height").GetInt32());
                var target = Path.Combine(directory, key + "." + entry.Digest + ".bin"); var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { WorkspacePaths.SecureFile(temporary); await file.WriteAsync(original, token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); }
                    token.ThrowIfCancellationRequested(); WorkspacePaths.RejectLink(target); File.Move(temporary, target, true);
                    store.Put("meta", "slide-cache:" + key, JsonSerializer.SerializeToElement(entry, Json));
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                token.ThrowIfCancellationRequested(); return new("cached", entry.Mime, original, entry.Width, entry.Height);
            }
            finally { serial.Release(); }
        }
        finally { lock (gate) { active--; if (stopping && active == 0) drained.TrySetResult(); } }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; stopping = true; if (active == 0) drained.TrySetResult(); }
        try { lifetime.Cancel(); } catch (AggregateException) { }
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        lock (gate) { if (disposed) return; disposed = true; client.Dispose(); lifetime.Dispose(); }
    }
}
