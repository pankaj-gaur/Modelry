using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Modelry.Core.Gateway;
using Modelry.Core.Import;
using Modelry.Core.Model;

namespace Modelry.Core.Pages;

/// <summary>Where the Pages step creates things.</summary>
public sealed record ContentTargets(string ComponentFolderId, string ImageFolderId, string StructureGroupId);

/// <summary>An image, video or document from the zip that becomes a multimedia component.</summary>
public sealed class PlannedAsset
{
    public required string Url { get; init; }
    public string? FullPath { get; init; }
    public required string FileName { get; init; }
    public required string Title { get; init; }
    public string? SchemaTitle { get; set; }
    public string? SchemaId { get; set; }
    public string? MultimediaTypeId { get; set; }
    public string? AltText { get; set; }
    public string? Hash { get; init; }
    public long Bytes { get; init; }
    public string? ExistingId { get; set; }
    public string? CreatedId { get; set; }
    public string? Error { get; set; }
    public string? Id => CreatedId ?? ExistingId;
}

/// <summary>A component to create: one per mapped section, plus one per linked item (slides, list items).</summary>
public sealed class PlannedComponent
{
    public required string Title { get; init; }
    public required IaSchema Schema { get; init; }
    public required SchemaBinding Binding { get; init; }
    public string? SchemaId { get; set; }
    public int? SectionIndex { get; init; }
    public string? TemplateTitle { get; init; }
    public string? TemplateId { get; set; }
    public string? Region { get; init; }
    public PlannedComponent? Parent { get; init; }
    public List<PlannedComponent> Children { get; } = new();
    public List<string> Placeholders { get; } = new();
    public List<string> Errors { get; } = new();
    public string? ExistingId { get; set; }
    public string? CreatedId { get; set; }
    public string? Id => CreatedId ?? ExistingId;
    public IEnumerable<PlannedComponent> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Append(this);
}

public sealed class PlannedPage
{
    public required string Title { get; init; }
    public required string FileName { get; init; }
    public string? PageTemplateTitle { get; init; }
    public string? PageTemplateId { get; set; }
    public string? MetadataSchemaTitle { get; init; }
    public string? MetadataSchemaId { get; set; }
    public List<(string Region, string? RegionSchemaId, List<PlannedComponent> Components)> Regions { get; } = new();
    public List<string> Placeholders { get; } = new();
    public List<string> Errors { get; } = new();
    public string? ExistingId { get; set; }
    public string? CreatedId { get; set; }
}

