using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Modelry.Core.Excel;
using Modelry.Core.Gateway;
using Modelry.Core.Import;
using Modelry.Core.Model;
using Modelry.Tridion;
using Modelry.Web.Models;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>Upload → dry run (validation report) → background execution with live progress → results.</summary>
public sealed class ImportController : Controller
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
    private readonly WizardState _wizard;
    private readonly ILogger<ImportController> _log;

    public ImportController(GatewayAccessor gw, TridionSession session, UploadStore uploads, IMemoryCache cache, ImportJobStore jobs,
        InMemoryTridionGateway demo, IHostApplicationLifetime lifetime, IOptions<TridionOptions> o, WizardState wizard, ILogger<ImportController> log)
    {
        _gw = gw; _session = session; _uploads = uploads; _cache = cache; _jobs = jobs; _demo = demo;
        _lifetime = lifetime; _o = o.Value; _wizard = wizard; _log = log;
    }

    private string Owner => HttpContext.Session.Id;

    /// <summary>Starts the check (dry run) in the background – nothing is changed – and shows live progress.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult DryRun(string folderId)
    {
        var wiz = _wizard.Data;
        if (!wiz.HasIa) return Fail("Upload the IA workbook first.");
        if (string.IsNullOrWhiteSpace(folderId)) return FailStep("Schemas", "Choose the folder the schemas should be created in.");
        _wizard.Update(d => d.SchemaFolderId = folderId);
        var uploadId = wiz.UploadId!;
        var fileName = wiz.FileName!;
        var isDemo = _session.IsDemo;
        var connection = isDemo ? null : _session.Connection;
        var who = _session.Who;
        var job = _jobs.Add(new ImportJob { Owner = Owner, FileName = fileName, FolderId = folderId, StepNames = CheckSteps.Schemas, What = "Schemas", Mode = "Check" });
        var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _lifetime.ApplicationStopping);
        _ = Task.Run(async () =>
        {
            ITridionGateway? gateway = null;
            try
            {
                job.ReportStep(1, "Opening " + fileName);
                gateway = isDemo ? _demo : CoreServiceGatewayFactory.Create(_o, connection!);
                IaWorkbook wb; List<Issue> issues;
                await using (var stream = _uploads.Open(uploadId)) (wb, issues) = IaExcelReader.Read(stream);
                job.ReportStep(2, "Opening the selected folder");
                var ctx = await ImportContext.LoadAsync(gateway, folderId, wb, new InlineProgress<string>(m =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    job.ReportActivity(m);
                }));
                job.FolderTitle = ctx.Target.Title; job.PublicationTitle = ctx.Target.PublicationTitle;
                job.ReportStep(3, $"Checking {wb.Schemas.Count} schemas, {wb.Fields.Count} fields and {wb.Categories.Count} categories");
                var plan = ImportPlanner.Build(wb, issues, ctx);
                job.CheckSnapshot = new SchemaCheckSnapshot(wb, ctx, plan, uploadId, folderId);
                job.CheckDone(new DryRunViewModel
                {
                    UploadId = uploadId, FileName = fileName, FolderId = folderId, FolderTitle = ctx.Target.Title,
                    PublicationTitle = ctx.Target.PublicationTitle, Plan = plan, CheckJobId = job.Id, CheckedUtc = DateTime.UtcNow
                }, "DryRun");
                _log.LogInformation("AUDIT {Who} checked '{File}' against {Folder}: {Errors} errors", who, fileName, folderId,
                    plan.Issues.Count(i => i.Level == IssueLevel.Error));
            }
            catch (OperationCanceledException) { job.Fail("The check was cancelled. Nothing was changed."); }
            catch (Exception ex) { _log.LogWarning(ex, "Check failed"); job.Fail($"The check could not run: {ex.Message}"); }
            finally
            {
                if (gateway is IDisposable d && gateway is not InMemoryTridionGateway) d.Dispose();
                linked.Dispose();
            }
        });
        return RedirectToAction(nameof(Progress), new { id = job.Id });
    }

    /// <summary>Starts the import in the background and shows the progress page.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Execute(string uploadId, string fileName, string folderId, string? checkJobId, bool recheck = false)
    {
        // Capture everything the background task needs – scoped services are not available after this request ends.
        var isDemo = _session.IsDemo;
        var connection = isDemo ? null : _session.Connection;
        var who = _session.Who;

        // Reuse the check's snapshot when it is recent and for the same workbook and folder – no second read of the publication.
        SchemaCheckSnapshot? snapshot = null;
        if (!recheck && _o.CheckReuseMinutes > 0 && checkJobId is not null && _jobs.Get(checkJobId, Owner) is { CheckSnapshot: SchemaCheckSnapshot s } check
            && s.UploadId == uploadId && s.FolderId == folderId && check.FinishedUtc > DateTime.UtcNow.AddMinutes(-_o.CheckReuseMinutes))
        {
            snapshot = s;
            check.CheckSnapshot = null;   // a snapshot is used once
        }

        var job = _jobs.Add(new ImportJob { Owner = Owner, FileName = fileName, FolderId = folderId });
        var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _lifetime.ApplicationStopping);
        _ = Task.Run(() => RunJobAsync(job, uploadId, isDemo, connection, who, snapshot, linked.Token).ContinueWith(_ => linked.Dispose()));
        return RedirectToAction(nameof(Progress), new { id = job.Id });
    }

    private async Task RunJobAsync(ImportJob job, string uploadId, bool isDemo, CoreServiceConnection? connection, string who,
        SchemaCheckSnapshot? snapshot, CancellationToken ct)
    {
        ITridionGateway? gateway = null;
        try
        {
            job.Start();
            gateway = isDemo ? _demo : CoreServiceGatewayFactory.Create(_o, connection!);
            IaWorkbook wb; ImportContext ctx; ImportPlan plan;
            if (snapshot is not null)
            {
                job.ReportActivity("Using the check you just ran – the publication is not read again");
                (wb, ctx, plan) = (snapshot.Workbook, snapshot.Context, snapshot.Plan);
            }
            else
            {
                // No recent check (or "check again" was ticked): read the publication now.
                List<Issue> issues;
                await using (var stream = _uploads.Open(uploadId)) (wb, issues) = IaExcelReader.Read(stream);
                ctx = await ImportContext.LoadAsync(gateway, job.FolderId, wb, new InlineProgress<string>(job.ReportActivity));
                plan = ImportPlanner.Build(wb, issues, ctx);
            }
            job.FolderTitle = ctx.Target.Title; job.PublicationTitle = ctx.Target.PublicationTitle;
            if (plan.HasErrors)
            {
                job.PlanFailed(new DryRunViewModel
                {
                    UploadId = uploadId, FileName = job.FileName, FolderId = job.FolderId, FolderTitle = ctx.Target.Title,
                    PublicationTitle = ctx.Target.PublicationTitle, Plan = plan
                });
                return;
            }
            var result = await new ImportExecutor(gateway, _o.CheckInComment).ExecuteAsync(wb, plan, ctx, ct, job);
            var resultId = Guid.NewGuid().ToString("N");
            _cache.Set("result:" + resultId, IaExcelWriter.WriteResults(result.Log), TimeSpan.FromHours(2));
            job.Complete(result, resultId);
            _log.LogInformation("AUDIT {Who} imported '{File}' into {Folder}: created {Created}, skipped {Skipped}, failed {Failed}{Cancelled}",
                who, job.FileName, job.FolderId, result.Created, result.Skipped, result.Failed, result.Cancelled ? " (cancelled)" : "");
        }
        catch (OperationCanceledException)
        {
            job.Fail("The import was cancelled before any changes were made.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Import job {Job} failed", job.Id);
            job.Fail(ex.Message);
        }
        finally
        {
            if (gateway is IDisposable d && gateway is not InMemoryTridionGateway) d.Dispose();
        }
    }

    // Ownership is checked against the session id, so this keeps working if an OAuth token expires mid-import.
    [AllowAnonymousTridion]
    [HttpGet]
    public IActionResult Progress(string id)
    {
        var job = _jobs.Get(id, Owner);
        return job is null ? Fail("Import job not found or expired.") : View(job);
    }

    /// <summary>JSON polled by the progress page.</summary>
    // Ownership is checked against the session id, so this keeps working if an OAuth token expires mid-import.
    [AllowAnonymousTridion]
    [HttpGet]
    public IActionResult Status(string id)
    {
        var job = _jobs.Get(id, Owner);
        return job is null ? NotFound(new { error = "Job not found." }) : Json(job.Snapshot());
    }

    // Ownership is checked against the session id, so this keeps working if an OAuth token expires mid-import.
    [AllowAnonymousTridion]
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Cancel(string id)
    {
        var job = _jobs.Get(id, Owner);
        if (job is null) return NotFound(new { error = "Job not found." });
        job.Cancellation.Cancel();
        _log.LogInformation("AUDIT {Who} cancelled import job {Job}", _session.Who, id);
        return Json(new { ok = true });
    }

    /// <summary>Final page once the job has finished.</summary>
    // Ownership is checked against the session id, so this keeps working if an OAuth token expires mid-import.
    [AllowAnonymousTridion]
    [HttpGet]
    public IActionResult Job(string id)
    {
        var job = _jobs.Get(id, Owner);
        if (job is null) return Fail("Import job not found or expired.");
        return job.State switch
        {
            JobState.Running => RedirectToAction(nameof(Progress), new { id }),
            JobState.PlanErrors => View(job.PlanErrorsView, job.PlanErrorsModel),
            JobState.Failed => FailStep(job.What, job.IsCheck ? job.Error ?? "The check stopped." : $"Creation stopped: {job.Error}"),
            JobState.Checked => View(job.PlanErrorsView, job.PlanErrorsModel),
            _ => Completed(job)
        };
    }

    /// <summary>Records the run on the journey rail and shows the result.</summary>
    private IActionResult Completed(ImportJob job)
    {
        var run = new RunSummary
        {
            FolderPath = $"{job.PublicationTitle} / {job.FolderTitle}", Created = job.Result!.Created, Skipped = job.Result.Skipped,
            Failed = job.Result.Failed, Cancelled = job.Result.Cancelled, ResultId = job.ResultId, FinishedUtc = job.FinishedUtc ?? DateTime.UtcNow
        };
        _wizard.Update(d => { if (job.What == "Templates") d.TemplateRun = run; else d.SchemaRun = run; });
        return View("Result", new ImportResultViewModel { ResultId = job.ResultId!, FolderTitle = job.FolderTitle, Result = job.Result!, What = job.What });
    }

    // Ownership is checked against the session id, so this keeps working if an OAuth token expires mid-import.
    [AllowAnonymousTridion]
    [HttpGet]
    public IActionResult Results(string id) =>
        _cache.TryGetValue("result:" + id, out byte[]? bytes) && bytes is not null
            ? File(bytes, Xlsx, $"IA_Import_Results_{DateTime.UtcNow:yyyyMMdd_HHmm}.xlsx")
            : Fail("Results have expired.");

    private static IEnumerable<string> CategoryTitles(Modelry.Core.Model.IaWorkbook wb) =>
        wb.Categories.Select(c => c.Title).Concat(wb.Keywords.Select(k => k.CategoryTitle));

    private IActionResult Fail(string message)
    {
        TempData["Error"] = message;
        return RedirectToAction("Index", "Home");
    }

    private IActionResult FailStep(string step, string message)
    {
        TempData["Error"] = message;
        return RedirectToAction(step, "Wizard");
    }
}
