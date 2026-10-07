using System.Collections.Concurrent;
using Modelry.Core.Model;

namespace Modelry.Core.Gateway;

/// <summary>
/// Demo-mode gateway: an in-memory "Tridion" so the UI, export and import can be tried without a CMS.
/// Thread-safe enough for a single demo instance; state is lost on restart.
/// </summary>
public sealed class InMemoryTridionGateway : ITridionGateway
{
    private sealed class Item
    {
        public required string Id { get; init; }
        public required string Title { get; set; }
        public required string Kind { get; init; }   // pub, folder, schema, category, keyword, ct
        public string? ParentId { get; init; }
        public required string PublicationId { get; init; }
        public SchemaWriteModel? Schema { get; set; }
        public IaCategory? Category { get; set; }
        public string? KeywordMetadataSchemaId { get; set; }
        public TemplateDetails? Template { get; set; }
    }

    private readonly ConcurrentDictionary<string, Item> _items = new();
    private readonly List<MultimediaTypeInfo> _mmTypes = new()
    {
        new("tcm:0-1-65544", "Jpeg image", new[] { "jpg", "jpeg" }),
        new("tcm:0-2-65544", "PNG image", new[] { "png" }),
        new("tcm:0-3-65544", "Gif image", new[] { "gif" }),
        new("tcm:0-4-65544", "SVG image", new[] { "svg" }),
        new("tcm:0-5-65544", "WebP image", new[] { "webp" }),
        new("tcm:0-6-65544", "Adobe Acrobat PDF", new[] { "pdf" }),
        new("tcm:0-7-65544", "MS Word", new[] { "docx", "doc" }),
        new("tcm:0-8-65544", "MS Excel", new[] { "xlsx", "xls", "csv" }),
        new("tcm:0-9-65544", "MPEG-4 video", new[] { "mp4" }),
    };
    private int _seq = 100;

    public InMemoryTridionGateway()
    {
        foreach (var (n, pubTitle) in new[] { (1, "000 Empty Parent (demo)"), (2, "010 Schema Master (demo)"), (3, "020 Website EN (demo)") })
        {
            var pubId = $"tcm:0-{n}-1";
            _items[pubId] = new Item { Id = pubId, Title = pubTitle, Kind = "pub", PublicationId = pubId };
            var root = Add("folder", "Building Blocks", pubId, null, 2);
            if (n > 1)
            {
                Add("folder", "Schemas", pubId, root.Id, 2);
                Add("folder", "System", pubId, root.Id, 2);
                var dxa = Add("folder", "DXA Templates", pubId, root.Id, 2);
                foreach (var (kind, title, view) in new[] { ("ct", "DXA Base Component Template", "Core:Entity:Article"), ("ct", "Article CT", "Core:Entity:Article"),
                                                            ("pt", "DXA Base Page Template", "Core:Page:GeneralPage") })
                {
                    var it = Add(kind, title, pubId, dxa.Id, kind == "ct" ? 32 : 128);
                    it.Template = new TemplateDetails
                    {
                        Id = it.Id, Title = title, Kind = kind == "ct" ? TemplateKind.Component : TemplateKind.Page, View = view, Dynamic = false,
                        Priority = "Medium", HasMetadataSchema = true,
                        MetadataFields = kind == "ct" ? new(StringComparer.OrdinalIgnoreCase) { "controller", "action", "routeValues", "view", "regionView", "regionName", "htmlClasses" }
                                                      : new(StringComparer.OrdinalIgnoreCase) { "view", "includes" },
                        Includes = kind == "pt" ? new List<string> { "system/include/header", "system/include/footer" } : new List<string>()
                    };
                }
            }
        }
    }

    private Item Add(string kind, string title, string pubId, string? parentId, int type)
    {
        var pubNo = pubId.Split('-')[1];
        var id = $"tcm:{pubNo}-{Interlocked.Increment(ref _seq)}-{type}";
        var item = new Item { Id = id, Title = title, Kind = kind, ParentId = parentId, PublicationId = pubId };
        _items[id] = item;
        return item;
    }

