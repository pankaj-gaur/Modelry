using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Modelry.Core.Excel;
using Modelry.Core.Gateway;
using Modelry.Core.Import;
using Modelry.Core.Templates;
using Modelry.Tridion;
using Modelry.Web.Models;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>Template export and template import (separate from schemas): upload → dry run → background job with progress.</summary>
public sealed class TemplatesController : Controller
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly GatewayAccessor _gw;
    private readonly TridionSession _session;
    private readonly UploadStore _uploads;
    private readonly IMemoryCache _cache;
    private readonly ImportJobStore _jobs;
    private readonly InMemoryTridionGateway _demo;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TridionOptions _o;
    private readonly TemplateFieldOptions _fields;
    private readonly WizardState _wizard;
    private readonly ILogger<TemplatesController> _log;

    public TemplatesController(GatewayAccessor gw, TridionSession session, UploadStore uploads, IMemoryCache cache, ImportJobStore jobs,
        InMemoryTridionGateway demo, IHostApplicationLifetime lifetime, IOptions<TridionOptions> o, IOptions<TemplateFieldOptions> fields,
        WizardState wizard, ILogger<TemplatesController> log)
    {
        _gw = gw; _session = session; _uploads = uploads; _cache = cache; _jobs = jobs; _demo = demo; _lifetime = lifetime;
        _o = o.Value; _fields = fields.Value; _wizard = wizard; _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> Export(string folderId, bool recursive = true, CancellationToken ct = default)
    {
        try
        {
            var r = await new TemplateExportService(_gw.Gateway).ExportAsync(folderId, recursive, _fields, ct);
            _log.LogInformation("AUDIT {Who} exported {Count} templates from {Folder}; warnings: {Warnings}", _session.Who, r.Count, folderId, string.Join(" | ", r.Warnings));
            return File(r.Content, Xlsx, r.FileName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Template export failed");
            TempData["Error"] = $"Template export failed: {ex.Message}";
            return RedirectToAction("Index", "Export");
        }
    }

    /// <summary>Starts the template check (dry run) in the background – nothing is changed – and shows live progress.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult DryRun(string folderId, string? baseCtId, string? basePtId,
        string? headerInclude, string? footerInclude, bool applyRegionConstraints = false)
    {
        var wiz = _wizard.Data;
        if (!wiz.HasIa) return Fail("Upload the IA workbook first.");
        if (string.IsNullOrWhiteSpace(folderId)) return Fail("Choose the folder the templates should be created in.");
        _wizard.Update(d => d.TemplateFolderId = folderId);
        var uploadId = wiz.UploadId!;
        var fileName = wiz.FileName!;
        var isDemo = _session.IsDemo;
        var connection = isDemo ? null : _session.Connection;
        var who = _session.Who;
        var options = Options(baseCtId, basePtId, headerInclude, footerInclude, applyRegionConstraints);
        var job = _jobs.Add(new ImportJob
        {
            Owner = HttpContext.Session.Id, FileName = fileName, FolderId = folderId, StepNames = CheckSteps.Templates, What = "Templates", Mode = "Check"
        });
        var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _lifetime.ApplicationStopping);
        _ = Task.Run(async () =>
        {
            ITridionGateway? gateway = null;
            try
            {
                gateway = isDemo ? _demo : CoreServiceGatewayFactory.Create(_o, connection!);
                var (plan, ctx) = await PlanAsync(gateway, uploadId, folderId, options, job, reportSteps: true, linked.Token);
                job.FolderTitle = ctx.Target.Title; job.PublicationTitle = ctx.Target.PublicationTitle;
                job.CheckSnapshot = new TemplateCheckSnapshot(plan, ctx, options, uploadId, folderId);
                var model = Model(uploadId, fileName, folderId, ctx, plan, baseCtId, basePtId, headerInclude, footerInclude, applyRegionConstraints);
                job.CheckDone(new TemplateDryRunViewModel
                {
                    UploadId = model.UploadId, FileName = model.FileName, FolderId = model.FolderId, FolderTitle = model.FolderTitle,
                    PublicationTitle = model.PublicationTitle, Plan = model.Plan, BaseComponentTemplateId = model.BaseComponentTemplateId,
                    BasePageTemplateId = model.BasePageTemplateId, HeaderInclude = model.HeaderInclude, FooterInclude = model.FooterInclude,
                    ApplyRegionConstraints = model.ApplyRegionConstraints, CheckJobId = job.Id, CheckedUtc = DateTime.UtcNow
                }, "~/Views/Templates/DryRun.cshtml");
                _log.LogInformation("AUDIT {Who} checked templates '{File}' against {Folder}: {Errors} errors", who, fileName, folderId,
                    plan.Issues.Count(i => i.Level == Modelry.Core.Model.IssueLevel.Error));
            }
            catch (OperationCanceledException) { job.Fail("The check was cancelled. Nothing was changed."); }
            catch (Exception ex) { _log.LogWarning(ex, "Template check failed"); job.Fail($"The template check could not run: {ex.Message}"); }
            finally
            {
                if (gateway is IDisposable d && gateway is not InMemoryTridionGateway) d.Dispose();
                linked.Dispose();
            }
        });
        return RedirectToAction("Progress", "Import", new { id = job.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Execute(string uploadId, string fileName, string folderId, string? baseCtId, string? basePtId,
        string? headerInclude, string? footerInclude, bool applyRegionConstraints = false, string? checkJobId = null, bool recheck = false)
    {
        var isDemo = _session.IsDemo;
        var connection = isDemo ? null : _session.Connection;
        var who = _session.Who;
        var options0 = Options(baseCtId, basePtId, headerInclude, footerInclude, applyRegionConstraints);

        // Reuse the check's snapshot when recent and for the same workbook, folder and choices.
        TemplateCheckSnapshot? snapshot = null;
        if (!recheck && _o.CheckReuseMinutes > 0 && checkJobId is not null && _jobs.Get(checkJobId, HttpContext.Session.Id) is { CheckSnapshot: TemplateCheckSnapshot s } check
            && s.UploadId == uploadId && s.FolderId == folderId && SameChoices(s.Options, options0)
            && check.FinishedUtc > DateTime.UtcNow.AddMinutes(-_o.CheckReuseMinutes))
        {
            snapshot = s;
            check.CheckSnapshot = null;
        }
        var job = _jobs.Add(new ImportJob
        {
            Owner = HttpContext.Session.Id, FileName = fileName, FolderId = folderId, StepNames = TemplateSteps.Names, What = "Templates"
        });
        var options = Options(baseCtId, basePtId, headerInclude, footerInclude, applyRegionConstraints);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _lifetime.ApplicationStopping);
        _ = Task.Run(async () =>
        {
            ITridionGateway? gateway = null;
            try
            {
                job.Start();
                gateway = isDemo ? _demo : CoreServiceGatewayFactory.Create(_o, connection!);
                TemplatePlan plan; TemplateContext ctx;
                if (snapshot is not null)
                {
                    job.ReportActivity("Using the check you just ran – the publication is not read again");
                    (plan, ctx) = (snapshot.Plan, snapshot.Context);
                }
                else (plan, ctx) = await PlanAsync(gateway, uploadId, folderId, options, job, reportSteps: false, linked.Token);   // fresh read
                job.FolderTitle = ctx.Target.Title; job.PublicationTitle = ctx.Target.PublicationTitle;
                if (plan.HasErrors)
                {
                    job.PlanFailed(Model(uploadId, fileName, folderId, ctx, plan, baseCtId, basePtId, headerInclude, footerInclude, applyRegionConstraints),
                        "~/Views/Templates/DryRun.cshtml");
                    return;
                }
                var result = await new TemplateExecutor(gateway).ExecuteAsync(plan, ctx, options, linked.Token, job);
                var resultId = Guid.NewGuid().ToString("N");
                _cache.Set("result:" + resultId, IaExcelWriter.WriteResults(result.Log), TimeSpan.FromHours(2));
                job.Complete(result, resultId);
                _log.LogInformation("AUDIT {Who} imported templates '{File}' into {Folder}: created {Created}, skipped {Skipped}, failed {Failed}",
                    who, fileName, folderId, result.Created, result.Skipped, result.Failed);
            }
            catch (OperationCanceledException) { job.Fail("The import was cancelled before any changes were made."); }
            catch (Exception ex) { _log.LogError(ex, "Template job {Job} failed", job.Id); job.Fail(ex.Message); }
            finally
            {
                if (gateway is IDisposable d && gateway is not InMemoryTridionGateway) d.Dispose();
                linked.Dispose();
            }
        });
        return RedirectToAction("Progress", "Import", new { id = job.Id });
    }

    private static bool SameChoices(TemplateImportOptions a, TemplateImportOptions b) =>
        a.BaseComponentTemplateId == b.BaseComponentTemplateId && a.BasePageTemplateId == b.BasePageTemplateId &&
        a.ApplyRegionConstraints == b.ApplyRegionConstraints && a.DefaultIncludes.SequenceEqual(b.DefaultIncludes);

    private TemplateImportOptions Options(string? baseCtId, string? basePtId, string? header, string? footer, bool apply)
    {
        var includes = new[] { header, footer }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        return new TemplateImportOptions
        {
            BaseComponentTemplateId = string.IsNullOrWhiteSpace(baseCtId) ? null : baseCtId,
            BasePageTemplateId = string.IsNullOrWhiteSpace(basePtId) ? null : basePtId,
            DefaultIncludes = includes, ApplyRegionConstraints = apply, Fields = _fields
        };
    }

    /// <summary>Reads the workbook, loads the publication snapshot and plans. With a job, reports steps (check) or activity (create).</summary>
    private async Task<(TemplatePlan, TemplateContext)> PlanAsync(ITridionGateway gw, string uploadId, string folderId, TemplateImportOptions options,
        ImportJob? job = null, bool reportSteps = false, CancellationToken ct = default)
    {
        if (reportSteps) job?.ReportStep(1, "Opening the workbook");
        Modelry.Core.Model.IaWorkbook wb; List<Modelry.Core.Model.Issue> issues;
        await using (var stream = _uploads.Open(uploadId)) (wb, issues) = IaExcelReader.Read(stream, schemasRequired: false);
        if (reportSteps) job?.ReportStep(2, "Opening the selected folder");
        var activity = job is null ? null : new Modelry.Core.Import.InlineProgress<string>(m => { ct.ThrowIfCancellationRequested(); job.ReportActivity(m); });
        var ctx = await TemplateContext.LoadAsync(gw, folderId, wb, options, activity);
        if (reportSteps) job?.ReportStep(3, $"Checking {wb.Templates.Count} templates");
        return (TemplatePlanner.Build(wb, issues, ctx, options), ctx);
    }

    private static TemplateDryRunViewModel Model(string uploadId, string fileName, string folderId, TemplateContext ctx, TemplatePlan plan,
        string? baseCtId, string? basePtId, string? header, string? footer, bool apply) => new()
    {
        UploadId = uploadId, FileName = fileName, FolderId = folderId, FolderTitle = ctx.Target.Title, PublicationTitle = ctx.Target.PublicationTitle,
        Plan = plan, BaseComponentTemplateId = baseCtId, BasePageTemplateId = basePtId, HeaderInclude = header, FooterInclude = footer,
        ApplyRegionConstraints = apply
    };

    private IActionResult Fail(string message)
    {
        TempData["Error"] = message;
        return RedirectToAction("Templates", "Wizard");
    }
}
