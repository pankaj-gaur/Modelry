using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Modelry.Web.Services;

/// <summary>Marks actions that may run without a Tridion connection.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowAnonymousTridionAttribute : Attribute { }

/// <summary>Redirects to the login page (or returns 401 for API calls) when there is no valid connection.</summary>
public sealed class RequireTridionSessionFilter : IActionFilter
{
    private readonly TridionSession _session;
    public RequireTridionSessionFilter(TridionSession session) => _session = session;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<AllowAnonymousTridionAttribute>().Any()) return;
        if (_session.IsConnected) return;
        var isApi = context.HttpContext.Request.Path.StartsWithSegments("/api");
        context.Result = isApi
            ? new UnauthorizedObjectResult(new { error = "Your session has ended. Sign in again to continue." })
            : _session.Cms is null
                ? new RedirectToActionResult("Index", "Start", null)
                : new RedirectToActionResult("Login", "Account", new { expired = !string.IsNullOrEmpty(_session.UserName) });
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
