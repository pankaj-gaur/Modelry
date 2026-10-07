#if CORESERVICE_PROXY
using System.ServiceModel;
using System.Xml;
using System.Xml.Linq;
using Modelry.Core.Gateway;
using Modelry.Core.Model;
using CS = Tridion.ContentManager.CoreService.Client;
#if CORESERVICE_SESSIONAWARE
// wsHttp endpoint contract (only with transport security – .NET 8 cannot use message security).
using CoreServiceContract = Tridion.ContentManager.CoreService.Client.ISessionAwareCoreService;
#else
// basicHttp endpoint contract (default): Windows authentication or OAuth bearer token.
using CoreServiceContract = Tridion.ContentManager.CoreService.Client.ICoreService;
#endif

namespace Modelry.Tridion;

/// <summary>
/// ITridionGateway over the Tridion Core Service (proxy generated with dotnet-svcutil).
/// Lines marked "VERIFY" use API members to confirm against your generated proxy on first build / dry run.
/// </summary>
public sealed class CoreServiceTridionGateway : ITridionGateway, IDisposable
{
    private readonly ChannelFactory<CoreServiceContract> _factory;
    private readonly CoreServiceContract _client;
    private static readonly CS.ReadOptions ReadOpts = new();
    private List<MultimediaTypeInfo>? _mmTypes;
    private readonly string _endpoint;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, List<MultimediaTypeInfo> Types)> MmCache = new();

    public CoreServiceTridionGateway(TridionOptions options, CoreServiceConnection connection)
    {
        _endpoint = connection.Url;
        _factory = new ChannelFactory<CoreServiceContract>(CoreServiceChannel.CreateBinding(options, connection), new EndpointAddress(connection.Url));
        CoreServiceChannel.Configure(_factory, connection);
        _client = _factory.CreateChannel();
    }

    public void Dispose()
    {
        if (_client is ICommunicationObject c)
        {
            try { if (c.State == CommunicationState.Faulted) c.Abort(); else c.Close(); } catch { c.Abort(); }
        }
        try { _factory.Close(); } catch { _factory.Abort(); }
    }

    // ------------------------------------------------------------------ browsing
    public async Task<string> GetApiVersionAsync() => await _client.GetApiVersionAsync();

    public async Task<IReadOnlyList<TreeNode>> GetPublicationsAsync()
    {
        var items = await _client.GetSystemWideListAsync(new CS.PublicationsFilterData());
        return items.OrderBy(i => i.Title).Select(i => new TreeNode(i.Id, i.Title, NodeType.Publication, true)).ToList();
    }

    public async Task<TreeNode> GetPublicationRootFolderAsync(string publicationId)
    {
        var pub = (CS.PublicationData)await _client.ReadAsync(publicationId, ReadOpts);
        var root = pub.RootFolder;                                                   // VERIFY: PublicationData.RootFolder
        return new TreeNode(root.IdRef, root.Title, NodeType.Folder, true, await CountSchemasAsync(root.IdRef));
    }

    public async Task<IReadOnlyList<TreeNode>> GetSubFoldersAsync(string folderId)
    {
        var items = await _client.GetListAsync(folderId, new CS.OrganizationalItemItemsFilterData { ItemTypes = new[] { CS.ItemType.Folder }, Recursive = false });
        var nodes = new List<TreeNode>();
        foreach (var i in items.OrderBy(i => i.Title))
            nodes.Add(new TreeNode(i.Id, i.Title, NodeType.Folder, true, await CountSchemasAsync(i.Id)));
        return nodes;
    }

    public async Task<FolderInfo> GetFolderAsync(string folderId)
    {
        var f = (CS.FolderData)await _client.ReadAsync(folderId, ReadOpts);
        var pubId = PublicationIdOf(folderId);
        var pub = await _client.ReadAsync(pubId, ReadOpts);
        return new FolderInfo(f.Id, f.Title, pubId, pub.Title, PathFromRoot(LocationPath(f), f.Title));
    }

    /// <summary>LocationInfo.Path, e.g. "\\010 Schema Master\\Building Blocks\\Schemas" (the item's parent).</summary>
    private static string? LocationPath(object item) => Reflect.GetString(Reflect.Get(item, "LocationInfo"), "Path");   // VERIFY: LocationInfo.Path

    /// <summary>Converts a Tridion location path (+ own title for folders) to a '/'-path below the root folder.</summary>
    private static string PathFromRoot(string? locationPath, string? ownTitle = null)
    {
        var parts = (locationPath ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (ownTitle is not null) parts.Add(ownTitle);
        var below = parts.Skip(2).ToList();            // drop publication + root folder
        return below.Count == 0 ? "." : string.Join("/", below);
    }

    public async Task<IReadOnlyList<NamedItem>> ListSchemasInFolderAsync(string folderId) =>
        (await _client.GetListAsync(folderId, new CS.OrganizationalItemItemsFilterData { ItemTypes = new[] { CS.ItemType.Schema }, Recursive = false }))
            .Select(i => new NamedItem(i.Id, i.Title)).ToList();

    public async Task<IReadOnlyList<NamedItem>> ListPublicationSchemasAsync(string publicationId) =>
        (await _client.GetListAsync(publicationId, new CS.RepositoryItemsFilterData { ItemTypes = new[] { CS.ItemType.Schema }, Recursive = true }))
            .Select(i => new NamedItem(i.Id, i.Title)).ToList();

    public async Task<SchemaInfo> ReadSchemaInfoAsync(string schemaId)
    {
        var s = (CS.SchemaData)await _client.ReadAsync(schemaId, ReadOpts);
        return new SchemaInfo(s.Id, s.Purpose?.ToString() ?? "", PathFromRoot(LocationPath(s)));
    }

    public async Task<TreeNode> CreateFolderAsync(string parentFolderId, string title)
    {
        var f = (CS.FolderData)await _client.GetDefaultDataAsync(CS.ItemType.Folder, parentFolderId, ReadOpts);
        f.Title = title.Trim();
        var created = await _client.CreateAsync(f, ReadOpts);
        return new TreeNode(created.Id, created.Title, NodeType.Folder, false, 0);
    }

    public async Task<int> CountSchemasAsync(string folderId) =>
        (await _client.GetListAsync(folderId, new CS.OrganizationalItemItemsFilterData { ItemTypes = new[] { CS.ItemType.Schema }, Recursive = false })).Length;

    public async Task<IReadOnlyList<SchemaSummary>> GetSchemasInFolderAsync(string folderId)
    {
        var items = await _client.GetListAsync(folderId, new CS.OrganizationalItemItemsFilterData { ItemTypes = new[] { CS.ItemType.Schema }, Recursive = false });
        var list = new List<SchemaSummary>();
        foreach (var i in items)
        {
            var purpose = (i as CS.SchemaData)?.Purpose?.ToString();                 // list data may omit Purpose …
            if (string.IsNullOrEmpty(purpose) || purpose == "UnknownByClient")
                purpose = ((CS.SchemaData)await _client.ReadAsync(i.Id, ReadOpts)).Purpose?.ToString(); // … then read the item
            list.Add(new SchemaSummary(i.Id, i.Title, purpose ?? ""));
        }
        return list;
    }

    // ------------------------------------------------------------------ reading schemas
    public async Task<SchemaDetails> ReadSchemaAsync(string schemaId)
    {
        var s = (CS.SchemaData)await _client.ReadAsync(schemaId, ReadOpts);
        var sf = await _client.ReadSchemaFieldsAsync(schemaId, false, ReadOpts);    // VERIFY: ReadSchemaFields(id, expandEmbedded, options)
        var mm = await GetMultimediaTypesAsync();
        var purposeText = s.Purpose?.ToString() ?? "Component";
        IaVocabulary.TryParsePurpose(purposeText, out var purpose);
        var bp = s.BluePrintInfo;
        var details = new SchemaDetails
        {
            Schema = new IaSchema
            {
                Title = s.Title, Purpose = purpose,
                RootElementName = sf.RootElementName ?? s.RootElementName, NamespaceUri = sf.NamespaceUri ?? s.NamespaceUri,
                Description = s.Description,
                AllowedMultimediaTypes = (s.AllowedMultimediaTypes ?? Array.Empty<CS.LinkToMultimediaTypeData>())
                    .Select(l => mm.FirstOrDefault(t => t.Id == l.IdRef)?.Extensions.FirstOrDefault() ?? l.Title).ToList(),
                SourceId = s.Id,
                BluePrintStatus = bp?.IsShared == true ? "Shared" : bp?.IsLocalized == true ? "Localized" : "Local",
                OwningPublication = bp?.OwningRepository?.Title
            }
        };
        var order = 1;
        foreach (var d in sf.Fields ?? Array.Empty<CS.ItemFieldDefinitionData>())
            details.Fields.Add(ToIaField(d, s.Title, IaFieldSection.Content, order++, details.CategoryIds));
        order = 1;
        foreach (var d in sf.MetadataFields ?? Array.Empty<CS.ItemFieldDefinitionData>())
            details.Fields.Add(ToIaField(d, s.Title, IaFieldSection.Metadata, order++, details.CategoryIds));
        if (s.RegionDefinition is { } rd) details.RegionRows.AddRange(ToRegionRows(rd, s.Title)); // VERIFY: SchemaData.RegionDefinition
        return details;
    }

    private static IaField ToIaField(CS.ItemFieldDefinitionData d, string schemaTitle, IaFieldSection section, int order, HashSet<string> categoryIds)
    {
        var f = new IaField
        {
            SchemaTitle = schemaTitle, Section = section, Order = order, XmlName = d.Name, Label = d.Description ?? d.Name,
            MinOccurs = d.MinOccurs, MaxOccurs = d.MaxOccurs, Mandatory = d.MinOccurs > 0,
            DefaultValue = Reflect.GetString(d, "DefaultValue"),
            Height = int.TryParse(Reflect.GetString(d, "Height"), out var h) ? h : null
        };
        // Most specific types first (Multimedia link derives from Component link; Xhtml may derive from multi-line text).
        switch (d)
        {
            case CS.MultimediaLinkFieldDefinitionData mm:
                f.Type = IaFieldType.MultimediaLink;
                f.AllowedTargetSchemas = (mm.AllowedTargetSchemas ?? Array.Empty<CS.LinkToSchemaData>()).Select(l => l.Title).ToList();
                break;
            case CS.ComponentLinkFieldDefinitionData cl:
                f.Type = IaFieldType.ComponentLink;
                f.AllowedTargetSchemas = (cl.AllowedTargetSchemas ?? Array.Empty<CS.LinkToSchemaData>()).Select(l => l.Title).ToList();
                f.AllowMultimediaLinks = Reflect.GetString(cl, "AllowMultimediaLinks") == "True";
                break;
            case CS.XhtmlFieldDefinitionData: f.Type = IaFieldType.RichText; break;
            case CS.MultiLineTextFieldDefinitionData: f.Type = IaFieldType.MultiLineText; break;
            case CS.SingleLineTextFieldDefinitionData: f.Type = IaFieldType.Text; break;
            case CS.NumberFieldDefinitionData: f.Type = IaFieldType.Number; break;
            case CS.DateFieldDefinitionData: f.Type = IaFieldType.Date; break;
            case CS.ExternalLinkFieldDefinitionData: f.Type = IaFieldType.ExternalLink; break;
            case CS.KeywordFieldDefinitionData k:
                f.Type = IaFieldType.Keyword;
                f.Category = k.Category?.Title;
                if (k.Category?.IdRef is { } cid) categoryIds.Add(cid);
                break;
            case CS.EmbeddedSchemaFieldDefinitionData e:
                f.Type = IaFieldType.EmbeddedSchema;
                f.EmbeddedSchema = e.EmbeddedSchema?.Title;
                break;
        }
        var list = Reflect.Get(d, "List");
        if (list is not null)
        {
            var type = Reflect.GetString(list, "Type");
            if (!string.IsNullOrEmpty(type) && type != "None") f.ListType = type;
            f.ListValues = Reflect.GetStrings(list, "Entries");
        }
        return f;
    }

    private static IEnumerable<IaRegionRow> ToRegionRows(CS.RegionDefinitionData rd, string schemaTitle)
    {
        var constraint = new IaRegionRow { RegionSchemaTitle = schemaTitle, RowType = IaRegionRowType.Constraint };
        foreach (var c in rd.ComponentPresentationConstraints ?? Array.Empty<CS.ComponentPresentationConstraintData>())
        {
            switch (c)
            {
                case CS.OccurrenceConstraintData o:                                  // VERIFY: OccurrenceConstraintData
                    constraint.MinOccurs = o.MinOccurs; constraint.MaxOccurs = o.MaxOccurs; break;
                case CS.TypeConstraintData t:                                        // VERIFY: TypeConstraintData
                    if (Reflect.Get(t, "BasedOnSchema") is CS.LinkToSchemaData ls && ls.IdRef is not null && ls.IdRef != "tcm:0-0-0") constraint.AllowedComponentSchemas.Add(ls.Title);
                    if (Reflect.Get(t, "BasedOnComponentTemplate") is CS.LinkToComponentTemplateData lt && lt.IdRef is not null && lt.IdRef != "tcm:0-0-0") constraint.AllowedComponentTemplates.Add(lt.Title);
                    break;
            }
        }
        var rows = new List<IaRegionRow>();
        if (constraint.MinOccurs is not null || constraint.MaxOccurs is not null || constraint.AllowedComponentSchemas.Count > 0 || constraint.AllowedComponentTemplates.Count > 0)
            rows.Add(constraint);
        // NestedRegions is generated as a collection class (NestedRegionDataList), not an array.
        foreach (var n in (rd.NestedRegions as System.Collections.IEnumerable)?.OfType<CS.NestedRegionData>() ?? Enumerable.Empty<CS.NestedRegionData>())
            rows.Add(new IaRegionRow
            {
                RegionSchemaTitle = schemaTitle, RowType = IaRegionRowType.NestedRegion, NestedRegionName = n.RegionName,
                NestedRegionSchema = n.RegionSchema?.Title, Mandatory = Reflect.GetString(n, "IsMandatory") == "True"
            });
        return rows;
    }

    public async Task<IaCategory> ReadCategoryAsync(string categoryId)
    {
        var c = (CS.CategoryData)await _client.ReadAsync(categoryId, ReadOpts);
        return new IaCategory
        {
            Title = c.Title,
            XmlName = Reflect.GetString(c, "XmlName") ?? c.Title,
            Description = c.Description,
            Publishable = Reflect.GetString(c, "IsPublishable", "Publishable") != "False",
            UseForIdentification = Reflect.GetString(c, "UseForIdentification") == "True",
            KeywordMetadataSchema = (Reflect.Get(c, "KeywordMetadataSchema") as CS.LinkToSchemaData)?.Title,
            SourceId = c.Id
        };
    }

    // ------------------------------------------------------------------ lookups
    public async Task<IReadOnlyList<NamedItem>> GetCategoriesAsync(string publicationId) =>
        (await _client.GetListAsync(publicationId, new CS.RepositoryItemsFilterData { ItemTypes = new[] { CS.ItemType.Category }, Recursive = false }))
            .Select(i => new NamedItem(i.Id, i.Title)).ToList();

    public async Task<IReadOnlyList<NamedItem>> GetKeywordsAsync(string categoryId) =>
        (await _client.GetListAsync(categoryId, new CS.OrganizationalItemItemsFilterData { ItemTypes = new[] { CS.ItemType.Keyword }, Recursive = true }))
            .Select(i => new NamedItem(i.Id, i.Title)).ToList();

    public async Task<IReadOnlyList<NamedItem>> GetComponentTemplatesAsync(string publicationId) =>
        (await _client.GetListAsync(publicationId, new CS.RepositoryItemsFilterData { ItemTypes = new[] { CS.ItemType.ComponentTemplate }, Recursive = true }))
            .Select(i => new NamedItem(i.Id, i.Title)).ToList();

    public async Task<IReadOnlyList<MultimediaTypeInfo>> GetMultimediaTypesAsync()
    {
        if (_mmTypes is not null) return _mmTypes;
        if (MmCache.TryGetValue(_endpoint, out var cached) && cached.At > DateTime.UtcNow.AddMinutes(-30)) return _mmTypes = cached.Types;
        var items = await _client.GetSystemWideListAsync(new CS.MultimediaTypesFilterData());
        var result = new List<MultimediaTypeInfo>();
        foreach (var i in items)
        {
            var exts = Reflect.GetStrings(i, "FileExtensions");
            if (exts.Count == 0)  // list data may be partial – read the item
                exts = Reflect.GetStrings(await _client.ReadAsync(i.Id, ReadOpts), "FileExtensions");
            result.Add(new MultimediaTypeInfo(i.Id, i.Title, exts.Select(e => e.TrimStart('.').ToLowerInvariant()).ToList()));
        }
        MmCache[_endpoint] = (DateTime.UtcNow, result);
        return _mmTypes = result;
    }

    // ------------------------------------------------------------------ taxonomy writes
    public async Task<string> CreateCategoryAsync(string publicationId, IaCategory category, string? keywordMetadataSchemaId)
    {
        var c = (CS.CategoryData)await _client.GetDefaultDataAsync(CS.ItemType.Category, publicationId, ReadOpts);
        c.Title = category.Title;
        c.Description = category.Description ?? category.Title;
        Reflect.TrySet(c, "XmlName", category.XmlName);
        Reflect.TrySetAny(c, category.Publishable, "IsPublishable", "Publishable");
        Reflect.TrySet(c, "UseForIdentification", category.UseForIdentification);
        if (keywordMetadataSchemaId is not null)
            Reflect.TrySet(c, "KeywordMetadataSchema", new CS.LinkToSchemaData { IdRef = keywordMetadataSchemaId });
        return (await _client.CreateAsync(c, ReadOpts)).Id;
    }

    public async Task SetCategoryKeywordMetadataSchemaAsync(string categoryId, string schemaId)
    {
        var c = (CS.CategoryData)await _client.ReadAsync(categoryId, ReadOpts);
        Reflect.TrySet(c, "KeywordMetadataSchema", new CS.LinkToSchemaData { IdRef = schemaId });
        await _client.UpdateAsync(c, ReadOpts);
    }

    public async Task<string> CreateKeywordAsync(string categoryId, IaKeyword keyword, IReadOnlyList<string> parentKeywordIds)
    {
        var k = (CS.KeywordData)await _client.GetDefaultDataAsync(CS.ItemType.Keyword, categoryId, ReadOpts);
        k.Title = keyword.Title;
        k.Description = keyword.Description ?? keyword.Title;
        Reflect.TrySet(k, "Key", keyword.Key ?? keyword.Title);
        Reflect.TrySet(k, "IsAbstract", keyword.IsAbstract);
        if (parentKeywordIds.Count > 0)
            k.ParentKeywords = parentKeywordIds.Select(id => new CS.LinkToKeywordData { IdRef = id }).ToArray();
        return (await _client.CreateAsync(k, ReadOpts)).Id;
    }

    // ------------------------------------------------------------------ schema writes
    public async Task<string> CreateSchemaAsync(string folderId, SchemaWriteModel model)
    {
        var s = (CS.SchemaData)await _client.GetDefaultDataAsync(CS.ItemType.Schema, folderId, ReadOpts);
        s.Title = model.Schema.Title;
        s.Description = model.Schema.Description ?? model.Schema.Title;
        s.Purpose = Enum.Parse<CS.SchemaPurpose>(model.Schema.Purpose.ToString());     // VERIFY: enum member names incl. Region
        if (!string.IsNullOrWhiteSpace(model.Schema.RootElementName)) s.RootElementName = model.Schema.RootElementName;
        if (model.Schema.Purpose == IaSchemaPurpose.Embedded) s.NamespaceUri = string.Empty;   // CM: "Namespace URI must be empty for embedded schemas"
        else if (!string.IsNullOrWhiteSpace(model.Schema.NamespaceUri)) s.NamespaceUri = model.Schema.NamespaceUri;
        if (model.Schema.Purpose == IaSchemaPurpose.Multimedia && model.MultimediaTypeIds.Count == 0)
            throw new InvalidOperationException("Multimedia schemas need at least one allowed Multimedia Type that exists in the CMS.");
        if (model.MultimediaTypeIds.Count > 0)
            s.AllowedMultimediaTypes = model.MultimediaTypeIds.Select(id => new CS.LinkToMultimediaTypeData { IdRef = id }).ToArray();
        await ApplyFieldsAsync(s, model);
        if (model.Region is not null) s.RegionDefinition = ToRegionDefinition(model.Region);
        var created = await _client.CreateAsync(s, ReadOpts);
        await TryCheckInAsync(created.Id, model.CheckInComment);
        return created.Id;
    }

    public async Task UpdateSchemaAsync(string schemaId, SchemaWriteModel model)
    {
        var s = (CS.SchemaData)await _client.CheckOutAsync(schemaId, true, ReadOpts);
        try
        {
            await ApplyFieldsAsync(s, model);
            if (model.Region is not null) s.RegionDefinition = ToRegionDefinition(model.Region);
            await _client.UpdateAsync(s, ReadOpts);
            await TryCheckInAsync(schemaId, model.CheckInComment);
        }
        catch
        {
            try { await _client.UndoCheckOutAsync(schemaId, true, ReadOpts); } catch { /* best effort */ }
            throw;
        }
    }

    private async Task ApplyFieldsAsync(CS.SchemaData s, SchemaWriteModel model)
    {
        var sf = new CS.SchemaFieldsData
        {
            RootElementName = s.RootElementName,
            NamespaceUri = model.Schema.Purpose == IaSchemaPurpose.Embedded ? string.Empty : s.NamespaceUri,
            Fields = model.ContentFields.Select(ToDefinition).ToArray(),
            MetadataFields = model.MetadataFields.Select(ToDefinition).ToArray()
        };
        object xsd = await _client.ConvertSchemaFieldsToXsdAsync(sf);                 // VERIFY: ConvertSchemaFieldsToXsd(SchemaFieldsData)
        s.Xsd = XsdText(xsd);
    }

    private static string XsdText(object xsd) => xsd switch
    {
        XElement x => x.ToString(SaveOptions.DisableFormatting),
        XmlNode n => n.OuterXml,
        string str => str,
        _ => xsd.ToString() ?? ""
    };

    private static CS.ItemFieldDefinitionData ToDefinition(FieldWriteModel fw)
    {
        var f = fw.Field;
        CS.ItemFieldDefinitionData d = f.Type switch
        {
            IaFieldType.Text => new CS.SingleLineTextFieldDefinitionData(),
            IaFieldType.MultiLineText => new CS.MultiLineTextFieldDefinitionData(),
            IaFieldType.RichText => new CS.XhtmlFieldDefinitionData(),
            IaFieldType.Number => new CS.NumberFieldDefinitionData(),
            IaFieldType.Date => new CS.DateFieldDefinitionData(),
            IaFieldType.ExternalLink => new CS.ExternalLinkFieldDefinitionData(),
            IaFieldType.ComponentLink => new CS.ComponentLinkFieldDefinitionData
            {
                AllowedTargetSchemas = fw.AllowedTargetSchemaIds.Select(id => new CS.LinkToSchemaData { IdRef = id }).ToArray()
            },
            IaFieldType.MultimediaLink => new CS.MultimediaLinkFieldDefinitionData
            {
                AllowedTargetSchemas = fw.AllowedTargetSchemaIds.Select(id => new CS.LinkToSchemaData { IdRef = id }).ToArray()
            },
            IaFieldType.Keyword => new CS.KeywordFieldDefinitionData { Category = new CS.LinkToCategoryData { IdRef = fw.CategoryId } },
            IaFieldType.EmbeddedSchema => new CS.EmbeddedSchemaFieldDefinitionData { EmbeddedSchema = new CS.LinkToSchemaData { IdRef = fw.EmbeddedSchemaId } },
            _ => throw new NotSupportedException(f.Type.ToString())
        };
        d.Name = f.XmlName;
        d.Description = Describe(f);
        d.MinOccurs = f.MinOccurs;
        d.MaxOccurs = f.MaxOccurs;                                                   // -1 = unbounded
        if (f.Type == IaFieldType.ComponentLink) Reflect.TrySet(d, "AllowMultimediaLinks", f.AllowMultimediaLinks);
        if (f.Height is not null) Reflect.TrySet(d, "Height", f.Height);
        if (f.DefaultValue is not null) Reflect.TrySet(d, "DefaultValue", f.DefaultValue);
        if (f.ListType is not null || f.ListValues.Count > 0) SetList(d, f);
        return d;
    }

    /// <summary>Label plus help text (Tridion has no separate help property), max 255 chars.</summary>
    private static string Describe(IaField f)
    {
        var text = string.IsNullOrWhiteSpace(f.HelpText) ? f.Label : $"{f.Label} – {f.HelpText}";
        return text.Length > 255 ? text[..252] + "..." : text;
    }

    private static void SetList(CS.ItemFieldDefinitionData d, IaField f)
    {
        var prop = d.GetType().GetProperty("List");
        if (prop is null) return;
        var list = Activator.CreateInstance(prop.PropertyType)!;
        Reflect.TrySet(list, "Type", f.ListType ?? "Select");
        if (f.ListValues.Count > 0) Reflect.TrySet(list, "Entries", f.ListValues);
        prop.SetValue(d, list);
    }

    private static CS.RegionDefinitionData ToRegionDefinition(RegionWriteModel r)
    {
        var constraints = new List<CS.ComponentPresentationConstraintData>();
        if (r.MinOccurs is not null || r.MaxOccurs is not null)
            constraints.Add(new CS.OccurrenceConstraintData { MinOccurs = r.MinOccurs ?? 0, MaxOccurs = r.MaxOccurs ?? -1 });
        foreach (var id in r.AllowedSchemaIds)
        {
            var t = new CS.TypeConstraintData();
            Reflect.TrySet(t, "BasedOnSchema", new CS.LinkToSchemaData { IdRef = id });
            constraints.Add(t);
        }
        foreach (var id in r.AllowedTemplateIds)
        {
            var t = new CS.TypeConstraintData();
            Reflect.TrySet(t, "BasedOnComponentTemplate", new CS.LinkToComponentTemplateData { IdRef = id });
            constraints.Add(t);
        }

        var nested = r.NestedRegions.Select(n =>
        {
            // RegionSchema is an ExpandableLinkToSchemaData in the generated proxy.
            var nr = new CS.NestedRegionData { RegionName = n.Name, RegionSchema = new CS.ExpandableLinkToSchemaData { IdRef = n.RegionSchemaId } };
            Reflect.TrySet(nr, "IsMandatory", n.Mandatory);
            return nr;
        }).ToList();

        var def = new CS.RegionDefinitionData();
        Reflect.SetCollection(def, "ComponentPresentationConstraints", constraints);
        Reflect.SetCollection(def, "NestedRegions", nested);
        return def;
    }

    // ------------------------------------------------------------------ templates
    private static readonly (string Name, int Value)[] PriorityValues = { ("High", 300), ("Medium", 200), ("Low", 100), ("Never Link", 0) };
    private readonly Dictionary<string, (string Ns, List<string> Fields)> _metaSchemas = new();

    private static TemplateKind KindOf(string tcmUri) => tcmUri.Split('-').ElementAtOrDefault(2) == "128" ? TemplateKind.Page : TemplateKind.Component;

    public async Task<IReadOnlyList<TemplateSummary>> GetTemplatesAsync(string publicationId, TemplateKind kind)
    {
        var type = kind == TemplateKind.Component ? CS.ItemType.ComponentTemplate : CS.ItemType.PageTemplate;
        var items = await _client.GetListAsync(publicationId, new CS.RepositoryItemsFilterData { ItemTypes = new[] { type }, Recursive = true });
        return items.OrderBy(i => i.Title).Select(i => new TemplateSummary(i.Id, i.Title, kind)).ToList();
    }

    public async Task<IReadOnlyList<TemplateSummary>> GetTemplatesInFolderAsync(string folderId)
    {
        var items = await _client.GetListAsync(folderId, new CS.OrganizationalItemItemsFilterData
            { ItemTypes = new[] { CS.ItemType.ComponentTemplate, CS.ItemType.PageTemplate }, Recursive = false });
        return items.OrderBy(i => i.Title).Select(i => new TemplateSummary(i.Id, i.Title, KindOf(i.Id))).ToList();
    }

    /// <summary>Namespace and ordered field names of a template metadata schema (cached).</summary>
    private async Task<(string Ns, List<string> Fields)> MetadataSchemaAsync(string schemaId)
    {
        if (_metaSchemas.TryGetValue(schemaId, out var cached)) return cached;
        var sf = await _client.ReadSchemaFieldsAsync(schemaId, false, ReadOpts);
        var fields = (sf.MetadataFields ?? Array.Empty<CS.ItemFieldDefinitionData>()).Concat(sf.Fields ?? Array.Empty<CS.ItemFieldDefinitionData>())
            .Select(f => f.Name).ToList();
        return _metaSchemas[schemaId] = (sf.NamespaceUri ?? "", fields);
    }

    public async Task<TemplateDetails> ReadTemplateAsync(string templateId, TemplateFieldOptions fields)
    {
        var item = await _client.ReadAsync(templateId, ReadOpts);
        var kind = KindOf(templateId);
        var metaSchemaId = (Reflect.Get(item, "MetadataSchema") as CS.LinkToSchemaData)?.IdRef;
        var hasSchema = metaSchemaId is not null && metaSchemaId != "tcm:0-0-0";
        var metaFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasSchema) metaFields.UnionWith((await MetadataSchemaAsync(metaSchemaId!)).Fields);
        var xml = Reflect.GetString(item, "Metadata");
        var meta = string.IsNullOrWhiteSpace(xml) ? null : XElement.Parse(xml);
        IEnumerable<string> Values(string field) => meta?.Elements().Where(e => e.Name.LocalName == field).Select(e => e.Value) ?? Enumerable.Empty<string>();

        string? priority = null;
        if (int.TryParse(Reflect.GetString(item, "Priority"), out var p))
            priority = PriorityValues.OrderByDescending(v => v.Value).First(v => p >= v.Value).Name;
        var dynText = fields.CtDynamicField.Length > 0 ? Values(fields.CtDynamicField).FirstOrDefault() : null;
        bool? dynamic = kind == TemplateKind.Component
            ? (dynText is not null ? dynText.Equals(fields.CtDynamicTrueValue, StringComparison.OrdinalIgnoreCase) : Reflect.GetString(item, "IsRepositoryPublishable") == "True")
            : null;
        var related = Reflect.Get(item, "RelatedSchemas") as System.Collections.IEnumerable;
        return new TemplateDetails
        {
            Id = item.Id, Title = item.Title, Kind = kind, Description = Reflect.GetString(item, "Description"),
            View = Values(kind == TemplateKind.Component ? fields.CtViewField : fields.PtViewField).FirstOrDefault(),
            Controller = kind == TemplateKind.Component ? Values(fields.CtControllerField).FirstOrDefault() : null,
            Action = kind == TemplateKind.Component ? Values(fields.CtActionField).FirstOrDefault() : null,
            RouteValues = kind == TemplateKind.Component ? Values(fields.CtRouteValuesField).FirstOrDefault() : null,
            HtmlClasses = kind == TemplateKind.Component ? Values(fields.CtHtmlClassesField).FirstOrDefault() : null,
            Dynamic = dynamic, Priority = kind == TemplateKind.Component ? priority : null,
            LinkedSchemas = related?.Cast<object>().Select(l => Reflect.GetString(l, "Title") ?? "").Where(s => s.Length > 0).ToList() ?? new List<string>(),
            PageSchema = kind == TemplateKind.Page ? (Reflect.Get(item, "PageSchema") as CS.LinkToSchemaData)?.Title : null,   // VERIFY: PageTemplateData.PageSchema
            Includes = kind == TemplateKind.Page ? Values(fields.PtIncludesField).ToList() : new List<string>(),
            MetadataFields = metaFields, HasMetadataSchema = hasSchema
        };
    }

    public async Task<string> CreateTemplateAsync(string folderId, TemplateWriteModel model)
    {
        // 1. clone the base template (same publication context, so copy is allowed)
        var copy = await _client.CopyAsync(model.BaseTemplateId, folderId, true, ReadOpts);           // VERIFY: Copy(id, destinationId, makeUnique, options)
        try
        {
            var t = await _client.CheckOutAsync(copy.Id, true, ReadOpts);
            t.Title = model.Title;
            Reflect.TrySet(t, "Description", model.Description ?? model.Title);

            // 2. metadata: change only the configured DXA fields, keep everything else from the base
            var metaSchemaId = (Reflect.Get(t, "MetadataSchema") as CS.LinkToSchemaData)?.IdRef;
            if (metaSchemaId is not null && metaSchemaId != "tcm:0-0-0")
            {
                var (ns, order) = await MetadataSchemaAsync(metaSchemaId);
                var xml = Reflect.GetString(t, "Metadata");
                var f = model.Fields;
                if (model.View is not null)
                    xml = SetMetadata(xml, ns, order, model.Kind == TemplateKind.Component ? f.CtViewField : f.PtViewField, new[] { model.View });
                if (model.Kind == TemplateKind.Component)
                    foreach (var (value, field) in new[] { (model.Controller, f.CtControllerField), (model.Action, f.CtActionField),
                                                           (model.RouteValues, f.CtRouteValuesField), (model.HtmlClasses, f.CtHtmlClassesField) })
                        if (value is not null) xml = SetMetadata(xml, ns, order, field, new[] { value });
                if (model.Kind == TemplateKind.Component && model.Dynamic is not null && f.CtDynamicField.Length > 0 && order.Contains(f.CtDynamicField, StringComparer.OrdinalIgnoreCase))
                    xml = SetMetadata(xml, ns, order, f.CtDynamicField, new[] { model.Dynamic.Value ? f.CtDynamicTrueValue : f.CtDynamicFalseValue });
                if (model.Kind == TemplateKind.Page && model.Includes is not null)
                    xml = SetMetadata(xml, ns, order, f.PtIncludesField, model.Includes);
                Reflect.TrySet(t, "Metadata", xml);
            }

            // 3. template properties
            if (model.Kind == TemplateKind.Component)
            {
                Reflect.TrySet(t, "RelatedSchemas", model.LinkedSchemaIds.Select(id => new CS.LinkToSchemaData { IdRef = id }).ToArray());
                if (model.Priority is not null)
                    Reflect.TrySet(t, "Priority", PriorityValues.First(v => v.Name.Equals(model.Priority, StringComparison.OrdinalIgnoreCase)).Value);
                if (model.Dynamic is not null) Reflect.TrySet(t, "IsRepositoryPublishable", model.Dynamic.Value);
            }
            else if (model.PageSchemaId is not null)
            {
                Reflect.TrySet(t, "PageSchema", new CS.LinkToSchemaData { IdRef = model.PageSchemaId });                    // VERIFY: PageTemplateData.PageSchema
            }

            await _client.UpdateAsync(t, ReadOpts);
            await TryCheckInAsync(copy.Id, model.Fields.CheckInComment);
            return copy.Id;
        }
        catch
        {
            // do not leave a half-configured "Copy of …" behind
            try { await _client.UndoCheckOutAsync(copy.Id, true, ReadOpts); } catch { /* ignore */ }
            try { await _client.DeleteAsync(copy.Id); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>Replaces the values of one metadata field, inserting it in schema order when it does not exist yet.</summary>
    private static string SetMetadata(string? xml, string ns, List<string> order, string field, IEnumerable<string> values)
    {
        var doc = string.IsNullOrWhiteSpace(xml) ? new XElement(XName.Get("Metadata", ns)) : XElement.Parse(xml);
        XNamespace n = doc.Name.Namespace;
        doc.Elements().Where(e => e.Name.LocalName == field).Remove();
        var els = values.Select(v => new XElement(n + field, v)).ToList();
        if (els.Count > 0)
        {
            var idx = order.FindIndex(o => o.Equals(field, StringComparison.OrdinalIgnoreCase));
            var after = doc.Elements().LastOrDefault(e => { var i = order.FindIndex(o => o == e.Name.LocalName); return i >= 0 && i < idx; });
            if (after is not null) after.AddAfterSelf(els); else doc.AddFirst(els);
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public async Task SetRegionTemplateConstraintsAsync(string regionSchemaId, IReadOnlyList<string> templateIds, string checkInComment)
    {
        var s = (CS.SchemaData)await _client.CheckOutAsync(regionSchemaId, true, ReadOpts);
        try
        {
            var rd = s.RegionDefinition ?? new CS.RegionDefinitionData();
            var kept = ((Reflect.Get(rd, "ComponentPresentationConstraints") as System.Collections.IEnumerable)?.Cast<CS.ComponentPresentationConstraintData>()
                        ?? Enumerable.Empty<CS.ComponentPresentationConstraintData>())
                       .Where(c => c is not CS.TypeConstraintData).ToList();                 // keep occurrence limits, replace type constraints
            foreach (var id in templateIds)
            {
                var tc = new CS.TypeConstraintData();
                Reflect.TrySet(tc, "BasedOnComponentTemplate", new CS.LinkToComponentTemplateData { IdRef = id });
                kept.Add(tc);
            }
            Reflect.SetCollection(rd, "ComponentPresentationConstraints", kept);
            s.RegionDefinition = rd;
            await _client.UpdateAsync(s, ReadOpts);
            await TryCheckInAsync(regionSchemaId, checkInComment);
        }
        catch
        {
            try { await _client.UndoCheckOutAsync(regionSchemaId, true, ReadOpts); } catch { /* best effort */ }
            throw;
        }
    }

    private async Task TryCheckInAsync(string id, string comment)
    {
        try { await _client.CheckInAsync(id, true, comment, ReadOpts); }
        catch (FaultException) { /* Create/Update may already have checked the item in */ }
    }

    private static string PublicationIdOf(string tcmUri)
    {
        // tcm:{pub}-{item}-{type}[-v{n}] → tcm:0-{pub}-1
        var pub = tcmUri.Split(':')[1].Split('-')[0];
        return $"tcm:0-{pub}-1";
    }
}
#endif
