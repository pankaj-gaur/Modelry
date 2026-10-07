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

    public async Task<string> VerifyAsync(string baseUrl, bool useToken, string? user, string secret, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/libs/granite/security/currentuser.json");
        req.Headers.Authorization = useToken
            ? new AuthenticationHeaderValue("Bearer", secret)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
