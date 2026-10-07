using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Modelry.Core.Excel;
using Modelry.Tridion;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>The guided journey: upload the IA once, then create schemas, then templates.</summary>
public sealed class WizardController : Controller
{
    private readonly WizardState _wizard;
    private readonly TridionSession _session;
    private readonly UploadStore _uploads;
    private readonly TridionOptions _o;
    private readonly ILogger<WizardController> _log;

    public WizardController(WizardState wizard, TridionSession session, UploadStore uploads, IOptions<TridionOptions> o, ILogger<WizardController> log)
    { _wizard = wizard; _session = session; _uploads = uploads; _o = o.Value; _log = log; }

    [HttpGet]
    public IActionResult Upload() => View(_wizard.Data);

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file)
    {
        if (file is null || file.Length == 0) return Back("Choose an Information Architecture workbook (.xlsx) to upload.");
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return Back("The file must be an Excel workbook (.xlsx).");
        if (file.Length > _o.MaxUploadMegabytes * 1024L * 1024L) return Back($"The file is larger than {_o.MaxUploadMegabytes} MB.");
        var id = await _uploads.SaveAsync(file);
        try
        {
            await using var stream = _uploads.Open(id);
            var (wb, issues) = IaExcelReader.Read(stream, schemasRequired: false);
            var summary = new IaSummary
            {
                Schemas = wb.Schemas.Count, Fields = wb.Fields.Count, Categories = wb.Categories.Count, Keywords = wb.Keywords.Count,
                ComponentTemplates = wb.Templates.Count(t => t.Kind == Modelry.Core.Model.IaTemplateKind.ComponentTemplate),
                PageTemplates = wb.Templates.Count(t => t.Kind == Modelry.Core.Model.IaTemplateKind.PageTemplate),
                RegionRows = wb.Regions.Count,
                SchemasByPurpose = wb.Schemas.GroupBy(s => s.Purpose.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                ReadErrors = issues.Where(i => i.Level == Modelry.Core.Model.IssueLevel.Error).Select(i => $"{i.Sheet}: {i.Message}").Take(10).ToList(),
                TemplatesSource = wb.TemplatesSource,
                SheetNames = wb.SheetNames,
                Notes = issues.Where(i => i.Level == Modelry.Core.Model.IssueLevel.Warning && i.Item == "Templates").Select(i => i.Message).ToList()
            };
            if (summary.Schemas == 0 && summary.ComponentTemplates + summary.PageTemplates == 0)
                return Back("The workbook has no rows in the Schemas or Templates sheets. Start from the sample IA template.");
            _wizard.Update(d =>
            {
                d.UploadId = id; d.FileName = file.FileName; d.UploadedUtc = DateTime.UtcNow; d.Summary = summary;
                d.SchemaRun = null; d.TemplateRun = null;
            });
            _log.LogInformation("AUDIT {Who} uploaded IA '{File}' ({Schemas} schemas, {Templates} templates)", _session.Who, file.FileName,
                summary.Schemas, summary.ComponentTemplates + summary.PageTemplates);
            return RedirectToAction(nameof(Upload));
        }
        catch (IaWorkbookFormatException ex) { return Back(ex.Message); }
        catch (Exception ex) { _log.LogWarning(ex, "IA upload failed"); return Back($"The workbook could not be read: {ex.Message}"); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult RemoveIa()
    {
        _wizard.Clear();
        return RedirectToAction(nameof(Upload));
    }

    [HttpGet]
    public IActionResult Schemas() => _wizard.Data.HasIa ? View(_wizard.Data) : RedirectToAction(nameof(Upload));

    [HttpGet]
    public IActionResult Templates() => _wizard.Data.HasIa ? View(_wizard.Data) : RedirectToAction(nameof(Upload));

    [HttpGet]
    public IActionResult Finish() => _wizard.Data.HasIa ? View(_wizard.Data) : RedirectToAction(nameof(Upload));

    private IActionResult Back(string error)
    {
        TempData["Error"] = error;
        return RedirectToAction(nameof(Upload));
    }
}
