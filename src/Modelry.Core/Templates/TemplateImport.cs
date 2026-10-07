using Modelry.Core.Excel;
using Modelry.Core.Gateway;
using Modelry.Core.Import;
using Modelry.Core.Model;

namespace Modelry.Core.Templates;

public static class TemplateSteps
{
    public static readonly string[] Names =
    {
        "Preparing – reading workbook and loading publication snapshot",
        "Creating folders",
        "Creating Component Templates",
        "Creating Page Templates",
        "Applying template constraints to region schemas",
    };
}

/// <summary>Choices made in the UI for a template import.</summary>
public sealed class TemplateImportOptions
{
    public string? BaseComponentTemplateId { get; init; }
    public string? BasePageTemplateId { get; init; }
    /// <summary>Includes for Page Template rows that do not list their own (empty = keep the base template's includes).</summary>
    public List<string> DefaultIncludes { get; init; } = new();
    public bool ApplyRegionConstraints { get; init; }
    public required TemplateFieldOptions Fields { get; init; }
}

public sealed record TemplateLocation(string Id, string Title, TemplateKind Kind, string FolderPath);

/// <summary>Snapshot of the target publication for template planning.</summary>
public sealed class TemplateContext
{
    public required FolderInfo Target { get; init; }
    public required string TargetPathFromRoot { get; init; }
    public Dictionary<string, string> FolderIdsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SchemaLocation> ExistingSchemas { get; } = new();
    public List<TemplateLocation> ExistingTemplates { get; } = new();
    /// <summary>All templates visible in the publication (incl. inherited) – for base / region lookups.</summary>
    public List<TemplateSummary> PublicationTemplates { get; } = new();
    public Dictionary<string, TemplateDetails> Bases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SchemaDetails> RegionSchemas { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string FullPath(string relativePath) =>
        relativePath == "." ? TargetPathFromRoot : FolderTraversal.Combine(TargetPathFromRoot, relativePath);

    public static async Task<TemplateContext> LoadAsync(ITridionGateway gw, string targetFolderId, IaWorkbook wb, TemplateImportOptions options,
        IProgress<string>? activity = null)
    {
        activity?.Report("Opening the selected folder");
        var target = await gw.GetFolderAsync(targetFolderId);
        var root = await gw.GetPublicationRootFolderAsync(target.PublicationId);
        var ctx = new TemplateContext { Target = target, TargetPathFromRoot = target.PathFromRoot };
        if (target.PathFromRoot != ".")
        {
            ctx.FolderIdsByPath["."] = root.Id;
            var parts = target.PathFromRoot.Split('/');
            for (var i = 1; i < parts.Length; i++) ctx.FolderIdsByPath[string.Join("/", parts.Take(i))] = "";   // ancestors exist
        }

        // target subtree only: folders + existing templates (create vs skip)
        activity?.Report($"Reading folders below {target.Title}");
        var subtree = await FolderTraversal.GetFoldersAsync(gw, target.Id, target.Title, recursive: true,
            n => activity?.Report($"Reading folders below {target.Title} – {n} folders so far"));
        for (var i = 0; i < subtree.Count; i++)
        {
            var f = subtree[i];
            var path = ctx.FullPath(f.RelativePath);
            ctx.FolderIdsByPath[path] = f.Id;
            foreach (var t in await gw.GetTemplatesInFolderAsync(f.Id))
                ctx.ExistingTemplates.Add(new TemplateLocation(t.Id, t.Title, t.Kind, path));
            activity?.Report($"Looking for existing templates in the target folder – folder {i + 1} of {subtree.Count}, {ctx.ExistingTemplates.Count} found");
        }

        // schemas: one publication-wide list, then type + folder only for those the templates refer to
        activity?.Report("Listing all schemas in the publication");
        var all = await gw.ListPublicationSchemasAsync(target.PublicationId);
        var referenced = ImportContext.ReferencedTitles(wb);
        var hits = all.Where(s => referenced.Contains(s.Title)).ToList();
        for (var i = 0; i < hits.Count; i++)
        {
            activity?.Report($"Reading referenced schemas – {i + 1} of {hits.Count}: {hits[i].Title}");
            var info = await gw.ReadSchemaInfoAsync(hits[i].Id);
            ctx.ExistingSchemas.Add(new SchemaLocation(hits[i].Id, hits[i].Title, info.Purpose, "", info.PathFromRoot));
        }
        foreach (var s in all.Where(s => !referenced.Contains(s.Title)))
            ctx.ExistingSchemas.Add(new SchemaLocation(s.Id, s.Title, "", "", "?"));

        activity?.Report("Reading all Component and Page Templates in the publication");
        ctx.PublicationTemplates.AddRange(await gw.GetTemplatesAsync(target.PublicationId, TemplateKind.Component));
        ctx.PublicationTemplates.AddRange(await gw.GetTemplatesAsync(target.PublicationId, TemplateKind.Page));

        // base templates: UI choices + per-row overrides
        var baseRefs = new List<string>();
        if (options.BaseComponentTemplateId is not null) baseRefs.Add(options.BaseComponentTemplateId);
        if (options.BasePageTemplateId is not null) baseRefs.Add(options.BasePageTemplateId);
        foreach (var t in wb.Templates.Where(x => x.BaseTemplate is not null))
        {
            var kind = t.Kind == IaTemplateKind.ComponentTemplate ? TemplateKind.Component : TemplateKind.Page;
            if (ctx.ResolveTemplate(t.BaseTemplate!, kind) is { Count: 1 } hit) baseRefs.Add(hit[0].Id);
        }
        foreach (var id in baseRefs.Distinct())
        {
            activity?.Report($"Reading base template {id}");
            try { ctx.Bases[id] = await gw.ReadTemplateAsync(id, options.Fields); } catch { /* reported by the planner */ }
        }

        // region schemas whose template constraints may be applied
        if (options.ApplyRegionConstraints)
        {
            var resolver = new SchemaResolver(Array.Empty<SchemaPlan>(), ctx.ExistingSchemas);
            foreach (var title in wb.Regions.Where(r => r.RowType == IaRegionRowType.Constraint && r.AllowedComponentTemplates.Count > 0)
                                            .Select(r => r.RegionSchemaTitle).Distinct(StringComparer.OrdinalIgnoreCase))
                if (resolver.Resolve(title) is { Kind: ResolutionKind.Existing, Existing: { } s })
                {
                    activity?.Report($"Reading region schema '{title}'");
                    ctx.RegionSchemas[title] = await gw.ReadSchemaAsync(s.Id);
                }
        }
        return ctx;
    }

    /// <summary>Finds templates by TCM URI or title among all templates visible in the publication.</summary>
    public List<TemplateSummary> ResolveTemplate(string reference, TemplateKind kind)
    {
        var r = reference.Trim();
        if (r.StartsWith("tcm:", StringComparison.OrdinalIgnoreCase))
            return PublicationTemplates.Where(t => t.Kind == kind && t.Id.Equals(r, StringComparison.OrdinalIgnoreCase)).ToList();
        return PublicationTemplates.Where(t => t.Kind == kind && IaWorkbook.Same(t.Title, r)).ToList();
    }
}

public sealed class TemplatePlanItem
{
    public required IaTemplate Template { get; init; }
    public required TemplateKind Kind { get; init; }
    public required string FolderPath { get; init; }
    public PlanAction Action { get; set; }
    public string? ExistingId { get; set; }
    public string? BaseTemplateId { get; set; }
    public string? BaseTemplateTitle { get; set; }
    public string Label => $"{(Kind == TemplateKind.Component ? "CT" : "PT")}: {FolderPath}/{Template.Title}";
}

public sealed class RegionConstraintPlan
{
    public required string RegionSchemaTitle { get; init; }
    public string? RegionSchemaId { get; set; }
    public List<string> TemplateTitles { get; } = new();
    public bool Apply { get; set; }
}

public sealed class TemplatePlan
{
    public List<Issue> Issues { get; } = new();
    public List<string> FoldersToCreate { get; } = new();
    public List<TemplatePlanItem> Items { get; } = new();
    public List<RegionConstraintPlan> RegionConstraints { get; } = new();
    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);
}

