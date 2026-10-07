using Modelry.Core.Excel;
using Modelry.Core.Gateway;
using Modelry.Core.Model;

namespace Modelry.Core.Import;

/// <summary>
/// Validates an IA workbook against the target publication and produces an ordered plan.
/// Used for the dry run and again (fresh snapshot) right before execution.
/// </summary>
public static class ImportPlanner
{
    private static readonly IaSchemaPurpose[] PurposeOrder =
    {
        IaSchemaPurpose.Embedded, IaSchemaPurpose.Multimedia, IaSchemaPurpose.Metadata, IaSchemaPurpose.Component,
        IaSchemaPurpose.TemplateParameters, IaSchemaPurpose.Bundle, IaSchemaPurpose.Region
    };

    public static ImportPlan Build(IaWorkbook wb, IEnumerable<Issue> readIssues, ImportContext ctx)
    {
        var plan = new ImportPlan();
        plan.Issues.AddRange(readIssues);
        void Err(string sheet, int row, string item, string msg) => plan.Issues.Add(new Issue(IssueLevel.Error, sheet, row, item, msg));
        void Warn(string sheet, int row, string item, string msg) => plan.Issues.Add(new Issue(IssueLevel.Warning, sheet, row, item, msg));
        void Info(string sheet, int row, string item, string msg) => plan.Issues.Add(new Issue(IssueLevel.Info, sheet, row, item, msg));

        // ---------------- schemas & folders
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaPlans = new List<SchemaPlan>();
        foreach (var s in wb.Schemas)
        {
            var path = ctx.FullPath(s.RelativePath);
            if (!seen.Add(path + "|" + s.Title)) { Err(IaFormat.SchemasSheet, s.Row, s.Title, "Duplicate schema (same title and folder)."); continue; }
            if (s.Purpose == IaSchemaPurpose.Embedded && s.NamespaceUri is not null)
            {
                Info(IaFormat.SchemasSheet, s.Row, s.Title, "Namespace URI is ignored – Tridion requires embedded schemas to have no namespace.");
                s.NamespaceUri = null;
            }
            if (s.RootElementName is not null && !IaVocabulary.IsValidXmlName(s.RootElementName))
                Err(IaFormat.SchemasSheet, s.Row, s.Title, $"Invalid XML root element name '{s.RootElementName}'.");
            var existing = ctx.ExistingSchemas.FirstOrDefault(e => IaWorkbook.Same(e.Title, s.Title) && IaWorkbook.Same(e.RelativePath, path));
            var sp = new SchemaPlan { Schema = s, FolderPath = path, Action = existing is null ? PlanAction.Create : PlanAction.Skip, ExistingId = existing?.Id };
            if (existing is not null) Warn(IaFormat.SchemasSheet, s.Row, s.Title, $"Already exists in '{path}' ({existing.Id}) – will be skipped.");
            schemaPlans.Add(sp);
            if (sp.Action == PlanAction.Create) AddFolders(path, ctx, plan);
        }
        foreach (var dup in schemaPlans.GroupBy(p => p.Schema.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Warn(IaFormat.SchemasSheet, dup.First().Schema.Row, dup.Key, "Title used in more than one folder – references to it must include a folder path (e.g. 'Embedded/Link').");

        var resolver = new SchemaResolver(schemaPlans, ctx.ExistingSchemas);
        var planByTitle = schemaPlans.GroupBy(p => p.Schema.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // ---------------- categories & keywords
        var catPlans = new Dictionary<string, CategoryPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in wb.Categories)
        {
            if (catPlans.ContainsKey(c.Title)) { Err(IaFormat.CategoriesSheet, c.Row, c.Title, "Duplicate category."); continue; }
            if (!IaVocabulary.IsValidXmlName(c.XmlName)) Err(IaFormat.CategoriesSheet, c.Row, c.Title, $"Invalid XML name '{c.XmlName}'.");
            var existing = ctx.Categories.FirstOrDefault(x => IaWorkbook.Same(x.Title, c.Title));
            var cp = new CategoryPlan { Category = c, Action = existing is null ? PlanAction.Create : PlanAction.UseExisting, ExistingId = existing?.Id };
            if (existing is not null) Info(IaFormat.CategoriesSheet, c.Row, c.Title, $"Category exists ({existing.Id}) – reused, not modified.");
            if (c.KeywordMetadataSchema is not null && cp.Action == PlanAction.Create)
            {
                var r = resolver.Resolve(c.KeywordMetadataSchema);
                if (r.Kind is ResolutionKind.Missing or ResolutionKind.Ambiguous)
                    Err(IaFormat.CategoriesSheet, c.Row, c.Title, $"Keyword Metadata Schema '{c.KeywordMetadataSchema}' is {r.Kind.ToString().ToLower()}.");
                else if (r.Purpose != "Metadata")
                    Warn(IaFormat.CategoriesSheet, c.Row, c.Title, $"Keyword Metadata Schema '{c.KeywordMetadataSchema}' is not a Metadata schema.");
            }
            catPlans[c.Title] = cp;
            plan.Categories.Add(cp);
        }
        bool CategoryKnown(string title) => catPlans.ContainsKey(title) || ctx.Categories.Any(x => IaWorkbook.Same(x.Title, title));

        var kwRows = wb.Keywords.GroupBy(k => k.CategoryTitle, StringComparer.OrdinalIgnoreCase);
        foreach (var group in kwRows)
        {
            if (!CategoryKnown(group.Key))
            {
                foreach (var k in group) Err(IaFormat.KeywordsSheet, k.Row, k.Title, $"Category '{group.Key}' is neither in the Categories sheet nor in the publication.");
                continue;
            }
            var existingCat = ctx.Categories.FirstOrDefault(x => IaWorkbook.Same(x.Title, group.Key));
            var existingKws = existingCat is not null && ctx.KeywordsByCategoryId.TryGetValue(existingCat.Id, out var l) ? l : new List<NamedItem>();
            var byTitle = new Dictionary<string, KeywordPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in group)
            {
                if (byTitle.ContainsKey(k.Title)) { Err(IaFormat.KeywordsSheet, k.Row, k.Title, "Duplicate keyword in category."); continue; }
                var ex = existingKws.FirstOrDefault(x => IaWorkbook.Same(x.Title, k.Title));
                byTitle[k.Title] = new KeywordPlan { Keyword = k, Action = ex is null ? PlanAction.Create : PlanAction.UseExisting, ExistingId = ex?.Id };
            }
            foreach (var kp in byTitle.Values)
            {
                var depth = 0; var cur = kp; var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (cur.Keyword.ParentKeyword is { } parent)
                {
                    if (!guard.Add(cur.Keyword.Title)) { Err(IaFormat.KeywordsSheet, kp.Keyword.Row, kp.Keyword.Title, "Circular parent keywords."); break; }
                    if (byTitle.TryGetValue(parent, out var p)) { cur = p; depth++; continue; }
                    if (!existingKws.Any(x => IaWorkbook.Same(x.Title, parent)))
                        Err(IaFormat.KeywordsSheet, kp.Keyword.Row, kp.Keyword.Title, $"Parent keyword '{parent}' not found.");
                    depth++; break;
                }
                kp.Depth = depth;
                if (kp.Action == PlanAction.UseExisting) Info(IaFormat.KeywordsSheet, kp.Keyword.Row, kp.Keyword.Title, "Keyword exists – reused.");
                plan.Keywords.Add(kp);
            }
        }
        plan.Keywords.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        // ---------------- fields
        var schemaTitles = new HashSet<string>(wb.Schemas.Select(s => s.Title), StringComparer.OrdinalIgnoreCase);
        var embeddedDeps = new Dictionary<SchemaPlan, HashSet<SchemaPlan>>();
        foreach (var f in wb.Fields)
        {
            var item = $"{f.SchemaTitle}.{f.XmlName}";
            if (!schemaTitles.Contains(f.SchemaTitle)) { Err(IaFormat.FieldsSheet, f.Row, item, $"Schema '{f.SchemaTitle}' is not in the Schemas sheet."); continue; }
            var owners = planByTitle[f.SchemaTitle];
            if (owners.Count > 1) Warn(IaFormat.FieldsSheet, f.Row, item, "Schema title is used in several folders; field is applied to all of them.");
            foreach (var owner in owners)
            {
                if (!IaVocabulary.AllowedSections(owner.Schema.Purpose).Contains(f.Section))
                    Err(IaFormat.FieldsSheet, f.Row, item, $"{owner.Schema.Purpose} schemas cannot have {f.Section} fields.");
            }
            if (!IaVocabulary.IsValidXmlName(f.XmlName)) Err(IaFormat.FieldsSheet, f.Row, item, "Invalid XML name.");
            if (string.IsNullOrWhiteSpace(f.Label)) Err(IaFormat.FieldsSheet, f.Row, item, "Description (Label) is mandatory.");
            if (f.Mandatory && f.MinOccurs < 1) { f.MinOccurs = 1; Warn(IaFormat.FieldsSheet, f.Row, item, "Mandatory = Y but Min Occurs = 0 – Min Occurs set to 1."); }
            if (f.MaxOccurs >= 0 && f.MinOccurs > f.MaxOccurs) Err(IaFormat.FieldsSheet, f.Row, item, "Min Occurs is greater than Max Occurs.");
            if (f.MaxOccurs == 0) Err(IaFormat.FieldsSheet, f.Row, item, "Max Occurs cannot be 0.");

            var listCapable = f.Type is IaFieldType.Text or IaFieldType.Number or IaFieldType.Date or IaFieldType.Keyword;
            if (f.ListType is not null && !IaVocabulary.ListTypes.Contains(f.ListType, StringComparer.OrdinalIgnoreCase))
                Err(IaFormat.FieldsSheet, f.Row, item, $"List Type must be one of {string.Join(", ", IaVocabulary.ListTypes)}.");
            if (f.ListValues.Count > 0 && !listCapable) Warn(IaFormat.FieldsSheet, f.Row, item, "List values are ignored for this field type.");
            if (f.ListValues.Count > 0 && f.ListType is null && f.Type != IaFieldType.Keyword) { f.ListType = "Select"; Info(IaFormat.FieldsSheet, f.Row, item, "List values without List Type – 'Select' used."); }
            if (f.MaxLength is not null) Warn(IaFormat.FieldsSheet, f.Row, item, "Max Length is not applied (XSD facet) – reported only.");
            if (f.FormatArea is not null) Info(IaFormat.FieldsSheet, f.Row, item, "Format Area is not applied – Tridion default formatting features are used.");

            switch (f.Type)
            {
                case IaFieldType.Keyword:
                    if (f.Category is null) Err(IaFormat.FieldsSheet, f.Row, item, "Keyword field needs a Category.");
                    else if (!CategoryKnown(f.Category)) Err(IaFormat.FieldsSheet, f.Row, item, $"Category '{f.Category}' not found in sheet or publication.");
                    break;
                case IaFieldType.EmbeddedSchema:
                    if (f.EmbeddedSchema is null) { Err(IaFormat.FieldsSheet, f.Row, item, "Embedded Schema field needs 'Embedded Schema'."); break; }
                    var er = resolver.Resolve(f.EmbeddedSchema);
                    if (er.Kind is ResolutionKind.Missing or ResolutionKind.Ambiguous)
                        Err(IaFormat.FieldsSheet, f.Row, item, $"Embedded schema '{f.EmbeddedSchema}' is {er.Kind.ToString().ToLower()}.");
                    else if (er.Purpose != "Embedded")
                        Err(IaFormat.FieldsSheet, f.Row, item, $"'{f.EmbeddedSchema}' is a {er.Purpose} schema, not Embedded.");
                    else if (er.Plan is not null)
                        foreach (var owner in owners)
                        {
                            if (owner == er.Plan) { Err(IaFormat.FieldsSheet, f.Row, item, "Schema embeds itself."); continue; }
                            if (!embeddedDeps.TryGetValue(owner, out var set)) embeddedDeps[owner] = set = new HashSet<SchemaPlan>();
                            set.Add(er.Plan);
                        }
                    break;
                case IaFieldType.ComponentLink:
                case IaFieldType.MultimediaLink:
                    foreach (var t in f.AllowedTargetSchemas)
                    {
                        var tr = resolver.Resolve(t);
                        if (tr.Kind is ResolutionKind.Missing or ResolutionKind.Ambiguous)
                            Err(IaFormat.FieldsSheet, f.Row, item, $"Allowed target schema '{t}' is {tr.Kind.ToString().ToLower()}.");
                        else if (f.Type == IaFieldType.MultimediaLink && tr.Purpose != "Multimedia")
                            Err(IaFormat.FieldsSheet, f.Row, item, $"Multimedia link target '{t}' is not a Multimedia schema.");
                        else if (f.Type == IaFieldType.ComponentLink && tr.Purpose is not ("Component" or "Multimedia"))
                            Warn(IaFormat.FieldsSheet, f.Row, item, $"Component link target '{t}' is a {tr.Purpose} schema.");
                    }
                    if (f.AllowedTargetSchemas.Count > 0) foreach (var o in owners) o.NeedsSecondPass = true;
                    break;
            }
        }
        foreach (var g in wb.Fields.GroupBy(x => (x.SchemaTitle.ToLowerInvariant(), x.Section, x.XmlName.ToLowerInvariant())).Where(g => g.Count() > 1))
            Err(IaFormat.FieldsSheet, g.First().Row, $"{g.First().SchemaTitle}.{g.First().XmlName}", "Duplicate XML name in the same schema section.");
        if (wb.Fields.Any(f => f.HelpText is not null))
            Info(IaFormat.FieldsSheet, 0, "Help Text", "Help text is appended to the field description (Tridion has no separate help-text property).");

        // ---------------- multimedia types
        // Tridion requires every Multimedia schema to allow at least one Multimedia Type.
        var knownExtensions = string.Join(", ", ctx.MultimediaTypes.SelectMany(m => m.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e));
        foreach (var sp in schemaPlans.Where(p => p.Schema.Purpose == IaSchemaPurpose.Multimedia && p.Action == PlanAction.Create))
        {
            var missing = sp.Schema.AllowedMultimediaTypes
                .Where(ext => !ctx.MultimediaTypes.Any(m => m.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))).ToList();
            var matched = sp.Schema.AllowedMultimediaTypes.Count - missing.Count;
            if (sp.Schema.AllowedMultimediaTypes.Count == 0)
                Err(IaFormat.SchemasSheet, sp.Schema.Row, sp.Schema.Title,
                    $"Multimedia schemas need at least one file extension in 'Allowed Multimedia Types'. Extensions known to this CMS: {knownExtensions}.");
            else if (matched == 0)
                Err(IaFormat.SchemasSheet, sp.Schema.Row, sp.Schema.Title,
                    $"None of the extensions ({string.Join(", ", missing)}) match a Multimedia Type in this CMS, and Tridion requires at least one. " +
                    $"Ask a CMS administrator to add a Multimedia Type for '{missing[0]}' (Administration → Multimedia Types), or change the list. " +
                    $"Extensions known to this CMS: {knownExtensions}.");
            else if (missing.Count > 0)
                Warn(IaFormat.SchemasSheet, sp.Schema.Row, sp.Schema.Title,
                    $"No Multimedia Type for extension(s) {string.Join(", ", missing)} in this CMS – left out; the other types are applied.");
        }

        // ---------------- regions
        foreach (var rr in wb.Regions)
        {
            var item = rr.RegionSchemaTitle;
            if (!planByTitle.TryGetValue(rr.RegionSchemaTitle, out var owners)) { Err(IaFormat.RegionsSheet, rr.Row, item, "Region schema is not in the Schemas sheet."); continue; }
            foreach (var o in owners)
            {
                if (o.Schema.Purpose != IaSchemaPurpose.Region) Err(IaFormat.RegionsSheet, rr.Row, item, "Schema purpose is not Region.");
                o.NeedsSecondPass = true;
            }
            if (rr.RowType == IaRegionRowType.NestedRegion)
            {
                if (rr.NestedRegionName is null || !IaVocabulary.IsValidXmlName(rr.NestedRegionName)) Err(IaFormat.RegionsSheet, rr.Row, item, "Nested region needs a valid 'Nested Region Name'.");
                if (rr.NestedRegionSchema is null) { Err(IaFormat.RegionsSheet, rr.Row, item, "Nested region needs 'Nested Region Schema'."); continue; }
                var nr = resolver.Resolve(rr.NestedRegionSchema);
                if (nr.Kind is ResolutionKind.Missing or ResolutionKind.Ambiguous) Err(IaFormat.RegionsSheet, rr.Row, item, $"Nested region schema '{rr.NestedRegionSchema}' is {nr.Kind.ToString().ToLower()}.");
                else if (nr.Purpose != "Region") Err(IaFormat.RegionsSheet, rr.Row, item, $"'{rr.NestedRegionSchema}' is not a Region schema.");
            }
            else
            {
                if (rr.MinOccurs is not null && rr.MaxOccurs is >= 0 && rr.MinOccurs > rr.MaxOccurs) Err(IaFormat.RegionsSheet, rr.Row, item, "Min Occurs is greater than Max Occurs.");
                foreach (var t in rr.AllowedComponentSchemas)
                {
                    var tr = resolver.Resolve(t);
                    if (tr.Kind is ResolutionKind.Missing or ResolutionKind.Ambiguous) Err(IaFormat.RegionsSheet, rr.Row, item, $"Allowed component schema '{t}' is {tr.Kind.ToString().ToLower()}.");
                }
                foreach (var t in rr.AllowedComponentTemplates)
                    if (!ctx.ComponentTemplates.Any(ct => IaWorkbook.Same(ct.Title, t)))
                        Info(IaFormat.RegionsSheet, rr.Row, item, $"Component template '{t}' does not exist yet – apply it later with the template import ('Apply template constraints to region schemas').");
            }
        }

        // ---------------- ordering: every item after everything it references (see DependencyOrder)
        foreach (var purpose in PurposeOrder)
            plan.Schemas.AddRange(schemaPlans.Where(p => p.Schema.Purpose == purpose));
        DependencyOrder.Build(wb, plan, resolver, catPlans, plan.Issues);

        plan.Issues.Insert(0, new Issue(IssueLevel.Info, "Plan", 0, "Summary",
            $"Folders to create: {plan.FoldersToCreate.Count}; categories to create: {plan.Categories.Count(c => c.Action == PlanAction.Create)}; " +
            $"keywords to create: {plan.Keywords.Count(k => k.Action == PlanAction.Create)}; schemas to create: {plan.Schemas.Count(s => s.Action == PlanAction.Create)}; " +
            $"schemas skipped (exist): {plan.Schemas.Count(s => s.Action == PlanAction.Skip)}; " +
            $"creation steps in dependency order: {plan.CreationOrder.Count}."));
        return plan;
    }

    private static void AddFolders(string path, ImportContext ctx, ImportPlan plan)
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
