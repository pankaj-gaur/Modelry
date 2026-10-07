using Microsoft.AspNetCore.Mvc;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

public sealed class HomeController : Controller
{
    private readonly WizardState _wizard;
    public HomeController(WizardState wizard) => _wizard = wizard;

    /// <summary>Sends the user to the first unfinished step of the journey.</summary>
    public IActionResult Index()
    {
        var d = _wizard.Data;
        if (!d.HasIa) return RedirectToAction("Upload", "Wizard");
        if (d.SchemaRun is null) return RedirectToAction("Schemas", "Wizard");
        if (d.TemplateRun is null) return RedirectToAction("Templates", "Wizard");
        return RedirectToAction("Finish", "Wizard");
    }

    [AllowAnonymousTridion]
    public IActionResult Error() => View();
}