public static class TemplatePlanner
{
    private static readonly string[] Priorities = { "High", "Medium", "Low", "Never Link" };

    public static TemplatePlan Build(IaWorkbook wb, IEnumerable<Issue> readIssues, TemplateContext ctx, TemplateImportOptions options)
    {
        var plan = new TemplatePlan();
        plan.Issues.AddRange(readIssues.Where(i => i.Sheet == IaFormat.TemplatesSheet || i.Level != IssueLevel.Error));
        const string sheet = IaFormat.TemplatesSheet;
        void Err(int row, string item, string msg) => plan.Issues.Add(new Issue(IssueLevel.Error, sheet, row, item, msg));
        void Warn(int row, string item, string msg) => plan.Issues.Add(new Issue(IssueLevel.Warning, sheet, row, item, msg));
        void Info(string s, int row, string item, string msg) => plan.Issues.Add(new Issue(IssueLevel.Info, s, row, item, msg));

        if (wb.Templates.Count == 0) Err(0, "Templates", "The workbook has no rows in the 'Templates' sheet.");

        TemplateDetails? Base(string? id, TemplateKind kind, int row, string item, string what)
        {
            if (id is null) { Err(row, item, $"No base {what} selected."); return null; }
            if (!ctx.Bases.TryGetValue(id, out var b)) { Err(row, item, $"Base {what} {id} could not be read."); return null; }
            if (b.Kind != kind) { Err(row, item, $"'{b.Title}' is not a {what}."); return null; }
            return b;
        }

        var resolver = new SchemaResolver(Array.Empty<SchemaPlan>(), ctx.ExistingSchemas);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in wb.Templates.OrderBy(x => x.Kind))
        {
            var kind = t.Kind == IaTemplateKind.ComponentTemplate ? TemplateKind.Component : TemplateKind.Page;
            var path = ctx.FullPath(t.RelativePath);
            var item = $"{path}/{t.Title}";
            if (!seen.Add(kind + "|" + path + "|" + t.Title)) { Err(t.Row, item, "Duplicate template (same type, title and folder)."); continue; }
            var existing = ctx.ExistingTemplates.FirstOrDefault(e => e.Kind == kind && IaWorkbook.Same(e.Title, t.Title) && IaWorkbook.Same(e.FolderPath, path));
            var pi = new TemplatePlanItem { Template = t, Kind = kind, FolderPath = path, Action = existing is null ? PlanAction.Create : PlanAction.Skip, ExistingId = existing?.Id };
            plan.Items.Add(pi);
            if (existing is not null) { Warn(t.Row, item, $"Already exists ({existing.Id}) – will be skipped."); continue; }
            AddFolders(path, ctx, plan);

            // base template
            string? baseId = kind == TemplateKind.Component ? options.BaseComponentTemplateId : options.BasePageTemplateId;
            if (t.BaseTemplate is not null)
            {
                var hits = ctx.ResolveTemplate(t.BaseTemplate, kind);
                if (hits.Count != 1) { Err(t.Row, item, $"Base template '{t.BaseTemplate}' is {(hits.Count == 0 ? "not found" : "ambiguous – use its TCM URI")}."); continue; }
                baseId = hits[0].Id;
            }
            var b = Base(baseId, kind, t.Row, item, kind == TemplateKind.Component ? "Component Template" : "Page Template");
            if (b is null) continue;
            pi.BaseTemplateId = b.Id; pi.BaseTemplateTitle = b.Title;

            // metadata fields on the base template's metadata schema
            var viewField = kind == TemplateKind.Component ? options.Fields.CtViewField : options.Fields.PtViewField;
            if (t.View is not null && (!b.HasMetadataSchema || !b.MetadataFields.Contains(viewField)))
                Err(t.Row, item, $"Base template '{b.Title}' has no '{viewField}' metadata field – the DXA view cannot be set. Choose a DXA base template or adjust Templates:{(kind == TemplateKind.Component ? "CtViewField" : "PtViewField")}.");
            if (t.View is null) Warn(t.Row, item, "No DXA view – the base template's view is kept.");

            if (kind == TemplateKind.Component)
            {
                if (t.Dynamic is not null && options.Fields.CtDynamicField.Length > 0 && !b.MetadataFields.Contains(options.Fields.CtDynamicField))
                    Warn(t.Row, item, $"Base template has no '{options.Fields.CtDynamicField}' field – only the template's dynamic flag is set.");
                foreach (var (value, field, label) in new[] { (t.Controller, options.Fields.CtControllerField, "Controller"), (t.Action, options.Fields.CtActionField, "Action"),
                                                              (t.RouteValues, options.Fields.CtRouteValuesField, "Route Values"), (t.HtmlClasses, options.Fields.CtHtmlClassesField, "HTML Classes") })
                    if (value is not null && !b.MetadataFields.Contains(field))
                        Err(t.Row, item, $"Base template '{b.Title}' has no '{field}' metadata field – {label} cannot be set.");
                if (t.Action is not null && t.Controller is null)
                    Warn(t.Row, item, "Action is set without a Controller – it applies to the base template's controller (default Entity).");
                if (t.Priority is not null && !Priorities.Contains(t.Priority, StringComparer.OrdinalIgnoreCase))
                    Err(t.Row, item, $"Priority must be one of {string.Join(", ", Priorities)}.");
                if (t.LinkedSchemas.Count == 0) Warn(t.Row, item, "No linked schemas – the template will not be limited to specific schemas.");
                foreach (var s in t.LinkedSchemas)
                {
                    var r = resolver.Resolve(s);
                    if (r.Kind != ResolutionKind.Existing) Err(t.Row, item, $"Linked schema '{s}' is {(r.Kind == ResolutionKind.Ambiguous ? "ambiguous – use a path" : "not found in this publication (import the schemas first)")}.");
                    else if (r.Purpose is not ("Component" or "Multimedia")) Warn(t.Row, item, $"Linked schema '{s}' is a {r.Purpose} schema.");
                }
                if (t.PageSchema is not null || t.Includes.Count > 0 || t.NoIncludes) Warn(t.Row, item, "Page Schema / Includes are ignored for Component Templates.");
            }
            else
            {
                if (t.PageSchema is not null)
                {
                    var r = resolver.Resolve(t.PageSchema);
                    if (r.Kind != ResolutionKind.Existing) Err(t.Row, item, $"Page schema '{t.PageSchema}' is {(r.Kind == ResolutionKind.Ambiguous ? "ambiguous – use a path" : "not found in this publication")}.");
                    else if (r.Purpose != "Region") Err(t.Row, item, $"Page schema '{t.PageSchema}' is not a Region schema.");
                }
                var includesWanted = t.NoIncludes || t.Includes.Count > 0 || options.DefaultIncludes.Count > 0;
                if (includesWanted && !b.MetadataFields.Contains(options.Fields.PtIncludesField))
                    Err(t.Row, item, $"Base template '{b.Title}' has no '{options.Fields.PtIncludesField}' metadata field – includes cannot be set.");
                if (t.LinkedSchemas.Count > 0 || t.Dynamic is not null || t.Priority is not null)
                    Warn(t.Row, item, "Linked Schemas / Dynamic / Priority are ignored for Page Templates.");
                if (t.Controller is not null || t.Action is not null || t.RouteValues is not null || t.HtmlClasses is not null)
                    Warn(t.Row, item, "Controller / Action / Route Values / HTML Classes are only applied to Component Templates.");
            }
        }

