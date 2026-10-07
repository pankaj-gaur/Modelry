using System.Text;
using System.Text.RegularExpressions;
using Modelry.Core.Model;

namespace Modelry.Core.CodeGen;

/// <summary>
/// Turns the IA workbook into a language-neutral model plan: one class per schema that DXA maps to a view model,
/// typed properties per field, and the view registrations from the Templates sheet. Reads the workbook only – no CMS calls.
/// </summary>
public static class ModelPlanner
{
    private const string Sheet = "Models";

    /// <summary>DXA framework types the generated code references; a schema with one of these names gets the site prefix.</summary>
    private static readonly HashSet<string> ReservedTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "EntityModel", "ViewModel", "MediaItem", "Image", "Download", "YouTubeVideo", "Link", "Tag", "KeywordModel", "RichText",
        "PageModel", "RegionModel", "RegionModelSet", "MvcData", "SemanticVocabulary", "Object", "String", "DateTime", "List", "Type",
        "KeyValuePair", "Attribute", "Exception", "Uri", "Math", "Convert", "Environment"
    };

    /// <summary>Base-class names a property may not reuse (they would hide DXA members).</summary>
    private static readonly string[] EntityMembers = { "Id", "MvcData", "HtmlClasses", "XpmMetadata", "XpmPropertyMetadata", "ExtensionData", "IsVolatile" };
    private static readonly string[] MediaMembers = { "Url", "FileName", "FileSize", "MimeType", "IsEmbedded" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "webp", "svg", "bmp", "tif", "tiff", "avif", "ico" };

    private static readonly HashSet<string> BuiltInViewModels = new(StringComparer.OrdinalIgnoreCase)
        { "EntityModel", "PageModel", "RegionModel" };

    public static ModelPlan Build(IaWorkbook wb, ModelGenerationOptions options)
    {
        var rawPrefix = DeriveRawPrefix(wb);
        var prefix = Pascal(rawPrefix);
        if (prefix.Length == 0) prefix = "Site";
        var semanticPrefix = string.IsNullOrWhiteSpace(options.SemanticPrefix) ? prefix.ToLowerInvariant() : options.SemanticPrefix.Trim();
        if (!Regex.IsMatch(semanticPrefix, "^[A-Za-z][A-Za-z0-9]*$")) semanticPrefix = "m";
        if (string.IsNullOrWhiteSpace(options.RootNamespace)) options.RootNamespace = $"{prefix}.Web.Models";
        if (string.IsNullOrWhiteSpace(options.DefaultArea)) options.DefaultArea = MostCommonArea(wb) ?? prefix;

        var plan = new ModelPlan { Options = options, SemanticPrefix = semanticPrefix };
        void Issue(IssueLevel level, int row, string item, string message) => plan.Issues.Add(new Issue(level, Sheet, row, item, message));

        if (!IsValidNamespace(options.RootNamespace))
            Issue(IssueLevel.Error, 0, "Namespace", $"'{options.RootNamespace}' is not a valid C# namespace. Use dotted names such as Sabic.Web.Models.");
        if (!IsIdentifier(options.DefaultArea!))
            Issue(IssueLevel.Error, 0, "Area", $"'{options.DefaultArea}' is not a valid DXA area name (letters and digits only).");
        if (wb.Schemas.Count == 0)
            Issue(IssueLevel.Error, 0, "Schemas", "The workbook has no schemas, so there is nothing to generate.");

        // ---- 1. Which schemas become classes, and what kind --------------------------------------------------------
        var pageMeta = options.PageMetadataSchema is { Length: > 0 } pm ? wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, pm)) : null;
        if (options.PageMetadataSchema is { Length: > 0 } && pageMeta is null)
            Issue(IssueLevel.Error, 0, options.PageMetadataSchema, "The page metadata schema chosen is not in the workbook.");
        else if (pageMeta is not null && pageMeta.Purpose != IaSchemaPurpose.Metadata)
            Issue(IssueLevel.Warning, pageMeta.Row, pageMeta.Title, $"The page metadata schema has purpose {pageMeta.Purpose}, not Metadata.");

        var keywordMeta = wb.Categories.Where(c => c.KeywordMetadataSchema is not null).Select(c => c.KeywordMetadataSchema!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pageSchemas = wb.Templates.Where(t => t.Kind == IaTemplateKind.PageTemplate && t.PageSchema is not null)
            .Select(t => LastSegment(t.PageSchema!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kinds = new Dictionary<IaSchema, ModelKind>();
        foreach (var s in wb.Schemas)
        {
            var hasFields = wb.Fields.Any(f => IaWorkbook.Same(f.SchemaTitle, s.Title));
            ModelKind? kind = s.Purpose switch
            {
                IaSchemaPurpose.Component => ModelKind.Entity,
                IaSchemaPurpose.Embedded => ModelKind.Embedded,
                IaSchemaPurpose.Multimedia => ModelKind.Media,
                IaSchemaPurpose.Metadata when s == pageMeta => ModelKind.Page,
                IaSchemaPurpose.Metadata when keywordMeta.Contains(s.Title) => ModelKind.Keyword,
                IaSchemaPurpose.Region when hasFields && pageSchemas.Contains(s.Title) => ModelKind.Page,
                IaSchemaPurpose.Region when hasFields => ModelKind.Region,
                _ => null
            };
            if (kind is null)
            {
                plan.Skipped.Add(new SkippedSchema(s, s.Purpose switch
                {
                    IaSchemaPurpose.Metadata => "Structure Group or Publication metadata: read by navigation or localization code, not mapped to a view model by DXA. Choose it as the page metadata schema if pages use it.",
                    IaSchemaPurpose.Region when pageSchemas.Contains(s.Title) => "Page schema without fields: DXA's PageModel (or the page metadata model) is registered for its Page Template views.",
                    IaSchemaPurpose.Region => "Region schema without fields: DXA's RegionModel is used for its region views.",
                    _ => $"{s.Purpose} schemas are not mapped to DXA view models."
                }));
                continue;
            }
            if (kind == ModelKind.Embedded && s.Purpose == IaSchemaPurpose.Embedded && !hasFields)
                Issue(IssueLevel.Warning, s.Row, s.Title, "Embedded schema without fields – the class is empty.");
            kinds[s] = kind.Value;
        }

        // ---- 2. Class names -------------------------------------------------------------------------------------
        var names = new Dictionary<IaSchema, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (s, kind) in kinds.OrderBy(k => k.Key.Row))
        {
            string name;
            var fromSheet = PreferredName(wb, s, kind, plan);
            if (fromSheet is not null) name = fromSheet;
            else if (kind == ModelKind.Page && s == pageMeta) name = prefix + "PageModel";
            else name = Pascal(StripPrefix(s.Title, rawPrefix));
            if (name.Length == 0) name = prefix + "Model";
            if (!char.IsLetter(name[0]) || ReservedTypeNames.Contains(name) || IaWorkbook.Same(name, prefix)) name = prefix + name;
            var unique = name;
            for (var i = 2; !used.Add(unique); i++) unique = name + i;
            if (unique != name)
                Issue(IssueLevel.Warning, s.Row, s.Title, $"Class name '{name}' is already used by another schema, so this one is '{unique}'.");
            names[s] = unique;
        }

        // ---- 3. Classes and properties --------------------------------------------------------------------------
        var schemaByTitle = wb.Schemas.GroupBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        IaSchema? Find(string reference) => schemaByTitle.TryGetValue(LastSegment(reference), out var s) ? s : null;
        string? NameOf(string reference) => Find(reference) is { } s && names.TryGetValue(s, out var n) ? n : null;
        var pageMetaName = pageMeta is not null && names.TryGetValue(pageMeta, out var pmn) ? pmn : null;

        foreach (var (s, kind) in kinds.OrderBy(k => k.Value).ThenBy(k => names[k.Key], StringComparer.Ordinal))
        {
            var baseType = kind switch
            {
                ModelKind.Entity or ModelKind.Embedded => "EntityModel",
                ModelKind.Media => s.AllowedMultimediaTypes.Count > 0 && s.AllowedMultimediaTypes.All(ImageExtensions.Contains) ? "Image" : "Download",
                ModelKind.Keyword => "KeywordModel",
                ModelKind.Page => s != pageMeta && pageMetaName is not null ? pageMetaName : "PageModel",
                _ => "RegionModel"
            };
            var cls = new ModelClass
            {
                Name = names[s], Schema = s, Kind = kind, BaseType = baseType,
                Folder = "Models/" + kind switch
                {
                    ModelKind.Entity => "Entity", ModelKind.Embedded => "Embedded", ModelKind.Media => "Media",
                    ModelKind.Keyword => "Keyword", ModelKind.Page => "Page", _ => "Region"
                }
            };
            var taken = new HashSet<string>(BaseMembers(kind, baseType), StringComparer.OrdinalIgnoreCase);
            if (kind == ModelKind.Page && baseType == pageMetaName && pageMeta is not null)
                foreach (var f in wb.Fields.Where(f => IaWorkbook.Same(f.SchemaTitle, pageMeta.Title))) taken.Add(Pascal(f.XmlName));
            var shortName = cls.Name.StartsWith(prefix, StringComparison.Ordinal) && cls.Name.Length > prefix.Length && char.IsUpper(cls.Name[prefix.Length])
                ? cls.Name[prefix.Length..] : cls.Name;

            var fields = wb.FieldsOf(s.Title, IaFieldSection.Content).Concat(wb.FieldsOf(s.Title, IaFieldSection.Metadata));
            foreach (var f in fields)
            {
                var item = $"{s.Title}.{f.XmlName}";
                var isList = f.MaxOccurs != 1;
                var kindOf = ModelValueKind.Text;
                string? model = null;
                switch (f.Type)
                {
                    case IaFieldType.RichText: kindOf = ModelValueKind.RichText; break;
                    case IaFieldType.Number: kindOf = ModelValueKind.Number; break;
                    case IaFieldType.Date: kindOf = ModelValueKind.Date; break;
                    case IaFieldType.EmbeddedSchema:
                        model = f.EmbeddedSchema is null ? null : NameOf(f.EmbeddedSchema);
                        if (model is null)
                        {
                            Issue(IssueLevel.Warning, f.Row, item, $"Embedded schema '{f.EmbeddedSchema}' has no model class, so the field is left out. Check the Embedded Schema column.");
                            continue;
                        }
                        kindOf = ModelValueKind.Model;
                        break;
                    case IaFieldType.ComponentLink:
                    {
                        var targets = f.AllowedTargetSchemas.Select(t => (Ref: t, Schema: Find(t))).ToList();
                        foreach (var t in targets.Where(t => t.Schema is null))
                            Issue(IssueLevel.Info, f.Row, item, $"Allowed target '{t.Ref}' is not in the workbook – typed as EntityModel.");
                        if (targets.Count == 0) kindOf = ModelValueKind.Link;
                        else if (targets.Count == 1 && targets[0].Schema is { } ts && names.TryGetValue(ts, out var tn)
                                 && kinds[ts] is ModelKind.Entity or ModelKind.Media)
                        { kindOf = ModelValueKind.Model; model = tn; }
                        else kindOf = targets.All(t => t.Schema?.Purpose == IaSchemaPurpose.Multimedia) ? ModelValueKind.AnyMedia : ModelValueKind.AnyEntity;
                        break;
                    }
                    case IaFieldType.MultimediaLink:
                    {
                        var targets = f.AllowedTargetSchemas.Select(Find).ToList();
                        if (targets.Count == 1 && targets[0] is { } ts && names.TryGetValue(ts, out var tn) && kinds[ts] == ModelKind.Media)
                        { kindOf = ModelValueKind.Model; model = tn; }
                        else kindOf = ModelValueKind.AnyMedia;
                        break;
                    }
                    case IaFieldType.Keyword:
                    {
                        var cat = wb.Categories.FirstOrDefault(c => IaWorkbook.Same(c.Title, f.Category));
                        model = cat?.KeywordMetadataSchema is { } kms ? NameOf(kms) : null;
                        kindOf = model is null ? ModelValueKind.Tag : ModelValueKind.Model;
                        if (f.Category is not null && cat is null)
                            Issue(IssueLevel.Info, f.Row, item, $"Category '{f.Category}' is not in the Categories sheet – typed as Tag.");
                        break;
                    }
                    default: kindOf = ModelValueKind.Text; break;
                }

                string? helper = null, checkedValue = null;
                var name = Pascal(f.XmlName);
                string? reason = null;
                var isCheckbox = f.Type is IaFieldType.Text && string.Equals(f.ListType, "Checkbox", StringComparison.OrdinalIgnoreCase);
                if (isCheckbox && !isList && f.ListValues.Count == 1)
                {
                    // Single-value checkbox ("Yes"): map the text, expose a bool for the views.
                    helper = name;
                    checkedValue = f.ListValues[0];
                    name += "Text";
                }
                if (name == cls.Name) { name += isList ? "List" : "Value"; reason = "same as the class name"; }
                if (taken.Contains(name)) { name = shortName + name; reason ??= "would hide a DXA base-class member"; }
                if (helper is not null && taken.Contains(helper)) helper = shortName + helper;
                var unique = name;
                for (var i = 2; taken.Contains(unique) || unique == cls.Name; i++) unique = name + i;
                if (unique != name) reason = "duplicate field name";
                name = unique;
                taken.Add(name);
                if (helper is not null) taken.Add(helper);
                if (reason is not null)
                    Issue(IssueLevel.Info, f.Row, item, $"Property is named '{name}' ({reason}); it still maps to '{f.XmlName}'.");
                cls.Properties.Add(new ModelProperty
                {
                    Name = name, Field = f, Kind = kindOf, ModelName = model, IsList = isList,
                    BooleanHelperName = helper, CheckedValue = checkedValue, RenameReason = reason
                });
            }
            plan.Classes.Add(cls);
        }

        // ---- 4. View registrations ------------------------------------------------------------------------------
        void Register(string? qualifiedView, string? controller, string modelType, ModelKind targetKind, string source, int row)
        {
            if (string.IsNullOrWhiteSpace(qualifiedView) || qualifiedView.TrimStart().StartsWith("("))
            {
                Issue(IssueLevel.Warning, row, source, "No DXA View in the Templates sheet, so nothing is registered for this template.");
                return;
            }
            var (area, view) = SplitView(qualifiedView, options.DefaultArea!);
            if (!IsIdentifier(area) || !IsIdentifier(view))
            {
                Issue(IssueLevel.Warning, row, source, $"'{qualifiedView}' is not a valid DXA view name (Area:ViewName), so it is not registered.");
                return;
            }
            var ctrl = controller is null ? null : controller.Contains(':') ? controller[(controller.IndexOf(':') + 1)..].Trim() : controller.Trim();
            var reg = new ViewRegistration(area, view, string.IsNullOrEmpty(ctrl) ? null : ctrl, modelType, source);
            var effective = $"{area}:{reg.Controller ?? DefaultController(targetKind)}:{view}".ToLowerInvariant();
            var existing = plan.Registrations.FirstOrDefault(r =>
                $"{r.Area}:{r.Controller ?? DefaultController(KindOfType(plan, r.ModelType))}:{r.View}".ToLowerInvariant() == effective);
            if (existing is not null)
            {
                if (existing.ModelType != modelType)
                    Issue(IssueLevel.Warning, row, source, $"View '{qualifiedView}' is already registered with {existing.ModelType} (from {existing.Source}); DXA allows one model type per view, so {modelType} is not registered.");
                return;
            }
            plan.Registrations.Add(reg);
        }

        foreach (var t in wb.Templates.Where(t => t.Kind == IaTemplateKind.ComponentTemplate))
        {
            var linked = t.LinkedSchemas.Select(l => (Ref: l, Name: NameOf(l), Schema: Find(l))).ToList();
            string? model = null;
            if (t.ViewModelType is { } vmt && plan.Classes.Any(c => c.Name == vmt)) model = vmt;
            else if (linked.FirstOrDefault(l => l.Name is not null && l.Schema is not null && kinds[l.Schema] == ModelKind.Entity) is { Name: { } n }) model = n;
            if (model is null)
            {
                Issue(IssueLevel.Warning, t.Row, t.Title, linked.Count == 0
                    ? "Component Template without linked schemas – its view is not registered."
                    : $"None of the linked schemas ({string.Join("; ", t.LinkedSchemas)}) has a component model – its view is not registered.");
                continue;
            }
            if (linked.Count(l => l.Name is not null) > 1)
                Issue(IssueLevel.Warning, t.Row, t.Title, $"Linked to {linked.Count} schemas; DXA needs one model type per view, so the view is registered with {model}. Give the other schemas their own templates or a shared base class.");
            Register(t.View, t.Controller, model, ModelKind.Entity, t.Title, t.Row);
        }

        foreach (var t in wb.Templates.Where(t => t.Kind == IaTemplateKind.PageTemplate))
        {
            var pageSchema = t.PageSchema is null ? null : Find(t.PageSchema);
            var model = pageSchema is not null && names.TryGetValue(pageSchema, out var psn) && kinds[pageSchema] == ModelKind.Page ? psn
                : pageMetaName ?? "PageModel";
            Register(t.View, null, model, ModelKind.Page, t.Title, t.Row);
        }

        foreach (var rv in wb.RegionViews)
        {
            var schema = rv.RegionSchema is null ? null : Find(rv.RegionSchema);
            var model = schema is not null && names.TryGetValue(schema, out var rn) && kinds[schema] == ModelKind.Region ? rn : "RegionModel";
            Register(rv.View, null, model, ModelKind.Region, rv.Title, rv.Row);
        }

        var registered = plan.Registrations.Select(r => r.ModelType).ToHashSet(StringComparer.Ordinal);
        plan.ModelOnlyRegistrations.AddRange(plan.Classes.Where(c => c.Kind == ModelKind.Entity && !registered.Contains(c.Name)).Select(c => c.Name));

        if (wb.Templates.Count == 0)
            Issue(IssueLevel.Warning, 0, "Templates", "The workbook has no Templates sheet rows, so no views are registered – only model classes are generated.");
        foreach (var c in plan.Classes.Where(c => c.Kind == ModelKind.Entity && c.Properties.Count == 0))
            Issue(IssueLevel.Warning, c.Schema.Row, c.Schema.Title, "Component schema without fields – the class is empty.");
        return plan;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static string? PreferredName(IaWorkbook wb, IaSchema s, ModelKind kind, ModelPlan plan)
    {
        IEnumerable<string?> candidates = kind switch
        {
            ModelKind.Entity => wb.Templates.Where(t => t.Kind == IaTemplateKind.ComponentTemplate && t.LinkedSchemas.Count >= 1
                                                         && IaWorkbook.Same(LastSegment(t.LinkedSchemas[0]), s.Title)).Select(t => t.ViewModelType),
            ModelKind.Page when s.Purpose == IaSchemaPurpose.Region => wb.Templates.Where(t => t.Kind == IaTemplateKind.PageTemplate && t.PageSchema is not null
                                                         && IaWorkbook.Same(LastSegment(t.PageSchema), s.Title)).Select(t => t.ViewModelType),
            ModelKind.Region => wb.RegionViews.Where(r => r.RegionSchema is not null && IaWorkbook.Same(LastSegment(r.RegionSchema), s.Title)).Select(r => r.ViewModelType),
            _ => Array.Empty<string?>()
        };
        var names = candidates.Where(n => n is not null && IsIdentifier(n) && !BuiltInViewModels.Contains(n)).Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > 1)
            plan.Issues.Add(new Issue(IssueLevel.Warning, Sheet, s.Row, s.Title, $"Templates give different View Model Types ({string.Join(", ", names)}); '{names[0]}' is used."));
        return names.FirstOrDefault();
    }

    private static IEnumerable<string> BaseMembers(ModelKind kind, string baseType)
    {
        var list = new List<string>(EntityMembers);
        if (kind == ModelKind.Media) list.AddRange(MediaMembers);
        if (baseType == "Image") list.Add("AlternateText");
        if (baseType == "Download") list.Add("Description");
        if (kind == ModelKind.Keyword) list.AddRange(new[] { "Title", "Description", "Key", "TaxonomyId" });
        if (kind == ModelKind.Page) list.AddRange(new[] { "Title", "Url", "Meta", "Regions" });
        if (kind == ModelKind.Region) list.AddRange(new[] { "Name", "Entities", "Regions", "SchemaId" });
        return list;
    }

    private static string DefaultController(ModelKind kind) => kind switch { ModelKind.Page => "Page", ModelKind.Region => "Region", _ => "Entity" };

    private static ModelKind KindOfType(ModelPlan plan, string type) =>
        type == "PageModel" ? ModelKind.Page : type == "RegionModel" ? ModelKind.Region
        : plan.Classes.FirstOrDefault(c => c.Name == type)?.Kind switch { ModelKind.Page => ModelKind.Page, ModelKind.Region => ModelKind.Region, _ => ModelKind.Entity };

    public static (string Area, string View) SplitView(string qualified, string defaultArea)
    {
        var i = qualified.IndexOf(':');
        return i < 0 ? (defaultArea, qualified.Trim()) : (qualified[..i].Trim(), qualified[(i + 1)..].Trim());
    }

    private static string? MostCommonArea(IaWorkbook wb) =>
        wb.Templates.Select(t => t.View).Concat(wb.RegionViews.Select(r => r.View))
            .Where(v => v is not null && v.Contains(':') && !v.StartsWith("("))
            .Select(v => v![..v!.IndexOf(':')].Trim())
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    /// <summary>The text most schema titles start with ("SABIC" in "SABIC – Hero Banner"), or "".</summary>
    public static string DeriveRawPrefix(IaWorkbook wb)
    {
        var heads = wb.Schemas.Select(s => Regex.Match(s.Title, @"^\s*(.+?)\s+[–—-]\s+\S")).Where(m => m.Success).Select(m => m.Groups[1].Value.Trim()).ToList();
        if (heads.Count == 0 || heads.Count * 2 < wb.Schemas.Count) return "";
        return heads.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
    }

    /// <summary>Suggested namespace for the Models step form.</summary>
    public static string SuggestNamespace(IaWorkbook wb) => (Pascal(DeriveRawPrefix(wb)) is { Length: > 0 } p ? p : "Site") + ".Web.Models";

    public static string StripPrefix(string title, string rawPrefix)
    {
        if (rawPrefix.Length == 0) return title;
        var m = Regex.Match(title, $@"^\s*{Regex.Escape(rawPrefix)}(?:\s*[–—-]\s*|\s+)(.*)$", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : title;
    }

    private static string LastSegment(string reference)
    {
        var r = reference.Replace('\\', '/').Trim();
        var i = r.LastIndexOf('/');
        return i < 0 ? r : r[(i + 1)..].Trim();
    }

    /// <summary>"Hero Banner – Standard" → HeroBannerStandard; "SABIC" → Sabic; "xAxisLabels" → XAxisLabels.</summary>
    public static string Pascal(string text)
    {
        var sb = new StringBuilder();
        foreach (Match w in Regex.Matches(text, "[A-Za-z0-9]+"))
        {
            var word = w.Value;
            if (word.Length > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c)) && word.Any(char.IsLetter))
                word = word[0] + word[1..].ToLowerInvariant();
            sb.Append(char.ToUpperInvariant(word[0])).Append(word[1..]);
        }
        return sb.ToString();
    }

    public static bool IsIdentifier(string s) => Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_]*$");

    public static bool IsValidNamespace(string s) => s.Split('.').All(IsIdentifier);
}
