using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public sealed record ModelEndpoint(string BaseUrl, Uri RequestUri, string Provider)
{
    public static ModelEndpoint Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.Host is "localhost" or "127.0.0.1" or "[::1]")))
            throw new CoreException("模型地址请使用 HTTPS；本地模型可使用 localhost HTTP，地址不能包含用户名、密码、查询参数或片段。", "model_configuration");
        var builder = new UriBuilder(uri) { Host = uri.IdnHost.ToLowerInvariant(), Port = uri.IsDefaultPort ? -1 : uri.Port };
        var canonical = builder.Uri; var path = canonical.AbsolutePath.TrimEnd('/');
        var baseUrl = canonical.GetLeftPart(UriPartial.Authority) + path;
        var provider = canonical.Host == "api.deepseek.com" && path is "" or "/v1" ? "responses" : "chat";
        return new(baseUrl, new Uri(baseUrl + (provider == "responses" ? "/responses" : "/chat/completions")), provider);
    }
    internal static string Model(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) throw new CoreException("请填写有效的模型名称。", "model_configuration");
        return value.Trim();
    }
}
