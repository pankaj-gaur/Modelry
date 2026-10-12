#if CORESERVICE_PROXY
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Modelry.Core.Audit;
using Modelry.Core.Gateway;
using CS = Tridion.ContentManager.CoreService.Client;

namespace Modelry.Tridion;

/// <summary>
/// Audit (read-only) over the Core Service. Written to work unchanged on Tridion Sites 9.1 – 10.x with the one
/// CoreService201701 proxy:
///  • tables come from GetListXml, whose columns are XML attributes – an attribute an older CM does not return just leaves
///    the cell empty and is reported as "not available", it never throws;
///  • richer data (templates, Structure Groups, categories, publish info) is read per item, within the limits in
///    AuditOptions, and every call that fails is caught, noted and the scan carries on;
///  • proxy members that may differ between versions are reached through Reflect.
/// Lines marked "VERIFY" are API details to confirm on the first run against each CM version.
/// </summary>
public sealed partial class CoreServiceTridionGateway
{
    private static readonly XNamespace Tcm = "http://www.tridion.com/ContentManager/5.0";

    public async Task<IReadOnlyList<AuditRoot>> ListAuditRootsAsync(CancellationToken ct) =>
        (await GetPublicationsAsync()).Select(p => new AuditRoot(p.Id, p.Title, p.Id)).ToList();

