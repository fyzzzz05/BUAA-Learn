using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public sealed class ModelRequestException : Exception
{
    public string Code { get; }
    internal ModelRequestException(string code, string message) : base(message) { Code = code; }
}

/// Provider formatting/parsing is shared Rust code; no school transport, cookies,
/// redirects, proxy credentials or raw provider error bodies are reused here.
public sealed class ModelTransport : IDisposable
{
    public const int MaxResponseBytes = 32 * 1024 * 1024;
    private readonly HttpClient client;
    public ModelTransport(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null, DefaultProxyCredentials = null,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<JsonElement> CompleteAsync(ModelCallSettings settings, string system, string prompt, JsonElement? schema = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        settings.CheckLive();
        var body = CoreBridge.ModelRequest(settings.Endpoint.Provider, settings.Model, system, prompt, schema);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(180));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint.RequestUri) { Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json") };
        if (settings.Key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Key.Bearer);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ModelRequestException("authentication", "模型鉴权失败，请检查或替换该服务的密钥。");
            if ((int)response.StatusCode == 429) throw new ModelRequestException("quota", "模型服务限流或额度不足，请稍后重试。");
            if (response.StatusCode != HttpStatusCode.OK) throw new ModelRequestException("http", "模型请求失败，请检查服务地址和模型名称。");
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new ModelRequestException("response_limit", "模型响应超过大小限制，本次结果未保存。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            { if (buffer.Length + read > MaxResponseBytes) throw new ModelRequestException("response_limit", "模型响应超过大小限制，本次结果未保存。"); buffer.Write(chunk, 0, read); }
            var text = new UTF8Encoding(false, true).GetString(buffer.ToArray());
            try { return CoreBridge.ModelParse(settings.Endpoint.Provider, text); }
            catch (CoreException error) { throw new ModelRequestException(error.Code ?? "invalid_response", error.Message); }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new ModelRequestException("timeout", "模型请求超时，请稍后重试。"); }
        catch (HttpRequestException) { throw new ModelRequestException("network", "无法连接模型服务，请检查地址和网络。"); }
        catch (IOException) { throw new ModelRequestException("network", "读取模型响应失败，请稍后重试。"); }
        catch (DecoderFallbackException) { throw new ModelRequestException("invalid_response", "模型响应编码无效，本次结果未保存。"); }
        finally { request.Headers.Authorization = null; }
    }
    public void Dispose() => client.Dispose();
}
