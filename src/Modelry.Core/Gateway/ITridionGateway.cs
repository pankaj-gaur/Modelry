using Modelry.Core.Model;

namespace Modelry.Core.Gateway;

public enum NodeType { Publication, Folder, StructureGroup }

public sealed record TreeNode(string Id, string Title, NodeType Type, bool HasChildren, int? SchemaCount = null);
/// <param name="PathFromRoot">Folder path below the publication's root folder, '/'-separated ('.' = the root folder itself).</param>
public sealed record FolderInfo(string Id, string Title, string PublicationId, string PublicationTitle, string PathFromRoot = ".");

/// <summary>Schema type and location, read only for schemas a workbook actually refers to.</summary>
public sealed record SchemaInfo(string Id, string Purpose, string PathFromRoot);
public sealed record SchemaSummary(string Id, string Title, string Purpose);
public sealed record NamedItem(string Id, string Title);
public sealed record MultimediaTypeInfo(string Id, string Title, IReadOnlyList<string> Extensions);

/// <summary>Schema as read from Tridion; references are by title.</summary>
public sealed class SchemaDetails
{
    public IaSchema Schema { get; init; } = new();
    public List<IaField> Fields { get; init; } = new();
    public List<IaRegionRow> RegionRows { get; init; } = new();
    public HashSet<string> CategoryIds { get; init; } = new();
}

/// <summary>Field with references already resolved to TCM URIs.</summary>
public sealed class FieldWriteModel
{
    public required IaField Field { get; init; }
    public string? EmbeddedSchemaId { get; init; }
    public string? CategoryId { get; init; }
    public List<string> AllowedTargetSchemaIds { get; init; } = new();
}

public sealed class NestedRegionWriteModel
{
    public required string Name { get; init; }
    public required string RegionSchemaId { get; init; }
    public bool Mandatory { get; init; }
}

public sealed class RegionWriteModel
{
    public int? MinOccurs { get; init; }
    public int? MaxOccurs { get; init; }
    public List<string> AllowedSchemaIds { get; init; } = new();
    public List<string> AllowedTemplateIds { get; init; } = new();
    public List<NestedRegionWriteModel> NestedRegions { get; init; } = new();
}

public sealed class SchemaWriteModel
{
    public required IaSchema Schema { get; init; }
    public List<FieldWriteModel> ContentFields { get; init; } = new();
    public List<FieldWriteModel> MetadataFields { get; init; } = new();
    public List<string> MultimediaTypeIds { get; init; } = new();
    public RegionWriteModel? Region { get; init; }
    public string CheckInComment { get; init; } = "Created by Modelry";
}

public enum TemplateKind { Component, Page }

public sealed record TemplateSummary(string Id, string Title, TemplateKind Kind);

/// <summary>Names of the template metadata fields the utility reads / writes (DXA template metadata schema).</summary>
public sealed class TemplateFieldOptions
{
    // DXA "Component Template Metadata" schema (confirmed on SABIC dev): controller, action, routeValues, view, regionView, regionName, htmlClasses
    public string CtViewField { get; set; } = "view";
    public string CtControllerField { get; set; } = "controller";
    public string CtActionField { get; set; } = "action";
    public string CtRouteValuesField { get; set; } = "routeValues";
    public string CtHtmlClassesField { get; set; } = "htmlClasses";
    /// <summary>Optional metadata field for the dynamic flag. Empty (DXA default) = only the template's own "publish as dynamic" property is set.</summary>
    public string CtDynamicField { get; set; } = "";
    public string CtDynamicTrueValue { get; set; } = "true";
    public string CtDynamicFalseValue { get; set; } = "false";
    public string PtViewField { get; set; } = "view";
    public string PtIncludesField { get; set; } = "includes";
    public string DefaultHeaderInclude { get; set; } = "system/include/header";
    public string DefaultFooterInclude { get; set; } = "system/include/footer";
    public string CheckInComment { get; set; } = "Created by Modelry";
}