        // region constraints
        var constraintRows = wb.Regions.Where(r => r.RowType == IaRegionRowType.Constraint && r.AllowedComponentTemplates.Count > 0).ToList();
        if (!options.ApplyRegionConstraints)
        {
            if (constraintRows.Count > 0)
                Info(IaFormat.RegionsSheet, 0, "Region constraints", "Region Definitions list allowed Component Templates – not applied (option not selected).");
        }
        else
        {
            foreach (var g in constraintRows.GroupBy(r => r.RegionSchemaTitle, StringComparer.OrdinalIgnoreCase))
            {
                var rc = new RegionConstraintPlan { RegionSchemaTitle = g.Key };
                plan.RegionConstraints.Add(rc);
                var row = g.First().Row;
                if (!ctx.RegionSchemas.TryGetValue(g.Key, out var schema))
                { Plan(IssueLevel.Warning, row, g.Key, "Region schema not found in this publication – skipped."); continue; }
                if (schema.Schema.Purpose != IaSchemaPurpose.Region)
                { Plan(IssueLevel.Warning, row, g.Key, "Not a Region schema – skipped."); continue; }
                if (!string.Equals(schema.Schema.BluePrintStatus, "Local", StringComparison.OrdinalIgnoreCase))
                {
                    Plan(IssueLevel.Warning, row, g.Key, $"Region schema is {schema.Schema.BluePrintStatus?.ToLower()} here (owned by '{schema.Schema.OwningPublication}'). " +
                        "Constraints must be set in the owning publication, which must also see these templates – skipped.");
                    continue;
                }
                rc.RegionSchemaId = schema.Schema.SourceId;
                foreach (var title in g.SelectMany(r => r.AllowedComponentTemplates).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var inPlan = plan.Items.Any(p => p.Kind == TemplateKind.Component && IaWorkbook.Same(p.Template.Title, title));
                    var exists = ctx.ResolveTemplate(title, TemplateKind.Component).Count > 0;
                    if (inPlan || exists) rc.TemplateTitles.Add(title);
                    else Plan(IssueLevel.Warning, row, g.Key, $"Component Template '{title}' not found or planned – left out.");
                }
                rc.Apply = rc.TemplateTitles.Count > 0;
                if (rc.Apply) Plan(IssueLevel.Info, row, g.Key, $"Will be limited to {rc.TemplateTitles.Count} Component Template(s); its allowed-schema constraints are replaced (the templates' linked schemas apply).");
            }
        }
        void Plan(IssueLevel l, int row, string itemName, string msg) => plan.Issues.Add(new Issue(l, IaFormat.RegionsSheet, row, itemName, msg));