public sealed class ContentPlan
{
    public required ContentTargets Targets { get; init; }
    public required FolderInfo ComponentFolder { get; init; }
    public required FolderInfo ImageFolder { get; init; }
    public required FolderInfo StructureGroup { get; init; }
    public List<PlannedAsset> Assets { get; } = new();
    public List<PlannedComponent> Components { get; } = new();
    public required PlannedPage Page { get; init; }
    public List<Issue> Issues { get; } = new();
    public IEnumerable<PlannedComponent> AllComponents => Components.SelectMany(c => c.SelfAndDescendants());
    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);
    /// <summary>Lookup data the executor needs (namespaces, keywords, multimedia types).</summary>
    internal ContentContext Context { get; init; } = null!;
    internal Dictionary<FieldBinding, PlannedAsset> AssetByBinding { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<SchemaBinding, PlannedComponent> ComponentByBinding { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>What the planner read from the CMS.</summary>
internal sealed class ContentContext
{
    public required ITridionGateway Gateway { get; init; }
    public required IaWorkbook Workbook { get; init; }
    public Dictionary<string, string> SchemaIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ComponentTemplateIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PageTemplateIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExistingComponents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExistingImages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExistingPages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CategoryIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> Namespaces { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<NamedItem>> Keywords { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<MultimediaTypeInfo> MultimediaTypes { get; set; } = Array.Empty<MultimediaTypeInfo>();

    public async Task LoadNamespaceAsync(string schemaTitle)
    {
        if (Namespaces.ContainsKey(schemaTitle)) return;
        Namespaces[schemaTitle] = SchemaIds.TryGetValue(schemaTitle, out var id) ? (await Gateway.ReadSchemaAsync(id)).Schema.NamespaceUri : null;
    }

    public async Task LoadKeywordsAsync(string category)
    {
        if (Keywords.ContainsKey(category)) return;
        // Every keyword the CMS has in the category – including ones the IA does not list – except abstract ones.
        Keywords[category] = CategoryIds.TryGetValue(category, out var cid) ? (await Gateway.GetSelectableKeywordsAsync(cid)).OrderBy(k => k.Title).ToList() : new();
    }

    /// <summary>
    /// The keyword whose title appears in the hint text (longest first), else the category's first keyword. Candidates are
    /// all keywords the CMS has in the category, IA-listed or not, except abstract ones (abstract keywords cannot classify
    /// content). The CMS says which are abstract; the IA's Is Abstract column is applied too in case the two disagree.
    /// </summary>
    public (string Id, string Title)? Keyword(string category, string? hint)
    {
        if (!Keywords.TryGetValue(category, out var all) || all.Count == 0) return null;
        var abstractInIa = Workbook.Keywords.Where(k => IaWorkbook.Same(k.CategoryTitle, category) && k.IsAbstract)
            .Select(k => k.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = all.Where(k => !abstractInIa.Contains(k.Title)).ToList();
        if (list.Count == 0) return null;
        var match = hint is null ? null : list.OrderByDescending(k => k.Title.Length)
            .FirstOrDefault(k => hint.Contains(k.Title, StringComparison.OrdinalIgnoreCase) || HtmlSections.Words(k.Title).IsSubsetOf(HtmlSections.Words(hint)) && HtmlSections.Words(k.Title).Count > 0);
        var k = match ?? list[0];
        return (k.Id, k.Title);
    }
}

/// <summary>
/// The check for the Pages step: reads the target folders and publication once, decides what will be created or reused,
/// which mandatory fields get placeholders, and what cannot be created. Nothing is written.
/// </summary>
public static class ContentPlanner
{
    private const string Sheet = "Page content";

    /// <summary>Progress steps of the check (each is reported, in order, through the activity callback).</summary>
    public static readonly string[] CheckSteps =
    {
        "Reading the folders and the Structure Group",
        "Reading schemas, templates and categories",
        "Looking for items that already exist",
        "Planning images, components, placeholders and the page",
    };

    /// <param name="sections">Matched, non-excluded sections with their bindings, in page order.</param>
    /// <param name="resolveFile">URL as written in the HTML → full path of the file in the uploaded zip (null when not in it).</param>
    public static async Task<ContentPlan> BuildAsync(ITridionGateway gw, IaWorkbook wb, IaPage page, ContentTargets targets,
        IReadOnlyList<(SectionMatch Match, SchemaBinding Binding)> sections, Func<string, string?> resolveFile,
        IProgress<string>? activity = null)
    {
        activity?.Report(CheckSteps[0]);
        var compFolder = await gw.GetFolderAsync(targets.ComponentFolderId);
        var imgFolder = await gw.GetFolderAsync(targets.ImageFolderId);
        var sg = await gw.GetStructureGroupAsync(targets.StructureGroupId);
        var ctx = new ContentContext { Gateway = gw, Workbook = wb };

        activity?.Report(CheckSteps[1]);
        var allSchemas = await gw.ListPublicationSchemasAsync(compFolder.PublicationId);
        foreach (var s in allSchemas) ctx.SchemaIds.TryAdd(s.Title, s.Id);
        var duplicateSchemas = allSchemas.GroupBy(s => s.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Where(g => wb.Schemas.Any(x => IaWorkbook.Same(x.Title, g.Key))).ToList();
        foreach (var t in await gw.GetTemplatesAsync(sg.PublicationId, TemplateKind.Component)) ctx.ComponentTemplateIds.TryAdd(t.Title, t.Id);
        foreach (var t in await gw.GetTemplatesAsync(sg.PublicationId, TemplateKind.Page)) ctx.PageTemplateIds.TryAdd(t.Title, t.Id);
        foreach (var c in await gw.GetCategoriesAsync(compFolder.PublicationId)) ctx.CategoryIds.TryAdd(c.Title, c.Id);
        ctx.MultimediaTypes = await gw.GetMultimediaTypesAsync();

        activity?.Report(CheckSteps[2]);
        foreach (var i in await gw.ListItemsAsync(compFolder.Id, ContentItemType.Component)) ctx.ExistingComponents.TryAdd(i.Title, i.Id);
        foreach (var i in await gw.ListItemsAsync(imgFolder.Id, ContentItemType.Component)) ctx.ExistingImages.TryAdd(i.Title, i.Id);
        foreach (var i in await gw.ListItemsAsync(sg.Id, ContentItemType.Page)) ctx.ExistingPages.TryAdd(i.Title, i.Id);

        activity?.Report(CheckSteps[3]);
        var plan = new ContentPlan
        {
            Targets = targets, ComponentFolder = compFolder, ImageFolder = imgFolder, StructureGroup = sg, Context = ctx,
            Page = new PlannedPage
            {
                Title = page.PageName, FileName = FileNameFor(page), PageTemplateTitle = page.PageTemplate,
                MetadataSchemaTitle = page.PageMetadataSchema
            }
        };
        void Issue(IssueLevel l, string item, string msg) => plan.Issues.Add(new Issue(l, Sheet, 0, item, msg));
        foreach (var g in duplicateSchemas)
            Issue(IssueLevel.Warning, g.Key, $"{g.Count()} schemas in {compFolder.PublicationTitle} have this title ({string.Join(", ", g.Select(x => x.Id))}); " +
                $"{g.First().Id} is used. Rename or remove the others if that is the wrong one.");
        if (compFolder.PublicationId != sg.PublicationId)
            Issue(IssueLevel.Info, sg.PublicationTitle, $"Components go to {compFolder.PublicationTitle} and the page to {sg.PublicationTitle}: the page's publication must inherit from the components' one (BluePrint), or the page cannot use them.");

        // ---- components (sections, then the items linked from them)
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string title)
        {
            title = title.Length > 180 ? title[..177] + "…" : title;
            var t = title;
            for (var n = 2; !titles.Add(t); n++) t = $"{title} ({n})";
            return t;
        }
        foreach (var (sm, binding) in sections)
        {
            var label = Label(binding) ?? sm.Row?.UiSection ?? SectionMatcher.StripPrefix(binding.Schema.Title);
            var pc = new PlannedComponent
            {
                Title = Unique($"{page.PageId} – {Clip(label, 50)}"), Schema = binding.Schema, Binding = binding,
                SectionIndex = sm.Section.Index, TemplateTitle = sm.TemplateTitle, Region = sm.Region
            };
            AddChildren(pc, binding, plan, Unique);
            plan.Components.Add(pc);
            plan.ComponentByBinding[binding] = pc;
        }

        // ---- assets referenced by any binding
        var byHash = new Dictionary<string, PlannedAsset>();
        var byUrl = new Dictionary<string, PlannedAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var pc in plan.AllComponents)
            foreach (var fb in pc.Binding.All())
            {
                var url = AssetUrlOf(fb);
                if (url is null) continue;
                if (!byUrl.TryGetValue(url, out var asset))
                {
                    asset = MakeAsset(url, fb, wb, ctx, resolveFile, byHash);
                    byUrl[url] = asset;
                    if (!plan.Assets.Contains(asset)) plan.Assets.Add(asset);
                }
                plan.AssetByBinding[fb] = asset;
            }

        // Everything the executor will look up is loaded now, so it never needs this request's CMS connection.
        foreach (var t in plan.Assets.Select(a => a.SchemaTitle).Where(t => t is not null).Distinct()) await ctx.LoadNamespaceAsync(t!);

        // ---- ids, existing items, placeholders (dry build of the XML)
        foreach (var pc in plan.AllComponents)
        {
            if (!ctx.SchemaIds.TryGetValue(pc.Schema.Title, out var sid))
                pc.Errors.Add($"Schema '{pc.Schema.Title}' is not visible in {compFolder.PublicationTitle}. Create the schemas first, or choose a component folder in a publication that has them.");
            pc.SchemaId = sid;
            if (pc.TemplateTitle is not null)
            {
                if (ctx.ComponentTemplateIds.TryGetValue(pc.TemplateTitle, out var ct)) pc.TemplateId = ct;
                else pc.Errors.Add($"Component Template '{pc.TemplateTitle}' is not visible in {sg.PublicationTitle}. Create the templates first.");
            }
            if (ctx.ExistingComponents.TryGetValue(pc.Title, out var existing)) pc.ExistingId = existing;
            await ctx.LoadNamespaceAsync(pc.Schema.Title);
        }
        // Categories of every mandatory keyword field (also inside embedded schemas), for placeholders.
        foreach (var cat in wb.Fields.Where(f => f.Type == IaFieldType.Keyword && f.Mandatory && f.Category is not null).Select(f => f.Category!).Distinct(StringComparer.OrdinalIgnoreCase))
            await ctx.LoadKeywordsAsync(cat);
        foreach (var pc in plan.AllComponents.Where(c => c.ExistingId is null && c.SchemaId is not null))
        {
            var xml = Xml(plan, dryRun: true);
            xml.BuildContent(pc.Schema, pc.Binding);
            xml.BuildMetadata(pc.Schema);
            pc.Placeholders.AddRange(xml.Placeholders);
            pc.Errors.AddRange(xml.Errors);
        }

        // ---- page
        var pg = plan.Page;
        if (pg.PageTemplateTitle is not null && ctx.PageTemplateIds.TryGetValue(pg.PageTemplateTitle, out var pt)) pg.PageTemplateId = pt;
        else pg.Errors.Add($"Page Template '{pg.PageTemplateTitle}' is not visible in {sg.PublicationTitle}. Create the templates first.");
        if (pg.MetadataSchemaTitle is not null && ctx.SchemaIds.TryGetValue(pg.MetadataSchemaTitle, out var ms)) pg.MetadataSchemaId = ms;
        if (ctx.ExistingPages.TryGetValue(pg.Title, out var ep)) pg.ExistingId = ep;
        // Every region of the page schema is sent, in schema order, even when empty: the CM rejects a page that lacks a
        // mandatory region ("Missing region 'Hero'"), e.g. when that region's components could not be created.
        var nested = wb.Regions.Where(r => r.RowType == IaRegionRowType.NestedRegion && IaWorkbook.Same(r.RegionSchemaTitle, page.PageSchema) && r.NestedRegionName is not null)
            .Select(r => (Name: r.NestedRegionName!, Schema: r.NestedRegionSchema)).ToList();
        foreach (var extra in plan.Components.Where(c => c.Region is not null).Select(c => c.Region!).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(n => nested.All(x => !IaWorkbook.Same(x.Name, n))).ToList())
            nested.Add((extra, null));
        foreach (var (name, rs) in nested)
            pg.Regions.Add((name, rs is not null && ctx.SchemaIds.TryGetValue(rs, out var rsid) ? rsid : null,
                plan.Components.Where(c => IaWorkbook.Same(c.Region, name)).ToList()));
        if (pg.MetadataSchemaTitle is not null && wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, pg.MetadataSchemaTitle)) is { } metaSchema)
        {
            await ctx.LoadNamespaceAsync(metaSchema.Title);
            var xml = Xml(plan, dryRun: true);
            xml.BuildMetadata(metaSchema, PageMetadataValues(wb, metaSchema, page));
            pg.Placeholders.AddRange(xml.Placeholders);
            pg.Errors.AddRange(xml.Errors);
        }

        // ---- findings
        foreach (var a in plan.Assets.Where(a => a.Error is not null)) Issue(IssueLevel.Warning, a.Url, a.Error!);
        foreach (var c in plan.AllComponents)
            foreach (var e in c.Errors) Issue(IssueLevel.Error, c.Title, e);
        foreach (var e in pg.Errors) Issue(IssueLevel.Error, pg.Title, e);
        if (pg.ExistingId is not null) Issue(IssueLevel.Warning, pg.Title, "A page with this title already exists in the Structure Group – it is left as it is; components are still created.");
        return plan;
    }

    /// <summary>Creates linked items (slides, list items) as child components, depth first.</summary>
    private static void AddChildren(PlannedComponent parent, SchemaBinding binding, ContentPlan plan, Func<string, string> unique)
    {
        foreach (var fb in binding.Fields)
        {
            if (fb.Kind == BindKind.Items && fb.Field.Type == IaFieldType.ComponentLink)
            {
                var i = 0;
                foreach (var item in fb.Items)
                {
                    i++;
                    var label = Label(item) ?? $"{SectionMatcher.StripPrefix(item.Schema.Title)} {i}";
                    var child = new PlannedComponent
                    {
                        Title = unique($"{parent.Title.Split(" – ")[0]} – {Clip(label, 60)}"), Schema = item.Schema, Binding = item, Parent = parent
                    };
                    parent.Children.Add(child);
                    plan.ComponentByBinding[item] = child;
                    AddChildren(child, item, plan, unique);
                }
            }
            else if (fb.Embedded is not null) AddChildren(parent, fb.Embedded, plan, unique);
            else if (fb.Kind == BindKind.Items) foreach (var item in fb.Items) AddChildren(parent, item, plan, unique);
        }
    }

    private static PlannedAsset MakeAsset(string url, FieldBinding fb, IaWorkbook wb, ContentContext ctx, Func<string, string?> resolveFile,
        Dictionary<string, PlannedAsset> byHash)
    {
        var clean = url.Split('?', '#')[0];
        var fileName = Uri.UnescapeDataString(clean.Replace('\\', '/').Split('/').Last());
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var full = Regex.IsMatch(url, "^(https?:)?//", RegexOptions.IgnoreCase) || url.StartsWith("data:") ? null : resolveFile(url);
        string? hash = null; long bytes = 0;
        if (full is not null && File.Exists(full))
        {
            using var s = File.OpenRead(full);
            hash = Convert.ToHexString(SHA256.HashData(s));
            bytes = new FileInfo(full).Length;
            if (byHash.TryGetValue(hash, out var same)) return same; // identical file under another name: one component
        }
        var asset = new PlannedAsset
        {
            Url = url, FullPath = full, FileName = fileName, Title = Path.GetFileNameWithoutExtension(fileName), Hash = hash, Bytes = bytes,
            AltText = fb.Element?.LocalName == "img" ? fb.Element.GetAttribute("alt") : fb.Element?.QuerySelector("img")?.GetAttribute("alt")
        };
        if (full is null) asset.Error = Regex.IsMatch(url, "^(https?:)?//") ? "External URL – not uploaded; the field stays empty (or gets the URL where the schema has a URL field)." : "File is not in the zip.";
        // Multimedia schema: one the field allows for this file type, else any multimedia schema that accepts it.
        var allowed = fb.Field.AllowedTargetSchemas.Select(t => wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, t))).Where(s => s is not null).Select(s => s!);
        var schema = allowed.Concat(wb.Schemas.Where(s => s.Purpose == IaSchemaPurpose.Multimedia))
            .FirstOrDefault(s => s.Purpose == IaSchemaPurpose.Multimedia && s.AllowedMultimediaTypes.Contains(ext, StringComparer.OrdinalIgnoreCase));
        asset.SchemaTitle = schema?.Title;
        if (schema is null) asset.Error ??= $"No multimedia schema in the workbook accepts .{ext} files.";
        else if (ctx.SchemaIds.TryGetValue(schema.Title, out var sid)) asset.SchemaId = sid;
        else asset.Error ??= $"Schema '{schema.Title}' is not visible in the component publication.";
        asset.MultimediaTypeId = ctx.MultimediaTypes.FirstOrDefault(t => t.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))?.Id;
        if (asset.MultimediaTypeId is null) asset.Error ??= $"The CMS has no Multimedia Type for .{ext}.";
        if (ctx.ExistingImages.TryGetValue(asset.Title, out var existing)) asset.ExistingId = existing;
        if (hash is not null) byHash[hash] = asset;
        return asset;
    }

    /// <summary>The asset URL a binding points at: image/video sources, and links to documents (pdf, docx…).</summary>
    private static string? AssetUrlOf(FieldBinding fb) => fb.Kind switch
    {
        BindKind.ImageSrc or BindKind.VideoSrc when fb.Field.Type == IaFieldType.MultimediaLink => fb.AssetUrl,
        BindKind.LinkHref when fb.Field.Type is IaFieldType.MultimediaLink or IaFieldType.ComponentLink
                               && FieldBinder.IsDocumentHref(fb.Element?.GetAttribute("href")) => fb.Element!.GetAttribute("href"),
        _ => null
    };

    internal static ContentXml Xml(ContentPlan plan, bool dryRun)
    {
        var ctx = plan.Context;
        return new ContentXml(ctx.Workbook,
            title => ctx.Namespaces.TryGetValue(title, out var ns) ? ns : null,
            fb => plan.AssetByBinding.TryGetValue(fb, out var a) ? (dryRun ? a.Id ?? (a.Error is null ? "tcm:0-0-0" : null) : a.Id) : null,
            item => plan.ComponentByBinding.TryGetValue(item, out var c) ? (dryRun ? c.Id ?? (c.Errors.Count == 0 ? "tcm:0-0-0" : null) : c.Id) : null,
            ctx.Keyword);
    }

    internal static IReadOnlyDictionary<string, string> PageMetadataValues(IaWorkbook wb, IaSchema schema, IaPage page) =>
        wb.FieldsOf(schema.Title, IaFieldSection.Metadata).Where(f => Regex.IsMatch(f.XmlName, "title", RegexOptions.IgnoreCase) && f.Type == IaFieldType.Text)
            .ToDictionary(f => f.XmlName, _ => page.PageName);

    private static IEnumerable<IaField> Fields(IaWorkbook wb, string schemaTitle) =>
        wb.FieldsOf(schemaTitle, IaFieldSection.Content).Concat(wb.FieldsOf(schemaTitle, IaFieldSection.Metadata));

    /// <summary>The first heading-like text of a binding, for titles.</summary>
    private static string? Label(SchemaBinding b) =>
        b.Fields.FirstOrDefault(f => f.Kind == BindKind.Text && FieldBinder.Role(f.Field) == "heading" && f.Element is not null) is { } h
            ? HtmlSections.Collapse(h.Element!.TextContent) is { Length: > 0 } t ? t : null : null;

    private static string Clip(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    /// <summary>Last segment of the Proposed URL without extension, else the page name as a slug.</summary>
    public static string FileNameFor(IaPage page)
    {
        var seg = page.ProposedUrl?.TrimEnd('/').Split('/').LastOrDefault();
        var name = string.IsNullOrWhiteSpace(seg) || seg.StartsWith("{") ? page.PageName : Path.GetFileNameWithoutExtension(seg);
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "index" : slug;
    }
}

