using Microsoft.AspNetCore.DataProtection;
using Modelry.Tridion;

namespace Modelry.Web.Services;

/// <summary>
/// Per-user connection state kept server-side in session (target CMS, connection, credentials).
/// OAuth: the access token is kept, never the client secret.
/// Windows: the password is needed for every Core Service call, so it is kept encrypted (ASP.NET Core Data Protection)
/// in the in-memory session and discarded on logout / session timeout.
/// </summary>
public sealed class TridionSession
{
    private const string CmsKey = "modelry.cms";
    private const string ModeKey = "tia.mode", UrlKey = "tia.url", UserKey = "tia.user", TokenKey = "tia.token",
        PasswordKey = "tia.pwd", ExpiresKey = "tia.expires", VersionKey = "tia.version";
    private readonly ISession _s;
    private readonly IDataProtector _protector;

    public TridionSession(IHttpContextAccessor http, IDataProtectionProvider dp)
    {
        _s = http.HttpContext!.Session;
        _protector = dp.CreateProtector("Modelry.Session.Credential");
    }

    /// <summary>"tridion" or "aem" – chosen on the start page.</summary>
    public string? Cms => _s.GetString(CmsKey);
    public bool IsAem => Cms == "aem";
    public string CmsName => Cms == "aem" ? "Adobe Experience Manager" : "Tridion Sites";
    public void SetCms(string cms) { _s.Clear(); _s.SetString(CmsKey, cms); }

    public string? Mode => _s.GetString(ModeKey);
    public bool IsDemo => Mode == "demo";
    public string? CoreServiceUrl => _s.GetString(UrlKey);
    public string? UserName => _s.GetString(UserKey);
    public string? ApiVersion => _s.GetString(VersionKey);
    public DateTimeOffset ExpiresAt => DateTimeOffset.TryParse(_s.GetString(ExpiresKey), out var d) ? d : DateTimeOffset.MinValue;

    public bool IsConnected => Mode switch
    {
        "demo" => true,
        "windows" => !string.IsNullOrEmpty(_s.GetString(PasswordKey)),
        "oauth" => !string.IsNullOrEmpty(_s.GetString(TokenKey)) && ExpiresAt > DateTimeOffset.UtcNow,
        "aem" => !string.IsNullOrEmpty(_s.GetString(PasswordKey)) || !string.IsNullOrEmpty(_s.GetString(TokenKey)),
        _ => false
    };

    public string Who => IsDemo ? "Demo mode" : $"{UserName} @ {new Uri(CoreServiceUrl!).Host}";
    public string Host => IsDemo ? "In-memory demo" : Uri.TryCreate(CoreServiceUrl, UriKind.Absolute, out var u) ? u.Host : "";

    public CoreServiceConnection Connection => Mode switch
    {
        "windows" => new CoreServiceConnection(CoreServiceUrl!, AuthMode.Windows, UserName: UserName,
                         Password: _protector.Unprotect(_s.GetString(PasswordKey)!)),
        "oauth" => new CoreServiceConnection(CoreServiceUrl!, AuthMode.OAuth, AccessToken: _protector.Unprotect(_s.GetString(TokenKey)!)),
        _ => throw new InvalidOperationException("Not connected to Tridion.")
    };

    public void SignInWindows(string url, string user, string password, string version)
    {
        Reset();
        _s.SetString(ModeKey, "windows"); _s.SetString(UrlKey, url); _s.SetString(UserKey, user);
        _s.SetString(PasswordKey, _protector.Protect(password)); _s.SetString(VersionKey, version);
    }

    public void SignInOAuth(string url, string clientId, string token, DateTimeOffset expires, string version)
    {
        Reset();
        _s.SetString(ModeKey, "oauth"); _s.SetString(UrlKey, url); _s.SetString(UserKey, clientId);
        _s.SetString(TokenKey, _protector.Protect(token)); _s.SetString(ExpiresKey, expires.ToString("O")); _s.SetString(VersionKey, version);
    }

    public void SignInDemo()
    {
        Reset();
        _s.SetString(CmsKey, "tridion");
        _s.SetString(ModeKey, "demo"); _s.SetString(VersionKey, "Demo mode (in-memory)");
    }

    public void SignInAem(string url, string user, string secret, bool isToken, string version)
    {
        Reset();
        _s.SetString(ModeKey, "aem"); _s.SetString(UrlKey, url); _s.SetString(UserKey, user);
        _s.SetString(isToken ? TokenKey : PasswordKey, _protector.Protect(secret)); _s.SetString(VersionKey, version);
    }

    /// <summary>Signs out but keeps the chosen CMS.</summary>
    public void SignOut()
    {
        var cms = Cms;
        _s.Clear();
        if (cms is not null) _s.SetString(CmsKey, cms);
    }

    /// <summary>Before a new sign-in: clears the old connection but keeps the chosen CMS and the uploaded IA.</summary>
    private void Reset()
    {
        var cms = Cms;
        var wizard = _s.GetString("modelry.wizard");
        _s.Clear();
        if (cms is not null) _s.SetString(CmsKey, cms);
        if (wizard is not null) _s.SetString("modelry.wizard", wizard);
    }
}
