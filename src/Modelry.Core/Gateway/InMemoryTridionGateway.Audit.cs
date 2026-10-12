using System.Collections.Concurrent;
using System.Xml.Linq;
using Modelry.Core.Audit;
using Modelry.Core.Model;

namespace Modelry.Core.Gateway;

/// <summary>
/// Demo-mode audit. Real demo items (schemas, templates, categories, keywords, folders, Structure Groups and anything the
/// Pages step created) are listed as they are; on top of that a publication with component schemas gets a deterministic
/// set of sample components, multimedia, Structure Groups and pages with a mix of publish states, so the dashboard shows
/// what a real site looks like. Nothing is stored – the sample rows are rebuilt on every scan.
/// </summary>
public sealed partial class InMemoryTridionGateway
{
    private readonly ConcurrentDictionary<string, AuditRow> _sampleRows = new();

    public async Task<IReadOnlyList<AuditRoot>> ListAuditRootsAsync(CancellationToken ct) =>
        (await GetPublicationsAsync()).Select(p => new AuditRoot(p.Id, p.Title, p.Id)).ToList();

    public async Task<AuditSnapshot> ScanPublicationAsync(string publicationId, AuditOptions options, TemplateFieldOptions fields,
        CmsCapabilities capabilities, bool deep, IProgress<AuditProgress> progress, CancellationToken ct)
    {
        var pub = Get(publicationId);
        var snap = new AuditSnapshot { PublicationId = publicationId, PublicationTitle = pub.Title, Capabilities = capabilities, StartedUtc = DateTime.UtcNow, Deep = deep };
        var visible = Visible(publicationId);
        // Demo publications have their own Structure Group trees (not BluePrinted), so only the audited publication's count.
        var items = _items.Values.Where(i => i.Kind != "pub" && (i.Kind is "sg" or "page" ? i.PublicationId == publicationId : visible.Contains(i.PublicationId))).ToList();
        var pubNo = publicationId.Split('-')[1];

        async Task Step(int step, string activity)
        {
            progress.Report(new AuditProgress(step, activity));
            await Task.Delay(250, ct);   // lets the demo show its progress
        }

        await Step(1, "Reading the publication");
        await Step(2, "Reading folders and building blocks");
        foreach (var i in items)
        {
            var kind = i.Kind switch
            {
                "schema" => AuditKind.Schemas,
                "ct" => AuditKind.ComponentTemplates,
                "pt" => AuditKind.PageTemplates,
                "component" => AuditKind.Components,
                "mm" => AuditKind.Multimedia,
                "category" => AuditKind.Categories,
                "keyword" => AuditKind.Keywords,
                "sg" => AuditKind.StructureGroups,
                "page" => AuditKind.Pages,
                _ => (AuditKind?)null
            };
            if (kind is null) continue;
            var row = new AuditRow
            {
                Id = i.Id, Title = i.Title, Kind = kind.Value,
                Path = kind is AuditKind.Categories or AuditKind.Keywords ? "" : kind == AuditKind.StructureGroups ? SgPath(i) : ContainerPath(i.ParentId),
                Origin = i.PublicationId == publicationId ? BluePrintOrigin.Local : BluePrintOrigin.Shared,
                FromPublication = i.PublicationId == publicationId ? null : TitleOf(i.PublicationId),
                Modified = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc).AddHours(-(Math.Abs(i.Id.GetHashCode()) % 2000)),
                ModifiedBy = "demo\\modelry",
                Created = new DateTime(2025, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddHours(-(Math.Abs(i.Id.GetHashCode()) % 4000)),
                CreatedBy = "demo\\modelry"
            };
            switch (kind)
            {
                case AuditKind.Schemas:
                    row.Values["purpose"] = i.Schema?.Schema.Purpose.ToString() ?? "";
                    break;
                case AuditKind.ComponentTemplates or AuditKind.PageTemplates when i.Template is { } t:
                    row.Values["view"] = t.View ?? "";
                    row.Values["controller"] = t.Controller ?? "";
                    row.Values["linkedSchemas"] = string.Join(", ", t.LinkedSchemas);
                    row.Values["_linkedSchemaIds"] = string.Join(AuditFinalizer.ListSeparator,
                        t.LinkedSchemas.SelectMany(title => items.Where(s => s.Kind == "schema" && IaWorkbook.Same(s.Title, title))).Select(s => s.Id));
                    row.Values["dynamic"] = t.Dynamic is null ? "" : t.Dynamic.Value ? "Yes" : "No";
                    row.Values["includes"] = string.Join(", ", t.Includes);
                    break;
                case AuditKind.Components or AuditKind.Multimedia when i.SchemaId is not null:
                    row.Values["schema"] = TitleOf(i.SchemaId);
                    row.Values["_schemaId"] = i.SchemaId;
                    row.Publish = PublishState.Unpublished;
                    break;
                case AuditKind.Categories when i.Category is { } c:
                    row.Values["xmlName"] = c.XmlName;
                    row.Values["publishable"] = c.Publishable ? "Yes" : "No";
                    break;
                case AuditKind.Keywords:
                    row.Values["category"] = i.ParentId is null ? "" : TitleOf(i.ParentId);
                    row.Values["_categoryId"] = i.ParentId ?? "";
                    row.Values["key"] = i.Title;
                    row.Values["abstract"] = i.IsAbstract ? "Yes" : "No";
                    break;
                case AuditKind.StructureGroups:
                    row.Values["directory"] = i.ParentId is null ? "" : Slug(i.Title);
                    row.Values["url"] = SgUrl(i);
                    row.Values["active"] = "Yes";
                    if (i.ParentId is not null) row.Values["_parentId"] = i.ParentId;
                    break;
                case AuditKind.Pages:
                    row.Values["_sgId"] = i.ParentId ?? "";
                    row.Values["url"] = i.ParentId is null ? "/" : SgUrl(Get(i.ParentId));
                    if (i.Page is { } p)
                    {
                        row.Values["pageTemplate"] = TitleOf(p.PageTemplateId); row.Values["_pageTemplateId"] = p.PageTemplateId;
                        row.Values["fileName"] = p.FileName;
                        row.Values["url"] = row.Value("url").TrimEnd('/') + "/" + p.FileName + ".html";
                        var cps = p.Regions.SelectMany(r => r.Presentations).ToList();
                        row.Values["cps"] = cps.Count.ToString();
                        row.Values["_componentIds"] = string.Join(AuditFinalizer.ListSeparator, cps.Select(c => c.ComponentId).Distinct());
                        row.Values["_ctIds"] = string.Join(AuditFinalizer.ListSeparator, cps.Select(c => c.ComponentTemplateId).Distinct());
                    }
                    row.Publish = PublishState.Unpublished;
                    break;
            }
            snap.Items[kind.Value].Add(row);
        }

        await Step(3, "Reading Structure Groups and pages");
        AddSampleContent(snap, publicationId, pubNo);
        await Step(4, "Reading categories and keywords");
        await Step(5, "Reading schemas and templates");
        await Step(6, "Reading publish status");
        await Step(7, "Reading pages and multimedia");
        if (deep) { await Step(8, "Reading where items are used"); AddSampleUsage(snap, int.Parse(pubNo)); }
        foreach (var r in snap.Items.Values.SelectMany(r => r))
            foreach (var t in r.PublishedTo) snap.PublishTargets.Add(t.Target);
        snap.Note("Demo mode: schemas, templates and taxonomy are the demo CMS's own; components, multimedia and pages are generated sample content.");
        return snap;
    }