    public async Task<AuditSnapshot> ScanPublicationAsync(string publicationId, AuditOptions options, TemplateFieldOptions fields,
        CmsCapabilities capabilities, bool deep, IProgress<AuditProgress> progress, CancellationToken ct)
    {
        var snap = new AuditSnapshot { PublicationId = publicationId, Capabilities = capabilities, StartedUtc = DateTime.UtcNow, Deep = deep };
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);   // list attributes returned by this CM
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelCalls), CancellationToken = ct };
        var pubNo = PublicationNumber(publicationId);

        // 1 ---------------------------------------------------------------- publication
        progress.Report(new AuditProgress(1, "Reading the publication"));
        var pub = (CS.PublicationData)await _client.ReadAsync(publicationId, ReadOpts);
        snap.PublicationTitle = pub.Title;
        var rootFolder = pub.RootFolder?.IdRef;                                                               // VERIFY: PublicationData.RootFolder
        var rootSg = (Reflect.Get(pub, "RootStructureGroup") as CS.LinkToStructureGroupData)?.IdRef;           // VERIFY: PublicationData.RootStructureGroup
        if (rootSg == "tcm:0-0-0") rootSg = null;

        // 2 ---------------------------------------------------------------- folders: schemas, templates, components
        var folderCount = 0;
        if (rootFolder is not null)
        {
            var level = new List<(string Id, string Path)> { (rootFolder, "") };
            while (level.Count > 0)
            {
                var next = new ConcurrentBag<(string, string)>();
                await Parallel.ForEachAsync(level, parallel, async (folder, token) =>
                {
                    var n = Interlocked.Increment(ref folderCount);
                    if (n % 10 == 1) progress.Report(new AuditProgress(2, $"Folder {n}: {(folder.Path.Length == 0 ? "(root)" : folder.Path)}"));
                    var filter = ItemsFilter(CS.ItemType.Folder, CS.ItemType.Schema, CS.ItemType.Component, CS.ItemType.ComponentTemplate, CS.ItemType.PageTemplate);
                    foreach (var e in await ListXmlAsync(folder.Id, filter, seen))
                    {
                        var id = (string?)e.Attribute("ID") ?? "";
                        var title = (string?)e.Attribute("Title") ?? id;
                        switch (TypeOf(e, id))
                        {
                            case 2: next.Add((id, Join(folder.Path, title))); break;
                            case 8: AddRow(snap, Row(e, AuditKind.Schemas, folder.Path)); break;
                            case 16: AddRow(snap, Row(e, AuditKind.Components, folder.Path)); break;   // split into Multimedia in step 5
                            case 32: AddRow(snap, Row(e, AuditKind.ComponentTemplates, folder.Path)); break;
                            case 128: AddRow(snap, Row(e, AuditKind.PageTemplates, folder.Path)); break;
                        }
                    }
                });
                level = next.ToList();
            }
        }
        else snap.Note("The publication's root folder could not be read – schemas, templates and components are not listed.");
        snap.Note($"{folderCount} folders read.");

        // 3 ---------------------------------------------------------------- Structure Groups and pages
        var sgParent = new ConcurrentDictionary<string, string?>();
        if (rootSg is not null)
        {
            var root = await _client.ReadAsync(rootSg, ReadOpts);
            var rootBp = Reflect.Get(root, "BluePrintInfo");
            AddRow(snap, new AuditRow
            {
                Id = rootSg, Title = root.Title, Kind = AuditKind.StructureGroups, Path = "",
                Origin = Reflect.GetString(rootBp, "IsShared") == "True" ? BluePrintOrigin.Shared
                    : Reflect.GetString(rootBp, "IsLocalized") == "True" ? BluePrintOrigin.Localized : BluePrintOrigin.Local,
                FromPublication = Reflect.GetString(Reflect.Get(rootBp, "OwningRepository"), "Title"),
                Modified = Reflect.Get(Reflect.Get(root, "VersionInfo"), "RevisionDate") as DateTime?
            });
            sgParent[rootSg] = null;
            var level = new List<(string Id, string Path)> { (rootSg, "") };
            var sgCount = 0;
            while (level.Count > 0)
            {
                var next = new ConcurrentBag<(string, string)>();
                await Parallel.ForEachAsync(level, parallel, async (sg, token) =>
                {
                    var n = Interlocked.Increment(ref sgCount);
                    if (n % 10 == 1) progress.Report(new AuditProgress(3, $"Structure Group {n}: {(sg.Path.Length == 0 ? "(root)" : sg.Path)}"));
                    foreach (var e in await ListXmlAsync(sg.Id, ItemsFilter(CS.ItemType.StructureGroup, CS.ItemType.Page), seen))
                    {
                        var id = (string?)e.Attribute("ID") ?? "";
                        var title = (string?)e.Attribute("Title") ?? id;
                        if (TypeOf(e, id) == 4)
                        {
                            var row = Row(e, AuditKind.StructureGroups, sg.Path);
                            row.Values["_parentId"] = sg.Id;
                            AddRow(snap, row);
                            sgParent[id] = sg.Id;
                            next.Add((id, Join(sg.Path, title)));
                        }
                        else if (TypeOf(e, id) == 64)
                        {
                            var row = Row(e, AuditKind.Pages, sg.Path);   // path = the Structure Group holding the page
                            row.Values["_sgId"] = sg.Id;
                            AddRow(snap, row);
                        }
                    }
                });
                level = next.ToList();
            }
            // Paths of Structure Groups are their parent's path; show their own path instead (more useful for SGs).
            foreach (var r in snap.Items[AuditKind.StructureGroups].Where(r => r.Id != rootSg)) r.Path = Join(r.Path, r.Title);
            await ReadStructureGroupsAsync(snap, rootSg, sgParent, options, parallel, progress);
        }
        else snap.Note("The publication has no root Structure Group – no pages to audit.");

        // 4 ---------------------------------------------------------------- categories and keywords
        progress.Report(new AuditProgress(4, "Reading categories"));
        var categories = await ListXmlAsync(publicationId, Filter(new CS.RepositoryItemsFilterData(), false, CS.ItemType.Category), seen);
        foreach (var e in categories) AddRow(snap, Row(e, AuditKind.Categories, ""));
        var catNo = 0;
        await Parallel.ForEachAsync(snap.Items[AuditKind.Categories].ToList(), parallel, async (cat, token) =>
        {
            progress.Report(new AuditProgress(4, $"Category {Interlocked.Increment(ref catNo)} of {categories.Count}: {cat.Title}"));
            try
            {
                var keywords = await ListXmlAsync(cat.Id, Filter(new CS.OrganizationalItemItemsFilterData(), true, CS.ItemType.Keyword), seen);
                HashSet<string>? selectable = null;
                if (!keywords.Any(k => k.Attribute("IsAbstract") is not null))
                {
                    try { selectable = (await GetSelectableKeywordsAsync(cat.Id)).Select(k => AuditFinalizer.ItemKey(k.Id)).ToHashSet(); }   // VERIFY: KeywordsFilterData lists all non-abstract keywords
                    catch { /* abstract flag stays empty */ }
                }
                foreach (var k in keywords)
                {
                    var row = Row(k, AuditKind.Keywords, "");
                    row.Values["category"] = cat.Title;
                    row.Values["_categoryId"] = cat.Id;
                    row.Values["key"] = Attr(k, "Key") ?? "";
                    var abs = Attr(k, "IsAbstract");
                    row.Values["abstract"] = abs is not null ? YesNo(abs) : selectable is null ? "" : selectable.Contains(AuditFinalizer.ItemKey(row.Id)) ? "No" : "Yes";
                    AddRow(snap, row);
                }
                if (snap.Items[AuditKind.Categories].Count <= options.MaxCategoryReads)
                {
                    var c = await ReadCategoryAsync(cat.Id);
                    cat.Values["xmlName"] = c.XmlName;
                    cat.Values["publishable"] = c.Publishable ? "Yes" : "No";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snap.Note($"Category '{cat.Title}' could not be read: {ex.GetBaseException().Message}");
            }
        });
        if (snap.Items[AuditKind.Categories].Count > options.MaxCategoryReads)
        {
            var why = $"More than {options.MaxCategoryReads} categories – read limit (Audit:MaxCategoryReads). Open a category to see it.";
            snap.MarkUnavailable("Categories.xmlName", why); snap.MarkUnavailable("Categories.publishable", why);
        }
        if (snap.Items[AuditKind.Keywords].Count > 0 && snap.Items[AuditKind.Keywords].All(k => k.Value("key").Length == 0) && !seen.ContainsKey("Key"))
            snap.MarkUnavailable("Keywords.key", "Keyword keys are not in list data on this CMS – open a keyword to see its key.");

        // 5 ---------------------------------------------------------------- schemas, templates, component → schema, page → template
        await ReadSchemasAsync(snap, publicationId, parallel, progress);
        await ReadTemplatesAsync(snap, options, fields, progress, ct);
        await MapUsingItemsAsync(snap, AuditKind.Schemas, CS.ItemType.Component, pubNo, parallel, progress, ct);
        await MapUsingItemsAsync(snap, AuditKind.PageTemplates, CS.ItemType.Page, pubNo, parallel, progress, ct);

        // 6 ---------------------------------------------------------------- publish status
        await ReadPublishStatusAsync(snap, options, pubNo, seen, parallel, progress);

        // 7 ---------------------------------------------------------------- pages and multimedia, item by item
        await ReadPagesAsync(snap, options, parallel, progress);
        await ReadItemDetailsAsync(snap, options, parallel, progress);

        // 8 ---------------------------------------------------------------- deep scan: where used
        if (deep) await ReadUsageAsync(snap, options, parallel, progress);

        // list columns this CM did / did not return
        var allRows = snap.Items.Values.SelectMany(r => r).ToList();
        if (allRows.Count > 0 && !allRows.Any(r => r.Modified is not null))
            snap.MarkUnavailable("*.modified", "Not in list data on this CMS – open an item to see its dates.");
        if (allRows.Count > 0 && !allRows.Any(r => r.ModifiedBy is not null))
            snap.MarkUnavailable("*.modifiedBy", options.ReadItemDetails
                ? "Could not be read on this CMS – open an item to see who changed it last."
                : "Not in list data – switch on Audit:ReadItemDetails to read it.");
        if (allRows.Count > 0 && !allRows.Any(r => r.Created is not null))
        {
            var why = options.ReadItemDetails ? "Could not be read on this CMS – open an item to see when and by whom it was created."
                : "Not in list data – switch on Audit:ReadItemDetails to read it.";
            snap.MarkUnavailable("*.created", why); snap.MarkUnavailable("*.createdBy", why);
        }
        if (!seen.ContainsKey("IsLocalized"))
            snap.Note("This CMS's list data does not flag localized items: items are shown as Shared (inherited) or Local. Open an item to see whether a local item is a localized copy.");
        snap.Note("List columns returned by this CMS: " + string.Join(", ", seen.Keys.OrderBy(k => k)));
        return snap;
    }

    // ------------------------------------------------------------------ list helpers

    private static CS.OrganizationalItemItemsFilterData ItemsFilter(params CS.ItemType[] types) =>
        Filter(new CS.OrganizationalItemItemsFilterData(), false, types);

    /// <summary>Sets types, recursion and the Extended column set on any list filter (tolerant of proxy differences).</summary>
    private static T Filter<T>(T filter, bool recursive, params CS.ItemType[] types) where T : class
    {
        Reflect.TrySet(filter, "ItemTypes", types);
        Reflect.TrySet(filter, "Recursive", recursive);
        Reflect.TrySet(filter, "BaseColumns", "Extended");                                                    // VERIFY: ListBaseColumns.Extended
        return filter;
    }

    /// <summary>GetListXml, normalised to the item elements, recording which attributes this CM returns.</summary>
    private async Task<List<XElement>> ListXmlAsync(string id, CS.SubjectRelatedListFilterData filter, ConcurrentDictionary<string, byte>? seen)
    {
        object raw = await _client.GetListXmlAsync(id, filter);                                               // VERIFY: returns XElement (or XmlElement)
        var root = raw switch
        {
            XElement x => x,
            System.Xml.XmlNode n => XElement.Parse(n.OuterXml),
            _ => XElement.Parse(raw.ToString() ?? "<ListItems/>")
        };
        var items = root.Elements().Where(e => e.Name.LocalName == "Item").ToList();
        if (seen is not null)
            foreach (var a in items.Take(5).SelectMany(e => e.Attributes())) seen.TryAdd(a.Name.LocalName, 0);
        return items;
    }

    private static string? Attr(XElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var a = e.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (a is not null && a.Value.Length > 0) return a.Value;
        }
        return null;
    }

    private static int TypeOf(XElement e, string id) =>
        int.TryParse(Attr(e, "Type"), out var t) ? t : int.TryParse(id.Split('-').ElementAtOrDefault(2), out var u) ? u : 16;

    private static AuditRow Row(XElement e, AuditKind kind, string path)
    {
        var id = Attr(e, "ID") ?? "";
        var row = new AuditRow { Id = id, Title = Attr(e, "Title") ?? id, Kind = kind, Path = path };
        if (DateTime.TryParse(Attr(e, "Modified", "RevisionDate"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var m)) row.Modified = m;
        row.ModifiedBy = Attr(e, "Revisor", "ModifiedBy", "LastModifiedBy");
        var from = Attr(e, "FromPub", "OwningPublication");
        var shared = Attr(e, "IsShared");
        var localized = Attr(e, "IsLocalized");
        row.Origin = IsTrue(localized) ? BluePrintOrigin.Localized
            : IsTrue(shared) || (shared is null && from is not null) ? BluePrintOrigin.Shared
            : BluePrintOrigin.Local;
        if (row.Origin != BluePrintOrigin.Local) row.FromPublication = from;
        if (Attr(e, "IsPublished") is { } published) row.Values["_listPublished"] = IsTrue(published) ? "1" : "0";
        row.Values["_icon"] = Attr(e, "Icon") ?? "";
        return row;
    }

    private static bool IsTrue(string? v) => v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
    private static string YesNo(string? v) => IsTrue(v) ? "Yes" : "No";
    private static string Join(string path, string title) => path.Length == 0 ? title : title.Length == 0 ? path : $"{path}/{title}";
    private static int PublicationNumber(string tcmUri) => int.TryParse(tcmUri.Split(':')[1].Split('-')[tcmUri.StartsWith("tcm:0-") ? 1 : 0], out var n) ? n : -1;
    private static int ItemPublicationNumber(string tcmUri) => int.TryParse(tcmUri.Split(':')[1].Split('-')[0], out var n) ? n : -1;

    private static void AddRow(AuditSnapshot snap, AuditRow row)
    {
        var list = snap.Items[row.Kind];
        lock (list) list.Add(row);
    }

    // ------------------------------------------------------------------ step 3: Structure Group details

    private async Task ReadStructureGroupsAsync(AuditSnapshot snap, string rootSg, ConcurrentDictionary<string, string?> parent,
        AuditOptions options, ParallelOptions parallel, IProgress<AuditProgress> progress)
    {
        var sgs = snap.Items[AuditKind.StructureGroups];
        if (sgs.Count > options.MaxStructureGroupReads)
        {
            var why = $"More than {options.MaxStructureGroupReads} Structure Groups – read limit (Audit:MaxStructureGroupReads).";
            foreach (var c in new[] { "directory", "url", "active" }) snap.MarkUnavailable($"StructureGroups.{c}", why);
            snap.MarkUnavailable("Pages.url", why);
            return;
        }
        var directory = new ConcurrentDictionary<string, string>();
        var done = 0;
        await Parallel.ForEachAsync(sgs, parallel, async (sg, token) =>
        {
            if (Interlocked.Increment(ref done) % 10 == 1) progress.Report(new AuditProgress(3, $"Structure Group details {done} of {sgs.Count}"));
            try
            {
                var data = await _client.ReadAsync(sg.Id, ReadOpts);
                directory[sg.Id] = Reflect.GetString(data, "Directory") ?? "";                                // VERIFY: StructureGroupData.Directory
                sg.Values["directory"] = directory[sg.Id];
                var active = Reflect.GetString(data, "IsActive");                                             // VERIFY: StructureGroupData.IsActive
                sg.Values["active"] = active is null ? "" : YesNo(active);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snap.Note($"Structure Group '{sg.Title}' could not be read: {ex.GetBaseException().Message}");
            }
        });
        string Url(string id)
        {
            var parts = new List<string>();
            for (string? p = id; p is not null; p = parent.GetValueOrDefault(p))
                if (directory.TryGetValue(p, out var d) && d.Length > 0) parts.Insert(0, d.Trim('/'));
            return "/" + string.Join("/", parts);
        }
        foreach (var sg in sgs) sg.Values["url"] = Url(sg.Id);
        foreach (var page in snap.Items[AuditKind.Pages]) page.Values["url"] = Url(page.Value("_sgId"));
    }

    // ------------------------------------------------------------------ step 5: schemas, templates, using items

    private async Task ReadSchemasAsync(AuditSnapshot snap, string publicationId, ParallelOptions parallel, IProgress<AuditProgress> progress)
    {
        progress.Report(new AuditProgress(5, "Reading schema purposes"));
        var purpose = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var typed = await _client.GetListAsync(publicationId, new CS.RepositoryItemsFilterData { ItemTypes = new[] { CS.ItemType.Schema }, Recursive = true });
            foreach (var i in typed)
                if ((i as CS.SchemaData)?.Purpose?.ToString() is { Length: > 0 } p && p != "UnknownByClient") purpose[AuditFinalizer.ItemKey(i.Id)] = p;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { snap.Note("Schema list could not be read: " + ex.GetBaseException().Message); }

        var missing = snap.Items[AuditKind.Schemas].Where(s => !purpose.ContainsKey(AuditFinalizer.ItemKey(s.Id))).ToList();
        var done = 0;
        await Parallel.ForEachAsync(missing, parallel, async (s, token) =>
        {
            if (Interlocked.Increment(ref done) % 10 == 1) progress.Report(new AuditProgress(5, $"Schema {done} of {missing.Count}: {s.Title}"));
            try { purpose[AuditFinalizer.ItemKey(s.Id)] = ((CS.SchemaData)await _client.ReadAsync(s.Id, ReadOpts)).Purpose?.ToString() ?? ""; }
            catch (Exception ex) when (ex is not OperationCanceledException) { snap.Note($"Schema '{s.Title}' could not be read: {ex.GetBaseException().Message}"); }
        });
        foreach (var s in snap.Items[AuditKind.Schemas]) s.Values["purpose"] = purpose.GetValueOrDefault(AuditFinalizer.ItemKey(s.Id)) ?? "";
    }

    /// <summary>Templates are read one at a time (the metadata-schema cache in ReadTemplateAsync is not thread-safe).</summary>
    private async Task ReadTemplatesAsync(AuditSnapshot snap, AuditOptions options, TemplateFieldOptions fields, IProgress<AuditProgress> progress, CancellationToken ct)
    {
        var templates = snap.Items[AuditKind.ComponentTemplates].Concat(snap.Items[AuditKind.PageTemplates]).ToList();
        if (templates.Count > options.MaxTemplateReads)
        {
            var why = $"More than {options.MaxTemplateReads} templates – read limit (Audit:MaxTemplateReads). Open a template to see it.";
            foreach (var c in new[] { "view", "controller", "linkedSchemas", "dynamic" }) snap.MarkUnavailable($"ComponentTemplates.{c}", why);
            foreach (var c in new[] { "view", "includes" }) snap.MarkUnavailable($"PageTemplates.{c}", why);
            return;
        }
        var schemasByTitle = snap.Items[AuditKind.Schemas].ToLookup(s => s.Title, StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var t in templates)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 5 == 1) progress.Report(new AuditProgress(5, $"Template {done} of {templates.Count}: {t.Title}"));
            try
            {
                var d = await ReadTemplateAsync(t.Id, fields);
                t.Values["view"] = d.View ?? "";
                if (t.Kind == AuditKind.ComponentTemplates)
                {
                    t.Values["controller"] = d.Controller ?? "";
                    t.Values["linkedSchemas"] = string.Join(", ", d.LinkedSchemas);
                    t.Values["_linkedSchemaIds"] = string.Join(AuditFinalizer.ListSeparator,
                        d.LinkedSchemas.SelectMany(title => schemasByTitle[title]).Select(s => s.Id));
                    t.Values["dynamic"] = d.Dynamic is null ? "" : d.Dynamic.Value ? "Yes" : "No";
                }
                else t.Values["includes"] = string.Join(", ", d.Includes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snap.Note($"Template '{t.Title}' could not be read: {ex.GetBaseException().Message}");
            }
        }
    }

    /// <summary>
    /// Which schema each component uses (or which Page Template each page uses), from the "where used" list of every
    /// schema / Page Template – one call per schema or template instead of one read per component or page.
    /// Components on Multimedia schemas move to the Multimedia table here.
    /// </summary>
    private async Task MapUsingItemsAsync(AuditSnapshot snap, AuditKind sourceKind, CS.ItemType usingType, int pubNo,
        ParallelOptions parallel, IProgress<AuditProgress> progress, CancellationToken ct)
    {
        var components = usingType == CS.ItemType.Component;
        var sources = snap.Items[sourceKind]
            .Where(s => !components || s.Value("purpose") is "Component" or "Multimedia" or "" ).ToList();
        var targets = components ? snap.Items[AuditKind.Components] : snap.Items[AuditKind.Pages];
        if (targets.Count == 0 || sources.Count == 0) return;
        var byKey = targets.ToDictionary(t => AuditFinalizer.ItemKey(t.Id), StringComparer.OrdinalIgnoreCase);
        var failures = 0;
        var done = 0;
        await Parallel.ForEachAsync(sources, parallel, async (source, token) =>
        {
            if (Interlocked.Increment(ref done) % 5 == 1)
                progress.Report(new AuditProgress(5, $"{(components ? "Components per schema" : "Pages per Page Template")}: {done} of {sources.Count}"));
            try
            {
                var filter = new CS.UsingItemsFilterData();
                Reflect.TrySet(filter, "ItemTypes", new[] { usingType });
                Reflect.TrySet(filter, "BaseColumns", "IdAndTitle");
                Reflect.TrySet(filter, "IncludedVersions", "OnlyLatestVersions");                             // VERIFY: VersionCondition.OnlyLatestVersions
                Reflect.TrySet(filter, "InRepository", new CS.LinkToRepositoryData { IdRef = $"tcm:0-{pubNo}-1" }); // VERIFY: UsingItemsFilterData.InRepository
                foreach (var e in await ListXmlAsync(source.Id, filter, null))
                {
                    var id = Attr(e, "ID") ?? "";
                    // Match by item number and type: shared items can be listed with their owning publication's URI.
                    if (!byKey.TryGetValue(AuditFinalizer.ItemKey(id), out var target)) continue;
                    if (components)
                    {
                        target.Values["schema"] = source.Title;
                        target.Values["_schemaId"] = source.Id;
                        target.Values["_multimedia"] = source.Value("purpose") == "Multimedia" ? "1" : "";
                    }
                    else
                    {
                        target.Values["pageTemplate"] = source.Title;
                        target.Values["_pageTemplateId"] = source.Id;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Interlocked.Increment(ref failures) == 1)
                    snap.Note($"'Where used' list failed for {source.Title}: {ex.GetBaseException().Message}");
            }
        });

        if (failures == sources.Count)
        {
            var why = "'Where used' lists are not available on this CMS – open an item to see its " + (components ? "schema." : "Page Template.");
            if (components)
            {
                snap.MarkUnavailable("Components.schema", why); snap.MarkUnavailable("Multimedia.schema", why);
                snap.Note("Without 'where used' lists, multimedia components are only recognised by their list icon; any that are not are counted under Components.");
            }
            else snap.MarkUnavailable("Pages.pageTemplate", why);
        }
        else if (failures > 0) snap.Note($"{failures} of {sources.Count} 'where used' lists failed; some {(components ? "schemas" : "Page Templates")} are missing in the table.");

        if (!components) return;
        // Multimedia: by schema purpose; where the schema is unknown, by the list icon (multimedia icons carry the file extension, e.g. "T16L0P0Mjpg").
        var moved = targets.Where(t => t.Value("_multimedia") == "1"
                                       || (t.Value("_schemaId").Length == 0 && System.Text.RegularExpressions.Regex.IsMatch(t.Value("_icon"), @"M[a-z0-9]+$")))   // VERIFY: icon format
            .ToList();
        foreach (var m in moved)
        {
            targets.Remove(m);
            snap.Items[AuditKind.Multimedia].Add(new AuditRow
            {
                Id = m.Id, Title = m.Title, Kind = AuditKind.Multimedia, Path = m.Path, Origin = m.Origin, FromPublication = m.FromPublication,
                Modified = m.Modified, ModifiedBy = m.ModifiedBy
            }.With(m.Values));
        }
    }

    // ------------------------------------------------------------------ step 6: publish status

    private async Task ReadPublishStatusAsync(AuditSnapshot snap, AuditOptions options, int pubNo, ConcurrentDictionary<string, byte> seen,
        ParallelOptions parallel, IProgress<AuditProgress> progress)
    {
        var items = snap.Items[AuditKind.Components].Concat(snap.Items[AuditKind.Multimedia]).Concat(snap.Items[AuditKind.Pages]).ToList();
        if (items.Count == 0) return;
        var mode = options.PublishInfoMode?.Trim().ToLowerInvariant() ?? "auto";
        var listFlag = seen.ContainsKey("IsPublished");

        if (mode == "off")
        {
            foreach (var i in items) i.Publish = PublishState.Unknown;
            MarkPublishUnavailable(snap, "Publish status is switched off (Audit:PublishInfoMode = Off).");
            return;
        }

        var perItem = mode == "peritem" || (mode == "auto" && items.Count <= options.MaxPublishInfoItems);
        if (perItem)
        {
            var done = 0;
            var failures = 0;
            await Parallel.ForEachAsync(items, parallel, async (item, token) =>
            {
                if (Interlocked.Increment(ref done) % 25 == 1) progress.Report(new AuditProgress(6, $"Publish status {done} of {items.Count}"));
                if (Volatile.Read(ref failures) >= 20) return;   // the call is not working on this CM – stop trying
                try
                {
                    var infos = await _client.GetListPublishInfoAsync(item.Id);                               // VERIFY: GetListPublishInfo(idOrUrl)
                    foreach (var t in Targets(infos, pubNo)) { lock (item.PublishedTo) item.PublishedTo.Add(t); lock (snap.PublishTargets) snap.PublishTargets.Add(t.Target); }
                    item.Publish = item.PublishedTo.Count > 0 ? PublishState.Published : PublishState.Unpublished;
                    // BluePrint: published from a child publication (only this publication's targets are shown in the table,
                    // but "unused" must not flag an item a child site publishes).
                    if (OtherPublications(infos, pubNo).Any()) item.Values["_publishedElsewhere"] = "1";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (Interlocked.Increment(ref failures) == 1) snap.Note("Publish info could not be read per item: " + ex.GetBaseException().Message);
                }
            });
            if (failures < 20 && items.All(i => i.Publish != PublishState.NotApplicable)) return;
            snap.Note("Falling back to the list's published flag for items without publish info.");
        }
        else if (mode == "auto")
            snap.Note($"{items.Count} components and pages – above the per-item limit of {options.MaxPublishInfoItems} (Audit:MaxPublishInfoItems), " +
                      "so publish status comes from the list's published flag (no target breakdown).");

        foreach (var i in items.Where(i => i.Publish == PublishState.NotApplicable))
            i.Publish = i.Value("_listPublished") switch { "1" => PublishState.Published, "0" => PublishState.Unpublished, _ => PublishState.Unknown };
        if (!listFlag && items.Any(i => i.Publish == PublishState.Unknown))
            MarkPublishUnavailable(snap, "This CMS's list data has no published flag and per-item publish info was not read – open an item to see where it is published.");
    }

    private static void MarkPublishUnavailable(AuditSnapshot snap, string why)
    {
        foreach (var k in new[] { AuditKind.Components, AuditKind.Multimedia, AuditKind.Pages }) snap.MarkUnavailable($"{k}.publish", why);
    }

    /// <summary>Publication numbers other than the audited one that the item is published from.</summary>
    private static IEnumerable<int> OtherPublications(IEnumerable? infos, int pubNo)
    {
        if (infos is null) yield break;
        foreach (var info in infos.Cast<object>())
        {
            var repo = Reflect.GetString(Reflect.Get(info, "Repository", "Publication"), "IdRef");             // VERIFY: PublishInfoData.Repository
            if (repo is not null && PublicationNumber(repo) is var n && n > 0 && n != pubNo) yield return n;
        }
    }

    /// <summary>Publish targets from PublishInfoData: target type (9.x+) or publication target (older), only for the audited publication.</summary>
    private static IEnumerable<PublishTarget> Targets(IEnumerable? infos, int pubNo)
    {
        if (infos is null) yield break;
        foreach (var info in infos.Cast<object>())
        {
            var repo = Reflect.GetString(Reflect.Get(info, "Repository", "Publication"), "IdRef");             // VERIFY: PublishInfoData.Repository
            if (repo is not null && PublicationNumber(repo) is var n && n > 0 && n != pubNo) continue;
            var target = Reflect.GetString(Reflect.Get(info, "TargetType", "PublicationTarget"), "Title")    // VERIFY: PublishInfoData.TargetType
                         ?? Reflect.GetString(info, "TargetTypeId", "PublicationTargetId") ?? "Target";
            var at = Reflect.Get(info, "PublishedAt") as DateTime?;                                          // VERIFY: PublishInfoData.PublishedAt
            var user = Reflect.GetString(Reflect.Get(info, "User"), "Title");
            yield return new PublishTarget(target, at, user);
        }
    }

    // ------------------------------------------------------------------ step 7: pages and multimedia

    /// <summary>
    /// Reads each page for its file name and component presentations (native regions on 9.x+ and the classic list). This
    /// gives the page URL, empty pages, duplicate file names, and which components and Component Templates are on pages.
    /// </summary>
    private async Task ReadPagesAsync(AuditSnapshot snap, AuditOptions options, ParallelOptions parallel, IProgress<AuditProgress> progress)
    {
        var pages = snap.Items[AuditKind.Pages];
        if (pages.Count == 0) return;
        if (pages.Count > options.MaxPageReads)
        {
            var why = $"More than {options.MaxPageReads} pages – read limit (Audit:MaxPageReads). Open a page to see it.";
            foreach (var c in new[] { "fileName", "cps" }) snap.MarkUnavailable($"Pages.{c}", why);
            return;
        }
        // Page Template file extensions (".html", ".aspx" …) for the page URL.
        var extension = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pt in snap.Items[AuditKind.PageTemplates])
        {
            try { extension[AuditFinalizer.ItemKey(pt.Id)] = Reflect.GetString(await _client.ReadAsync(pt.Id, ReadOpts), "FileExtension") ?? ""; }   // VERIFY: PageTemplateData.FileExtension
            catch (Exception ex) when (ex is not OperationCanceledException) { /* URL without extension */ }
        }
        var done = 0;
        var failures = 0;
        await Parallel.ForEachAsync(pages, parallel, async (page, token) =>
        {
            if (Interlocked.Increment(ref done) % 10 == 1) progress.Report(new AuditProgress(7, $"Page {done} of {pages.Count}: {page.Title}"));
            try
            {
                var item = await _client.ReadAsync(page.Id, ReadOpts);
                ApplyVersionInfo(page, item);
                var fileName = Reflect.GetString(item, "FileName") ?? "";
                var ptId = Reflect.GetString(Reflect.Get(item, "PageTemplate"), "IdRef") ?? page.Value("_pageTemplateId");
                if (page.Value("pageTemplate").Length == 0 && Reflect.GetString(Reflect.Get(item, "PageTemplate"), "Title") is { } ptTitle)
                {
                    page.Values["pageTemplate"] = ptTitle;
                    page.Values["_pageTemplateId"] = ptId;
                }
                var ext = ptId.Length > 0 ? extension.GetValueOrDefault(AuditFinalizer.ItemKey(ptId)) ?? "" : "";
                page.Values["fileName"] = fileName;
                var sgUrl = page.Value("url").TrimEnd('/');
                if (fileName.Length > 0) page.Values["url"] = $"{sgUrl}/{fileName}{(ext.Length > 0 ? "." + ext.TrimStart('.') : "")}";
                var components = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var templates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var count = 0;
                void Take(IEnumerable? cps)
                {
                    foreach (var cp in cps?.Cast<object>() ?? Enumerable.Empty<object>())
                    {
                        count++;
                        if (Reflect.GetString(Reflect.Get(cp, "Component"), "IdRef") is { } c) components.Add(c);
                        if (Reflect.GetString(Reflect.Get(cp, "ComponentTemplate"), "IdRef") is { } t) templates.Add(t);
                    }
                }
                void Walk(object? regions)
                {
                    foreach (var r in (regions as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                    {
                        Take(Reflect.Get(r, "ComponentPresentations") as IEnumerable);
                        Walk(Reflect.Get(r, "Regions"));
                    }
                }
                Take(Reflect.Get(item, "ComponentPresentations") as IEnumerable);
                Walk(Reflect.Get(item, "Regions"));                                                           // VERIFY: PageData.Regions (9.x+)
                page.Values["cps"] = count.ToString(CultureInfo.InvariantCulture);
                page.Values["_componentIds"] = string.Join(AuditFinalizer.ListSeparator, components);
                page.Values["_ctIds"] = string.Join(AuditFinalizer.ListSeparator, templates);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Interlocked.Increment(ref failures) == 1) snap.Note($"Page '{page.Title}' could not be read: {ex.GetBaseException().Message}");
            }
        });
        if (failures > 0) snap.Note($"{failures} of {pages.Count} pages could not be read; their file name and component presentations are empty.");
        // Page reads give each page's Page Template even where the "where used" lists did not.
        if (failures < pages.Count && pages.All(p => p.Value("pageTemplate").Length > 0 || p.Value("cps").Length == 0))
            snap.Unavailable.Remove("Pages.pageTemplate");
    }

    /// <summary>
    /// Reads every item that list data does not fully describe – in batches of Audit:ItemReadBatchSize with ReadItems where the
    /// Core Service has it, otherwise one by one – for created / modified dates and users, each component's schema and
    /// multimedia type, and each multimedia component's file name, size and type (the binary itself is not downloaded).
    /// Pages already read in the page step are skipped.
    /// </summary>
    private async Task ReadItemDetailsAsync(AuditSnapshot snap, AuditOptions options, ParallelOptions parallel, IProgress<AuditProgress> progress)
    {
        var mmColumns = new[] { "fileName", "mmType", "sizeKb" };
        if (!options.ReadItemDetails)
        {
            foreach (var c in mmColumns) snap.MarkUnavailable($"Multimedia.{c}", "Item details are switched off (Audit:ReadItemDetails).");
            return;
        }
        var rows = snap.Items.Values.SelectMany(r => r).Where(r => r.Kind != AuditKind.Pages || r.CreatedBy is null).ToList();
        if (rows.Count == 0) return;
        if (rows.Count > options.MaxItemDetailReads)
        {
            var why = $"More than {options.MaxItemDetailReads:N0} items – read limit (Audit:MaxItemDetailReads). Open an item to see it.";
            foreach (var c in mmColumns) snap.MarkUnavailable($"Multimedia.{c}", why);
            snap.MarkUnavailable("*.created", why); snap.MarkUnavailable("*.createdBy", why);
            snap.Note($"{rows.Count:N0} items – above the item-details limit of {options.MaxItemDetailReads:N0} (Audit:MaxItemDetailReads), so created dates, users and multimedia file details were not read.");
            return;
        }

        var readItems = FindReadItems();
        var batch = readItems is null ? 1 : Math.Clamp(options.ItemReadBatchSize, 1, 500);
        var chunks = rows.Chunk(batch).ToList();
        var done = 0;
        var failures = 0;
        var batchFailures = 0;
        await Parallel.ForEachAsync(chunks, parallel, async (chunk, token) =>
        {
            var n = Interlocked.Add(ref done, chunk.Length);
            if (n == chunk.Length || n % 250 < chunk.Length) progress.Report(new AuditProgress(7, $"Item details {n:N0} of {rows.Count:N0}"));
            var items = new List<object>();
            if (readItems is not null && chunk.Length > 1 && Volatile.Read(ref batchFailures) < 3)
            {
                try { items.AddRange(await ReadItemsAsync(readItems, chunk.Select(r => r.Id).ToArray())); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (Interlocked.Increment(ref batchFailures) == 1) snap.Note("Reading items in batches failed, reading them one by one: " + ex.GetBaseException().Message);
                }
            }
            var byKey = items.Select(i => (Key: AuditFinalizer.ItemKey(Reflect.GetString(i, "Id") ?? ""), Item: i))
                .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Item, StringComparer.OrdinalIgnoreCase);
            foreach (var row in chunk)
            {
                if (!byKey.TryGetValue(AuditFinalizer.ItemKey(row.Id), out var item))
                {
                    try { item = await _client.ReadAsync(row.Id, ReadOpts); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (Interlocked.Increment(ref failures) == 1) snap.Note($"'{row.Title}' could not be read: {ex.GetBaseException().Message}");
                        continue;
                    }
                }
                ApplyVersionInfo(row, item);
                if (row.Kind is AuditKind.Components or AuditKind.Multimedia) ApplyComponentDetails(row, item);
            }
        });
        if (failures > 0) snap.Note($"{failures:N0} of {rows.Count:N0} items could not be read; their dates, users and file details are empty.");

        // The item tells for sure whether a component is multimedia (list icons and "where used" can miss some).
        var components = snap.Items[AuditKind.Components];
        var moved = components.Where(r => r.Value("_isMultimedia") == "1").ToList();
        if (moved.Count > 0)
        {
            components.RemoveAll(r => r.Value("_isMultimedia") == "1");
            foreach (var r in moved) { var mm = new AuditRow { Id = r.Id, Title = r.Title, Kind = AuditKind.Multimedia, Path = r.Path, Origin = r.Origin, FromPublication = r.FromPublication, Created = r.Created, CreatedBy = r.CreatedBy, Modified = r.Modified, ModifiedBy = r.ModifiedBy, Publish = r.Publish }; foreach (var kv in r.Values) mm.Values[kv.Key] = kv.Value; mm.PublishedTo.AddRange(r.PublishedTo); snap.Items[AuditKind.Multimedia].Add(mm); }
            snap.Note($"{moved.Count} components were multimedia components and are listed under Multimedia.");
        }
        // Schemas came from the items themselves where "where used" did not give them.
        if (components.Concat(snap.Items[AuditKind.Multimedia]).Any(r => r.Value("schema").Length > 0))
        {
            snap.Unavailable.Remove("Components.schema");
            snap.Unavailable.Remove("Multimedia.schema");
        }
        var total = snap.Items[AuditKind.Multimedia].Sum(m => long.TryParse(m.Value("sizeKb"), out var kb) ? kb : 0);
        if (total > 0) snap.Note($"Multimedia binaries: {total / 1024.0:N1} MB in total.");
    }

    /// <summary>ReadItems(string[] ids, ReadOptions) on this Core Service proxy, or null (older proxies: one read per item).</summary>
    private System.Reflection.MethodInfo? FindReadItems() =>
        new[] { _client.GetType() }.Concat(_client.GetType().GetInterfaces())
            .Select(t => t.GetMethod("ReadItemsAsync", new[] { typeof(string[]), typeof(CS.ReadOptions) }))      // VERIFY: ICoreService.ReadItems
            .FirstOrDefault(m => m is not null);

    private async Task<IEnumerable<object>> ReadItemsAsync(System.Reflection.MethodInfo method, string[] ids)
    {
        var task = (Task)method.Invoke(_client, new object[] { ids, ReadOpts })!;
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task) as IEnumerable;
        return result?.Cast<object>().Where(o => o is not null).ToList() ?? new List<object>();
    }

    /// <summary>Created / modified dates and users from the item's VersionInfo (versioned and organisational items).</summary>
    private static void ApplyVersionInfo(AuditRow row, object item)
    {
        var v = Reflect.Get(item, "VersionInfo");                                                             // VERIFY: FullVersionInfo
        if (v is null) return;
        if (Reflect.Get(v, "CreationDate") is DateTime created) row.Created = created;
        row.CreatedBy ??= UserName(Reflect.Get(v, "Creator"));
        if (row.Modified is null && Reflect.Get(v, "RevisionDate", "LastModifiedDate") is DateTime modified) row.Modified = modified;
        row.ModifiedBy ??= UserName(Reflect.Get(v, "Revisor", "LastModifiedBy"));
    }

    private static string? UserName(object? link) =>
        Reflect.GetString(link, "Title") is { Length: > 0 } t ? t : Reflect.GetString(link, "Description");

    /// <summary>Schema, multimedia flag and binary details of a component.</summary>
    private static void ApplyComponentDetails(AuditRow row, object item)
    {
        var schema = Reflect.Get(item, "Schema");                                                             // VERIFY: ComponentData.Schema
        if (row.Value("schema").Length == 0 && Reflect.GetString(schema, "IdRef") is { Length: > 0 } schemaId)
        {
            row.Values["schema"] = Reflect.GetString(schema, "Title") ?? schemaId;
            row.Values["_schemaId"] = schemaId;
        }
        var bin = Reflect.Get(item, "BinaryContent");                                                         // VERIFY: ComponentData.BinaryContent
        var multimedia = string.Equals(Reflect.GetString(item, "ComponentType"), "Multimedia", StringComparison.OrdinalIgnoreCase) || bin is not null;
        if (multimedia && row.Kind == AuditKind.Components) row.Values["_isMultimedia"] = "1";
        if (bin is null) return;
        row.Values["fileName"] = Reflect.GetString(bin, "Filename", "FileName") ?? "";
        row.Values["mmType"] = Reflect.GetString(Reflect.Get(bin, "MultimediaType"), "Title") ?? "";
        if (long.TryParse(Reflect.GetString(bin, "FileSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            row.Values["sizeKb"] = ((size + 1023) / 1024).ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ step 8: deep scan – where used

    /// <summary>
    /// "Where used" for every category (by schemas), keyword, multimedia component and component (by pages, components and
    /// organisational items, in any publication – an item in a master publication used only by child sites still counts).
    /// Kinds that would take the total above Audit:MaxUsageItems are left out and marked.
    /// </summary>
    private async Task ReadUsageAsync(AuditSnapshot snap, AuditOptions options, ParallelOptions parallel, IProgress<AuditProgress> progress)
    {
        var plan = new (AuditKind Kind, string Column, CS.ItemType[] Types)[]
        {
            (AuditKind.Categories, "schemas", new[] { CS.ItemType.Schema }),
            (AuditKind.Keywords, "usedBy", new[] { CS.ItemType.Component, CS.ItemType.Page, CS.ItemType.StructureGroup, CS.ItemType.Folder,
                                                   CS.ItemType.ComponentTemplate, CS.ItemType.PageTemplate, CS.ItemType.Publication }),
            (AuditKind.Multimedia, "usedBy", new[] { CS.ItemType.Component, CS.ItemType.Page, CS.ItemType.Keyword, CS.ItemType.StructureGroup, CS.ItemType.Folder }),
            (AuditKind.Components, "usedBy", new[] { CS.ItemType.Component, CS.ItemType.Page, CS.ItemType.Keyword, CS.ItemType.StructureGroup, CS.ItemType.Folder }),
        };
        var budget = options.MaxUsageItems;
        var pubNo = PublicationNumber(snap.PublicationId);
        // Publication titles, to show which other (child) publications use an item.
        var pubTitles = new Dictionary<int, string>();
        try { foreach (var p in await GetPublicationsAsync()) pubTitles[PublicationNumber(p.Id)] = p.Title; }
        catch (Exception ex) when (ex is not OperationCanceledException) { snap.Note("Publication titles could not be read: " + ex.GetBaseException().Message); }
        foreach (var (kind, column, types) in plan)
        {
            var rows = snap.Items[kind];
            void Unavailable(string why)
            {
                snap.MarkUnavailable($"{kind}.{column}", why);
                if (column == "usedBy") snap.MarkUnavailable($"{kind}.usedIn", why);
            }
            if (rows.Count == 0) continue;
            if (rows.Count > budget)
            {
                Unavailable($"Not read: more items than the deep-scan limit left (Audit:MaxUsageItems = {options.MaxUsageItems}).");
                continue;
            }
            budget -= rows.Count;
            var done = 0;
            var failures = 0;
            await Parallel.ForEachAsync(rows, parallel, async (row, token) =>
            {
                if (Interlocked.Increment(ref done) % 25 == 1) progress.Report(new AuditProgress(8, $"Where used – {AuditCatalog.Title(kind)} {done} of {rows.Count}"));
                if (Volatile.Read(ref failures) >= 20) return;
                try
                {
                    var filter = new CS.UsingItemsFilterData();
                    Reflect.TrySet(filter, "ItemTypes", types);
                    Reflect.TrySet(filter, "BaseColumns", "IdAndTitle");
                    Reflect.TrySet(filter, "IncludedVersions", "OnlyLatestVersions");
                    var users = (await ListXmlAsync(row.Id, filter, null)).Select(e => Attr(e, "ID") ?? "").Where(id => id.Length > 0).Distinct().ToList();
                    row.Values[column] = users.Count.ToString(CultureInfo.InvariantCulture);
                    if (column == "usedBy")
                    {
                        // Using items in other publications (child sites in the BluePrint) still count as "used".
                        var others = users.Select(ItemPublicationNumber).Where(n => n > 0 && n != pubNo).Distinct()
                            .Select(n => pubTitles.TryGetValue(n, out var t) ? t : $"tcm:0-{n}-1").OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
                        row.Values["usedIn"] = string.Join(", ", others);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (Interlocked.Increment(ref failures) == 1) snap.Note($"'Where used' failed for {AuditCatalog.Singular(kind)} '{row.Title}': {ex.GetBaseException().Message}");
                }
            });
            if (failures >= 20) Unavailable("'Where used' lists are not available on this CMS.");
            else if (failures > 0) snap.Note($"{failures} 'where used' lists failed for {AuditCatalog.Title(kind)}; those items are left out of the usage checks.");
        }
    }

    // ------------------------------------------------------------------ detail (pop-up)

    public async Task<AuditItemDetail> ReadAuditDetailAsync(string itemId, AuditKind kind, TemplateFieldOptions fields, CancellationToken ct)
    {
        var item = await _client.ReadAsync(itemId, ReadOpts);
        var d = new AuditItemDetail { Id = item.Id, Title = item.Title, Kind = kind };
        var general = d.Section("General")
            .Add("ID", item.Id)
            .Add("Title", item.Title)
            .Add("Description", Reflect.GetString(item, "Description"))
            .Add("Location", Reflect.GetString(Reflect.Get(item, "LocationInfo"), "Path"))
            .Add("WebDAV URL", Reflect.GetString(Reflect.Get(item, "LocationInfo"), "WebDavUrl"));

        var v = Reflect.Get(item, "VersionInfo");
        d.Section("Versions")
            .Add("Version", Reflect.GetString(v, "Version"))
            .Add("Created", When(Reflect.Get(v, "CreationDate")))
            .Add("Created by", Reflect.GetString(Reflect.Get(v, "Creator"), "Title"))
            .Add("Last modified", When(Reflect.Get(v, "RevisionDate")))
            .Add("Modified by", Reflect.GetString(Reflect.Get(v, "Revisor"), "Title"));

        var bp = Reflect.Get(item, "BluePrintInfo");
        if (bp is not null)
        {
            var shared = Reflect.GetString(bp, "IsShared") == "True";
            var localized = Reflect.GetString(bp, "IsLocalized") == "True";
            d.Section("BluePrint")
                .Add("Status", shared ? "Shared" : localized ? "Localized" : "Local")
                .Add("Owning publication", Reflect.GetString(Reflect.Get(bp, "OwningRepository"), "Title"))
                .Add("Primary parent", Reflect.GetString(Reflect.Get(bp, "PrimaryBluePrintParentItem"), "Title"));
        }

        switch (kind)
        {
            case AuditKind.Schemas:
            {
                var s = await ReadSchemaAsync(itemId);
                d.Section("Schema")
                    .Add("Purpose", s.Schema.Purpose.ToString())
                    .Add("Root element", s.Schema.RootElementName)
                    .Add("Namespace", s.Schema.NamespaceUri)
                    .Add("Allowed multimedia types", string.Join(", ", s.Schema.AllowedMultimediaTypes));
                var fieldsSec = d.Section("Fields");
                foreach (var f in s.Fields.OrderBy(f => f.Section).ThenBy(f => f.Order))
                    fieldsSec.Items.Add($"{f.Section} · {f.XmlName} ({f.Type}{(f.Mandatory ? ", mandatory" : "")}{(f.MaxOccurs != 1 ? ", multi-value" : "")})");
                if (s.RegionRows.Count > 0)
                {
                    var r = d.Section("Region definition");
                    foreach (var row in s.RegionRows) r.Items.Add($"{row.RowType}: {row.NestedRegionName ?? string.Join(", ", row.AllowedComponentSchemas.Concat(row.AllowedComponentTemplates))}");
                }
                break;
            }
            case AuditKind.ComponentTemplates or AuditKind.PageTemplates:
            {
                var t = await ReadTemplateAsync(itemId, fields);
                d.Section("Template")
                    .Add("View", t.View).Add("Controller", t.Controller).Add("Action", t.Action).Add("Route values", t.RouteValues)
                    .Add("HTML classes", t.HtmlClasses).Add("Dynamic", t.Dynamic is null ? null : t.Dynamic.Value ? "Yes" : "No")
                    .Add("Priority", t.Priority).Add("Page schema", t.PageSchema)
                    .Add("Includes", string.Join(", ", t.Includes)).Add("Metadata schema", t.HasMetadataSchema ? "Yes" : "No");
                if (t.LinkedSchemas.Count > 0) d.Section("Linked schemas").Items.AddRange(t.LinkedSchemas);
                break;
            }
            case AuditKind.Components or AuditKind.Multimedia:
            {
                general.Add("Schema", Reflect.GetString(Reflect.Get(item, "Schema"), "Title"));
                var bin = Reflect.Get(item, "BinaryContent");
                if (bin is not null)
                    d.Section("Binary")
                        .Add("File name", Reflect.GetString(bin, "Filename", "FileName"))
                        .Add("Size", long.TryParse(Reflect.GetString(bin, "FileSize"), out var size) ? $"{size / 1024.0:0.#} KB" : null)
                        .Add("Multimedia type", Reflect.GetString(Reflect.Get(bin, "MultimediaType"), "Title"));
                Xml(d, "Content", Reflect.GetString(item, "Content"));
                Xml(d, "Metadata", Reflect.GetString(item, "Metadata"));
                await PublishSectionAsync(d, itemId);
                break;
            }
            case AuditKind.Pages:
            {
                general.Add("File name", Reflect.GetString(item, "FileName"))
                       .Add("Page Template", Reflect.GetString(Reflect.Get(item, "PageTemplate"), "Title"));
                var cps = d.Section("Component presentations");
                void Walk(object? regions, string prefix)
                {
                    if (regions is not IEnumerable list) return;
                    foreach (var r in list)
                    {
                        var name = prefix + (Reflect.GetString(r, "RegionName") ?? "?");
                        foreach (var cp in (Reflect.Get(r, "ComponentPresentations") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                            cps.Items.Add($"{name}: {Cp(cp)}");
                        Walk(Reflect.Get(r, "Regions"), name + "/");
                    }
                }
                Walk(Reflect.Get(item, "Regions"), "");
                foreach (var cp in (Reflect.Get(item, "ComponentPresentations") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                    cps.Items.Add(Cp(cp));
                if (cps.Items.Count == 0) cps.Items.Add("No component presentations.");
                Xml(d, "Metadata", Reflect.GetString(item, "Metadata"));
                await PublishSectionAsync(d, itemId);
                break;
            }
            case AuditKind.StructureGroups:
                d.Section("Structure Group")
                    .Add("Directory", Reflect.GetString(item, "Directory"))
                    .Add("Active", Reflect.GetString(item, "IsActive") is { } a ? YesNo(a) : null)
                    .Add("Default Page Template", Reflect.GetString(Reflect.Get(item, "DefaultPageTemplate", "PageTemplate"), "Title"));
                break;
            case AuditKind.Categories:
            {
                var c = await ReadCategoryAsync(itemId);
                d.Section("Category")
                    .Add("XML name", c.XmlName).Add("Publishable", c.Publishable ? "Yes" : "No")
                    .Add("Use for identification", c.UseForIdentification ? "Yes" : "No")
                    .Add("Keyword metadata schema", c.KeywordMetadataSchema);
                var kw = await GetKeywordsAsync(itemId);
                var sec = d.Section($"Keywords ({kw.Count})");
                sec.Items.AddRange(kw.Select(k => k.Title).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(500));
                if (kw.Count > 500) sec.Items.Add($"… and {kw.Count - 500} more (see the Keywords table).");
                break;
            }
            case AuditKind.Keywords:
                d.Section("Keyword")
                    .Add("Key", Reflect.GetString(item, "Key"))
                    .Add("Abstract", Reflect.GetString(item, "IsAbstract") is { } ab ? YesNo(ab) : null)
                    .Add("Category", Reflect.GetString(Reflect.Get(item, "Category"), "Title"))
                    .Add("Parent keywords", string.Join(", ", ((Reflect.Get(item, "ParentKeywords") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                        .Select(p => Reflect.GetString(p, "Title"))));
                Xml(d, "Metadata", Reflect.GetString(item, "Metadata"));
                break;
        }
        d.Sections.RemoveAll(s => s.Rows.Count == 0 && s.Items.Count == 0 && s.Xml is null);
        return d;
    }

    private async Task PublishSectionAsync(AuditItemDetail d, string itemId)
    {
        var sec = d.Section("Publishing");
        try
        {
            var targets = Targets(await _client.GetListPublishInfoAsync(itemId), ItemPublicationNumber(itemId)).ToList();
            if (targets.Count == 0) sec.Items.Add("Not published.");
            foreach (var t in targets) sec.Items.Add($"{t.Target}{(t.PublishedAt is { } at ? $" · {at:yyyy-MM-dd HH:mm}" : "")}{(t.User is { Length: > 0 } u ? $" · {u}" : "")}");
        }
        catch (Exception ex) { sec.Items.Add("Publish info could not be read: " + ex.GetBaseException().Message); }
    }

    private static string Cp(object cp) =>
        $"{Reflect.GetString(Reflect.Get(cp, "Component"), "Title") ?? "?"} — {Reflect.GetString(Reflect.Get(cp, "ComponentTemplate"), "Title") ?? "?"}";

    private static void Xml(AuditItemDetail d, string title, string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return;
        string pretty;
        try { pretty = XElement.Parse(xml).ToString(); } catch { pretty = xml; }
        d.Section(title).Xml = pretty.Length > 20000 ? pretty[..20000] + "\n…(truncated)" : pretty;
    }

    private static string? When(object? v) => v is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : Reflect.Format(v);
}

internal static class AuditRowExtensions
{
    public static AuditRow With(this AuditRow row, Dictionary<string, string> values)
    {
        foreach (var (k, v) in values) row.Values[k] = v;
        return row;
    }
}
#endif
