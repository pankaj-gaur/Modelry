using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Modelry.Web.Services;

/// <summary>
/// Verifies an Adobe Experience Manager connection (AEM as a Cloud Service or 6.5) by reading the current user.
/// Basic authentication (local / on-premise accounts) or a bearer access token (e.g. AEMaaCS local development token / service credentials).
/// </summary>
public sealed class AemConnectionClient
{
    private readonly HttpClient _http;
    public AemConnectionClient(HttpClient http) => _http = http;

    private static HttpRequestMessage Request(string url, bool useToken, string? user, string secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = useToken
            ? new AuthenticationHeaderValue("Bearer", secret)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    /// <summary>
    /// "AEM as a Cloud Service" (host *.adobeaemcloud.com), else "AEM x.y.z" from the system overview export when the user may
    /// read it, else "AEM (version unknown)". Never fails the sign-in.
    /// </summary>
    public async Task<string> VersionAsync(string baseUrl, bool useToken, string? user, string secret, CancellationToken ct = default)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) && u.Host.EndsWith("adobeaemcloud.com", StringComparison.OrdinalIgnoreCase))
            return "AEM as a Cloud Service";
        try
        {
            using var req = Request(baseUrl.TrimEnd('/') + "/libs/granite/operations/content/systemoverview/export.json", useToken, user, secret);   // VERIFY: needs admin rights on 6.5
            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                var m = System.Text.RegularExpressions.Regex.Match(body, @"Adobe Experience Manager[^0-9]{0,20}(\d+\.\d+(\.\d+)*)");
                if (m.Success) return "AEM " + m.Groups[1].Value;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return "AEM (version unknown)";
    }

    public async Task<string> VerifyAsync(string baseUrl, bool useToken, string? user, string secret, CancellationToken ct = default)
    {
        using var req = Request(baseUrl.TrimEnd('/') + "/libs/granite/security/currentuser.json", useToken, user, secret);
        using var res = await _http.SendAsync(req, ct);
        if ((int)res.StatusCode is 401 or 403) throw new InvalidOperationException("AEM rejected the credentials (HTTP " + (int)res.StatusCode + ").");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"AEM answered HTTP {(int)res.StatusCode} – check the URL.");
        var body = await res.Content.ReadAsStringAsync(ct);
        string? id = null;
        try { id = JsonDocument.Parse(body).RootElement.GetProperty("authorizableId").GetString(); }
        catch { throw new InvalidOperationException("The URL did not return AEM user information – check that it points to an AEM author instance."); }
        if (string.IsNullOrEmpty(id) || id == "anonymous") throw new InvalidOperationException("Signed in as 'anonymous' – the credentials were not accepted.");
        return id;
    }
}