        plan.Issues.Insert(0, new Issue(IssueLevel.Info, "Plan", 0, "Summary",
            $"Folders to create: {plan.FoldersToCreate.Count}; Component Templates to create: {plan.Items.Count(i => i.Kind == TemplateKind.Component && i.Action == PlanAction.Create)}; " +
            $"Page Templates to create: {plan.Items.Count(i => i.Kind == TemplateKind.Page && i.Action == PlanAction.Create)}; " +
            $"skipped (exist): {plan.Items.Count(i => i.Action == PlanAction.Skip)}; region schemas to constrain: {plan.RegionConstraints.Count(r => r.Apply)}."));
        return plan;
    }

    private static void AddFolders(string path, TemplateContext ctx, TemplatePlan plan)
    {
        if (path == ".") return;
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            var p = string.Join("/", parts.Take(i));
            if (!ctx.FolderIdsByPath.ContainsKey(p) && !plan.FoldersToCreate.Contains(p, StringComparer.OrdinalIgnoreCase))
                plan.FoldersToCreate.Add(p);
        }
    }
}

/// <summary>Executes a template plan: folders → Component Templates → Page Templates → region constraints.</summary>
public sealed class TemplateExecutor
{
    private readonly ITridionGateway _gw;
    public TemplateExecutor(ITridionGateway gw) => _gw = gw;