    private Item Get(string id) => _items.TryGetValue(id, out var i) ? i : throw new InvalidOperationException($"Item {id} not found.");
    private string TitleOf(string id) => _items.TryGetValue(id, out var i) ? i.Title : id;

    public Task<string> GetApiVersionAsync() => Task.FromResult("Demo mode (in-memory)");

    public Task<IReadOnlyList<TreeNode>> GetPublicationsAsync() =>
        Task.FromResult<IReadOnlyList<TreeNode>>(_items.Values.Where(i => i.Kind == "pub").OrderBy(i => i.Title)
            .Select(i => new TreeNode(i.Id, i.Title, NodeType.Publication, true)).ToList());

    public Task<TreeNode> GetPublicationRootFolderAsync(string publicationId)
    {
        var root = _items.Values.Single(i => i.Kind == "folder" && i.PublicationId == publicationId && i.ParentId is null);
        return Task.FromResult(Node(root));
    }

    private TreeNode Node(Item f) => new(f.Id, f.Title, NodeType.Folder,
        _items.Values.Any(i => i.Kind == "folder" && i.ParentId == f.Id),
        _items.Values.Count(i => i.Kind == "schema" && i.ParentId == f.Id));

    public Task<IReadOnlyList<TreeNode>> GetSubFoldersAsync(string folderId) =>
        Task.FromResult<IReadOnlyList<TreeNode>>(_items.Values.Where(i => i.Kind == "folder" && i.ParentId == folderId)
            .OrderBy(i => i.Title).Select(Node).ToList());

    public Task<FolderInfo> GetFolderAsync(string folderId)
    {
        var f = Get(folderId);
        return Task.FromResult(new FolderInfo(f.Id, f.Title, f.PublicationId, TitleOf(f.PublicationId), PathFromRoot(f)));
    }

    /// <summary>'/'-separated path below the root folder ('.' for the root folder).</summary>
    private string PathFromRoot(Item folder)
    {
        var parts = new List<string>();
        for (var cur = folder; cur.ParentId is not null; cur = Get(cur.ParentId)) parts.Insert(0, cur.Title);
        return parts.Count == 0 ? "." : string.Join("/", parts);
    }

    public Task<IReadOnlyList<NamedItem>> ListSchemasInFolderAsync(string folderId) =>
        Task.FromResult<IReadOnlyList<NamedItem>>(_items.Values.Where(i => i.Kind == "schema" && i.ParentId == folderId)
            .Select(i => new NamedItem(i.Id, i.Title)).ToList());

    public Task<IReadOnlyList<NamedItem>> ListPublicationSchemasAsync(string publicationId) =>
        Task.FromResult<IReadOnlyList<NamedItem>>(_items.Values.Where(i => i.Kind == "schema" && i.PublicationId == publicationId)
            .Select(i => new NamedItem(i.Id, i.Title)).ToList());

    public Task<SchemaInfo> ReadSchemaInfoAsync(string schemaId)
    {
        var s = Get(schemaId);
        return Task.FromResult(new SchemaInfo(s.Id, s.Schema!.Schema.Purpose.ToString(), PathFromRoot(Get(s.ParentId!))));
    }

    public Task<TreeNode> CreateFolderAsync(string parentFolderId, string title)
    {
        var parent = Get(parentFolderId);
        if (_items.Values.Any(i => i.Kind == "folder" && i.ParentId == parentFolderId && IaWorkbook.Same(i.Title, title)))
            throw new InvalidOperationException($"A folder named '{title}' already exists here.");
        return Task.FromResult(Node(Add("folder", title.Trim(), parent.PublicationId, parentFolderId, 2)));
    }

    public Task<IReadOnlyList<SchemaSummary>> GetSchemasInFolderAsync(string folderId) =>
        Task.FromResult<IReadOnlyList<SchemaSummary>>(_items.Values.Where(i => i.Kind == "schema" && i.ParentId == folderId)
            .OrderBy(i => i.Title).Select(i => new SchemaSummary(i.Id, i.Title, i.Schema!.Schema.Purpose.ToString())).ToList());