    private string ContainerPath(string? folderId)
    {
        var parts = new List<string>();
        for (var id = folderId; id is not null && _items.TryGetValue(id, out var f) && f.ParentId is not null; id = f.ParentId) parts.Insert(0, f.Title);
        return string.Join("/", parts);
    }

    private string SgPath(Item sg)
    {
        var parts = new List<string>();
        for (Item? s = sg; s?.ParentId is not null; s = _items.GetValueOrDefault(s.ParentId)) parts.Insert(0, s.Title);
        return string.Join("/", parts);
    }

    private string SgUrl(Item sg)
    {
        var parts = new List<string>();
        for (Item? s = sg; s?.ParentId is not null; s = _items.GetValueOrDefault(s.ParentId)) parts.Insert(0, Slug(s.Title));
        return "/" + string.Join("/", parts);
    }

    private static string Slug(string title) => new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>Deterministic sample content (same ids and states on every scan).</summary>
    private void AddSampleContent(AuditSnapshot snap, string publicationId, string pubNo)
    {
        var schemas = snap.Items[AuditKind.Schemas];
        var componentSchemas = schemas.Where(s => s.Value("purpose") == "Component").ToList();
        if (componentSchemas.Count == 0) return;
        var mmSchema = schemas.FirstOrDefault(s => s.Value("purpose") == "Multimedia");
        var pageTemplates = snap.Items[AuditKind.PageTemplates];
        var rootSg = snap.Items[AuditKind.StructureGroups].FirstOrDefault(s => s.Path.Length == 0 && s.Origin == BluePrintOrigin.Local);
        var rnd = new Random(int.Parse(pubNo) * 7919);
        var editors = new[] { "demo\\a.sharma", "demo\\j.smith", "demo\\m.alharbi", "demo\\content.team" };
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var seq = 90000;
        string Id(int type) => $"tcm:{pubNo}-{++seq}-{type}";

        AuditRow Sample(AuditKind kind, string title, string path, int type)
        {
            var row = new AuditRow
            {
                Id = Id(type), Title = title, Kind = kind, Path = path,
                Origin = rnd.Next(12) == 0 ? BluePrintOrigin.Localized : BluePrintOrigin.Local,
                Modified = now.AddDays(-rnd.Next(1, 400)).AddMinutes(-rnd.Next(0, 600)),
                ModifiedBy = editors[rnd.Next(editors.Length)]
            };
            var seed = row.Id.Sum(c => c);   // stable, and leaves the random sequence of the demo data unchanged
            row.Created = row.Modified!.Value.AddDays(-(30 + seed % 870));
            row.CreatedBy = editors[seed % editors.Length];
            if (row.Origin == BluePrintOrigin.Localized) row.FromPublication = "010 Schema Master (demo)";
            if (AuditCatalog.IsPublishable(kind))
            {
                var roll = rnd.Next(10);
                if (roll < 6) row.PublishedTo.Add(new PublishTarget("Live", row.Modified!.Value.AddDays(rnd.Next(0, 20)), "demo\\publisher"));
                if (roll < 8) row.PublishedTo.Add(new PublishTarget("Staging", row.Modified!.Value.AddHours(rnd.Next(1, 48)), editors[rnd.Next(editors.Length)]));
                row.Publish = row.PublishedTo.Count > 0 ? PublishState.Published : PublishState.Unpublished;
                // some items were edited after their last publish
                if (row.PublishedTo.Count > 0 && rnd.Next(6) == 0) row.Modified = row.PublishedTo.Max(t => t.PublishedAt)!.Value.AddDays(3);
            }
            _sampleRows[row.Id] = row;
            snap.Items[kind].Add(row);
            return row;
        }

        var words = new[] { "Polycarbonate", "Sustainability", "Specialty", "Innovation", "Petrochemicals", "Agri-nutrients", "Circular economy",
                            "Automotive", "Healthcare", "Packaging", "Investor", "Careers", "Research", "Safety", "Energy" };
        var components = new List<AuditRow>();
        foreach (var s in componentSchemas)
        {
            var n = 2 + rnd.Next(6);
            for (var k = 0; k < n; k++)
            {
                var c = Sample(AuditKind.Components, $"{s.Title} – {words[rnd.Next(words.Length)]} {k + 1}", $"Content/{s.Title}", 16);
                c.Values["schema"] = s.Title;
                c.Values["_schemaId"] = s.Id;
                components.Add(c);
            }
        }
        if (componentSchemas.Count > 1)
        {
            var copy = Sample(AuditKind.Components, $"Copy of {componentSchemas[1].Title} – test", $"Content/{componentSchemas[1].Title}", 16);
            copy.Values["schema"] = componentSchemas[1].Title;
            copy.Values["_schemaId"] = componentSchemas[1].Id;
            components.Add(copy);
        }
        if (mmSchema is not null)
            for (var k = 0; k < 14; k++)
            {
                var ext = new[] { "jpg", "png", "svg", "pdf" }[rnd.Next(4)];
                var m = Sample(AuditKind.Multimedia, $"{Slug(words[rnd.Next(words.Length)])}-{k + 1}.{ext}", ext == "pdf" ? "Media/Documents" : "Media/Images", 16);
                m.Values["schema"] = mmSchema.Title;
                m.Values["_schemaId"] = mmSchema.Id;
                m.Values["fileName"] = m.Title;
                m.Values["mmType"] = ext switch { "jpg" => "Jpeg image", "png" => "PNG image", "svg" => "SVG image", _ => "Adobe Acrobat PDF" };
                m.Values["sizeKb"] = (k % 6 == 5 ? 2200 + rnd.Next(4000) : 20 + rnd.Next(900)).ToString();
            }

        var pt = pageTemplates.FirstOrDefault(p => p.Origin == BluePrintOrigin.Local) ?? pageTemplates.FirstOrDefault();
        var sgs = new List<AuditRow>();
        if (rootSg is not null)
        {
            var sgByPath = new Dictionary<string, string>();
            foreach (var (title, active) in new[] { ("Products", true), ("Products/Polymers", true), ("Products/Chemicals", true),
                                                    ("Sustainability", true), ("News", true), ("Careers", true), ("Legacy campaigns", false), ("Old campaign 2019", true) })
            {
                var sg = Sample(AuditKind.StructureGroups, title.Split('/').Last(), title, 4);
                sgByPath[title] = sg.Id;
                sg.Values["_parentId"] = title.Contains('/') ? sgByPath[title[..title.LastIndexOf('/')]] : rootSg.Id;
                sg.Values["directory"] = Slug(title.Split('/').Last());
                sg.Values["url"] = "/" + string.Join("/", title.Split('/').Select(Slug));
                sg.Values["active"] = active ? "Yes" : "No";
                sgs.Add(sg);
            }
            sgs.RemoveAll(x => x.Title == "Old campaign 2019");   // stays empty
            sgs.Insert(0, rootSg);
            var ct = snap.Items[AuditKind.ComponentTemplates].FirstOrDefault(c => c.Origin == BluePrintOrigin.Local && !c.Title.Contains("Base"))
                     ?? snap.Items[AuditKind.ComponentTemplates].FirstOrDefault();
            AuditRow? previous = null;
            for (var k = 0; k < 22; k++)
            {
                var sg = sgs[rnd.Next(sgs.Count)];
                var title = k == 0 ? "Home" : $"{words[rnd.Next(words.Length)]} {k}";
                var fileName = k == 0 ? "index" : k == 7 ? "Campaign Page" : Slug(title);
                if (k == 9 && previous is not null) { sg = sgs.First(x => x.Id == previous.Value("_sgId")); fileName = previous.Value("fileName"); }   // duplicate file name
                var page = Sample(AuditKind.Pages, title, sg.Path, 64);
                page.Values["_sgId"] = sg.Id;
                page.Values["fileName"] = fileName;
                page.Values["url"] = (sg.Value("url").Length > 0 ? sg.Value("url") : "/").TrimEnd('/') + "/" + fileName + ".html";
                if (pt is not null) { page.Values["pageTemplate"] = pt.Title; page.Values["_pageTemplateId"] = pt.Id; }
                var placed = k == 11 ? new List<AuditRow>() : Enumerable.Range(0, 1 + rnd.Next(5)).Select(_ => components[rnd.Next(components.Count)]).Distinct().ToList();
                page.Values["cps"] = placed.Count.ToString();
                page.Values["_cps"] = string.Join(AuditFinalizer.ListSeparator, placed.Select(c => c.Title));
                page.Values["_componentIds"] = string.Join(AuditFinalizer.ListSeparator, placed.Select(c => c.Id));
                page.Values["_ctIds"] = placed.Count > 0 && ct is not null ? ct.Id : "";
                previous = page;
            }
        }
    }

