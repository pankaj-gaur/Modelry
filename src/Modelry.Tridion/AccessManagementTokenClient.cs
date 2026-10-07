using System.Text.Json;

namespace Modelry.Tridion;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed class TridionAuthException : Exception
{
    public TridionAuthException(string message) : base(message) { }
}

/// <summary>Requests an OAuth access token (client credentials) from Tridion Access Management.</summary>
public sealed class AccessManagementTokenClient
{
    private readonly HttpClient _http;
    public AccessManagementTokenClient(HttpClient http) => _http = http;

    public async Task<AccessToken> RequestAsync(string tokenUrl, string clientId, string clientSecret, string? scope, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };
        if (!string.IsNullOrWhiteSpace(scope)) form["scope"] = scope;
        using var response = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new TridionAuthException($"Token request failed ({(int)response.StatusCode} {response.ReasonPhrase}). {Shorten(body)}");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var token))
            throw new TridionAuthException("Token response did not contain 'access_token'.");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return new AccessToken(token.GetString()!, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30));
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
