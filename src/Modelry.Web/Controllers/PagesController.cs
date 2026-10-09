using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Caching.Memory;
using Modelry.Core.CodeGen;
using Modelry.Core.Excel;
using Modelry.Core.Model;
using Modelry.Core.Pages;
using Modelry.Web.Models;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>
/// Pages step, one page at a time: upload the page's HTML zip, review which component each section becomes (with live
/// thumbnails), then download the Razor views. Creating components and the page in Tridion follows in the next release.
/// </summary>
public sealed class PagesController : Controller
{
    private const long ZipLimit = PageRunStore.MaxBytes + 10 * 1024 * 1024;
    private readonly WizardState _wizard;
    private readonly TridionSession _session;
    private readonly UploadStore _uploads;
    private readonly PageRunStore _runs;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PagesController> _log;
    private readonly GatewayAccessor _gw;
    private readonly ImportJobStore _jobs;
    private readonly Modelry.Core.Gateway.InMemoryTridionGateway _demo;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Modelry.Tridion.TridionOptions _o;

    public PagesController(WizardState wizard, TridionSession session, UploadStore uploads, PageRunStore runs, IMemoryCache cache, ILogger<PagesController> log,
        GatewayAccessor gw, ImportJobStore jobs, Modelry.Core.Gateway.InMemoryTridionGateway demo, IHostApplicationLifetime lifetime,
        Microsoft.Extensions.Options.IOptions<Modelry.Tridion.TridionOptions> o)
    {
        _wizard = wizard; _session = session; _uploads = uploads; _runs = runs; _cache = cache; _log = log;
        _gw = gw; _jobs = jobs; _demo = demo; _lifetime = lifetime; _o = o.Value;
    }

