using System.Net;
using System.Text;
using System.Text.Json;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public sealed class SchoolTransport : IScopedSchoolTransport, IDisposable
{
    private readonly HttpClient client;
    private readonly CancellationToken lifetime;
    private readonly AsyncLocal<CancellationToken?> operation = new();
    public const int MaxResponseBytes = 32 * 1024 * 1024;
    public SchoolTransport(CancellationToken lifetime, HttpMessageHandler? handler = null)
    {
        this.lifetime = lifetime;
        client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    public string Send(string request) => SendAsync(request).GetAwaiter().GetResult();
    public IDisposable UseCancellation(CancellationToken cancellation)
    {
        var previous = operation.Value;
        operation.Value = cancellation;
        return new Scope(() => operation.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? restore = restore;
        public void Dispose() => Interlocked.Exchange(ref restore, null)?.Invoke();
    }
    private async Task<string> SendAsync(string raw)
    {
        using var json = JsonDocument.Parse(raw);
        var data = json.RootElement;
        var url = new Uri(data.GetProperty("url").GetString()!, UriKind.Absolute);
        var headers = data.GetProperty("headers").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        var method = data.GetProperty("method").GetString()!;
        ValidateRequest(url, method, headers);
        using var message = new HttpRequestMessage(new HttpMethod(method), url);
        foreach (var (key, value) in headers)
            if (!message.Headers.TryAddWithoutValidation(key, value)) throw new CoreException("学校请求头不受支持。");
        if (data.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null)
        {
            if (method != "POST") throw new CoreException("学校请求格式异常。");
            message.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime, operation.Value ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400) throw new CoreException("学校接口返回跳转，请重新登录。");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new CoreException("学校响应超过大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > MaxResponseBytes) throw new CoreException("学校响应超过大小限制。");
            bytes.Write(buffer, 0, read);
        }
        var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        return JsonSerializer.Serialize(new { status = (int)response.StatusCode, body = text });
    }
    public static void ValidateRequest(Uri url, string method, IReadOnlyDictionary<string, string> headers)
    {
        var allowed = url.Host is "spoc.buaa.edu.cn" or "classroom.msa.buaa.edu.cn" or "yjapi.msa.buaa.edu.cn";
        if (!allowed || url.Scheme != "https" || url.Port != 443 || url.UserInfo.Length != 0 || url.Fragment.Length != 0 || method is not ("GET" or "POST"))
            throw new CoreException("学校请求地址不受支持。");
        foreach (var (key, value) in headers)
        {
            if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) || value.Contains('\r') || value.Contains('\n'))
                throw new CoreException("学校请求头无效。");
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                throw new CoreException("学校请求头不受支持。");
            if (url.Host == "spoc.buaa.edu.cn" ? key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                : key.Equals("Token", StringComparison.OrdinalIgnoreCase) || key.Equals("RoleCode", StringComparison.OrdinalIgnoreCase))
                throw new CoreException("学校凭据不能发送到此地址。");
        }
    }
    public void Dispose() => client.Dispose();
}