    /// <summary>Deep-scan sample: how many items use each component, multimedia component, keyword and category.</summary>
    private static void AddSampleUsage(AuditSnapshot snap, int seed)
    {
        var rnd = new Random(seed * 104729);
        var onPages = snap.Items[AuditKind.Pages].SelectMany(p => p.Value("_componentIds").Split(AuditFinalizer.ListSeparator, StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        // Child publications in the demo BluePrint: some items are used (or published) only there.
        var children = new[] { "030 Website – Arabic (demo)", "040 Website – Chinese (demo)" };
        void Usage(AuditRow row, int used)
        {
            var elsewhere = rnd.Next(7) == 0 ? children[rnd.Next(children.Length)] : null;
            row.Values["usedBy"] = (used + (elsewhere is null ? 0 : 1)).ToString();
            row.Values["usedIn"] = elsewhere ?? "";
        }
        foreach (var c in snap.Items[AuditKind.Components]) Usage(c, onPages.GetValueOrDefault(c.Id) + (rnd.Next(3) == 0 ? 1 : 0));
        foreach (var m in snap.Items[AuditKind.Multimedia]) Usage(m, rnd.Next(5) == 0 ? 0 : 1 + rnd.Next(4));
        foreach (var k in snap.Items[AuditKind.Keywords]) Usage(k, rnd.Next(3) == 0 ? 0 : rnd.Next(1, 12));
        // One unused, unpublished component that a child publication publishes (e.g. as a dynamic presentation): not unused.
        if (snap.Items[AuditKind.Components].FirstOrDefault(c => c.Value("usedBy") == "0" && c.Publish == PublishState.Unpublished) is { } dcp)
            dcp.Values["_publishedElsewhere"] = "1";
        foreach (var c in snap.Items[AuditKind.Categories]) c.Values["schemas"] = (rnd.Next(2)).ToString();
    }

    public async Task<AuditItemDetail> ReadAuditDetailAsync(string itemId, AuditKind kind, TemplateFieldOptions fields, CancellationToken ct)
    {
        if (_sampleRows.TryGetValue(itemId, out var sample)) return SampleDetail(sample);
        var item = Get(itemId);
        var d = new AuditItemDetail { Id = item.Id, Title = item.Title, Kind = kind };
        d.Section("General").Add("ID", item.Id).Add("Title", item.Title).Add("Publication", TitleOf(item.PublicationId))
            .Add("Location", item.ParentId is null ? null : ContainerPath(item.ParentId));
        switch (kind)
        {
            case AuditKind.Schemas:
            {
                var s = await ReadSchemaAsync(itemId);
                d.Section("Schema").Add("Purpose", s.Schema.Purpose.ToString()).Add("Root element", s.Schema.RootElementName)
                    .Add("Namespace", s.Schema.NamespaceUri).Add("Description", s.Schema.Description);
                var f = d.Section("Fields");
                foreach (var field in s.Fields.OrderBy(x => x.Section).ThenBy(x => x.Order))
                    f.Items.Add($"{field.Section} · {field.XmlName} ({field.Type}{(field.Mandatory ? ", mandatory" : "")})");
                break;
            }
            case AuditKind.ComponentTemplates or AuditKind.PageTemplates when item.Template is { } t:
                d.Section("Template").Add("View", t.View).Add("Controller", t.Controller).Add("Action", t.Action)
                    .Add("Dynamic", t.Dynamic is null ? null : t.Dynamic.Value ? "Yes" : "No").Add("Priority", t.Priority)
                    .Add("Includes", string.Join(", ", t.Includes));
                if (t.LinkedSchemas.Count > 0) d.Section("Linked schemas").Items.AddRange(t.LinkedSchemas);
                break;
            case AuditKind.Categories:
            {
                var c = await ReadCategoryAsync(itemId);
                d.Section("Category").Add("XML name", c.XmlName).Add("Publishable", c.Publishable ? "Yes" : "No")
                    .Add("Keyword metadata schema", c.KeywordMetadataSchema);
                var kw = await GetKeywordsAsync(itemId);
                d.Section($"Keywords ({kw.Count})").Items.AddRange(kw.Select(k => k.Title).OrderBy(k => k));
                break;
            }
            case AuditKind.Keywords:
                d.Section("Keyword").Add("Category", item.ParentId is null ? null : TitleOf(item.ParentId)).Add("Abstract", item.IsAbstract ? "Yes" : "No");
                break;
            case AuditKind.Components or AuditKind.Multimedia:
                d.Section("General").Add("Schema", item.SchemaId is null ? null : TitleOf(item.SchemaId));
                if (item.Bytes > 0) d.Section("Binary").Add("Size", $"{item.Bytes / 1024.0:0.#} KB");
                if (item.Content is not null) d.Section("Content").Xml = Pretty(item.Content);
                d.Section("Publishing").Items.Add("Not published.");
                break;
            case AuditKind.Pages when item.Page is { } p:
                d.Section("General").Add("File name", p.FileName).Add("Page Template", TitleOf(p.PageTemplateId));
                var cps = d.Section("Component presentations");
                foreach (var r in p.Regions) foreach (var cp in r.Presentations) cps.Items.Add($"{r.Name}: {TitleOf(cp.ComponentId)} — {TitleOf(cp.ComponentTemplateId)}");
                d.Section("Publishing").Items.Add("Not published.");
                break;
        }
        return d;
    }

    private static AuditItemDetail SampleDetail(AuditRow r)
    {
        var d = new AuditItemDetail { Id = r.Id, Title = r.Title, Kind = r.Kind };
        d.Section("General").Add("ID", r.Id).Add("Title", r.Title).Add("Location", r.Path).Add("Schema", r.Value("schema"))
            .Add("URL", r.Kind == AuditKind.Pages ? r.Value("url") : null).Add("Page Template", r.Value("pageTemplate"));
        d.Section("Versions").Add("Created", r.Created?.ToString("yyyy-MM-dd HH:mm")).Add("Created by", r.CreatedBy)
            .Add("Last modified", r.Modified?.ToString("yyyy-MM-dd HH:mm")).Add("Modified by", r.ModifiedBy);
        d.Section("BluePrint").Add("Status", AuditCatalog.Origin(r.Origin)).Add("Owning publication", r.FromPublication);
        if (r.Kind == AuditKind.StructureGroups)
            d.Section("Structure Group").Add("Directory", r.Value("directory")).Add("Active", r.Value("active"));
        if (r.Kind == AuditKind.Pages)
            d.Section("Component presentations").Items.AddRange(r.Value("_cps").Split(AuditFinalizer.ListSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => $"Main: {t} — Article CT"));
        if (r.Kind == AuditKind.Multimedia)
            d.Section("Binary").Add("File name", r.Title).Add("Size", $"{(Math.Abs(r.Id.GetHashCode()) % 900) + 40} KB");
        if (r.Kind == AuditKind.Components)
            d.Section("Content").Xml = Pretty($"<Content xmlns=\"uuid:demo\"><headline>{System.Security.SecurityElement.Escape(r.Title)}</headline><summary>Sample text for the demo audit.</summary></Content>");
        if (AuditCatalog.IsPublishable(r.Kind))
        {
            var p = d.Section("Publishing");
            if (r.PublishedTo.Count == 0) p.Items.Add("Not published.");
            foreach (var t in r.PublishedTo) p.Items.Add($"{t.Target} · {t.PublishedAt:yyyy-MM-dd HH:mm} · {t.User}");
        }
        d.Sections.RemoveAll(s => s.Rows.Count == 0 && s.Items.Count == 0 && s.Xml is null);
        return d;
    }

    private static string Pretty(string xml)
    {
        try { return XElement.Parse(xml).ToString(); } catch { return xml; }
    }
}