/// <summary>Template as read from Tridion (references by title).</summary>
public sealed class TemplateDetails
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required TemplateKind Kind { get; init; }
    public string? Description { get; init; }
    public string? View { get; init; }
    public string? Controller { get; init; }
    public string? Action { get; init; }
    public string? RouteValues { get; init; }
    public string? HtmlClasses { get; init; }
    public bool? Dynamic { get; init; }
    public string? Priority { get; init; }
    public List<string> LinkedSchemas { get; init; } = new();
    public string? PageSchema { get; init; }
    public List<string> Includes { get; init; } = new();
    /// <summary>Local names of the metadata fields present on the template (for validation).</summary>
    public HashSet<string> MetadataFields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasMetadataSchema { get; init; }
}

/// <summary>A template to create by cloning a base template, with references resolved to TCM URIs.</summary>
public sealed class TemplateWriteModel
{
    public required TemplateKind Kind { get; init; }
    public required string BaseTemplateId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? View { get; init; }
    public string? Controller { get; init; }
    public string? Action { get; init; }
    public string? RouteValues { get; init; }
    public string? HtmlClasses { get; init; }
    public bool? Dynamic { get; init; }
    public string? Priority { get; init; }
    public List<string> LinkedSchemaIds { get; init; } = new();
    public string? PageSchemaId { get; init; }
    /// <summary>null = keep the base template's includes; empty = no includes.</summary>
    public List<string>? Includes { get; init; }
    public required TemplateFieldOptions Fields { get; init; }
}

// ---------------------------------------------------------------- content (Pages step)

public enum ContentItemType { Component, Page }

/// <summary>A component to create. Content and Metadata are complete XML documents in the schema's namespace.</summary>
public sealed class ComponentWriteModel
{
    public required string Title { get; init; }
    public required string SchemaId { get; init; }
    public required string Content { get; init; }
    public string? Metadata { get; init; }
    public string CheckInComment { get; init; } = "Created by Modelry";
}

/// <summary>A multimedia component to create from a file in the uploaded page zip.</summary>
public sealed class MultimediaWriteModel
{
    public required string Title { get; init; }
    public required string SchemaId { get; init; }
    public required string FileName { get; init; }
    public required byte[] Data { get; init; }
    public required string MultimediaTypeId { get; init; }
    public string? Metadata { get; init; }
    public string CheckInComment { get; init; } = "Created by Modelry";
}

public sealed record PresentationWriteModel(string ComponentId, string ComponentTemplateId);

/// <summary>One native region of a page (Tridion 10) with its component presentations in order.</summary>
public sealed class PageRegionWriteModel
{
    public required string Name { get; init; }
    public string? RegionSchemaId { get; init; }
    public List<PresentationWriteModel> Presentations { get; init; } = new();
}

public sealed class PageWriteModel
{
    public required string Title { get; init; }
    /// <summary>File name without extension.</summary>
    public required string FileName { get; init; }
    public required string PageTemplateId { get; init; }
    public string? MetadataSchemaId { get; init; }
    public string? Metadata { get; init; }
    public List<PageRegionWriteModel> Regions { get; init; } = new();
    public string CheckInComment { get; init; } = "Created by Modelry";
}

