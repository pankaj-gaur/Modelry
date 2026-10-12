using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Modelry.Core.Gateway;
using static Modelry.Core.Audit.Aem.AemJsonClient;

namespace Modelry.Core.Audit.Aem;

/// <summary>
/// Audit of an AEM site (AEM 6.5 and AEM as a Cloud Service) over the author instance's read APIs: QueryBuilder
/// (/bin/querybuilder.json) for lists and the Sling GET servlet (.json) for single nodes. Read-only.
///
/// Mapping to the audit's building blocks: Content Fragment Models (/conf/…/settings/dam/cfm/models), Components (/apps),
/// editable Templates (/conf/…/settings/wcm/templates), Content and Experience Fragments, Assets (the site's DAM folder),
/// Tag namespaces and Tags (/content/cq:tags), Site sections (pages that have child pages) and Pages.
/// Publish status comes from the replication properties (cq:lastReplicationAction[_publish|_preview]); MSM status from the
/// jcr:content mixins (cq:LiveRelationship = live copy, cq:LiveSyncCancelled = inheritance cancelled).
/// Lines marked "VERIFY" are API details to confirm on the first run against a real instance.
/// </summary>
public sealed partial class AemAuditGateway : IAuditGateway
{
    private readonly AemJsonClient _aem;
    private static readonly string[] SystemApps = { "cq", "dam", "wcm", "granite", "sling", "settings", "system", "core", "msm", "commerce",
                                                    "fd", "screens", "social", "forms", "cloudconfigs", "foundation", "experience-fragments" };

    public AemAuditGateway(HttpClient http, AemConnection connection) => _aem = new AemJsonClient(http, connection);

    [GeneratedRegex(@"^[a-z]{2}([_-][a-zA-Z]{2})?$")]
    private static partial Regex LanguageName();

    private static string NameOf(string path) => path.TrimEnd('/').Split('/').Last();
    private static string ParentOf(string path) { var i = path.TrimEnd('/').LastIndexOf('/'); return i <= 0 ? "/" : path[..i]; }

