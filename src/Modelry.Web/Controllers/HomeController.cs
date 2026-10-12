using Microsoft.AspNetCore.Mvc;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

public sealed class HomeController : Controller
{
    private readonly WizardState _wizard;
    private readonly TridionSession _session;
    public HomeController(WizardState wizard, TridionSession session) { _wizard = wizard; _session = session; }

    /// <summary>
    /// After sign-in, Tridion users choose Build (from an IA workbook) or Audit (an existing publication). Once a workbook
    /// is uploaded, sends the user to the first unfinished step of the Build journey.
    /// </summary>
    public IActionResult Index()
    {
        var d = _wizard.Data;
        if (!d.HasIa) return RedirectToAction(nameof(Choose));
        if (d.SchemaRun is null) return RedirectToAction("Schemas", "Wizard");
        if (d.TemplateRun is null) return RedirectToAction("Templates", "Wizard");
        if (d.ModelRun is null) return RedirectToAction("Models", "Wizard");
        return RedirectToAction("Finish", "Wizard");
    }

    /// <summary>Build or Audit.</summary>
    [HttpGet]
    public IActionResult Choose() => View();

    [AllowAnonymousTridion]
    public IActionResult Error() => View();
}
