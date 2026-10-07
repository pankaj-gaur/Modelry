using Microsoft.AspNetCore.Mvc;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>First page: choose the target CMS.</summary>
[AllowAnonymousTridion]
public sealed class StartController : Controller
{
    private readonly TridionSession _session;
    public StartController(TridionSession session) => _session = session;

    [HttpGet]
    public IActionResult Index() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Choose(string cms)
    {
        if (cms is not ("tridion" or "aem")) return RedirectToAction(nameof(Index));
        _session.SetCms(cms);
        return RedirectToAction("Login", "Account");
    }
}
