using System.Text.Json;
using System.Text.RegularExpressions;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

public static partial class LoginProtocol
{
    // Only executed after checking the top-level origin, never on an SSO/password page.
    public const string CaptureScript = """
        (() => { try {
          const token = localStorage.getItem('token');
          const user = JSON.parse(localStorage.getItem('userinfo') || 'null');
          if (!token || !user?.jsdm || !user?.yhdm) return null;
          return {token, refresh: localStorage.getItem('refreshToken') || '',
                  role: String(user.jsdm), owner: String(user.yhdm)};
        } catch (_) { return null; } })()
        """;

    // Seed list from the existing school flow; add a host only after verifying a real redirect.
    private static readonly HashSet<string> LoginHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "sso.buaa.edu.cn", "spoc.buaa.edu.cn", "classroom.msa.buaa.edu.cn",
        "yjapi.msa.buaa.edu.cn", "portal.msa.buaa.edu.cn", "www.msa.buaa.edu.cn", "resource.msa.buaa.edu.cn"
    };
    private static readonly HashSet<string> CookieDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "buaa.edu.cn", "msa.buaa.edu.cn", "classroom.msa.buaa.edu.cn", "yjapi.msa.buaa.edu.cn",
        "portal.msa.buaa.edu.cn", "www.msa.buaa.edu.cn", "resource.msa.buaa.edu.cn"
    };
    public static bool IsAllowed(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && LoginHosts.Contains(uri.Host);
    public static bool CanCapture(Uri uri) => IsAllowed(uri) && uri.Host == "spoc.buaa.edu.cn";
    public static Uri? UpgradeRecording(Uri uri) => uri.Scheme == "http" && uri.Host == "yjapi.msa.buaa.edu.cn" && uri.Port == 80 && uri.UserInfo.Length == 0
        ? new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri : null;
    public static string? CourseId(Uri uri)
    {
        if (!IsAllowed(uri) || !uri.Host.EndsWith(".msa.buaa.edu.cn", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "course_id")
            {
                var id = Uri.UnescapeDataString(pair[1]);
                if (id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return id;
            }
        }
        return null;
    }
    public static string? RecordingBearer(string name, string domain, string value)
    {
        if (name != "_token" || !CookieDomains.Contains(domain.TrimStart('.')) || value.Length > 65536) return null;
        var match = BearerPattern().Match(Uri.UnescapeDataString(value));
        return match.Success ? match.Groups[1].Value : null;
    }
    [GeneratedRegex("s:[0-9]+:\"_token\";i:[0-9]+;s:[0-9]+:\"([^\"\\r\\n]+)\";", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BearerPattern();

    public static Uri RecordingEntry(JsonElement signature)
    {
        var raw = signature.GetProperty("ktsl_url").GetString();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !IsAllowed(uri))
            throw new CoreException("学校未提供受支持的录播入口。");
        var keys = new[] { "bind_id", "username", "realname", "tenant_code", "timestamp", "signature", "allow_live" };
        var parts = keys.Where(k => signature.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .Select(k => Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(signature.GetProperty(k).ToString()));
        return new UriBuilder(uri) { Query = string.Join("&", new[] { uri.Query.TrimStart('?') }.Concat(parts).Where(x => x.Length != 0)) }.Uri;
    }
}
