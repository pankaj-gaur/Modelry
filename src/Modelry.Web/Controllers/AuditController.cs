using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Modelry.Core.Audit;
using Modelry.Core.Audit.Aem;
using Modelry.Core.Gateway;
using Modelry.Tridion;
using Modelry.Web.Models;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>
/// Audit an existing CMS: choose a publication → background scan with live progress → dashboard with health score, findings
/// and building-block counts → detail tables → item pop-up; saved audits can be reopened and compared.
/// Read-only: nothing in the CMS is changed or published.
/// </summary>
public sealed class AuditController : Controller
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly GatewayAccessor _gw;
    private readonly TridionSession _session;
    private readonly AuditJobStore _jobs;
    private readonly AuditSnapshotStore _history;
    private readonly InMemoryTridionGateway _demo;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TridionOptions _o;
    private readonly AuditOptions _audit;
    private readonly TemplateFieldOptions _fields;
    private readonly IHttpClientFactory _http;
    private readonly AuditSummaryService _summaries;
    private readonly ILogger<AuditController> _log;

    public AuditController(GatewayAccessor gw, TridionSession session, AuditJobStore jobs, AuditSnapshotStore history, InMemoryTridionGateway demo,
        IHostApplicationLifetime lifetime, IOptions<TridionOptions> o, IOptions<AuditOptions> audit, IOptions<TemplateFieldOptions> fields,
        IHttpClientFactory http, AuditSummaryService summaries, ILogger<AuditController> log)
    {
        _gw = gw; _session = session; _jobs = jobs; _history = history; _demo = demo; _lifetime = lifetime;
        _o = o.Value; _audit = audit.Value; _fields = fields.Value; _http = http; _summaries = summaries; _log = log;
    }

    private string Owner => HttpContext.Session.Id;
    /// <summary>Which CMS instance audits belong to (saved history, reuse): "demo" / "demo-aem" or the service URL.</summary>
    private string Cms => _session.IsDemo ? (_session.IsAem ? "demo-aem" : "demo") : _session.CoreServiceUrl ?? "";
    private string Noun => AuditCatalog.RootNoun(_session.AuditCms);

    /// <summary>Everything needed to open an audit gateway, captured from the session (usable after the request ends).</summary>
    private sealed record AuditTarget(bool IsDemo, bool IsAem, CoreServiceConnection? Tridion, AemConnection? Aem);

    private AuditTarget Target() => new(_session.IsDemo, _session.IsAem,
        !_session.IsDemo && !_session.IsAem ? _session.Connection : null, !_session.IsDemo && _session.IsAem ? _session.AemConnection : null);

    /// <summary>The audit gateway for a target: Tridion Core Service, AEM author over HTTP, or the matching demo.</summary>
    private IAuditGateway OpenGateway(AuditTarget t, out IDisposable? toDispose)
    {
        toDispose = null;
        if (t.IsAem) return t.IsDemo
            ? new AemAuditGateway(DemoAemHandler.CreateClient(), DemoAemHandler.Connection)
            : new AemAuditGateway(_http.CreateClient("aem-audit"), t.Aem!);
        if (t.IsDemo) return _demo;
        var gw = CoreServiceGatewayFactory.Create(_o, t.Tridion!);
        toDispose = gw as IDisposable;
        return gw as IAuditGateway ?? throw new InvalidOperationException("This connection does not support audits.");
    }

    private static bool ValidRoot(string? id, bool aem) => !string.IsNullOrWhiteSpace(id) && (aem
        ? id.StartsWith("/content/", StringComparison.Ordinal) && !id.Contains("..") && !id.Contains('?') && !id.Contains('#')
        : id.StartsWith("tcm:0-", StringComparison.Ordinal));

    /// <summary>Step 1: choose the publication to audit; saved audits of this CMS are listed below.</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var vm = new AuditIndexViewModel
        {
            Capabilities = _session.Capabilities, Recent = _jobs.ForOwner(Owner, Cms), Saved = _history.List(Cms), HistoryEnabled = _history.Enabled,
            Cms = _session.AuditCms
        };
        IDisposable? dispose = null;
        try { vm.Roots = await OpenGateway(Target(), out dispose).ListAuditRootsAsync(HttpContext.RequestAborted); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audit: {Noun}s could not be listed", Noun);
            vm.Error = $"The {Noun}s could not be listed: {ex.GetBaseException().Message}";
        }
        finally { dispose?.Dispose(); }
        return View(vm);
    }

    /// <summary>Starts the scan in the background (or reuses a recent one) and opens the dashboard.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Run(string publicationId, string? publicationTitle, bool refresh = false, bool deep = false, string? confPath = null, string? damPath = null)
    {
        var aem = _session.IsAem;
        publicationId = publicationId?.Trim().TrimEnd('/') ?? "";
        if (!ValidRoot(publicationId, aem))
        {
            TempData["Error"] = $"Choose a {Noun} to audit.";
            return RedirectToAction(nameof(Index));
        }
        confPath = string.IsNullOrWhiteSpace(confPath) ? null : confPath.Trim().TrimEnd('/');
        damPath = string.IsNullOrWhiteSpace(damPath) ? null : damPath.Trim().TrimEnd('/');
        if (aem && ((confPath is not null && (!confPath.StartsWith("/conf/", StringComparison.Ordinal) || confPath.Contains("..")))
                    || (damPath is not null && (!damPath.StartsWith("/content/dam", StringComparison.Ordinal) || damPath.Contains("..")))))
        {
            TempData["Error"] = "The configuration folder must start with /conf/ and the DAM folder with /content/dam.";
            return RedirectToAction(nameof(Index));
        }
        if (confPath is not null || damPath is not null) refresh = true;   // a different scope is a different audit
        if (!refresh && _audit.ReuseMinutes > 0 && _jobs.Recent(Owner, Cms, publicationId, TimeSpan.FromMinutes(_audit.ReuseMinutes), deep) is { } recent)
            return RedirectToAction(nameof(Dashboard), new { id = recent.Id });

        // Capture everything the background task needs – scoped services are not available after this request ends.
        var target = Target();
        var who = _session.Who;
        var host = _session.Host;
        var caps = _session.Capabilities;
        var cms = Cms;
        var options = _audit.Clone();
        options.ScopeConf = aem ? confPath : null;
        options.ScopeDam = aem ? damPath : null;
        var fields = _fields;
        var job = _jobs.Add(new AuditJob
        {
            Owner = Owner, PublicationId = publicationId, PublicationTitle = publicationTitle ?? publicationId, Cms = cms, Deep = deep, CmsKind = _session.AuditCms,
            WithSummary = _summaries.Enabled
        });
        var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _lifetime.ApplicationStopping);
        _ = Task.Run(async () =>
        {
            IDisposable? dispose = null;
            try
            {
                var audit = OpenGateway(target, out dispose);
                var snap = await audit.ScanPublicationAsync(publicationId, options, fields, caps, deep, job, linked.Token);
                snap.CmsHost = host; snap.RunBy = who;
                AuditFinalizer.Finalize(snap, options);
                snap.SavedId = _history.Save(cms, snap);
                if (_history.Enabled && snap.SavedId is null) snap.Note("This audit could not be saved for later comparison – see the log (Audit:SnapshotFolder).");
                if (job.WithSummary)
                {
                    // Last step: the AI executive summary (1.5–3 minutes). It never fails the audit – a failed or skipped
                    // summary is shown on the dashboard with "Try again". Stop skips it.
                    job.Report(new AuditProgress(job.StepNames.Length, "Asking the AI service – usually 2–3 minutes. Stop skips the summary and opens the dashboard."));
                    var previous = _history.List(cms, publicationId)
                        .Where(m => m.Id != snap.SavedId && m.FinishedUtc < snap.FinishedUtc).OrderByDescending(m => m.FinishedUtc).FirstOrDefault();
                    await _summaries.RunAsync(job.Id, cms, snap,
                        previous is null ? null : new AuditSummaryPrompt.Previous(previous.FinishedUtc, previous.Score, previous.Findings), who, linked.Token);
                    if (_lifetime.ApplicationStopping.IsCancellationRequested) throw new OperationCanceledException();
                }
                job.Complete(snap);
                _jobs.Supersede(job);
                _log.LogInformation("AUDIT {Who} audited {Publication} ({Id}) on {Version}{Deep}: score {Score}, {Counts} in {Seconds:0}s",
                    who, snap.PublicationTitle, publicationId, caps.Display, deep ? " (deep)" : "", snap.Health?.Overall,
                    string.Join(", ", snap.Items.Select(kv => $"{kv.Key} {kv.Value.Count}")), (snap.FinishedUtc - snap.StartedUtc).TotalSeconds);
            }
            catch (OperationCanceledException) { job.Cancel(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Audit of {Publication} failed", publicationId);
                job.Fail(ex.GetBaseException().Message);
            }
            finally
            {
                dispose?.Dispose();
                linked.Dispose();
            }
        });
        return RedirectToAction(nameof(Dashboard), new { id = job.Id });
    }

    /// <summary>Reopens a saved audit as a dashboard (findings are re-checked with the current rules).</summary>
    [HttpGet]
    public IActionResult Open(string publicationId, string savedId)
    {
        AuditSnapshot? snap = null;
        try { snap = _history.Load(Cms, publicationId, savedId); }
        catch (Exception ex) { _log.LogWarning(ex, "Saved audit {Id} could not be read", savedId); }
        if (snap is null)
        {
            TempData["Error"] = "That saved audit could not be found.";
            return RedirectToAction(nameof(Index));
        }
        AuditRules.Evaluate(snap, _audit);
        var job = _jobs.Add(new AuditJob
        {
            Owner = Owner, PublicationId = snap.PublicationId, PublicationTitle = snap.PublicationTitle, Cms = Cms, Deep = snap.Deep, FromHistory = true, CmsKind = snap.Cms
        });
        job.Complete(snap);
        return RedirectToAction(nameof(Dashboard), new { id = job.Id });
    }

    /// <summary>Progress while scanning; the dashboard once finished.</summary>
    [HttpGet]
    public IActionResult Dashboard(string id)
    {
        var job = _jobs.Get(id, Owner);
        if (job is null)
        {
            TempData["Error"] = "That audit is no longer open (audits stay open for two hours). Open it from the saved audits, or run it again.";
            return RedirectToAction(nameof(Index));
        }
        IReadOnlyList<SnapshotMeta> saved = job.Snapshot is null ? Array.Empty<SnapshotMeta>()
            : _history.List(Cms, job.PublicationId).Where(m => m.Id != job.Snapshot.SavedId).ToList();
        return View(new AuditDashboardViewModel { Job = job, PageSize = _audit.PageSize, Saved = saved, AiSummaryEnabled = _summaries.Enabled });
    }

    /// <summary>This audit against a saved one of the same publication: counts, added / removed items, findings, score, trend.</summary>
    [HttpGet]
    public IActionResult Compare(string id, string with)
    {
        if (_jobs.Get(id, Owner)?.Snapshot is not { } current) return RedirectToAction(nameof(Index));
        AuditSnapshot? other = null;
        try { other = _history.Load(Cms, current.PublicationId, with); }
        catch (Exception ex) { _log.LogWarning(ex, "Saved audit {Id} could not be read", with); }
        if (other is null)
        {
            TempData["Error"] = "That saved audit could not be found.";
            return RedirectToAction(nameof(Dashboard), new { id });
        }
        // Compare a copy of the current audit so re-evaluating rules does not touch the open dashboard.
        var after = CopyOf(current);
        var (before, later) = other.FinishedUtc <= after.FinishedUtc ? (other, after) : (after, other);
        var comparison = AuditComparison.Compare(before, later, _audit);
        return View(new AuditCompareViewModel
        {
            JobId = id, Comparison = comparison, CurrentIsLater = ReferenceEquals(later, after),
            Trend = _history.List(Cms, current.PublicationId).OrderBy(m => m.FinishedUtc).ToList()
        });
    }

    private static AuditSnapshot CopyOf(AuditSnapshot s)
    {
        using var ms = new MemoryStream();
        AuditSnapshotSerializer.Write(s, ms);
        ms.Position = 0;
        var copy = AuditSnapshotSerializer.Read(ms);
        copy.SavedId = s.SavedId;
        return copy;
    }

    [HttpGet("api/audit/{id}/status")]
    public IActionResult Status(string id) => _jobs.Get(id, Owner) is { } job ? Json(job.Status()) : NotFound(new { error = "Audit not found." });

    [HttpPost("api/audit/{id}/cancel"), ValidateAntiForgeryToken]
    public IActionResult Cancel(string id)
    {
        if (_jobs.Get(id, Owner) is not { } job) return NotFound();
        job.Cancellation.Cancel();
        return Ok();
    }

    /// <summary>Rows per page offered in the tables.</summary>
    public static readonly int[] RowsPerPageChoices = { 20, 50, 100 };

    /// <summary>The requested rows per page when it is one of the choices, otherwise Audit:PageSize (20 by default).</summary>
    private int RowsPerPage(int requested) =>
        RowsPerPageChoices.Contains(requested) ? requested : RowsPerPageChoices.Contains(_audit.PageSize) ? _audit.PageSize : RowsPerPageChoices[0];

    /// <summary>One page of a detail table, filtered and sorted on the server.</summary>
    [HttpGet("api/audit/{id}/rows")]
    public IActionResult Rows(string id, AuditKind kind, string? q, string? origin, string? publish, string? finding, string? sort, bool desc = false, int page = 1, int pageSize = 0)
    {
        pageSize = RowsPerPage(pageSize);
        if (_jobs.Get(id, Owner)?.Snapshot is not { } snap) return NotFound(new { error = "Audit not found." });
        var byItem = AuditRules.ByItem(snap, kind);
        var query = new AuditQuery
        {
            Search = q, Origin = origin, Publish = publish, Sort = sort, Descending = desc, Page = page, PageSize = pageSize,
            OnlyIds = string.IsNullOrWhiteSpace(finding) ? null
                : finding == "any" ? byItem.Keys.ToHashSet()
                : snap.Findings.Where(f => f.RuleId == finding && f.Kind == kind).Select(f => f.ItemId).ToHashSet()
        };
        var (rows, matching) = query.Apply(snap.Items[kind], kind);
        return Json(new
        {
            total = snap.Items[kind].Count, matching, page = Math.Max(1, page), pageSize,
            rows = rows.Select(r => new
            {
                id = r.Id, title = r.Title, path = r.Path, origin = AuditCatalog.Origin(r.Origin, snap.Cms), originKey = r.Origin.ToString(), from = r.FromPublication,
                created = r.Created?.ToString("yyyy-MM-dd HH:mm"), createdBy = r.CreatedBy,
                modified = r.Modified?.ToString("yyyy-MM-dd HH:mm"), modifiedBy = r.ModifiedBy,
                publish = r.Publish.ToString(),
                targets = r.PublishedTo.Select(t => new { target = t.Target, at = t.PublishedAt?.ToString("yyyy-MM-dd HH:mm") }),
                values = r.Values.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value),
                findings = byItem.GetValueOrDefault(r.Id) ?? new List<string>()
            })
        });
    }

    /// <summary>Items one rule flagged, paged.</summary>
    [HttpGet("api/audit/{id}/findings")]
    public IActionResult Findings(string id, string rule, int page = 1, int pageSize = 0)
    {
        if (_jobs.Get(id, Owner)?.Snapshot is not { } snap) return NotFound(new { error = "Audit not found." });
        var all = snap.Findings.Where(f => f.RuleId == rule).OrderBy(f => f.Kind).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var size = RowsPerPage(pageSize);
        page = Math.Max(1, page);
        return Json(new
        {
            total = all.Count, page, pageSize = size,
            rows = all.Skip((page - 1) * size).Take(size).Select(f => new
            {
                id = f.ItemId, kind = f.Kind.ToString(), kindTitle = AuditCatalog.Singular(f.Kind, snap.Cms), title = f.Title, path = f.Path, message = f.Message
            })
        });
    }

    /// <summary>Full detail of one item, read live from the CMS (only items that are part of this audit).</summary>
    [HttpGet("api/audit/{id}/item")]
    public async Task<IActionResult> Item(string id, AuditKind kind, string itemId, CancellationToken ct)
    {
        if (_jobs.Get(id, Owner)?.Snapshot is not { } snap) return NotFound(new { error = "Audit not found." });
        if (!snap.Items[kind].Any(r => r.Id == itemId)) return NotFound(new { error = "That item is not part of this audit." });
        try
        {
            IDisposable? dispose = null;
            AuditItemDetail d;
            try { d = await OpenGateway(Target(), out dispose).ReadAuditDetailAsync(itemId, kind, _fields, ct); }
            finally { dispose?.Dispose(); }
            var findings = snap.Findings.Where(f => f.ItemId == itemId && f.Kind == kind).ToList();
            if (findings.Count > 0)
            {
                var sec = new DetailSection { Title = "Findings" };
                sec.Items.AddRange(findings.Select(f => $"{snap.Rules.FirstOrDefault(r => r.Id == f.RuleId)?.Title ?? f.RuleId}: {f.Message}"));
                d.Sections.Insert(0, sec);
            }
            return Json(new
            {
                id = d.Id, title = d.Title, kind = AuditCatalog.Singular(kind, snap.Cms),
                sections = d.Sections.Select(s => new { title = s.Title, rows = s.Rows.Select(r => new { label = r.Key, value = r.Value }), items = s.Items, xml = s.Xml })
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audit detail failed for {Item}", itemId);
            return StatusCode(502, new { error = $"The item could not be read: {ex.GetBaseException().Message}" });
        }
    }

    /// <summary>The AI executive summary of an open audit: none yet, being written, finished or failed.</summary>
    [HttpGet("api/audit/{id}/summary")]
    public IActionResult Summary(string id)
    {
        if (_jobs.Get(id, Owner)?.Snapshot is not { } snap) return NotFound(new { error = "Audit not found." });
        return Json(_summaries.Get(id, Cms, snap)?.Status() ?? new { state = "none" });
    }

    /// <summary>
    /// Starts the AI executive summary (1.5–3 minutes, in the background). A finished summary is returned as it is unless
    /// <paramref name="regenerate"/> is set – every call is paid and rate limited.
    /// </summary>
    [HttpPost("api/audit/{id}/summary"), ValidateAntiForgeryToken]
    public IActionResult StartSummary(string id, bool regenerate = false)
    {
        if (_jobs.Get(id, Owner)?.Snapshot is not { } snap) return NotFound(new { error = "Audit not found." });
        if (!_summaries.Enabled) return StatusCode(503, new { error = "The AI summary is switched off (Orchestrator:Enabled)." });
        if (_summaries.Get(id, Cms, snap) is { } existing && (existing.State == AuditSummaryState.Running || (existing.State == AuditSummaryState.Completed && !regenerate)))
            return Json(existing.Status());
        var previous = _history.List(Cms, snap.PublicationId)
            .Where(m => m.Id != snap.SavedId && m.FinishedUtc < snap.FinishedUtc).OrderByDescending(m => m.FinishedUtc).FirstOrDefault();
        var job = _summaries.Start(id, Cms, snap, previous is null ? null : new AuditSummaryPrompt.Previous(previous.FinishedUtc, previous.Score, previous.Findings), _session.Who);
        return Json(job.Status());
    }

    /// <summary>
    /// The audit as a printable report – the browser saves it as PDF ("Export to PDF"). Holds the AI executive summary,
    /// health score, building blocks, every check and up to 25 flagged items per check.
    /// </summary>
    [HttpGet]
    public IActionResult Report(string id, bool print = false)
    {
        if (_jobs.Get(id, Owner)?.Snapshot is not { } snap)
        {
            TempData["Error"] = "That audit is no longer open. Open it from the saved audits to export it.";
            return RedirectToAction(nameof(Index));
        }
        var summary = _summaries.Get(id, Cms, snap) is { State: AuditSummaryState.Completed, Result: { } r } ? r : null;
        _log.LogInformation("AUDIT {Who} opened the PDF report of {Publication}", _session.Who, snap.PublicationTitle);
        return View(new AuditReportViewModel { JobId = id, Snapshot = snap, Summary = summary, Print = print });
    }

    [HttpGet]
    public IActionResult Export(string id)
    {
        if (_jobs.Get(id, Owner)?.Snapshot is not { } snap)
        {
            TempData["Error"] = "That audit is no longer open. Open it from the saved audits to export it.";
            return RedirectToAction(nameof(Index));
        }
        _log.LogInformation("AUDIT {Who} exported the audit of {Publication}", _session.Who, snap.PublicationTitle);
        var summary = _summaries.Get(id, Cms, snap) is { State: AuditSummaryState.Completed, Result: { } r } ? r : null;
        return File(AuditExcelWriter.Write(snap, summary), Xlsx, AuditExcelWriter.FileName(snap));
    }
}
