namespace Modelry.Tridion;

/// <summary>Bound to the "Tridion" section of appsettings.json.</summary>
public sealed class TridionOptions
{
    /// <summary>"Windows" (user id + password, basicHttp) or "OAuth" (client id + secret via Access Management).</summary>
    public string DefaultAuthMode { get; set; } = "Windows";
    /// <summary>Placeholder Core Service URL shown (editable) on the login screen. basicHttp endpoint.</summary>
    public string CoreServiceUrl { get; set; } = "https://{cms-host}/webservices/CoreService201701.svc/basicHttp";
    /// <summary>Tridion Access Management token endpoint (OAuth mode only).</summary>
    public string AccessManagementTokenUrl { get; set; } = "https://{access-management-host}/access-management/connect/token";
    /// <summary>Optional OAuth scope / resource requested with the token.</summary>
    public string? Scope { get; set; }
    /// <summary>OAuth mode binding: "basicHttp" (default) or "wsHttp" (transport security only; needs UseSessionAwareContract=true in the .csproj).</summary>
    public string OAuthBinding { get; set; } = "basicHttp";
    /// <summary>Hosts that may be reached over plain HTTP (dev environments only). Everything else requires HTTPS.</summary>
    public List<string> AllowHttpHosts { get; set; } = new();
    /// <summary>Optional explicit proxy (e.g. http://proxy.company.com:8080). Empty = system proxy settings.</summary>
    public string? ProxyUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public long MaxReceivedMessageSize { get; set; } = 64L * 1024 * 1024;
    /// <summary>If not empty, only these host names may be entered as CMS / token URLs (prevents SSRF).</summary>
    public List<string> AllowedHosts { get; set; } = new();
    public bool AllowDemoMode { get; set; } = true;
    public string CheckInComment { get; set; } = "Created by Modelry";
    public int SessionMinutes { get; set; } = 30;
    public int MaxUploadMegabytes { get; set; } = 10;
    /// <summary>Create reuses the check's snapshot if started within this many minutes (0 = always re-check).</summary>
    public int CheckReuseMinutes { get; set; } = 30;
}

public enum AuthMode { Windows, OAuth }

/// <summary>Everything needed to open a Core Service channel for one user.</summary>
public sealed record CoreServiceConnection(string Url, AuthMode Mode, string? AccessToken = null, string? UserName = null, string? Password = null)
{
    /// <summary>Splits DOMAIN\user or user@domain into a NetworkCredential.</summary>
    public System.Net.NetworkCredential ToNetworkCredential()
    {
        var user = UserName ?? "";
        var i = user.IndexOf('\\');
        return i > 0
            ? new System.Net.NetworkCredential(user[(i + 1)..], Password, user[..i])
            : new System.Net.NetworkCredential(user, Password);
    }
}