    public Task<int> CountSchemasAsync(string folderId) =>
        Task.FromResult(_items.Values.Count(i => i.Kind == "schema" && i.ParentId == folderId));

    public Task<SchemaDetails> ReadSchemaAsync(string schemaId)
    {
        var item = Get(schemaId);
        var m = item.Schema!;
        var s = m.Schema;
        var details = new SchemaDetails
        {
            Schema = new IaSchema
            {
                Title = item.Title, Purpose = s.Purpose, RootElementName = s.RootElementName ?? (s.Purpose == IaSchemaPurpose.Component ? "Content" : "Metadata"),
                NamespaceUri = s.Purpose == IaSchemaPurpose.Embedded ? null : s.NamespaceUri ?? $"uuid:{Guid.NewGuid()}", Description = s.Description,
                AllowedMultimediaTypes = m.MultimediaTypeIds.SelectMany(id => _mmTypes.First(t => t.Id == id).Extensions.Take(1)).Distinct().ToList(),
                SourceId = item.Id, BluePrintStatus = "Local", OwningPublication = TitleOf(item.PublicationId)
            }
        };
        foreach (var (fw, section) in m.ContentFields.Select(x => (x, IaFieldSection.Content)).Concat(m.MetadataFields.Select(x => (x, IaFieldSection.Metadata))))
        {
            var f = fw.Field;
            details.Fields.Add(new IaField
            {
                SchemaTitle = item.Title, Section = section, Order = f.Order, XmlName = f.XmlName, Label = f.Label, Type = f.Type,
                Mandatory = f.MinOccurs > 0, MinOccurs = f.MinOccurs, MaxOccurs = f.MaxOccurs,
                EmbeddedSchema = fw.EmbeddedSchemaId is null ? null : TitleOf(fw.EmbeddedSchemaId),
                AllowedTargetSchemas = fw.AllowedTargetSchemaIds.Select(TitleOf).ToList(),
                AllowMultimediaLinks = f.AllowMultimediaLinks, Category = fw.CategoryId is null ? null : TitleOf(fw.CategoryId),
                ListType = f.ListType, ListValues = f.ListValues, DefaultValue = f.DefaultValue, Height = f.Height,
                HelpText = f.HelpText
            });
            if (fw.CategoryId is not null) details.CategoryIds.Add(fw.CategoryId);
        }
        if (m.Region is { } r)
        {
            details.RegionRows.Add(new IaRegionRow
            {
                RegionSchemaTitle = item.Title, RowType = IaRegionRowType.Constraint, MinOccurs = r.MinOccurs, MaxOccurs = r.MaxOccurs,
                AllowedComponentSchemas = r.AllowedSchemaIds.Select(TitleOf).ToList(),
                AllowedComponentTemplates = r.AllowedTemplateIds.Select(TitleOf).ToList()
            });
            foreach (var n in r.NestedRegions)
                details.RegionRows.Add(new IaRegionRow
                {
                    RegionSchemaTitle = item.Title, RowType = IaRegionRowType.NestedRegion, NestedRegionName = n.Name,
                    NestedRegionSchema = TitleOf(n.RegionSchemaId), Mandatory = n.Mandatory
                });
        }
        return Task.FromResult(details);
    }

    public Task<IaCategory> ReadCategoryAsync(string categoryId)
    {
        var c = Get(categoryId);
        var cat = c.Category!;
        return Task.FromResult(new IaCategory
        {
            Title = c.Title, XmlName = cat.XmlName, Description = cat.Description, Publishable = cat.Publishable,
            UseForIdentification = cat.UseForIdentification,
            KeywordMetadataSchema = c.KeywordMetadataSchemaId is null ? null : TitleOf(c.KeywordMetadataSchemaId), SourceId = c.Id
        });
    }