/// <summary>
/// All Tridion access goes through this interface. Implementations: CoreServiceTridionGateway (real) and
/// InMemoryTridionGateway (demo mode). Organisation logic (traversal, planning) lives in Core.
/// </summary>
public interface ITridionGateway
{
    Task<string> GetApiVersionAsync();
    Task<IReadOnlyList<TreeNode>> GetPublicationsAsync();
    Task<TreeNode> GetPublicationRootFolderAsync(string publicationId);
    Task<IReadOnlyList<TreeNode>> GetSubFoldersAsync(string folderId);
    Task<FolderInfo> GetFolderAsync(string folderId);
    Task<TreeNode> CreateFolderAsync(string parentFolderId, string title);
    Task<IReadOnlyList<SchemaSummary>> GetSchemasInFolderAsync(string folderId);
    /// <summary>Schemas directly in a folder – titles and ids only (cheap: one call, no schema reads).</summary>
    Task<IReadOnlyList<NamedItem>> ListSchemasInFolderAsync(string folderId);
    /// <summary>Every schema visible in a publication, incl. inherited ones – titles and ids only (one call).</summary>
    Task<IReadOnlyList<NamedItem>> ListPublicationSchemasAsync(string publicationId);
    /// <summary>Type and folder of one schema (one read).</summary>
    Task<SchemaInfo> ReadSchemaInfoAsync(string schemaId);
    /// <summary>Cheap count for the tree (no schema reads).</summary>
    Task<int> CountSchemasAsync(string folderId);
    Task<SchemaDetails> ReadSchemaAsync(string schemaId);
    Task<IaCategory> ReadCategoryAsync(string categoryId);
    Task<IReadOnlyList<NamedItem>> GetCategoriesAsync(string publicationId);
    Task<IReadOnlyList<NamedItem>> GetKeywordsAsync(string categoryId);
    /// <summary>Keywords of a category that can classify content (not abstract), whether or not the IA lists them.</summary>
    Task<IReadOnlyList<NamedItem>> GetSelectableKeywordsAsync(string categoryId);
    Task<IReadOnlyList<NamedItem>> GetComponentTemplatesAsync(string publicationId);
    Task<IReadOnlyList<MultimediaTypeInfo>> GetMultimediaTypesAsync();
    /// <summary>Creates a category; keywordMetadataSchemaId is set when the schema already exists (else null).</summary>
    Task<string> CreateCategoryAsync(string publicationId, IaCategory category, string? keywordMetadataSchemaId);
    Task SetCategoryKeywordMetadataSchemaAsync(string categoryId, string schemaId);
    Task<string> CreateKeywordAsync(string categoryId, IaKeyword keyword, IReadOnlyList<string> parentKeywordIds);
    /// <summary>Creates the schema with its full definition (fields, allowed targets, region definition if given) and checks it in.</summary>
    Task<string> CreateSchemaAsync(string folderId, SchemaWriteModel model);
    // ---- templates
    /// <summary>All Component or Page Templates visible in a publication (incl. inherited), for pickers and lookups.</summary>
    Task<IReadOnlyList<TemplateSummary>> GetTemplatesAsync(string publicationId, TemplateKind kind);
    /// <summary>Component and Page Templates directly in a folder.</summary>
    Task<IReadOnlyList<TemplateSummary>> GetTemplatesInFolderAsync(string folderId);
    Task<TemplateDetails> ReadTemplateAsync(string templateId, TemplateFieldOptions fields);
    /// <summary>Copies the base template into the folder, applies title / view / flags / links and checks it in.</summary>
    Task<string> CreateTemplateAsync(string folderId, TemplateWriteModel model);
    /// <summary>
    /// Restricts a region schema to the given Component Templates: its type constraints become template constraints
    /// (each CT already limits the schemas through its linked schemas). Occurrence limits and nested regions are kept.
    /// </summary>
    Task SetRegionTemplateConstraintsAsync(string regionSchemaId, IReadOnlyList<string> templateIds, string checkInComment);

    /// <summary>Rewrites fields and region definition (used for references deferred by circular dependencies), then checks in.</summary>
    Task UpdateSchemaAsync(string schemaId, SchemaWriteModel model);

    // ---- content (Pages step)
    /// <summary>Components directly in a folder, or pages directly in a Structure Group – titles and ids.</summary>
    Task<IReadOnlyList<NamedItem>> ListItemsAsync(string containerId, ContentItemType type);
    Task<TreeNode> GetPublicationRootStructureGroupAsync(string publicationId);
    Task<IReadOnlyList<TreeNode>> GetSubStructureGroupsAsync(string structureGroupId);
    /// <summary>Title, publication and path of a Structure Group (FolderInfo is reused; PathFromRoot is below the root SG).</summary>
    Task<FolderInfo> GetStructureGroupAsync(string structureGroupId);
    /// <summary>Creates a Structure Group; directory is its URL segment.</summary>
    Task<TreeNode> CreateStructureGroupAsync(string parentStructureGroupId, string title, string directory);
    Task<string> CreateComponentAsync(string folderId, ComponentWriteModel model);
    Task<string> CreateMultimediaComponentAsync(string folderId, MultimediaWriteModel model);
    /// <summary>Creates a page with its native regions and component presentations, and checks it in. Nothing is published.</summary>
    Task<string> CreatePageAsync(string structureGroupId, PageWriteModel model);
    /// <summary>Component presentations the saved page actually holds, per region ("" = outside any region).</summary>
    Task<IReadOnlyDictionary<string, int>> GetPagePresentationCountsAsync(string pageId);
}
