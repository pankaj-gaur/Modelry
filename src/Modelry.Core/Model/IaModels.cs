namespace Modelry.Core.Model;

/// <summary>Schema purposes supported by the Information Architecture (IA) workbook.</summary>
public enum IaSchemaPurpose { Component, Multimedia, Embedded, Metadata, Region, TemplateParameters, Bundle }

public enum IaFieldSection { Content, Metadata }

public enum IaFieldType
{
    Text, MultiLineText, RichText, Number, Date, ExternalLink, ComponentLink, MultimediaLink, Keyword, EmbeddedSchema
}

public enum IaRegionRowType { Constraint, NestedRegion }

public sealed class IaSchema
{
    public string Title { get; set; } = "";
    public IaSchemaPurpose Purpose { get; set; }
    /// <summary>Folder path relative to the folder selected in the UI. "." or empty = the selected folder.</summary>
    public string RelativePath { get; set; } = ".";
    public string? RootElementName { get; set; }
    public string? NamespaceUri { get; set; }
    public string? Description { get; set; }
    /// <summary>File extensions (jpg, png, pdf …) mapped to Tridion Multimedia Types.</summary>
    public List<string> AllowedMultimediaTypes { get; set; } = new();
    public string? Notes { get; set; }
    // export-only information
    public string? SourceId { get; set; }
    public string? BluePrintStatus { get; set; }
    public string? OwningPublication { get; set; }
    public int Row { get; set; }
}

public sealed class IaField
{
    public string SchemaTitle { get; set; } = "";
    public IaFieldSection Section { get; set; } = IaFieldSection.Content;
    public int Order { get; set; }
    public string XmlName { get; set; } = "";
    public string Label { get; set; } = "";
    public IaFieldType Type { get; set; }
    public bool Mandatory { get; set; }
    public int MinOccurs { get; set; }
    /// <summary>-1 = unbounded.</summary>
    public int MaxOccurs { get; set; } = 1;
    public string? EmbeddedSchema { get; set; }
    public List<string> AllowedTargetSchemas { get; set; } = new();
    public bool AllowMultimediaLinks { get; set; }
    public string? Category { get; set; }
    /// <summary>Select | Radio | Checkbox | Tree (empty = no list).</summary>
    public string? ListType { get; set; }
    public List<string> ListValues { get; set; } = new();
    public string? DefaultValue { get; set; }
    public int? Height { get; set; }
    public string? FormatArea { get; set; }
    public int? MaxLength { get; set; }
    public string? HelpText { get; set; }
    public int Row { get; set; }
}

public sealed class IaRegionRow
{
    public string RegionSchemaTitle { get; set; } = "";
    public IaRegionRowType RowType { get; set; }
    public string? NestedRegionName { get; set; }
    public string? NestedRegionSchema { get; set; }
    public bool Mandatory { get; set; }
    public int? MinOccurs { get; set; }
    public int? MaxOccurs { get; set; }
    public List<string> AllowedComponentSchemas { get; set; } = new();
    public List<string> AllowedComponentTemplates { get; set; } = new();
    public string? Notes { get; set; }
    public int Row { get; set; }
}

public sealed class IaCategory
{
    public string Title { get; set; } = "";
    public string XmlName { get; set; } = "";
    public string? Description { get; set; }
    public bool Publishable { get; set; } = true;
    public bool UseForIdentification { get; set; }
    public string? KeywordMetadataSchema { get; set; }
    public string? SourceId { get; set; }
    public int Row { get; set; }
}

public sealed class IaKeyword
{
    public string CategoryTitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Key { get; set; }
    public string? ParentKeyword { get; set; }
    public string? Description { get; set; }
    public bool IsAbstract { get; set; }
    public int Row { get; set; }
}

public enum IaTemplateKind { ComponentTemplate, PageTemplate }