    public Task<IReadOnlyList<NamedItem>> GetCategoriesAsync(string publicationId) =>
        Task.FromResult<IReadOnlyList<NamedItem>>(_items.Values.Where(i => i.Kind == "category" && i.PublicationId == publicationId).Select(i => new NamedItem(i.Id, i.Title)).ToList());

    public Task<IReadOnlyList<NamedItem>> GetKeywordsAsync(string categoryId) =>
        Task.FromResult<IReadOnlyList<NamedItem>>(_items.Values.Where(i => i.Kind == "keyword" && i.ParentId == categoryId).Select(i => new NamedItem(i.Id, i.Title)).ToList());

    // ------------------------------------------------------------------ templates
    public Task<IReadOnlyList<TemplateSummary>> GetTemplatesAsync(string publicationId, TemplateKind kind) =>
        Task.FromResult<IReadOnlyList<TemplateSummary>>(_items.Values
            .Where(i => i.Kind == (kind == TemplateKind.Component ? "ct" : "pt") && i.PublicationId == publicationId)
            .OrderBy(i => i.Title).Select(i => new TemplateSummary(i.Id, i.Title, kind)).ToList());

    public Task<IReadOnlyList<TemplateSummary>> GetTemplatesInFolderAsync(string folderId) =>
        Task.FromResult<IReadOnlyList<TemplateSummary>>(_items.Values.Where(i => i.Kind is "ct" or "pt" && i.ParentId == folderId)
            .OrderBy(i => i.Title).Select(i => new TemplateSummary(i.Id, i.Title, i.Kind == "ct" ? TemplateKind.Component : TemplateKind.Page)).ToList());

    public Task<TemplateDetails> ReadTemplateAsync(string templateId, TemplateFieldOptions fields)
    {
        var t = Get(templateId).Template ?? throw new InvalidOperationException($"{templateId} is not a template.");
        return Task.FromResult(t);
    }

    public Task<string> CreateTemplateAsync(string folderId, TemplateWriteModel model)
    {
        var folder = Get(folderId);
        var baseT = Get(model.BaseTemplateId).Template ?? throw new InvalidOperationException("Base template not found.");
        if (baseT.Kind != model.Kind) throw new InvalidOperationException("Base template is of the wrong type.");
        var kind = model.Kind == TemplateKind.Component ? "ct" : "pt";
        if (_items.Values.Any(i => i.Kind == kind && i.ParentId == folderId && IaWorkbook.Same(i.Title, model.Title)))
            throw new InvalidOperationException("A template with this title already exists in the folder.");
        foreach (var s in model.LinkedSchemaIds) Get(s);
        if (model.PageSchemaId is not null && Get(model.PageSchemaId).Schema?.Schema.Purpose != IaSchemaPurpose.Region)
            throw new InvalidOperationException("Page schema must be a Region schema.");
        var item = Add(kind, model.Title, folder.PublicationId, folderId, kind == "ct" ? 32 : 128);
        item.Template = new TemplateDetails
        {
            Id = item.Id, Title = model.Title, Kind = model.Kind, Description = model.Description,
            View = model.View ?? baseT.View, Dynamic = model.Dynamic ?? baseT.Dynamic, Priority = model.Priority ?? baseT.Priority,
            Controller = model.Controller ?? baseT.Controller, Action = model.Action ?? baseT.Action,
            RouteValues = model.RouteValues ?? baseT.RouteValues, HtmlClasses = model.HtmlClasses ?? baseT.HtmlClasses,
            LinkedSchemas = model.LinkedSchemaIds.Select(TitleOf).ToList(),
            PageSchema = model.PageSchemaId is null ? baseT.PageSchema : TitleOf(model.PageSchemaId),
            Includes = model.Includes ?? baseT.Includes, MetadataFields = baseT.MetadataFields, HasMetadataSchema = baseT.HasMetadataSchema
        };
        return Task.FromResult(item.Id);
    }

