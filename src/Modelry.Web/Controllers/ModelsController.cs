using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Modelry.Core.CodeGen;
using Modelry.Core.Excel;
using Modelry.Core.Model;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>
/// Generates DXA .NET view models (C#) and their view registration from the uploaded IA workbook and offers them as a zip.
/// Works from the workbook alone – nothing is read from or written to the CMS.
/// </summary>
public sealed class ModelsController : Controller
{
    private readonly WizardState _wizard;
    private readonly TridionSession _session;
    private readonly UploadStore _uploads;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ModelsController> _log;

    public ModelsController(WizardState wizard, TridionSession session, UploadStore uploads, IMemoryCache cache, ILogger<ModelsController> log)
    { _wizard = wizard; _session = session; _uploads = uploads; _cache = cache; _log = log; }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(string? rootNamespace, string? pageMetadataSchema, string? semanticPrefix)
    {
        var d = _wizard.Data;
        if (!d.HasIa) return Fail("Upload the IA workbook first.", "Upload");
        if (_session.IsAem) return Fail("Model generation for AEM (Sling Models) arrives in a later release.");
        var ns = (rootNamespace ?? "").Trim();
        var page = string.IsNullOrWhiteSpace(pageMetadataSchema) ? null : pageMetadataSchema.Trim();
        var prefix = string.IsNullOrWhiteSpace(semanticPrefix) ? null : semanticPrefix.Trim();
        _wizard.Update(w => w.ModelSettings = new ModelSettings { Namespace = ns, PageMetadataSchema = page, SemanticPrefix = prefix });

        try
        {
            IaWorkbook wb;
            await using (var stream = _uploads.Open(d.UploadId!)) (wb, _) = IaExcelReader.Read(stream, schemasRequired: false);
            var plan = ModelPlanner.Build(wb, Options(d, ns, page, prefix));
            if (plan.HasErrors)
                return Fail("Fix the errors listed under Findings, then generate again.");

            var pkg = ModelPackage.Build(plan);
            var id = Guid.NewGuid().ToString("N");
            _cache.Set("models:" + id, pkg, TimeSpan.FromHours(2));
            _wizard.Update(w => w.ModelRun = new ModelRunSummary
            {
                Classes = pkg.Classes, Registrations = pkg.Registrations, Files = pkg.Files, Namespace = plan.Options.RootNamespace,
                Warnings = plan.Issues.Count(i => i.Level == IssueLevel.Warning), FileName = pkg.FileName, ResultId = id, FinishedUtc = DateTime.UtcNow
            });
            _log.LogInformation("AUDIT {Who} generated {Classes} DXA models ({Registrations} registrations) from '{File}' in {Namespace}",
                _session.Who, pkg.Classes, pkg.Registrations, d.FileName, plan.Options.RootNamespace);
            return RedirectToAction("Models", "Wizard");
        }
        catch (FileNotFoundException) { return Fail("The uploaded workbook is no longer available. Upload it again.", "Upload"); }
        catch (IaWorkbookFormatException ex) { return Fail(ex.Message); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Model generation failed");
            return Fail($"The models could not be generated: {ex.Message}");
        }
    }

    [HttpGet]
    public IActionResult Download(string id) =>
        _cache.TryGetValue("models:" + id, out ModelPackageResult? pkg) && pkg is not null
            ? File(pkg.Zip, "application/zip", pkg.FileName)
            : Fail("The generated zip has expired (they are kept for 2 hours). Generate the models again.");

    /// <summary>Options for the planner from the form values and the session.</summary>
    internal static ModelGenerationOptions Options(WizardData d, string? ns, string? pageMetadataSchema, string? semanticPrefix) => new()
    {
        RootNamespace = ns ?? "", PageMetadataSchema = pageMetadataSchema, SemanticPrefix = semanticPrefix,
        SourceFileName = d.FileName ?? "workbook.xlsx", GeneratedUtc = DateTime.UtcNow
    };

    /// <summary>Default page metadata schema: the only Metadata schema whose title mentions "page" (not a Structure Group one).</summary>
    internal static string? SuggestPageMetadata(IaWorkbook wb)
    {
        var candidates = wb.Schemas.Where(s => s.Purpose == IaSchemaPurpose.Metadata
                                               && s.Title.Contains("page", StringComparison.OrdinalIgnoreCase)
                                               && !s.Title.Contains("structure", StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? candidates[0].Title : null;
    }

    private IActionResult Fail(string message, string step = "Models")
    {
        TempData["Error"] = message;
        return RedirectToAction(step, "Wizard");
    }
}
