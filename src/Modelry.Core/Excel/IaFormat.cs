namespace Modelry.Core.Excel;

/// <summary>
/// Single definition of the IA workbook format (sheet names + column headers).
/// The reader looks columns up by header text, so column order may change; headers must not.
/// A trailing '*' in a header marks a mandatory column and is ignored when matching.
/// </summary>
public static class IaFormat
{
    public const string FormatVersion = "1.0";

    public const string ReadmeSheet = "README";
    public const string SchemasSheet = "Schemas";
    public const string FieldsSheet = "Fields";
    public const string RegionsSheet = "Region Definitions";
    public const string CategoriesSheet = "Categories";
    public const string KeywordsSheet = "Keywords";
    public const string ResultsSheet = "Import Results";
    public const string TemplatesSheet = "Templates";

    public static class Templates
    {
        public const string Type = "Template Type*";
        public const string Title = "Template Title*";
        public const string RelativePath = "Relative Folder Path*";
        public const string View = "DXA View";
        public const string Controller = "Controller";
        public const string Action = "Action";
        public const string RouteValues = "Route Values";
        public const string HtmlClasses = "HTML Classes";
        public const string LinkedSchemas = "Linked Schemas";
        public const string PageSchema = "Page Schema";
        public const string Dynamic = "Dynamic";
        public const string Priority = "Priority";
        public const string BaseTemplate = "Base Template (optional)";
        public const string Includes = "Includes";
        public const string Description = "Description";
        public const string SourceId = "Source TCM URI (export only)";
        /// <summary>Optional, read by model generation only (not written on export).</summary>
        public const string ViewModelType = "View Model Type";
        public static readonly string[] All = { Type, Title, RelativePath, View, Controller, Action, RouteValues, HtmlClasses, LinkedSchemas, PageSchema, Dynamic, Priority, BaseTemplate, Includes, Description, SourceId };
    }

    public const string PagesSheet = "Page Inventory";
    public const string MappingSheet = "Page-Schema Mapping";

    /// <summary>Optional sheet read by the Pages step only.</summary>
    public static class Pages
    {
        public const string PageId = "Page ID";
        public const string Site = "Site / Publication";
        public const string Section = "Section (Structure Group)";
        public const string PageName = "Page Name";
        public const string ProposedUrl = "Proposed URL";
        public const string PageSchema = "Page Schema";
        public const string PageMetadataSchema = "Page Metadata Schema";
        public const string PageTemplate = "Page Template";
        public const string AutoRendered = "Auto-rendered by Page Template";
        public static readonly string[] All = { PageId, Site, Section, PageName, ProposedUrl, PageSchema, PageMetadataSchema, PageTemplate, AutoRendered };
    }

    /// <summary>Optional sheet read by the Pages step only.</summary>
    public static class Mapping
    {
        public const string MapId = "Map ID";
        public const string PageId = "Page ID";
        public const string Region = "Region";
        public const string RegionSchema = "Region Schema";
        public const string UiSection = "UI Section / Component";
        public const string SpecIds = "Spec IDs";
        public const string SchemaTitle = "Schema Title";
        public const string ComponentTemplate = "Component Template";
        public const string ContentSource = "Content Source";
        public const string Notes = "Notes";
        public const string ReuseGroup = "Reuse Group";
        public static readonly string[] All = { MapId, PageId, Region, RegionSchema, UiSection, SpecIds, SchemaTitle, ComponentTemplate, ContentSource, Notes, ReuseGroup };
    }

    public static class Schemas
    {
        public const string Title = "Schema Title*";
        public const string Purpose = "Schema Purpose*";
        public const string RelativePath = "Relative Folder Path*";
        public const string RootElement = "XML Root Element Name";
        public const string Namespace = "Namespace URI";
        public const string Description = "Description";
        public const string MultimediaTypes = "Allowed Multimedia Types";
        public const string Notes = "Notes";
        public const string SourceId = "Source TCM URI (export only)";
        public const string BluePrint = "BluePrint Status (export only)";
        public const string OwningPublication = "Owning Publication (export only)";
        public static readonly string[] All = { Title, Purpose, RelativePath, RootElement, Namespace, Description, MultimediaTypes, Notes, SourceId, BluePrint, OwningPublication };
    }

    public static class Fields
    {
        public const string SchemaTitle = "Schema Title*";
        public const string Section = "Field Section*";
        public const string Order = "Field Order*";
        public const string XmlName = "XML Name*";
        public const string Label = "Description (Label)*";
        public const string Type = "Field Type*";
        public const string Mandatory = "Mandatory";
        public const string MinOccurs = "Min Occurs";
        public const string MaxOccurs = "Max Occurs";
        public const string EmbeddedSchema = "Embedded Schema";
        public const string AllowedTargets = "Allowed Target Schemas";
        public const string AllowMultimedia = "Allow Multimedia Links";
        public const string Category = "Category";
        public const string ListType = "List Type";
        public const string ListValues = "List Values";
        public const string DefaultValue = "Default Value";
        public const string Height = "Height";
        public const string FormatArea = "Format Area";
        public const string MaxLength = "Max Length";
        public const string HelpText = "Help Text";
        public static readonly string[] All = { SchemaTitle, Section, Order, XmlName, Label, Type, Mandatory, MinOccurs, MaxOccurs, EmbeddedSchema, AllowedTargets, AllowMultimedia, Category, ListType, ListValues, DefaultValue, Height, FormatArea, MaxLength, HelpText };
    }

    public static class Regions
    {
        public const string RegionSchema = "Region Schema Title*";
        public const string RowType = "Row Type*";
        public const string NestedName = "Nested Region Name";
        public const string NestedSchema = "Nested Region Schema";
        public const string Mandatory = "Mandatory";
        public const string MinOccurs = "Min Occurs";
        public const string MaxOccurs = "Max Occurs";
        public const string AllowedSchemas = "Allowed Component Schemas";
        public const string AllowedTemplates = "Allowed Component Templates";
        public const string Notes = "Notes";
        public static readonly string[] All = { RegionSchema, RowType, NestedName, NestedSchema, Mandatory, MinOccurs, MaxOccurs, AllowedSchemas, AllowedTemplates, Notes };
    }

    public static class Categories
    {
        public const string Title = "Category Title*";
        public const string XmlName = "XML Name*";
        public const string Description = "Description";
        public const string Publishable = "Publishable";
        public const string UseForIdentification = "Use for Identification";
        public const string KeywordMetadataSchema = "Keyword Metadata Schema";
        public const string SourceId = "Source TCM URI (export only)";
        public static readonly string[] All = { Title, XmlName, Description, Publishable, UseForIdentification, KeywordMetadataSchema, SourceId };
    }

    public static class Keywords
    {
        public const string Category = "Category Title*";
        public const string Title = "Keyword Title*";
        public const string Key = "Key";
        public const string Parent = "Parent Keyword";
        public const string Description = "Description";
        public const string IsAbstract = "Is Abstract";
        public static readonly string[] All = { Category, Title, Key, Parent, Description, IsAbstract };
    }

    public static string Normalize(string header) => header.Replace("*", "").Trim().ToLowerInvariant();
}
