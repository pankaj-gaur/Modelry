using Microsoft.AspNetCore.Mvc;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

[AllowAnonymousTridion]
public sealed class SampleController : Controller
{
    private readonly IWebHostEnvironment _env;
    public SampleController(IWebHostEnvironment env) => _env = env;

    [HttpGet]
    public IActionResult Template() =>
        PhysicalFile(Path.Combine(_env.WebRootPath, "samples", "IA_Template.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "IA_Template.xlsx");
}