    /// <summary>Path below <paramref name="root"/> ('/'-separated, "" for the root itself or anything outside it).</summary>
    private static string Rel(string path, string root) =>
        path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..] : "";

    // ------------------------------------------------------------------ roots

    /// <summary>Site roots: pages directly under /content (dam, tags, experience fragments and other system folders are not pages).</summary>
    public async Task<IReadOnlyList<AuditRoot>> ListAuditRootsAsync(CancellationToken ct)
    {
        var content = await _aem.GetAsync("/content.2.json", ct);
        var roots = new List<AuditRoot>();
        foreach (var (name, node) in Children(content))
        {
            if (Str(node, "jcr:primaryType") != "cq:Page") continue;
            var title = Str(Child(node, "jcr:content"), "jcr:title") ?? name;
            roots.Add(new AuditRoot("/content/" + name, title, "/content/" + name));
        }
        return roots.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ scan

    public async Task<AuditSnapshot> ScanPublicationAsync(string rootPath, AuditOptions options, TemplateFieldOptions fields,
        CmsCapabilities capabilities, bool deep, IProgress<AuditProgress> progress, CancellationToken ct)
    {
        rootPath = rootPath.TrimEnd('/');
        var snap = new AuditSnapshot { PublicationId = rootPath, Cms = AuditCms.Aem, Capabilities = capabilities, StartedUtc = DateTime.UtcNow, Deep = deep };
        var site = NameOf(rootPath);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelCalls), CancellationToken = ct };

        // 1 ---------------------------------------------------------------- the site
        progress.Report(new AuditProgress(1, "Reading " + rootPath));
        var rootContent = await _aem.TryGetAsync(EncodePath(rootPath + "/jcr:content") + ".json", ct)
                          ?? throw new InvalidOperationException($"{rootPath} is not a page on this AEM instance.");
        snap.PublicationTitle = Str(rootContent, "jcr:title") ?? site;
        var conf = (options.ScopeConf ?? Str(rootContent, "cq:conf") ?? "/conf/" + site).TrimEnd('/');
        var dam = (options.ScopeDam ?? "/content/dam/" + site).TrimEnd('/');
        var xf = "/content/experience-fragments/" + site;
        string? blueprint = null;
        try { blueprint = Str(await _aem.TryGetAsync(EncodePath(rootPath + "/jcr:content/cq:LiveSyncConfig") + ".json", ct), "cq:master"); }   // VERIFY: live copy root config
        catch (Exception ex) when (ex is not OperationCanceledException) { /* not a live copy */ }
        snap.Note($"Scope: pages under {rootPath}, configuration {conf}, assets and Content Fragments under {dam}, Experience Fragments under {xf}." +
                  (blueprint is not null ? $" This site is a live copy of {blueprint}." : ""));

        // 2 ---------------------------------------------------------------- pages and sections
        progress.Report(new AuditProgress(2, "Listing pages"));
        var pages = new List<AuditRow>();
        var pageCount = 0;
        await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string> { ["path"] = rootPath, ["path.self"] = "true", ["type"] = "cq:Page" }, 1, ct))
        {
            var path = Str(hit, "jcr:path") ?? "";
            if (path.Length == 0) continue;
            if (++pageCount % 200 == 0) progress.Report(new AuditProgress(2, $"{pageCount} pages"));
            var jc = Child(hit, "jcr:content");
            var row = new AuditRow
            {
                Id = path, Title = Str(jc, "jcr:title") ?? Str(jc, "pageTitle") ?? NameOf(path), Kind = AuditKind.Pages,
                Path = path == rootPath ? "" : Rel(ParentOf(path), rootPath),
                Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(jc, "cq:lastModified") ?? Date(jc, "jcr:lastModified"), ModifiedBy = Str(jc, "cq:lastModifiedBy") ?? Str(jc, "jcr:lastModifiedBy")
            };
            row.Values["url"] = path;
            row.Values["fileName"] = NameOf(path);
            row.Values["_pageTemplateId"] = Str(jc, "cq:template") ?? "";
            row.Values["_sgId"] = path == rootPath ? "" : ParentOf(path);
            ApplyMsm(row, jc, blueprint);
            ApplyReplication(row, jc, snap);
            pages.Add(row);
        }
        snap.Items[AuditKind.Pages].AddRange(pages);
        // Sections: pages that have child pages (AEM has no separate folder layer for pages).
        var parents = pages.Select(p => p.Value("_sgId")).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var p in pages.Where(p => parents.Contains(p.Id)))
        {
            var section = new AuditRow
            {
                Id = p.Id, Title = p.Title, Kind = AuditKind.StructureGroups, Path = Rel(p.Id, rootPath), Origin = p.Origin,
                FromPublication = p.FromPublication, Created = p.Created, CreatedBy = p.CreatedBy, Modified = p.Modified, ModifiedBy = p.ModifiedBy
            };
            section.Values["url"] = p.Id;
            section.Values["language"] = LanguageName().IsMatch(NameOf(p.Id)) ? "Yes" : "";
            if (p.Id != rootPath) section.Values["_parentId"] = ParentOf(p.Id);
            snap.Items[AuditKind.StructureGroups].Add(section);
        }

        // 3 ---------------------------------------------------------------- templates and Content Fragment Models
        progress.Report(new AuditProgress(3, "Reading templates in " + conf));
        var templateTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Optional(snap, "Templates", conf + "/settings/wcm/templates", async () =>
        {
            await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string> { ["path"] = conf + "/settings/wcm/templates", ["type"] = "cq:Template" }, 1, ct))
            {
                var path = Str(hit, "jcr:path") ?? "";
                var jc = Child(hit, "jcr:content");
                var row = new AuditRow
                {
                    Id = path, Title = Str(jc, "jcr:title") ?? NameOf(path), Kind = AuditKind.PageTemplates, Path = Rel(ParentOf(path), conf + "/settings/wcm/templates"),
                    Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(jc, "cq:lastModified") ?? Date(jc, "jcr:lastModified"), ModifiedBy = Str(jc, "cq:lastModifiedBy"), Origin = BluePrintOrigin.Local
                };
                row.Values["status"] = Str(jc, "status") ?? "";                                              // VERIFY: editable template status (enabled / disabled / draft)
                templateTitles[path] = row.Title;
                snap.Items[AuditKind.PageTemplates].Add(row);
            }
        });
        foreach (var p in pages)
        {
            var t = p.Value("_pageTemplateId");
            p.Values["pageTemplate"] = t.Length == 0 ? "" : templateTitles.TryGetValue(t, out var title) ? title : NameOf(t);
        }

        var modelTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Optional(snap, "Content Fragment Models", conf + "/settings/dam/cfm/models", async () =>
        {
            await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string> { ["path"] = conf + "/settings/dam/cfm/models", ["type"] = "cq:Template" }, 1, ct))
            {
                var path = Str(hit, "jcr:path") ?? "";
                var jc = Child(hit, "jcr:content");
                var row = new AuditRow
                {
                    Id = path, Title = Str(jc, "jcr:title") ?? NameOf(path), Kind = AuditKind.Schemas, Path = Rel(ParentOf(path), conf + "/settings/dam/cfm/models"),
                    Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(jc, "jcr:lastModified") ?? Date(hit, "jcr:created"), ModifiedBy = Str(jc, "jcr:lastModifiedBy"), Origin = BluePrintOrigin.Local
                };
                row.Values["status"] = Str(jc, "status") ?? "enabled";                                       // VERIFY: CF model status property
                row.Values["purpose"] = "Component";
                modelTitles[path] = row.Title;
                snap.Items[AuditKind.Schemas].Add(row);
            }
        });

        // 4 ---------------------------------------------------------------- components (/apps)
        var appsRoot = (options.AemComponentsRoot ?? "/apps").TrimEnd('/');
        progress.Report(new AuditProgress(4, "Reading components in " + appsRoot));
        await Optional(snap, "Components", appsRoot, async () =>
        {
            await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string> { ["path"] = appsRoot, ["type"] = "cq:Component" }, 0, ct))
            {
                var path = Str(hit, "jcr:path") ?? "";
                if (appsRoot == "/apps" && SystemApps.Contains(Rel(path, "/apps").Split('/')[0])) continue;
                var rt = path.StartsWith("/apps/", StringComparison.Ordinal) ? path[6..] : path;
                var row = new AuditRow
                {
                    Id = path, Title = Str(hit, "jcr:title") ?? NameOf(path), Kind = AuditKind.ComponentTemplates, Path = Rel(ParentOf(path), "/apps"),
                    Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(hit, "jcr:lastModified") ?? Date(hit, "jcr:created"), Origin = BluePrintOrigin.Local
                };
                row.Values["resourceType"] = rt;
                row.Values["group"] = Str(hit, "componentGroup") ?? "";
                row.Values["superType"] = Str(hit, "sling:resourceSuperType") ?? "";
                snap.Items[AuditKind.ComponentTemplates].Add(row);
            }
        });

        // 5 ---------------------------------------------------------------- Content and Experience Fragments
        progress.Report(new AuditProgress(5, "Reading Content Fragments in " + dam));
        await Optional(snap, "Content Fragments", dam, async () =>
        {
            await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string>
                { ["path"] = dam, ["type"] = "dam:Asset", ["property"] = "jcr:content/contentFragment", ["property.value"] = "true" }, 2, ct))
            {
                var path = Str(hit, "jcr:path") ?? "";
                var jc = Child(hit, "jcr:content");
                var model = Str(Child(jc, "data"), "cq:model") ?? "";                                       // VERIFY: jcr:content/data/cq:model
                var row = new AuditRow
                {
                    Id = path, Title = Str(jc, "jcr:title") ?? NameOf(path), Kind = AuditKind.Components, Path = Rel(ParentOf(path), dam),
                    Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(jc, "jcr:lastModified"), ModifiedBy = Str(jc, "jcr:lastModifiedBy")
                };
                row.Values["type"] = "Content Fragment";
                row.Values["schema"] = model.Length == 0 ? "" : modelTitles.TryGetValue(model, out var mt) ? mt : NameOf(model);
                row.Values["_schemaId"] = model;
                ApplyMsm(row, jc, blueprint);
                ApplyReplication(row, jc, snap);
                snap.Items[AuditKind.Components].Add(row);
            }
        });
        progress.Report(new AuditProgress(5, "Reading Experience Fragments in " + xf));
        await Optional(snap, "Experience Fragments", xf, async () =>
        {
            await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string>
                { ["path"] = xf, ["type"] = "cq:Page", ["property"] = "jcr:content/sling:resourceType", ["property.value"] = "cq/experience-fragments/components/experiencefragment" }, 1, ct))
            {
                var path = Str(hit, "jcr:path") ?? "";
                var jc = Child(hit, "jcr:content");
                var row = new AuditRow
                {
                    Id = path, Title = Str(jc, "jcr:title") ?? NameOf(path), Kind = AuditKind.Components, Path = Rel(ParentOf(path), xf),
                    Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(jc, "cq:lastModified") ?? Date(jc, "jcr:lastModified"), ModifiedBy = Str(jc, "cq:lastModifiedBy")
                };
                row.Values["type"] = "Experience Fragment";
                ApplyMsm(row, jc, blueprint);
                ApplyReplication(row, jc, snap);
                snap.Items[AuditKind.Components].Add(row);
            }
        }, quiet: true);

        // 6 ---------------------------------------------------------------- assets
        progress.Report(new AuditProgress(6, "Reading assets in " + dam));
        var assetTags = new Dictionary<string, List<string>>();
        await Optional(snap, "Assets", dam, async () =>
        {
            var n = 0;
            await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string> { ["path"] = dam, ["type"] = "dam:Asset" }, 2, ct))
            {
                var path = Str(hit, "jcr:path") ?? "";
                var jc = Child(hit, "jcr:content");
                if (Str(jc, "contentFragment") == "true") continue;
                if (++n % 200 == 0) progress.Report(new AuditProgress(6, $"{n} assets"));
                var meta = Child(jc, "metadata");
                var row = new AuditRow
                {
                    Id = path, Title = NameOf(path), Kind = AuditKind.Multimedia, Path = Rel(ParentOf(path), dam),
                    Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(jc, "jcr:lastModified"), ModifiedBy = Str(jc, "jcr:lastModifiedBy")
                };
                row.Values["fileName"] = NameOf(path);
                row.Values["mmType"] = Str(meta, "dc:format") ?? "";
                if (long.TryParse(Str(meta, "dam:size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                    row.Values["sizeKb"] = ((size + 1023) / 1024).ToString(CultureInfo.InvariantCulture);
                row.Values["assetTitle"] = Str(meta, "dc:title") ?? "";
                row.Values["_assetDescription"] = Str(meta, "dc:description") ?? "";
                row.Values["assetState"] = Str(jc, "dam:assetState") ?? "";                                   // VERIFY: processed / processing / failed
                var tags = Strs(meta, "cq:tags");
                if (tags.Count > 0) assetTags[path] = tags;
                ApplyMsm(row, jc, blueprint);
                ApplyReplication(row, jc, snap);
                snap.Items[AuditKind.Multimedia].Add(row);
            }
        });

        // 7 ---------------------------------------------------------------- tags
        progress.Report(new AuditProgress(7, "Reading tags"));
        var tagIdToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Optional(snap, "Tags", "/content/cq:tags", async () =>
        {
            var namespaces = new Dictionary<string, AuditRow>();
            var tagRows = new List<(AuditRow Row, string Ns)>();
            await foreach (var hit in _aem.QueryAsync(new Dictionary<string, string> { ["path"] = "/content/cq:tags", ["type"] = "cq:Tag" }, 0, ct))
            {
                var path = Str(hit, "jcr:path") ?? "";
                var rel = Rel(path, "/content/cq:tags");
                if (rel.Length == 0) continue;
                var parts = rel.Split('/');
                var title = Str(hit, "jcr:title") ?? parts[^1];
                if (parts.Length == 1)
                {
                    var ns = new AuditRow { Id = path, Title = title, Kind = AuditKind.Categories, Path = "", Origin = BluePrintOrigin.Local, Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(hit, "jcr:lastModified") ?? Date(hit, "jcr:created") };
                    ns.Values["xmlName"] = parts[0];
                    namespaces[parts[0]] = ns;
                    continue;
                }
                var tagId = parts[0] + ":" + string.Join("/", parts.Skip(1));
                var row = new AuditRow
                {
                    Id = path, Title = title, Kind = AuditKind.Keywords, Path = string.Join("/", parts.Skip(1).Take(parts.Length - 2)),
                    Origin = BluePrintOrigin.Local, Created = Date(hit, "jcr:created"), CreatedBy = Str(hit, "jcr:createdBy"), Modified = Date(hit, "jcr:lastModified") ?? Date(hit, "jcr:created")
                };
                row.Values["key"] = tagId;
                row.Values["_categoryId"] = "/content/cq:tags/" + parts[0];
                tagIdToPath[tagId] = path;
                tagRows.Add((row, parts[0]));
            }
            snap.Items[AuditKind.Categories].AddRange(namespaces.Values);
            foreach (var (row, ns) in tagRows)
            {
                row.Values["category"] = namespaces.TryGetValue(ns, out var nsRow) ? nsRow.Title : ns;
                snap.Items[AuditKind.Keywords].Add(row);
            }
        });

        // 8 ---------------------------------------------------------------- page content: components, references, links
        await CrawlPagesAsync(snap, rootPath, options, parallel, progress);
        if (deep) await CrawlUsageAsync(snap, options, parallel, progress, assetTags, tagIdToPath);

        snap.Note($"{snap.PublishTargets.Count} publish tier(s) seen: {string.Join(", ", snap.PublishTargets)}.");
        return snap;
    }

    /// <summary>Runs one optional part of the scan; a missing folder (404) or a refused query is noted, not fatal.</summary>
    private static async Task Optional(AuditSnapshot snap, string what, string where, Func<Task> run, bool quiet = false)
    {
        try { await run(); }
        catch (OperationCanceledException) { throw; }
        catch (AemHttpException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        { if (!quiet) snap.Note($"{what}: {where} was not found."); }
        catch (Exception ex) { snap.Note($"{what} could not be read from {where}: {ex.GetBaseException().Message}"); }
    }

    /// <summary>MSM: cq:LiveRelationship = live copy (inherits), cq:LiveSyncCancelled = inheritance cancelled.</summary>
    private static void ApplyMsm(AuditRow row, JsonElement? jc, string? blueprint)
    {
        var mixins = Strs(jc, "jcr:mixinTypes");
        row.Origin = mixins.Contains("cq:LiveSyncCancelled") ? BluePrintOrigin.Localized
            : mixins.Contains("cq:LiveRelationship") ? BluePrintOrigin.Shared : BluePrintOrigin.Local;
        if (row.Origin != BluePrintOrigin.Local) row.FromPublication = blueprint;
    }

    /// <summary>
    /// Replication status per tier. AEM as a Cloud Service writes cq:lastReplicationAction_publish / _preview (and keeps the
    /// unsuffixed one for Publish); AEM 6.5 writes only cq:lastReplicationAction. "Activate" = on that tier.
    /// </summary>
    private static void ApplyReplication(AuditRow row, JsonElement? jc, AuditSnapshot snap)
    {
        if (jc is not { ValueKind: JsonValueKind.Object } o) { row.Publish = PublishState.Unpublished; return; }
        var tiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // suffix → tier name
        foreach (var p in o.EnumerateObject())
            if (p.Name.StartsWith("cq:lastReplicationAction", StringComparison.Ordinal))
            {
                var suffix = p.Name["cq:lastReplicationAction".Length..];
                tiers[suffix] = suffix.Length == 0 ? "Publish" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(suffix.TrimStart('_'));
            }
        if (tiers.ContainsKey("") && tiers.ContainsKey("_publish")) tiers.Remove("");                // AEMaaCS: unsuffixed duplicates _publish
        foreach (var (suffix, tier) in tiers)
        {
            var action = Str(jc, "cq:lastReplicationAction" + suffix);
            if (suffix is "" or "_publish") row.Values["_lastAction"] = action ?? "";
            if (!string.Equals(action, "Activate", StringComparison.OrdinalIgnoreCase)) continue;
            row.PublishedTo.Add(new PublishTarget(tier, Date(jc, "cq:lastReplicated" + suffix), Str(jc, "cq:lastReplicatedBy" + suffix)));
            lock (snap.PublishTargets) snap.PublishTargets.Add(tier);
        }
        row.Publish = row.PublishedTo.Any(t => t.Target == "Publish") ? PublishState.Published
            : row.PublishedTo.Count > 0 ? PublishState.Published : PublishState.Unpublished;
    }

    // ------------------------------------------------------------------ page crawl

    /// <summary>
    /// Reads each page's content once: the components on it (resource types), the fragments, assets, tags and pages it refers to.
    /// Gives the component count per page, which components and templates are used, fragments on pages, and links between pages.
    /// </summary>
    private async Task CrawlPagesAsync(AuditSnapshot snap, string rootPath, AuditOptions options, ParallelOptions parallel, IProgress<AuditProgress> progress)
    {
        var pages = snap.Items[AuditKind.Pages];
        if (pages.Count > options.MaxPageReads)
        {
            var why = $"More than {options.MaxPageReads} pages – read limit (Audit:MaxPageReads). Open a page to see it.";
            snap.MarkUnavailable("Pages.cps", why);
            return;
        }
        var fragments = snap.Items[AuditKind.Components].Select(c => c.Id).OrderByDescending(p => p.Length).ToList();
        var done = 0;
        var failures = 0;
        await Parallel.ForEachAsync(pages, parallel, async (page, ct) =>
        {
            if (Interlocked.Increment(ref done) % 10 == 1) progress.Report(new AuditProgress(8, $"Page {done} of {pages.Count}: {page.Title}"));
            try
            {
                var content = await _aem.TryGetAsync(EncodePath(page.Id + "/jcr:content") + ".infinity.json", ct)
                              ?? await _aem.TryGetAsync(EncodePath(page.Id + "/jcr:content") + ".5.json", ct);   // VERIFY: .infinity can answer 300 on large pages
                if (content is null) { Interlocked.Increment(ref failures); return; }
                var refs = References(content.Value, rootPath, fragments, excludeRoot: true);
                page.Values["cps"] = refs.Components.ToString(CultureInfo.InvariantCulture);
                page.Values["_ctIds"] = string.Join(AuditFinalizer.ListSeparator, refs.ResourceTypes.Select(rt => "/apps/" + rt));
                page.Values["_componentIds"] = string.Join(AuditFinalizer.ListSeparator, refs.Fragments);
                page.Values["_assetRefs"] = string.Join(AuditFinalizer.ListSeparator, refs.Assets);
                page.Values["_tagRefs"] = string.Join(AuditFinalizer.ListSeparator, refs.Tags);
                page.Values["_pageLinks"] = string.Join(AuditFinalizer.ListSeparator, refs.Pages.Where(p => !p.Equals(page.Id, StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Interlocked.Increment(ref failures) == 1) snap.Note($"Page content of {page.Id} could not be read: {ex.GetBaseException().Message}");
            }
        });
        if (failures > 0) snap.Note($"{failures} of {pages.Count} pages' content could not be read; their components and references are empty.");
    }

    private sealed record Refs(int Components, HashSet<string> ResourceTypes, HashSet<string> Fragments, HashSet<string> Assets, HashSet<string> Tags, HashSet<string> Pages);

    /// <summary>Components and references found in a content tree.</summary>
    private static Refs References(JsonElement content, string rootPath, IReadOnlyList<string> fragments, bool excludeRoot)
    {
        var resourceTypes = new HashSet<string>(StringComparer.Ordinal);
        var frag = new HashSet<string>(StringComparer.Ordinal);
        var assets = new HashSet<string>(StringComparer.Ordinal);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var links = new HashSet<string>(StringComparer.Ordinal);
        var components = 0;
        void Walk(JsonElement node, bool isRoot)
        {
            if (Str(node, "sling:resourceType") is { Length: > 0 } rt && !(isRoot && excludeRoot))
            {
                components++;
                resourceTypes.Add(rt.TrimStart('/').StartsWith("apps/", StringComparison.Ordinal) ? rt.TrimStart('/')[5..] : rt.TrimStart('/'));
            }
            foreach (var p in node.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object) { Walk(p.Value, false); continue; }
                foreach (var (name, value) in AllStrings(p.Value, p.Name))
                {
                    if (name == "cq:tags") { tags.Add(value); continue; }
                    var v = value.Split('#', '?')[0];
                    if (v.EndsWith(".html", StringComparison.Ordinal)) v = v[..^5];
                    if (v.StartsWith("/content/dam/", StringComparison.Ordinal) || v.StartsWith("/content/experience-fragments/", StringComparison.Ordinal))
                    {
                        // A reference to a fragment or anything inside it (XF variation, CF element) counts for the fragment.
                        var f = fragments.FirstOrDefault(x => v.Equals(x, StringComparison.Ordinal) || v.StartsWith(x + "/", StringComparison.Ordinal));
                        if (f is not null) frag.Add(f);
                        else if (v.StartsWith("/content/dam/", StringComparison.Ordinal)) assets.Add(v);
                    }
                    else if (v.StartsWith(rootPath + "/", StringComparison.Ordinal) || v == rootPath) links.Add(v.Split("/jcr:content")[0]);
                }
            }
        }
        if (content.ValueKind == JsonValueKind.Object) Walk(content, true);
        return new Refs(components, resourceTypes, frag, assets, tags, links);
    }

    /// <summary>
    /// Deep scan: also reads every Content Fragment's data for its references, then counts, for every fragment, asset and
    /// tag, how many pages, fragments and assets refer to it ("Used by").
    /// </summary>
    private async Task CrawlUsageAsync(AuditSnapshot snap, AuditOptions options, ParallelOptions parallel, IProgress<AuditProgress> progress,
        Dictionary<string, List<string>> assetTags, Dictionary<string, string> tagIdToPath)
    {
        var fragmentRows = snap.Items[AuditKind.Components];
        var fragments = fragmentRows.Select(c => c.Id).OrderByDescending(p => p.Length).ToList();
        var fragmentRefs = new ConcurrentDictionary<string, Refs>();
        var cfs = fragmentRows.Where(f => f.Value("type") == "Content Fragment").ToList();
        if (cfs.Count + snap.Items[AuditKind.Pages].Count > options.MaxUsageItems)
        {
            foreach (var (kind, col) in new[] { (AuditKind.Components, "usedBy"), (AuditKind.Multimedia, "usedBy"), (AuditKind.Keywords, "usedBy") })
                snap.MarkUnavailable($"{kind}.{col}", $"Not read: more items than the deep-scan limit (Audit:MaxUsageItems = {options.MaxUsageItems}).");
            return;
        }
        var done = 0;
        await Parallel.ForEachAsync(cfs, parallel, async (cf, ct) =>
        {
            if (Interlocked.Increment(ref done) % 10 == 1) progress.Report(new AuditProgress(8, $"Where used – fragment {done} of {cfs.Count}"));
            try
            {
                var data = await _aem.TryGetAsync(EncodePath(cf.Id + "/jcr:content") + ".infinity.json", ct);   // VERIFY: CF data under jcr:content/data
                if (data is not null) fragmentRefs[cf.Id] = References(data.Value, "/content/__none__", fragments, excludeRoot: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { snap.Note($"Fragment {cf.Id} could not be read: {ex.GetBaseException().Message}"); }
        });
        // Experience Fragments are pages: read their master variations the same way.
        foreach (var xf in fragmentRows.Where(f => f.Value("type") == "Experience Fragment"))
        {
            try
            {
                var data = await _aem.TryGetAsync(EncodePath(xf.Id) + ".infinity.json", parallel.CancellationToken);
                if (data is not null) fragmentRefs[xf.Id] = References(data.Value, "/content/__none__", fragments.Where(f => f != xf.Id).ToList(), excludeRoot: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { snap.Note($"Experience Fragment {xf.Id} could not be read: {ex.GetBaseException().Message}"); }
        }

        var fragUse = new Dictionary<string, int>(StringComparer.Ordinal);
        var assetUse = new Dictionary<string, int>(StringComparer.Ordinal);
        var tagUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Count(IEnumerable<string> keys, Dictionary<string, int> into) { foreach (var k in keys) into[k] = into.GetValueOrDefault(k) + 1; }
        foreach (var p in snap.Items[AuditKind.Pages])
        {
            Count(Split(p.Value("_componentIds")), fragUse);
            Count(Split(p.Value("_assetRefs")), assetUse);
            Count(Split(p.Value("_tagRefs")), tagUse);
        }
        foreach (var (id, r) in fragmentRefs)
        {
            Count(r.Fragments.Where(f => f != id), fragUse);
            Count(r.Assets, assetUse);
            Count(r.Tags, tagUse);
        }
        foreach (var tags in assetTags.Values) Count(tags, tagUse);
        foreach (var f in fragmentRows) f.Values["usedBy"] = fragUse.GetValueOrDefault(f.Id).ToString(CultureInfo.InvariantCulture);
        foreach (var a in snap.Items[AuditKind.Multimedia]) a.Values["usedBy"] = assetUse.GetValueOrDefault(a.Id).ToString(CultureInfo.InvariantCulture);
        foreach (var t in snap.Items[AuditKind.Keywords]) t.Values["usedBy"] = tagUse.GetValueOrDefault(t.Value("key")).ToString(CultureInfo.InvariantCulture);
        snap.Note("Usage counts cover this site's pages, fragments and assets; other sites using the same items are not counted.");
    }

    private static IEnumerable<string> Split(string v) => v.Split(AuditFinalizer.ListSeparator, StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ detail (pop-up)

    public async Task<AuditItemDetail> ReadAuditDetailAsync(string itemId, AuditKind kind, TemplateFieldOptions fields, CancellationToken ct)
    {
        var d = new AuditItemDetail { Id = itemId, Title = NameOf(itemId), Kind = kind };
        var general = d.Section("General").Add("Path", itemId);
        JsonElement? jc = kind is AuditKind.Categories or AuditKind.Keywords or AuditKind.ComponentTemplates
            ? null : await _aem.TryGetAsync(EncodePath(itemId + "/jcr:content") + ".json", ct);

        void Versions(JsonElement? node)
        {
            d.Section("Versions")
                .Add("Created", Fmt(Date(node, "jcr:created"))).Add("Created by", Str(node, "jcr:createdBy"))
                .Add("Last modified", Fmt(Date(node, "cq:lastModified") ?? Date(node, "jcr:lastModified")))
                .Add("Modified by", Str(node, "cq:lastModifiedBy") ?? Str(node, "jcr:lastModifiedBy"));
        }
        void Publishing(JsonElement? node)
        {
            var row = new AuditRow { Id = itemId, Title = "", Kind = kind };
            var tmp = new AuditSnapshot { PublicationId = "", Cms = AuditCms.Aem };
            ApplyReplication(row, node, tmp);
            var sec = d.Section("Publishing");
            if (row.PublishedTo.Count == 0) sec.Items.Add($"Not published{(row.Value("_lastAction") is { Length: > 0 } a ? $" (last action: {a})" : "")}.");
            foreach (var t in row.PublishedTo) sec.Items.Add($"{t.Target}{(t.PublishedAt is { } at ? $" · {at:yyyy-MM-dd HH:mm}" : "")}{(t.User is { Length: > 0 } u ? $" · {u}" : "")}");
            var mixins = Strs(node, "jcr:mixinTypes");
            if (mixins.Contains("cq:LiveRelationship"))
                d.Section("MSM").Add("Status", mixins.Contains("cq:LiveSyncCancelled") ? "Live copy – inheritance cancelled" : "Live copy");
        }

        switch (kind)
        {
            case AuditKind.Pages or AuditKind.StructureGroups:
            {
                var content = await _aem.TryGetAsync(EncodePath(itemId + "/jcr:content") + ".infinity.json", ct) ?? jc;
                d.Title = Str(content, "jcr:title") ?? d.Title;
                general.Add("Title", Str(content, "jcr:title")).Add("Navigation title", Str(content, "navTitle"))
                       .Add("Template", Str(content, "cq:template")).Add("Page component", Str(content, "sling:resourceType"))
                       .Add("Description", Str(content, "jcr:description")).Add("Tags", string.Join(", ", Strs(content, "cq:tags")));
                Versions(content);
                Publishing(content);
                if (kind == AuditKind.Pages && content is { } c)
                {
                    var refs = References(c, "/content", Array.Empty<string>(), excludeRoot: true);
                    var comps = d.Section($"Components ({refs.Components})");
                    comps.Items.AddRange(refs.ResourceTypes.OrderBy(x => x));
                    if (refs.Assets.Count > 0) d.Section("Assets referenced").Items.AddRange(refs.Assets.OrderBy(x => x).Take(200));
                    var links = refs.Pages.Where(p => p != itemId).OrderBy(x => x).Take(200).ToList();
                    if (links.Count > 0) d.Section("Links to pages").Items.AddRange(links);
                }
                break;
            }
            case AuditKind.PageTemplates:
                d.Title = Str(jc, "jcr:title") ?? d.Title;
                d.Section("Template").Add("Title", Str(jc, "jcr:title")).Add("Status", Str(jc, "status")).Add("Description", Str(jc, "jcr:description"))
                    .Add("Allowed paths", string.Join(", ", Strs(jc, "allowedPaths")));
                Versions(jc);
                break;
            case AuditKind.Schemas:
            {
                d.Title = Str(jc, "jcr:title") ?? d.Title;
                d.Section("Content Fragment Model").Add("Title", Str(jc, "jcr:title")).Add("Status", Str(jc, "status")).Add("Description", Str(jc, "jcr:description"));
                var items = await _aem.TryGetAsync(EncodePath(itemId + "/jcr:content/model/cq:dialog/content/items") + ".2.json", ct);   // VERIFY: model field definitions
                var sec = d.Section("Fields");
                foreach (var (name, node) in Children(items))
                    sec.Items.Add($"{Str(node, "name") ?? name} – {Str(node, "fieldLabel") ?? ""} ({Str(node, "metaType") ?? Str(node, "valueType") ?? "?"})");
                Versions(jc);
                break;
            }
            case AuditKind.ComponentTemplates:
            {
                var node = await _aem.TryGetAsync(EncodePath(itemId) + ".1.json", ct);
                d.Title = Str(node, "jcr:title") ?? d.Title;
                d.Section("Component").Add("Title", Str(node, "jcr:title")).Add("Group", Str(node, "componentGroup"))
                    .Add("Super type", Str(node, "sling:resourceSuperType")).Add("Description", Str(node, "jcr:description"));
                var files = Children(node, includeContent: true).Select(c => c.Name).ToList();
                if (files.Count > 0) d.Section("Contents").Items.AddRange(files);
                break;
            }
            case AuditKind.Components:
            {
                d.Title = Str(jc, "jcr:title") ?? d.Title;
                if (itemId.StartsWith("/content/dam/", StringComparison.Ordinal))
                {
                    var data = Child(await _aem.TryGetAsync(EncodePath(itemId + "/jcr:content/data") + ".2.json", ct), "master");
                    d.Section("Content Fragment").Add("Title", Str(jc, "jcr:title")).Add("Model", Str(await _aem.TryGetAsync(EncodePath(itemId + "/jcr:content/data") + ".json", ct), "cq:model"));
                    if (data is { ValueKind: JsonValueKind.Object } m)
                    {
                        var el = d.Section("Elements (master)");
                        foreach (var p in m.EnumerateObject().Where(p => !p.Name.StartsWith("jcr:") && !p.Name.EndsWith("@ContentType") && p.Value.ValueKind != JsonValueKind.Object))
                        {
                            var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                            el.Add(p.Name, v.Length > 300 ? v[..300] + "…" : v);
                        }
                    }
                }
                else d.Section("Experience Fragment").Add("Title", Str(jc, "jcr:title")).Add("Template", Str(jc, "cq:template"));
                Versions(jc);
                Publishing(jc);
                break;
            }
            case AuditKind.Multimedia:
            {
                var meta = await _aem.TryGetAsync(EncodePath(itemId + "/jcr:content/metadata") + ".json", ct);
                d.Section("Asset").Add("Title (dc:title)", Str(meta, "dc:title")).Add("Description", Str(meta, "dc:description"))
                    .Add("Format", Str(meta, "dc:format")).Add("Size", long.TryParse(Str(meta, "dam:size"), out var size) ? $"{size / 1024.0:0.#} KB" : null)
                    .Add("Dimensions", Str(meta, "tiff:ImageWidth") is { } w ? $"{w} × {Str(meta, "tiff:ImageLength")}" : null)
                    .Add("Processing", Str(jc, "dam:assetState")).Add("Tags", string.Join(", ", Strs(meta, "cq:tags")));
                var renditions = await _aem.TryGetAsync(EncodePath(itemId + "/jcr:content/renditions") + ".1.json", ct);
                var rs = Children(renditions).Select(c => c.Name).ToList();
                if (rs.Count > 0) d.Section($"Renditions ({rs.Count})").Items.AddRange(rs);
                Versions(jc);
                Publishing(jc);
                break;
            }
            case AuditKind.Categories or AuditKind.Keywords:
            {
                var node = await _aem.TryGetAsync(EncodePath(itemId) + ".1.json", ct);
                d.Title = Str(node, "jcr:title") ?? d.Title;
                var parts = Rel(itemId, "/content/cq:tags").Split('/');
                d.Section(kind == AuditKind.Categories ? "Tag namespace" : "Tag").Add("Title", Str(node, "jcr:title")).Add("Description", Str(node, "jcr:description"))
                    .Add(kind == AuditKind.Keywords ? "Tag ID" : "Namespace", kind == AuditKind.Keywords && parts.Length > 1 ? parts[0] + ":" + string.Join("/", parts.Skip(1)) : parts[0]);
                var children = Children(node).Select(c => Str(c.Node, "jcr:title") ?? c.Name).ToList();
                if (children.Count > 0) d.Section($"Child tags ({children.Count})").Items.AddRange(children.Take(300));
                break;
            }
        }
        d.Sections.RemoveAll(s => s.Rows.Count == 0 && s.Items.Count == 0 && s.Xml is null);
        return d;
    }

    private static string? Fmt(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