    public async Task<ImportResult> ExecuteAsync(TemplatePlan plan, TemplateContext ctx, TemplateImportOptions options,
        CancellationToken ct = default, IProgress<ImportProgress>? progress = null)
    {
        var result = new ImportResult();
        var creates = plan.Items.Where(i => i.Action == PlanAction.Create).ToList();
        var tracker = new ProgressTracker(progress, plan.FoldersToCreate.Count + creates.Count + plan.RegionConstraints.Count(r => r.Apply), TemplateSteps.Names);
        void Log(IssueLevel lvl, string step, string item, string msg, string? id = null)
        {
            var e = new LogEntry(DateTime.UtcNow, lvl, step, item, msg, id);
            result.Log.Add(e); tracker.Entry(e);
        }
        if (plan.HasErrors) { Log(IssueLevel.Error, "Validate", "Plan", "Plan has errors – nothing executed."); return result; }

        var resolver = new SchemaResolver(Array.Empty<SchemaPlan>(), ctx.ExistingSchemas);
        var folderIds = new Dictionary<string, string>(ctx.FolderIdsByPath, StringComparer.OrdinalIgnoreCase);
        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // CT title -> id
        foreach (var s in plan.Items.Where(i => i.Action == PlanAction.Skip)) { result.Skipped++; if (s.Kind == TemplateKind.Component) created.TryAdd(s.Template.Title, s.ExistingId!); }

        try
        {
            tracker.Step(2, plan.FoldersToCreate.Count);
            foreach (var path in plan.FoldersToCreate.OrderBy(p => p.Count(c => c == '/')))
            {
                ct.ThrowIfCancellationRequested();
                tracker.Item(path);
                var idx = path.LastIndexOf('/');
                var parent = idx < 0 ? "." : path[..idx];
                if (!folderIds.TryGetValue(parent, out var pid)) { Log(IssueLevel.Error, "Folders", path, "Parent folder missing."); continue; }
                try
                {
                    var node = await _gw.CreateFolderAsync(pid, idx < 0 ? path : path[(idx + 1)..]);
                    folderIds[path] = node.Id;
                    Log(IssueLevel.Info, "Folders", path, "Folder created.", node.Id);
                }
                catch (Exception ex)
                {
                    string? existing = null;
                    try { existing = (await _gw.GetSubFoldersAsync(pid)).FirstOrDefault(n => IaWorkbook.Same(n.Title, idx < 0 ? path : path[(idx + 1)..]))?.Id; }
                    catch { /* report the original error */ }
                    if (existing is not null) { folderIds[path] = existing; Log(IssueLevel.Warning, "Folders", path, "Already exists – created by someone else after the check.", existing); }
                    else Log(IssueLevel.Error, "Folders", path, ex.Message);
                }
            }
            tracker.EndStep();

            foreach (var (step, kind) in new[] { (3, TemplateKind.Component), (4, TemplateKind.Page) })
            {
                var items = creates.Where(i => i.Kind == kind).ToList();
                tracker.Step(step, items.Count);
                foreach (var pi in items)
                {
                    ct.ThrowIfCancellationRequested();
                    tracker.Item(pi.Label);
                    var stepName = kind == TemplateKind.Component ? "Component Templates" : "Page Templates";
                    if (!folderIds.TryGetValue(pi.FolderPath, out var folderId)) { result.Failed++; Log(IssueLevel.Error, stepName, pi.Label, "Folder not available."); continue; }
                    var t = pi.Template;
                    List<string>? includes = null;
                    if (kind == TemplateKind.Page)
                        includes = t.NoIncludes ? new List<string>() : t.Includes.Count > 0 ? t.Includes : options.DefaultIncludes.Count > 0 ? options.DefaultIncludes : null;
                    var model = new TemplateWriteModel
                    {
                        Kind = kind, BaseTemplateId = pi.BaseTemplateId!, Title = t.Title, Description = t.Description, View = t.View,
                        Controller = kind == TemplateKind.Component ? t.Controller : null, Action = kind == TemplateKind.Component ? t.Action : null,
                        RouteValues = kind == TemplateKind.Component ? t.RouteValues : null, HtmlClasses = kind == TemplateKind.Component ? t.HtmlClasses : null,
                        Dynamic = kind == TemplateKind.Component ? t.Dynamic : null, Priority = kind == TemplateKind.Component ? t.Priority : null,
                        LinkedSchemaIds = kind == TemplateKind.Component
                            ? t.LinkedSchemas.Select(s => resolver.Resolve(s).Existing?.Id).Where(id => id is not null).Select(id => id!).ToList()
                            : new List<string>(),
                        PageSchemaId = kind == TemplateKind.Page && t.PageSchema is not null ? resolver.Resolve(t.PageSchema).Existing?.Id : null,
                        Includes = includes, Fields = options.Fields
                    };
                    try
                    {
                        var id = await _gw.CreateTemplateAsync(folderId, model);
                        result.Created++;
                        if (kind == TemplateKind.Component) created[t.Title] = id;
                        Log(IssueLevel.Info, stepName, pi.Label,
                            $"Created from '{pi.BaseTemplateTitle}'{(t.View is null ? "" : $", view {t.View}")}" +
                            (kind == TemplateKind.Component ? $", {model.LinkedSchemaIds.Count} linked schema(s){(t.Dynamic == true ? ", dynamic" : "")}." :
                             $"{(model.PageSchemaId is null ? "" : ", page schema set")}{(includes is null ? "" : $", includes: {(includes.Count == 0 ? "none" : string.Join(", ", includes))}")}."), id);
                    }
                    catch (Exception ex)
                    {
                        // Created by someone else after the check? Then skip it instead of failing.
                        string? existing = null;
                        try
                        {
                            existing = (await _gw.GetTemplatesInFolderAsync(folderId))
                                .FirstOrDefault(x => x.Kind == kind && IaWorkbook.Same(x.Title, t.Title))?.Id;
                        }
                        catch { /* report the original error */ }
                        if (existing is not null)
                        {
                            result.Skipped++;
                            if (kind == TemplateKind.Component) created[t.Title] = existing;
                            Log(IssueLevel.Warning, stepName, pi.Label, "Already exists – created by someone else after the check. Skipped – it was not changed.", existing);
                        }
                        else { result.Failed++; Log(IssueLevel.Error, stepName, pi.Label, ex.Message); }
                    }
                }
                tracker.EndStep();
            }

            var regions = plan.RegionConstraints.Where(r => r.Apply).ToList();
            tracker.Step(5, regions.Count);
            foreach (var rc in regions)
            {
                ct.ThrowIfCancellationRequested();
                tracker.Item(rc.RegionSchemaTitle);
                var ids = new List<string>();
                foreach (var title in rc.TemplateTitles)
                {
                    if (created.TryGetValue(title, out var id)) ids.Add(id);
                    else if (ctx.ResolveTemplate(title, TemplateKind.Component) is { Count: 1 } hit) ids.Add(hit[0].Id);
                    else Log(IssueLevel.Warning, "Region constraints", rc.RegionSchemaTitle, $"'{title}' not available – left out.");
                }
                if (ids.Count == 0) { Log(IssueLevel.Warning, "Region constraints", rc.RegionSchemaTitle, "No templates available – unchanged."); continue; }
                try
                {
                    await _gw.SetRegionTemplateConstraintsAsync(rc.RegionSchemaId!, ids, options.Fields.CheckInComment);
                    Log(IssueLevel.Info, "Region constraints", rc.RegionSchemaTitle, $"Limited to {ids.Count} Component Template(s).", rc.RegionSchemaId);
                }
                catch (Exception ex) { Log(IssueLevel.Error, "Region constraints", rc.RegionSchemaTitle, ex.Message, rc.RegionSchemaId); }
            }
            tracker.EndStep();
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            Log(IssueLevel.Warning, "Cancelled", "Import", "Cancelled by user – templates created before cancellation remain in Tridion.");
        }
        Log(IssueLevel.Info, "Done", "Summary", $"Created {result.Created}, skipped {result.Skipped}, failed {result.Failed} template(s){(result.Cancelled ? " – cancelled" : "")}.");
        return result;
    }
}

