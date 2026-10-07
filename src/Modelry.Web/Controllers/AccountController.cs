using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Modelry.Core.Gateway;
using Modelry.Tridion;
using Modelry.Web.Models;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

[AllowAnonymousTridion]
public sealed class AccountController : Controller
{
    private const string RememberCookie = "tia.remember";
    private readonly TridionOptions _o;
    private readonly TridionSession _session;
    private readonly AccessManagementTokenClient _tokens;
    private readonly AemConnectionClient _aem;
    private readonly IConfiguration _config;
    private readonly ILogger<AccountController> _log;

    public AccountController(IOptions<TridionOptions> o, TridionSession session, AccessManagementTokenClient tokens, AemConnectionClient aem,
        IConfiguration config, ILogger<AccountController> log)
    { _o = o.Value; _session = session; _tokens = tokens; _aem = aem; _config = config; _log = log; }

    [HttpGet]
    public IActionResult Login(bool expired = false)
    {
        if (_session.Cms is null) return RedirectToAction("Index", "Start");
        var vm = new LoginViewModel
        {
            AuthMode = _o.DefaultAuthMode, CoreServiceUrl = _o.CoreServiceUrl, TokenUrl = _o.AccessManagementTokenUrl, Scope = _o.Scope,
            DemoAllowed = _o.AllowDemoMode, ProxyAvailable = CoreServiceGatewayFactory.IsAvailable, Expired = expired,
            Cms = _session.Cms!, AemUrl = _config["Aem:AuthorUrl"] ?? "https://author-p{program}-e{environment}.adobeaemcloud.com"
        };
        // Remember mode, URLs and user / client id only – never the password or secret.
        if (Request.Cookies.TryGetValue(RememberCookie, out var remembered))
        {
            var p = remembered.Split('\n');
            if (p.Length == 4 && p[0] == "AEM") { vm.AemUrl = p[1]; vm.AemAuth = p[2]; vm.AemUser = p[3]; }
            else if (p.Length == 4)
            {
                vm.AuthMode = p[0]; vm.CoreServiceUrl = p[1];
                if (p[0] == "OAuth") { vm.TokenUrl = p[2]; vm.ClientId = p[3]; } else vm.UserName = p[3];
            }
        }
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel vm, CancellationToken ct)
    {
        vm.DemoAllowed = _o.AllowDemoMode; vm.ProxyAvailable = CoreServiceGatewayFactory.IsAvailable;
        vm.Cms = _session.Cms ?? "tridion";
        if (_session.IsAem) return await AemLoginAsync(vm, ct);
        if (vm.Demo)
        {
            if (!_o.AllowDemoMode) return Fail(vm, "Demo mode is disabled.");
            _session.SignInDemo();
            _log.LogInformation("AUDIT login demo from {Ip}", HttpContext.Connection.RemoteIpAddress);
            return RedirectToAction("Index", "Home");
        }

        var oauth = vm.AuthMode == "OAuth";
        var url = vm.CoreServiceUrl?.Trim() ?? "";
        var error = HostGuard.Validate(url, _o, "Core Service URL");
        if (error is null && oauth)
        {
            error = HostGuard.Validate(vm.TokenUrl, _o, "Token URL");
            if (error is null && (string.IsNullOrWhiteSpace(vm.ClientId) || string.IsNullOrWhiteSpace(vm.ClientSecret)))
                error = "Client ID and client secret are required.";
        }
        if (error is null && !oauth && (string.IsNullOrWhiteSpace(vm.UserName) || string.IsNullOrEmpty(vm.Password)))
            error = "User ID and password are required.";
        if (error is null && !CoreServiceGatewayFactory.IsAvailable)
            error = "Core Service proxy not generated – see README (demo mode still works).";
        if (error is not null) return Fail(vm, error);

        try
        {
            CoreServiceConnection connection;
            DateTimeOffset expires = default;
            if (oauth)
            {
                var token = await _tokens.RequestAsync(vm.TokenUrl!.Trim(), vm.ClientId!.Trim(), vm.ClientSecret!, vm.Scope, ct);
                connection = new CoreServiceConnection(url, AuthMode.OAuth, AccessToken: token.Value);
                expires = token.ExpiresAt;
            }
            else
            {
                connection = new CoreServiceConnection(url, AuthMode.Windows, UserName: vm.UserName!.Trim(), Password: vm.Password);
            }

            string version;
            var gw = CoreServiceGatewayFactory.Create(_o, connection);
            try { version = await gw.GetApiVersionAsync(); }
            finally { (gw as IDisposable)?.Dispose(); }

            var who = oauth ? vm.ClientId!.Trim() : vm.UserName!.Trim();
            if (oauth) _session.SignInOAuth(url, who, connection.AccessToken!, expires, version);
            else _session.SignInWindows(url, who, vm.Password!, version);

            Response.Cookies.Append(RememberCookie, $"{(oauth ? "OAuth" : "Windows")}\n{url}\n{vm.TokenUrl?.Trim()}\n{who}",
                new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddDays(30) });
            _log.LogInformation("AUDIT login {Mode} {User} to {Url} (API {Version}){Http}", oauth ? "OAuth" : "Windows", who, url, version,
                HostGuard.IsHttp(url) ? " over plain HTTP" : "");
            return RedirectToAction("Index", "Home");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Login failed for {User} to {Url}", oauth ? vm.ClientId : vm.UserName, url);
            return Fail(vm, $"Connection failed: {Explain(ex)}");
        }
    }

    private async Task<IActionResult> AemLoginAsync(LoginViewModel vm, CancellationToken ct)
    {
        var useToken = vm.AemAuth == "token";
        var url = vm.AemUrl?.Trim() ?? "";
        var error = HostGuard.Validate(url, _o, "AEM author URL");
        if (error is null && useToken && string.IsNullOrWhiteSpace(vm.AemToken)) error = "Paste an access token.";
        if (error is null && !useToken && (string.IsNullOrWhiteSpace(vm.AemUser) || string.IsNullOrEmpty(vm.AemPassword))) error = "Enter your user name and password.";
        if (error is not null) return FailAem(vm, error);
        try
        {
            var who = await _aem.VerifyAsync(url, useToken, vm.AemUser?.Trim(), useToken ? vm.AemToken!.Trim() : vm.AemPassword!, ct);
            _session.SignInAem(url, who, useToken ? vm.AemToken!.Trim() : vm.AemPassword!, useToken, "AEM");
            Response.Cookies.Append(RememberCookie, $"AEM\n{url}\n{vm.AemAuth}\n{(useToken ? "" : vm.AemUser?.Trim())}",
                new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddDays(30) });
            _log.LogInformation("AUDIT login AEM {User} to {Url}", who, url);
            return RedirectToAction("Index", "Home");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AEM login failed for {Url}", url);
            return FailAem(vm, $"Could not connect: {ex.Message}");
        }
    }

    private IActionResult FailAem(LoginViewModel vm, string error)
    {
        vm.Error = error; vm.AemPassword = null; vm.AemToken = null;
        return View("Login", vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        _log.LogInformation("AUDIT logout {Who}", _session.IsConnected ? _session.Who : "-");
        _session.SignOut();
        return RedirectToAction(nameof(Login));
    }

    /// <summary>Signs out and goes back to the CMS choice.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult ChangeCms()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Index", "Start");
    }

    private IActionResult Fail(LoginViewModel vm, string error)
    {
        vm.Error = error; vm.Password = null; vm.ClientSecret = null;
        return View(vm);
    }

    /// <summary>Turns common WCF / network errors into actionable messages.</summary>
    private static string Explain(Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        return ex switch
        {
            System.ServiceModel.Security.MessageSecurityException => "authentication rejected (check user ID / password, domain, or that the endpoint allows Windows authentication). " + msg,
            System.ServiceModel.EndpointNotFoundException or TimeoutException =>
                "server not reachable (network, VPN or proxy – see README 'Connectivity'). " + msg,
            _ => msg
        };
    }
}
