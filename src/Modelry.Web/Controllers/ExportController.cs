using Microsoft.AspNetCore.Mvc;
using Modelry.Core.Export;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

public sealed class ExportController : Controller
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly GatewayAccessor _gw;
    private readonly TridionSession _session;
    private readonly ILogger<ExportController> _log;
    public ExportController(GatewayAccessor gw, TridionSession session, ILogger<ExportController> log) { _gw = gw; _session = session; _log = log; }

    /// <summary>Stand-alone page: export schemas or templates of a folder into an IA workbook (Tridion only).</summary>
    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Download(string folderId, bool recursive = true, bool includeExtra = false, CancellationToken ct = default)
    {
        try
        {
            var result = await new ExportService(_gw.Gateway).ExportAsync(folderId,
                new ExportOptions { Recursive = recursive, IncludeParameterAndBundleSchemas = includeExtra }, ct);
            _log.LogInformation("AUDIT {Who} exported {Count} schemas from {Folder}; warnings: {Warnings}",
                _session.Who, result.SchemaCount, folderId, string.Join(" | ", result.Warnings));
            if (result.Warnings.Count > 0) Response.Headers["X-Export-Warnings"] = result.Warnings.Count.ToString();
            return File(result.Content, Xlsx, result.FileName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Export failed for {Folder}", folderId);
            TempData["Error"] = $"Export failed: {ex.Message}";
            return RedirectToAction(nameof(Index));
        }
    }
}