public sealed record TemplateExportResult(byte[] Content, string FileName, int Count, List<string> Warnings);

/// <summary>Exports the Component and Page Templates under a folder to a Templates sheet.</summary>
public sealed class TemplateExportService
{
    private readonly ITridionGateway _gw;
    public TemplateExportService(ITridionGateway gw) => _gw = gw;

    public async Task<TemplateExportResult> ExportAsync(string folderId, bool recursive, TemplateFieldOptions fields, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var folder = await _gw.GetFolderAsync(folderId);
        var folders = await FolderTraversal.GetFoldersAsync(_gw, folderId, folder.Title, recursive);
        var wb = new IaWorkbook();
        foreach (var f in folders)
            foreach (var t in await _gw.GetTemplatesInFolderAsync(f.Id))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var d = await _gw.ReadTemplateAsync(t.Id, fields);
                    wb.Templates.Add(new IaTemplate
                    {
                        Kind = d.Kind == TemplateKind.Component ? IaTemplateKind.ComponentTemplate : IaTemplateKind.PageTemplate,
                        Title = d.Title, RelativePath = f.RelativePath, View = d.View, Controller = d.Controller, Action = d.Action,
                        RouteValues = d.RouteValues, HtmlClasses = d.HtmlClasses, LinkedSchemas = d.LinkedSchemas, PageSchema = d.PageSchema,
                        Dynamic = d.Dynamic, Priority = d.Priority, Includes = d.Includes, NoIncludes = d.Kind == TemplateKind.Page && d.Includes.Count == 0,
                        Description = d.Description, SourceId = d.Id
                    });
                }
                catch (Exception ex) { warnings.Add($"{t.Title} ({t.Id}): {ex.Message}"); }
            }
        var source = $"{folder.PublicationTitle} / {folder.Title} ({folder.Id}){(recursive ? " incl. subfolders" : "")} – templates";
        var bytes = IaExcelWriter.WriteTemplates(wb, source);
        var safe = string.Concat(folder.Title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Replace(' ', '_');
        return new TemplateExportResult(bytes, $"IA_Templates_{safe}_{DateTime.UtcNow:yyyyMMdd_HHmm}.xlsx", wb.Templates.Count, warnings);
    }
}