/// <summary>Creates the planned multimedia components, components (items first) and the page. Existing items are reused.</summary>
public sealed class ContentExecutor
{
    public static readonly string[] StepNames =
    {
        "Preparing",
        "Uploading images and documents as multimedia components",
        "Creating components (slides and list items first)",
        "Creating the page in its Structure Group",
    };

    private readonly ITridionGateway _gw;
    private readonly string _comment;
    public ContentExecutor(ITridionGateway gw, string checkInComment = "Created by Modelry") { _gw = gw; _comment = checkInComment; }

    public async Task<ImportResult> ExecuteAsync(ContentPlan plan, IaPage page, CancellationToken ct, IProgress<ImportProgress>? progress = null)
    {
        var result = new ImportResult();
        var comps = plan.AllComponents.ToList();
        var tracker = new ProgressTracker(progress, plan.Assets.Count + comps.Count + 1, StepNames);
        void Log(IssueLevel l, string step, string item, string msg, string? id = null)
        {
            var e = new LogEntry(DateTime.UtcNow, l, step, item, msg, id);
            result.Log.Add(e);
            tracker.Entry(e);
        }

        // 1. multimedia
        tracker.Step(2, plan.Assets.Count);
        foreach (var a in plan.Assets)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
            tracker.Item(a.FileName);
            if (a.ExistingId is not null) { result.Skipped++; Log(IssueLevel.Info, "Multimedia", a.Title, "Already exists in the images folder – reused.", a.ExistingId); continue; }
            if (a.Error is not null || a.FullPath is null || a.SchemaId is null || a.MultimediaTypeId is null)
            { Log(IssueLevel.Warning, "Multimedia", a.FileName, a.Error ?? "Not created."); continue; }
            try
            {
                var schema = plan.Context.Workbook.Schemas.First(s => IaWorkbook.Same(s.Title, a.SchemaTitle));
                await plan.Context.LoadNamespaceAsync(schema.Title);
                var meta = ContentPlanner.Xml(plan, dryRun: false).BuildMetadata(schema,
                    a.AltText is { Length: > 0 } alt ? new Dictionary<string, string> { ["altText"] = alt } : null);
                a.CreatedId = await _gw.CreateMultimediaComponentAsync(plan.Targets.ImageFolderId, new MultimediaWriteModel
                {
                    Title = a.Title, SchemaId = a.SchemaId, FileName = a.FileName, Data = await File.ReadAllBytesAsync(a.FullPath, ct),
                    MultimediaTypeId = a.MultimediaTypeId, Metadata = meta, CheckInComment = _comment
                });
                result.Created++;
                Log(IssueLevel.Info, "Multimedia", a.Title, $"Multimedia component created ({a.FileName}).", a.CreatedId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failed++;
                Log(IssueLevel.Error, "Multimedia", a.FileName, $"{ex.Message} (schema {a.SchemaTitle}, {a.SchemaId})");
            }
        }
        tracker.EndStep();

        // 2. components, children before their parent
        tracker.Step(3, comps.Count);
        foreach (var pc in comps)
        {
            if (result.Cancelled || ct.IsCancellationRequested) { result.Cancelled = true; break; }
            tracker.Item(pc.Title);
            if (pc.ExistingId is not null) { result.Skipped++; Log(IssueLevel.Info, "Component", pc.Title, "Already exists in the component folder – reused.", pc.ExistingId); continue; }
            if (pc.SchemaId is null) { result.Failed++; Log(IssueLevel.Error, "Component", pc.Title, string.Join(" ", pc.Errors)); continue; }
            var xml = ContentPlanner.Xml(plan, dryRun: false);
            var content = xml.BuildContent(pc.Schema, pc.Binding);
            var meta = xml.BuildMetadata(pc.Schema);
            if (xml.Errors.Count > 0) { result.Failed++; Log(IssueLevel.Error, "Component", pc.Title, "Not created: " + string.Join("; ", xml.Errors)); continue; }
            try
            {
                pc.CreatedId = await _gw.CreateComponentAsync(plan.Targets.ComponentFolderId, new ComponentWriteModel
                { Title = pc.Title, SchemaId = pc.SchemaId, Content = content, Metadata = meta, CheckInComment = _comment });
                result.Created++;
                Log(IssueLevel.Info, "Component", pc.Title,
                    xml.Placeholders.Count == 0 ? "Component created." : $"Component created with placeholders: {string.Join("; ", xml.Placeholders)}.", pc.CreatedId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failed++;
                Log(IssueLevel.Error, "Component", pc.Title, $"{ex.Message} (schema {pc.Schema.Title}, {pc.SchemaId})");
            }
        }
        tracker.EndStep();

        // 3. page
        tracker.Step(4, 1);
        var pg = plan.Page;
        tracker.Item(pg.Title);
        if (result.Cancelled) { }
        else if (pg.ExistingId is not null) { result.Skipped++; Log(IssueLevel.Warning, "Page", pg.Title, "A page with this title already exists – not changed. Add the components to it by hand, or delete it and run again.", pg.ExistingId); }
        else if (pg.PageTemplateId is null) { result.Failed++; Log(IssueLevel.Error, "Page", pg.Title, string.Join(" ", pg.Errors)); }
        else
        {
            var regions = pg.Regions.Select(r => new PageRegionWriteModel
            {
                Name = r.Region, RegionSchemaId = r.RegionSchemaId,
                Presentations = r.Components.Where(c => c.Id is not null && c.TemplateId is not null).Select(c => new PresentationWriteModel(c.Id!, c.TemplateId!)).ToList()
            }).ToList();
            var missing = pg.Regions.SelectMany(r => r.Components).Count(c => c.Id is null || c.TemplateId is null);
            string? meta = null;
            if (pg.MetadataSchemaTitle is not null && plan.Context.Workbook.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, pg.MetadataSchemaTitle)) is { } ms)
                meta = ContentPlanner.Xml(plan, dryRun: false).BuildMetadata(ms, ContentPlanner.PageMetadataValues(plan.Context.Workbook, ms, page));
            try
            {
                pg.CreatedId = await _gw.CreatePageAsync(plan.Targets.StructureGroupId, new PageWriteModel
                {
                    Title = pg.Title, FileName = pg.FileName, PageTemplateId = pg.PageTemplateId, Regions = regions,
                    MetadataSchemaId = meta is null ? null : pg.MetadataSchemaId, Metadata = meta, CheckInComment = _comment
                });
                result.Created++;
                var sent = regions.Sum(r => r.Presentations.Count);
                Log(missing > 0 ? IssueLevel.Warning : IssueLevel.Info, "Page", pg.Title,
                    $"Page created ({pg.FileName}) with {sent} component presentations in {string.Join(", ", regions.Where(r => r.Presentations.Count > 0).Select(r => $"{r.Name} ({r.Presentations.Count})"))}"
                    + (missing > 0 ? $"; {missing} component(s) could not be placed because they were not created." : "."), pg.CreatedId);
                // What the CMS actually kept – it can drop presentations it cannot place.
                var saved = await _gw.GetPagePresentationCountsAsync(pg.CreatedId);
                var kept = saved.Values.Sum();
                if (kept < sent)
                    Log(IssueLevel.Error, "Page", pg.Title,
                        $"The CMS kept {kept} of the {sent} component presentations (saved: {(saved.Count == 0 ? "no regions" : string.Join(", ", saved.Select(kv => $"{(kv.Key.Length == 0 ? "page" : kv.Key)} {kv.Value}")))}). " +
                        "Check that the Page Template's page schema has regions with these names: " + string.Join(", ", regions.Where(r => r.Presentations.Count > 0).Select(r => r.Name)) + ".", pg.CreatedId);
                else if (saved.ContainsKey("") && saved.Count(kv => kv.Value > 0) == 1)
                    Log(IssueLevel.Warning, "Page", pg.Title, "The page's regions did not take the presentations, so they were added to the page itself; DXA places them by the region in each Component Template's metadata.", pg.CreatedId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failed++;
                Log(IssueLevel.Error, "Page", pg.Title, ex.Message);
            }
        }
        tracker.EndStep();
        return result;
    }
}