    public Task SetRegionTemplateConstraintsAsync(string regionSchemaId, IReadOnlyList<string> templateIds, string checkInComment)
    {
        var item = Get(regionSchemaId);
        var m = item.Schema ?? throw new InvalidOperationException("Not a schema.");
        if (m.Schema.Purpose != IaSchemaPurpose.Region) throw new InvalidOperationException("Not a Region schema.");
        foreach (var id in templateIds) Get(id);
        var region = m.Region ?? new RegionWriteModel();
        item.Schema = new SchemaWriteModel
        {
            Schema = m.Schema, ContentFields = m.ContentFields, MetadataFields = m.MetadataFields, MultimediaTypeIds = m.MultimediaTypeIds,
            CheckInComment = checkInComment,
            Region = new RegionWriteModel
            {
                MinOccurs = region.MinOccurs, MaxOccurs = region.MaxOccurs, NestedRegions = region.NestedRegions,
                AllowedSchemaIds = new List<string>(), AllowedTemplateIds = templateIds.ToList()
            }
        };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NamedItem>> GetComponentTemplatesAsync(string publicationId) =>
        Task.FromResult<IReadOnlyList<NamedItem>>(_items.Values.Where(i => i.Kind == "ct" && i.PublicationId == publicationId).Select(i => new NamedItem(i.Id, i.Title)).ToList());

    public Task<IReadOnlyList<MultimediaTypeInfo>> GetMultimediaTypesAsync() => Task.FromResult<IReadOnlyList<MultimediaTypeInfo>>(_mmTypes);

    public Task<string> CreateCategoryAsync(string publicationId, IaCategory category, string? keywordMetadataSchemaId)
    {
        var item = Add("category", category.Title, publicationId, null, 512);
        item.Category = category;
        item.KeywordMetadataSchemaId = keywordMetadataSchemaId;
        return Task.FromResult(item.Id);
    }

    public Task SetCategoryKeywordMetadataSchemaAsync(string categoryId, string schemaId)
    {
        Get(categoryId).KeywordMetadataSchemaId = schemaId;
        return Task.CompletedTask;
    }

    public Task<string> CreateKeywordAsync(string categoryId, IaKeyword keyword, IReadOnlyList<string> parentKeywordIds)
    {
        var cat = Get(categoryId);
        return Task.FromResult(Add("keyword", keyword.Title, cat.PublicationId, categoryId, 1024).Id);
    }

    public Task<string> CreateSchemaAsync(string folderId, SchemaWriteModel model)
    {
        var folder = Get(folderId);
        // Mirror CM rules so demo mode surfaces the same problems as a real import.
        if (model.Schema.Purpose == IaSchemaPurpose.Multimedia && model.MultimediaTypeIds.Count == 0)
            throw new InvalidOperationException("Invalid value for property 'AllowedMultimediaTypes' – a Multimedia schema needs at least one Multimedia Type.");
        if (model.Schema.Purpose == IaSchemaPurpose.Embedded && !string.IsNullOrEmpty(model.Schema.NamespaceUri))
            throw new InvalidOperationException("Namespace URI must be empty for embedded schemas.");
        foreach (var fw in model.ContentFields.Concat(model.MetadataFields))
        {
            if (fw.EmbeddedSchemaId is not null) Get(fw.EmbeddedSchemaId);
            foreach (var t in fw.AllowedTargetSchemaIds) Get(t);
            if (fw.CategoryId is not null) Get(fw.CategoryId);
        }
        if (_items.Values.Any(i => i.Kind == "schema" && i.ParentId == folderId && IaWorkbook.Same(i.Title, model.Schema.Title)))
            throw new InvalidOperationException("A schema with this title already exists in the folder.");
        var item = Add("schema", model.Schema.Title, folder.PublicationId, folderId, 8);
        item.Schema = model;
        return Task.FromResult(item.Id);
    }

    public Task UpdateSchemaAsync(string schemaId, SchemaWriteModel model)
    {
        Get(schemaId).Schema = model;
        return Task.CompletedTask;
    }
}