    private string Owner => HttpContext.Session.Id;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var d = _wizard.Data;
        if (!d.HasIa) return RedirectToAction("Upload", "Wizard");
        var vm = new PagesViewModel { Wizard = d, IsAem = _session.IsAem };
        if (vm.IsAem) return View(vm);
        try
        {
            var wb = await ReadWorkbookAsync(d);
            vm.Pages = wb.Pages.Select(p => (p, wb.Mapping.Count(r => IaWorkbook.Same(r.PageId, p.PageId) && SectionMatcher.ExpectsSection(wb, r)))).ToList();
            vm.CurrentRun = d.CurrentPageRunId is { } id ? _runs.Get(id, Owner) : null;
            if (vm.CurrentRun is not null) vm.CurrentPage = wb.Pages.FirstOrDefault(p => p.PageId == vm.CurrentRun.PageId);
        }
        catch (FileNotFoundException) { vm.ReadError = "The uploaded workbook is no longer available. Upload it again."; }
        catch (Exception ex) { _log.LogWarning(ex, "Pages step failed"); vm.ReadError = $"The workbook could not be read: {ex.Message}"; }
        vm.HtmlChoices = TempData["HtmlChoices"] is string hc ? hc.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : new();
        vm.SelectedPageId = TempData["PageId"] as string ?? vm.CurrentRun?.PageId;
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(ZipLimit), RequestFormLimits(MultipartBodyLengthLimit = ZipLimit)]
    public async Task<IActionResult> Upload(string? pageId, IFormFile? file, string? entry, bool allowScripts = false)
    {
        var d = _wizard.Data;
        if (!d.HasIa) return RedirectToAction("Upload", "Wizard");
        if (string.IsNullOrWhiteSpace(pageId)) return Back("Choose the page (Page ID and name) first.");
        if (file is null || file.Length == 0) return Back("Choose the page's HTML zip to upload.", pageId);
        try
        {
            var wb = await ReadWorkbookAsync(d);
            if (wb.Pages.All(p => p.PageId != pageId)) return Back($"Page {pageId} is not in the workbook's Page Inventory.");
            var run = await _runs.CreateAsync(file, Owner, pageId, entry, allowScripts);
            _wizard.Update(w => w.CurrentPageRunId = run.Id);
            _log.LogInformation("AUDIT {Who} uploaded HTML '{Zip}' for {Page} ({Files} files)", _session.Who, file.FileName, pageId, run.FileCount);
            return RedirectToAction(nameof(Review), new { id = run.Id });
        }
        catch (PageZipException ex)
        {
            if (ex.HtmlFiles.Count > 0) TempData["HtmlChoices"] = string.Join("\n", ex.HtmlFiles);
            return Back(ex.Message, pageId);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Page zip upload failed"); return Back($"The zip could not be read: {ex.Message}", pageId); }
    }

    [HttpGet]
    public async Task<IActionResult> Review(string id)
    {
        var d = _wizard.Data;
        var run = _runs.Get(id, Owner);
        if (run is null) return Back("That page upload has expired (uploads are kept for 2 hours). Upload the zip again.");
        try
        {
            var wb = await ReadWorkbookAsync(d);
            var page = wb.Pages.First(p => p.PageId == run.PageId);
            var analysis = PageAnalyzer.Analyze(wb, page, run);
            var vm = new PageReviewViewModel
            {
                Wizard = d, Run = run, Analysis = analysis,
                PreviewBase = Url.Content($"~/Pages/Preview/{run.Id}/"),
                Options = Options(wb, page),
                ViewsResult = d.PageRuns.FirstOrDefault(p => p.RunId == run.Id && p.ViewsResultId is not null)
            };
            return View(vm);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Page analysis failed");
            return Back($"The page could not be analysed: {ex.Message}");
        }
    }

    /// <summary>Saves the validation-screen choices (component per section, or exclude).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Choose(string id, Dictionary<int, string>? choice, Dictionary<int, string>? suggested, string? reset, string? next)
    {
        var run = _runs.Get(id, Owner);
        if (run is null) return Back("That page upload has expired. Upload the zip again.");
        SaveChoices(run, reset is null ? choice : null, suggested);
        _cache.Remove("contentplan:" + run.Id); // choices changed: the content check must run again
        return next == "content" ? RedirectToAction(nameof(Create), new { id }) : RedirectToAction(nameof(Review), new { id });
    }

    /// <summary>Keeps only the choices that differ from Modelry's suggestion (or were already the user's own).</summary>
    private static void SaveChoices(PageRun run, Dictionary<int, string>? choice, Dictionary<int, string>? suggested)
    {
        run.Choices.Clear();
        if (choice is null) return;
        foreach (var (i, v) in choice)
            if (!string.IsNullOrEmpty(v) && (suggested is null || !suggested.TryGetValue(i, out var s) || s != v)) run.Choices[i] = v;
    }

    /// <summary>Writes the Razor views for the matched sections, the page view and the region views, as a zip.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Views(string id, Dictionary<int, string>? choice, Dictionary<int, string>? suggested)
    {
        var d = _wizard.Data;
        var run = _runs.Get(id, Owner);
        if (run is null) return Back("That page upload has expired. Upload the zip again.");
        if (choice is not null && choice.Count > 0) SaveChoices(run, choice, suggested);
        try
        {
            var wb = await ReadWorkbookAsync(d);
            var page = wb.Pages.First(p => p.PageId == run.PageId);
            var analysis = PageAnalyzer.Analyze(wb, page, run);
            var settings = d.ModelSettings;
            var plan = ModelPlanner.Build(wb, ModelsController.Options(d, settings?.Namespace ?? ModelPlanner.SuggestNamespace(wb),
                settings is null ? ModelsController.SuggestPageMetadata(wb) : settings.PageMetadataSchema, settings?.SemanticPrefix));
            var existing = d.ViewSources.Where(kv => kv.Value != page.PageId).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var input = new ViewInput
            {
                Workbook = wb, Models = plan, Document = analysis.Document, Match = analysis.Match, Bindings = analysis.Bindings,
                SourceName = run.ZipName, ExistingViews = existing
            };
            var output = RazorViewWriter.Write(input);
            var zip = Zip(output.Files.Append(new GeneratedFile("README.md", RazorViewWriter.Readme(input, output))));
            var rid = Guid.NewGuid().ToString("N");
            var fileName = $"{page.PageId}_Views_{DateTime.UtcNow:yyyyMMdd_HHmm}.zip";
            _cache.Set("pageviews:" + rid, (zip, fileName), TimeSpan.FromHours(2));
            run.ViewsResultId = rid;
            _wizard.Update(w =>
            {
                foreach (var v in output.Written.Where(v => v.Kind is not ("Helper" or "Resources")))
                    w.ViewSources[v.Kind == "Region" ? "Region:" + v.View : v.View] = page.PageId;
                w.PageRuns.RemoveAll(p => p.PageId == page.PageId);
                w.PageRuns.Add(new PageRunSummary
                {
                    RunId = run.Id, PageId = page.PageId, PageName = page.PageName, Sections = analysis.Sections.Count,
                    Mapped = analysis.Match.Sections.Count(s => !s.Excluded), Views = output.Written.Count(v => v.Kind is not ("Helper" or "Resources")),
                    Findings = output.Issues.Count, ViewsResultId = rid, FileName = fileName, FinishedUtc = DateTime.UtcNow
                });
            });
            _log.LogInformation("AUDIT {Who} generated {Views} Razor views for {Page} from '{Zip}'", _session.Who, output.Written.Count(v => v.Kind is not ("Helper" or "Resources")), page.PageId, run.ZipName);
            return Redirect(Url.Action(nameof(Review), new { id })! + "#views");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "View generation failed");
            TempData["Error"] = $"The views could not be generated: {ex.Message}";
            return RedirectToAction(nameof(Review), new { id });
        }
    }

    [HttpGet]
    public IActionResult DownloadViews(string rid) =>
        _cache.TryGetValue("pageviews:" + rid, out (byte[] Zip, string Name) v)
            ? File(v.Zip, "application/zip", v.Name)
            : Back("The views zip has expired (kept for 2 hours). Generate the views again.");

    // ---------------------------------------------------------------- stage 2: create images, components and the page

    /// <summary>Where to create things (two folders and a Structure Group), and the check's findings once it has run.</summary>
    [HttpGet]
    public async Task<IActionResult> Create(string id)
    {
        var d = _wizard.Data;
        var run = _runs.Get(id, Owner);
        if (run is null) return Back("That page upload has expired (uploads are kept for 2 hours). Upload the zip again.");
        var wb = await ReadWorkbookAsync(d);
        var vm = new PageContentViewModel
        {
            Wizard = d, Run = run, Page = wb.Pages.First(p => p.PageId == run.PageId),
            Plan = _cache.TryGetValue("contentplan:" + run.Id, out ContentPlan? plan) ? plan : null
        };
        return View(vm);
    }

    /// <summary>The check: reads the chosen folders, Structure Group and publication, plans what to create. Changes nothing.
    /// Runs in the background with live progress (the schema step's progress page); the findings open when it is done.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckContent(string id, string? componentFolderId, string? imageFolderId, string? structureGroupId)
    {
        var d = _wizard.Data;
        var run = _runs.Get(id, Owner);
        if (run is null) return Back("That page upload has expired. Upload the zip again.");
        if (string.IsNullOrEmpty(componentFolderId) || string.IsNullOrEmpty(imageFolderId) || string.IsNullOrEmpty(structureGroupId))
        {
            TempData["Error"] = "Choose the component folder, the images folder and the Structure Group.";
            return RedirectToAction(nameof(Create), new { id });
        }
        IaWorkbook wb; IaPage page; List<(SectionMatch, SchemaBinding)> sections;
        try
        {
            wb = await ReadWorkbookAsync(d);
            page = wb.Pages.First(p => p.PageId == run.PageId);
            var analysis = PageAnalyzer.Analyze(wb, page, run);
            sections = analysis.Match.Sections.Where(s => !s.Excluded && analysis.Bindings.ContainsKey(s.Section.Index))
                .Select(s => (s, analysis.Bindings[s.Section.Index])).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TempData["Error"] = $"The page could not be read: {ex.Message}";
            return RedirectToAction(nameof(Create), new { id });
        }
        _cache.Remove("contentplan:" + run.Id);
        var isDemo = _session.IsDemo;
        var connection = isDemo ? null : _session.Connection;
        var job = _jobs.Add(new ImportJob
        {
            Owner = Owner, FileName = run.ZipName, FolderId = componentFolderId, What = "Pages", Mode = "Check",
            StepNames = ContentPlanner.CheckSteps, PageRunId = run.Id, PageId = run.PageId
        });
        var targets = new ContentTargets(componentFolderId, imageFolderId, structureGroupId);
        _ = Task.Run(async () =>
        {
            Modelry.Core.Gateway.ITridionGateway? gateway = null;
            try
            {
                job.ReportStep(1);
                gateway = isDemo ? _demo : Modelry.Tridion.CoreServiceGatewayFactory.Create(_o, connection!);
                var plan = await ContentPlanner.BuildAsync(gateway, wb, page, targets, sections, url => ResolveAsset(run, url),
                    new Modelry.Core.Import.InlineProgress<string>(msg =>
                    {
                        var k = Array.IndexOf(ContentPlanner.CheckSteps, msg);
                        if (k >= 0) job.ReportStep(k + 1); else job.ReportActivity(msg);
                    }));
                job.FolderTitle = plan.ComponentFolder.Title; job.PublicationTitle = plan.ComponentFolder.PublicationTitle;
                _cache.Set("contentplan:" + run.Id, plan, TimeSpan.FromMinutes(30));
                job.CheckDone(run.Id, "Pages");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Content check failed");
                job.Fail(ex.Message);
            }
            finally
            {
                if (gateway is IDisposable disp && gateway is not Modelry.Core.Gateway.InMemoryTridionGateway) disp.Dispose();
            }
        });
        return RedirectToAction("Progress", "Import", new { id = job.Id });
    }

    /// <summary>Creates what the check planned, in the background, with live progress (the schema step's progress page).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult CreateContent(string id)
    {
        var d = _wizard.Data;
        var run = _runs.Get(id, Owner);
        if (run is null) return Back("That page upload has expired. Upload the zip again.");
        if (!_cache.TryGetValue("contentplan:" + run.Id, out ContentPlan? plan) || plan is null)
        {
            TempData["Error"] = "The check has expired (kept for 30 minutes). Run it again.";
            return RedirectToAction(nameof(Create), new { id });
        }
        _cache.Remove("contentplan:" + run.Id); // a plan is used once: a second run re-checks and reuses what was created
        var isDemo = _session.IsDemo;
        var connection = isDemo ? null : _session.Connection;
        var who = _session.Who;
        var page = new IaPage { PageId = run.PageId, PageName = plan.Page.Title };
        var job = _jobs.Add(new ImportJob
        {
            Owner = Owner, FileName = run.ZipName, FolderId = plan.Targets.ComponentFolderId, What = "Pages", Mode = "Create",
            StepNames = ContentExecutor.StepNames, FolderTitle = plan.ComponentFolder.Title, PublicationTitle = plan.ComponentFolder.PublicationTitle,
            PageRunId = run.Id, PageId = run.PageId
        });
        var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _lifetime.ApplicationStopping);
        _ = Task.Run(async () =>
        {
            Modelry.Core.Gateway.ITridionGateway? gateway = null;
            try
            {
                job.Start();
                gateway = isDemo ? _demo : Modelry.Tridion.CoreServiceGatewayFactory.Create(_o, connection!);
                var result = await new ContentExecutor(gateway, _o.CheckInComment).ExecuteAsync(plan, page, linked.Token, job);
                var resultId = Guid.NewGuid().ToString("N");
                _cache.Set("result:" + resultId, IaExcelWriter.WriteResults(result.Log), TimeSpan.FromHours(2));
                job.Complete(result, resultId);
                _log.LogInformation("AUDIT {Who} created content for {Page}: created {Created}, reused {Skipped}, failed {Failed}",
                    who, run.PageId, result.Created, result.Skipped, result.Failed);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Page content job {Job} failed", job.Id);
                job.Fail(ex.Message);
            }
            finally
            {
                if (gateway is IDisposable disp && gateway is not Modelry.Core.Gateway.InMemoryTridionGateway) disp.Dispose();
                linked.Dispose();
            }
        });
        return RedirectToAction("Progress", "Import", new { id = job.Id });
    }

    /// <summary>A URL as written in the page HTML → the file in the extracted zip.</summary>
    private static string? ResolveAsset(PageRun run, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:") || System.Text.RegularExpressions.Regex.IsMatch(url, "^(https?:)?//")) return null;
        var path = new Uri(new Uri("http://page/" + run.EntryPath), url.Split('#')[0]).AbsolutePath.TrimStart('/');
        return PageRunStore.Resolve(run, path);
    }

    // ---------------------------------------------------------------- preview (sandboxed thumbnails)

    private static readonly FileExtensionContentTypeProvider Types = new();

    /// <summary>
    /// Serves the extracted page to the thumbnail frames. Frames are sandboxed (opaque origin, no cookies), so the random run
    /// id is the access key. The entry page is rewritten: sections are numbered, page scripts are removed unless the user
    /// allowed them, and a small script reports each section's position to the review screen.
    /// </summary>
    [AllowAnonymousTridion]
    [HttpGet("/Pages/Preview/{id}/{**path}")]
    public IActionResult Preview(string id, string? path)
    {
        var run = _runs.Get(id);
        if (run is null || string.IsNullOrEmpty(path)) return NotFound();
        var full = PageRunStore.Resolve(run, path);
        if (full is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts; frame-ancestors 'self'";
        Response.Headers["Cache-Control"] = "private, max-age=600";
        if (!Types.TryGetContentType(full, out var type)) type = "application/octet-stream";
        if (!string.Equals(path.Replace('\\', '/'), run.EntryPath, StringComparison.OrdinalIgnoreCase))
            return PhysicalFile(full, type);

        var doc = HtmlSections.Parse(System.IO.File.ReadAllText(full));
        if (run.SectionPaths.Count > 0)
            for (var i = 0; i < run.SectionPaths.Count; i++)
                HtmlSections.AtPath(doc, run.SectionPaths[i])?.SetAttribute(HtmlSections.SectionAttribute, i.ToString());
        else HtmlSections.Split(doc); // numbers the sections (data-modelry-section)
        if (!run.AllowScripts)
        {
            foreach (var s in doc.QuerySelectorAll("script").ToList()) s.Remove();
            foreach (var e in doc.QuerySelectorAll("*"))
                foreach (var a in e.Attributes.Where(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).ToList()) e.RemoveAttribute(a);
            foreach (var a in doc.QuerySelectorAll("[href]").Where(a => a.GetAttribute("href")!.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))) a.SetAttribute("href", "#");
        }
        var prefix = Url.Content($"~/Pages/Preview/{run.Id}/");
        foreach (var e in doc.QuerySelectorAll("[src],[href]"))
            foreach (var attr in new[] { "src", "href" })
                if (e.GetAttribute(attr) is { } v && v.StartsWith('/') && !v.StartsWith("//")) e.SetAttribute(attr, prefix + v.TrimStart('/'));
        var helper = doc.CreateElement("script");
        helper.TextContent = PreviewScript;
        (doc.Body ?? doc.DocumentElement).AppendChild(helper);
        var style = doc.CreateElement("style");
        style.TextContent = "html,body{scrollbar-width:none!important}::-webkit-scrollbar{display:none!important}*{animation:none!important;transition:none!important;scroll-behavior:auto!important}";
        doc.Head?.AppendChild(style);
        Response.Headers["Cache-Control"] = "no-store";
        return Content(doc.ToHtml(), "text/html; charset=utf-8");
    }

    // Measures the requested section, scrolls the frame to it and reports its size to the review screen. It scrolls again
    // whenever the frame is resized, because the review screen shrinks the frame to the section's height.
    private const string PreviewScript = """
        (function(){var n=new URLSearchParams(location.search).get('modelry-section');var top=0,h=0,left=0,w=0;
        function measure(){document.querySelectorAll('body *').forEach(function(e){var p=getComputedStyle(e).position;if(p==='fixed'||p==='sticky')e.style.setProperty('visibility','hidden','important');});
        var el=n!==null?document.querySelector('[data-modelry-section="'+n+'"]'):null;
        window.scrollTo(0,0);
        if(el){var r=el.getBoundingClientRect();top=r.top+window.pageYOffset;h=Math.max(1,r.height);left=r.left;w=r.width;}else{top=0;h=document.documentElement.scrollHeight;left=0;w=document.documentElement.clientWidth;}}
        function show(){window.scrollTo(0,top);}
        function send(){measure();show();parent.postMessage({modelry:'section',n:n,top:top,height:h,left:left,width:w},'*');}
        window.addEventListener('resize',show);
        if(document.readyState==='complete'){setTimeout(send,50);}else{window.addEventListener('load',function(){setTimeout(send,50);});}
        setTimeout(send,1500);})();
        """;

    // ---------------------------------------------------------------- helpers

    private async Task<IaWorkbook> ReadWorkbookAsync(WizardData d)
    {
        await using var stream = _uploads.Open(d.UploadId!);
        return IaExcelReader.Read(stream, schemasRequired: false).Workbook;
    }

    /// <summary>Choices for each section: this page's mapping rows, any other Component Template, or exclude.</summary>
    private static List<PageOption> Options(IaWorkbook wb, IaPage page)
    {
        var list = wb.Mapping.Where(r => IaWorkbook.Same(r.PageId, page.PageId) && SectionMatcher.ExpectsSection(wb, r))
            .Select(r => new PageOption($"map:{r.MapId}", $"{r.MapId} · {r.Region} · {SectionMatcher.StripPrefix(r.ComponentTemplate ?? r.SchemaTitle ?? "")}{(r.UiSection is null ? "" : " – " + r.UiSection)}", "This page's mapping"))
            .ToList();
        list.AddRange(wb.Templates.Where(t => t.Kind == IaTemplateKind.ComponentTemplate && t.LinkedSchemas.Count > 0)
            .OrderBy(t => t.Title).Select(t => new PageOption($"ct:{t.Title}", SectionMatcher.StripPrefix(t.Title), "Another Component Template")));
        return list;
    }

    private static byte[] Zip(IEnumerable<GeneratedFile> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var f in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(f.Path, CompressionLevel.Optimal).Open(), new UTF8Encoding(true));
                w.Write(f.Content.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            }
        return ms.ToArray();
    }

    private IActionResult Back(string message, string? pageId = null)
    {
        TempData["Error"] = message;
        if (pageId is not null) TempData["PageId"] = pageId;
        return RedirectToAction(nameof(Index));
    }
}