public sealed class IaTemplate
{
    public IaTemplateKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Folder path relative to the folder selected in the UI ('.' = that folder).</summary>
    public string RelativePath { get; set; } = ".";
    public string? View { get; set; }
    /// <summary>DXA controller (Module:Controller); empty = keep the base template's value (default Entity).</summary>
    public string? Controller { get; set; }
    public string? Action { get; set; }
    /// <summary>Comma-separated name:value pairs, e.g. "navType:Top".</summary>
    public string? RouteValues { get; set; }
    public string? HtmlClasses { get; set; }
    /// <summary>Optional C# view model class for the DXA view (column 'View Model Type'); empty = derived from the linked schema.</summary>
    public string? ViewModelType { get; set; }
    /// <summary>Component Templates: linked (related) schemas, by title or path/title.</summary>
    public List<string> LinkedSchemas { get; set; } = new();
    /// <summary>Page Templates: page (region) schema, by title or path/title.</summary>
    public string? PageSchema { get; set; }
    public bool? Dynamic { get; set; }
    /// <summary>High | Medium | Low | Never Link (Component Templates).</summary>
    public string? Priority { get; set; }
    /// <summary>Optional per-row base template (title or TCM URI); default = base chosen in the UI.</summary>
    public string? BaseTemplate { get; set; }
    /// <summary>Page Templates: include page paths. Empty = UI defaults; "(none)" = no includes.</summary>
    public List<string> Includes { get; set; } = new();
    public bool NoIncludes { get; set; }
    public string? Description { get; set; }
    public string? SourceId { get; set; }
    public int Row { get; set; }
}

/// <summary>A page in the Page Inventory sheet (optional; used by the Pages step).</summary>
public sealed class IaPage
{
    public string PageId { get; set; } = "";
    public string? Site { get; set; }
    public string? Section { get; set; }
    public string PageName { get; set; } = "";
    public string? ProposedUrl { get; set; }
    public string? PageSchema { get; set; }
    public string? PageMetadataSchema { get; set; }
    public string? PageTemplate { get; set; }
    /// <summary>What the Page Template renders itself (e.g. "Breadcrumb / SectionTabs") – not authored components.</summary>
    public string? AutoRendered { get; set; }
    public int Row { get; set; }
    public string Label => $"{PageId} – {PageName}";
}

/// <summary>One placement in the Page-Schema Mapping sheet: which component goes in which region of a page, in order.</summary>
public sealed class IaMappingRow
{
    public string MapId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string? Region { get; set; }
    public string? RegionSchema { get; set; }
    /// <summary>The section as named in the requirement specification, e.g. "Introductory statement".</summary>
    public string? UiSection { get; set; }
    public string? SpecIds { get; set; }
    public string? SchemaTitle { get; set; }
    public string? ComponentTemplate { get; set; }
    /// <summary>Authored | Query | Integration | SG-driven …</summary>
    public string? ContentSource { get; set; }
    public string? Notes { get; set; }
    public string? ReuseGroup { get; set; }
    public int Row { get; set; }
}

/// <summary>A DXA region view documented in the Templates sheet as a reference row ("(reference – region view)"); used for model registration only.</summary>
public sealed class IaRegionView
{
    public string Title { get; set; } = "";
    /// <summary>Area-qualified view name, e.g. "Sabic:Main".</summary>
    public string View { get; set; } = "";
    /// <summary>Region schema the view renders (read from the Page Schema column).</summary>
    public string? RegionSchema { get; set; }
    public string? ViewModelType { get; set; }
    public int Row { get; set; }
}

public sealed class IaWorkbook
{
    public List<IaTemplate> Templates { get; } = new();
    /// <summary>Region views listed as reference rows in the Templates sheet (not created in the CMS).</summary>
    public List<IaRegionView> RegionViews { get; } = new();
    /// <summary>Page Inventory rows (optional sheet).</summary>
    public List<IaPage> Pages { get; } = new();
    /// <summary>Page-Schema Mapping rows (optional sheet), in sheet order.</summary>
    public List<IaMappingRow> Mapping { get; } = new();
    /// <summary>Sheet names found in the workbook (for diagnostics).</summary>
    public List<string> SheetNames { get; } = new();
    /// <summary>Where the templates were read from: the 'Templates' sheet, a legacy reference sheet, or null (none).</summary>
    public string? TemplatesSource { get; set; }
    public List<IaSchema> Schemas { get; } = new();
    public List<IaField> Fields { get; } = new();
    public List<IaRegionRow> Regions { get; } = new();
    public List<IaCategory> Categories { get; } = new();
    public List<IaKeyword> Keywords { get; } = new();

    public IEnumerable<IaField> FieldsOf(string schemaTitle, IaFieldSection section) =>
        Fields.Where(f => Same(f.SchemaTitle, schemaTitle) && f.Section == section).OrderBy(f => f.Order);

    public IEnumerable<IaRegionRow> RegionRowsOf(string schemaTitle) =>
        Regions.Where(r => Same(r.RegionSchemaTitle, schemaTitle));

    public static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public enum IssueLevel { Error, Warning, Info }

public sealed record Issue(IssueLevel Level, string Sheet, int Row, string Item, string Message);
