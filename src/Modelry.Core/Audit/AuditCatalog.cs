namespace Modelry.Core.Audit;

/// <summary>A kind-specific column of a detail table / Excel sheet.</summary>
public sealed record AuditColumn(string Key, string Label, bool Numeric = false);

/// <summary>The CMS an audit ran against (AuditSnapshot.Cms).</summary>
public static class AuditCms
{
    public const string Tridion = "tridion";
    public const string Aem = "aem";
    public static bool IsAem(string? cms) => cms == Aem;
}

/// <summary>
/// Labels, columns and scan steps of the audit – shared by the dashboard, the detail table and the Excel export.
/// The nine building-block kinds are the same for every CMS; for AEM they carry AEM names
/// (Schemas = Content Fragment Models, Component Templates = Components, Page Templates = Templates,
/// Components = Content and Experience Fragments, Multimedia = Assets, Categories / Keywords = Tag namespaces / Tags,
/// Structure Groups = Site sections).
/// </summary>
public static class AuditCatalog
{
    public static readonly string[] TridionSteps =
    {
        "Opening the publication",
        "Reading folders and building blocks",
        "Reading Structure Groups and pages",
        "Reading categories and keywords",
        "Reading schemas and templates",
        "Reading publish status",
        "Reading pages and multimedia",
        "Reading where items are used",
    };

    public static readonly string[] AemSteps =
    {
        "Opening the site",
        "Reading pages and site sections",
        "Reading templates and Content Fragment Models",
        "Reading components",
        "Reading Content and Experience Fragments",
        "Reading assets",
        "Reading tags",
        "Reading page content and references",
    };

    /// <summary>Tridion steps (kept for callers that do not know the CMS).</summary>
    public static string[] Steps => TridionSteps;
    public static string[] StepsFor(string? cms) => AuditCms.IsAem(cms) ? AemSteps : TridionSteps;

    public static string Title(AuditKind kind, string? cms = null) => AuditCms.IsAem(cms)
        ? kind switch
        {
            AuditKind.Schemas => "Content Fragment Models",
            AuditKind.ComponentTemplates => "Components",
            AuditKind.PageTemplates => "Templates",
            AuditKind.Components => "Fragments",
            AuditKind.Multimedia => "Assets",
            AuditKind.Categories => "Tag namespaces",
            AuditKind.Keywords => "Tags",
            AuditKind.StructureGroups => "Site sections",
            AuditKind.Pages => "Pages",
            _ => kind.ToString()
        }
        : kind switch
        {
            AuditKind.Schemas => "Schemas",
            AuditKind.ComponentTemplates => "Component Templates",
            AuditKind.PageTemplates => "Page Templates",
            AuditKind.Components => "Components",
            AuditKind.Multimedia => "Multimedia",
            AuditKind.Categories => "Categories",
            AuditKind.Keywords => "Keywords",
            AuditKind.StructureGroups => "Structure Groups",
            AuditKind.Pages => "Pages",
            _ => kind.ToString()
        };

    /// <summary>Singular, for the pop-up heading.</summary>
    public static string Singular(AuditKind kind, string? cms = null) => AuditCms.IsAem(cms)
        ? kind switch
        {
            AuditKind.Schemas => "Content Fragment Model",
            AuditKind.ComponentTemplates => "Component",
            AuditKind.PageTemplates => "Template",
            AuditKind.Components => "Fragment",
            AuditKind.Multimedia => "Asset",
            AuditKind.Categories => "Tag namespace",
            AuditKind.Keywords => "Tag",
            AuditKind.StructureGroups => "Site section",
            AuditKind.Pages => "Page",
            _ => kind.ToString()
        }
        : kind switch
        {
            AuditKind.Schemas => "Schema",
            AuditKind.ComponentTemplates => "Component Template",
            AuditKind.PageTemplates => "Page Template",
            AuditKind.Components => "Component",
            AuditKind.Multimedia => "Multimedia component",
            AuditKind.Categories => "Category",
            AuditKind.Keywords => "Keyword",
            AuditKind.StructureGroups => "Structure Group",
            AuditKind.Pages => "Page",
            _ => kind.ToString()
        };

    /// <summary>What an audit root is called: publication (Tridion) or site (AEM).</summary>
    public static string RootNoun(string? cms) => AuditCms.IsAem(cms) ? "site" : "publication";

    /// <summary>Components, multimedia components and pages carry a publish status.</summary>
    public static bool IsPublishable(AuditKind kind) => kind is AuditKind.Components or AuditKind.Multimedia or AuditKind.Pages;

    /// <summary>Path column heading: folder for building blocks, Structure Group for pages, none for taxonomy.</summary>
    public static string? PathLabel(AuditKind kind, string? cms = null) => AuditCms.IsAem(cms)
        ? kind switch
        {
            AuditKind.Categories => null,
            AuditKind.Keywords => "Tag path",
            AuditKind.Pages or AuditKind.StructureGroups => "Section",
            _ => "Location"
        }
        : kind switch
        {
            AuditKind.Categories or AuditKind.Keywords => null,
            AuditKind.Pages or AuditKind.StructureGroups => "Structure Group",
            _ => "Folder"
        };

    public static IReadOnlyList<AuditColumn> Columns(AuditKind kind, string? cms = null) => AuditCms.IsAem(cms)
        ? kind switch
        {
            AuditKind.Schemas => new AuditColumn[] { new("status", "Status"), new("components", "Fragments", true) },
            AuditKind.ComponentTemplates => new AuditColumn[] { new("resourceType", "Resource type"), new("group", "Group"), new("superType", "Super type"), new("pages", "Used on pages", true) },
            AuditKind.PageTemplates => new AuditColumn[] { new("status", "Status"), new("pages", "Pages", true) },
            AuditKind.Components => new AuditColumn[] { new("type", "Type"), new("schema", "Model"), new("onPages", "On pages", true), new("usedBy", "Used by", true) },
            AuditKind.Multimedia => new AuditColumn[] { new("fileName", "File name"), new("mmType", "Format"), new("sizeKb", "Size (KB)", true), new("assetTitle", "Title (dc:title)"), new("assetState", "Processing"), new("usedBy", "Used by", true) },
            AuditKind.Categories => new AuditColumn[] { new("keywords", "Tags", true), new("xmlName", "Namespace") },
            AuditKind.Keywords => new AuditColumn[] { new("category", "Namespace"), new("key", "Tag ID"), new("usedBy", "Used by", true) },
            AuditKind.StructureGroups => new AuditColumn[] { new("url", "Path"), new("pages", "Pages", true), new("subGroups", "Sub-sections", true), new("language", "Language root") },
            AuditKind.Pages => new AuditColumn[] { new("url", "Path"), new("fileName", "Page name"), new("pageTemplate", "Template"), new("cps", "Components", true) },
            _ => Array.Empty<AuditColumn>()
        }
        : kind switch
        {
            AuditKind.Schemas => new AuditColumn[] { new("purpose", "Purpose"), new("components", "Components", true), new("linkedCts", "Component Templates", true) },
            AuditKind.ComponentTemplates => new AuditColumn[] { new("view", "View"), new("controller", "Controller"), new("linkedSchemas", "Linked schemas"), new("dynamic", "Dynamic"), new("pages", "Used on pages", true) },
            AuditKind.PageTemplates => new AuditColumn[] { new("view", "View"), new("includes", "Includes"), new("pages", "Pages", true) },
            AuditKind.Components => new AuditColumn[] { new("schema", "Schema"), new("onPages", "On pages", true), new("usedBy", "Used by", true), new("usedIn", "Used in other publications") },
            AuditKind.Multimedia => new AuditColumn[] { new("schema", "Schema"), new("fileName", "File name"), new("mmType", "Type"), new("sizeKb", "Size (KB)", true), new("usedBy", "Used by", true), new("usedIn", "Used in other publications") },
            AuditKind.Categories => new AuditColumn[] { new("keywords", "Keywords", true), new("xmlName", "XML name"), new("publishable", "Publishable"), new("schemas", "Used in schemas", true) },
            AuditKind.Keywords => new AuditColumn[] { new("category", "Category"), new("key", "Key"), new("abstract", "Abstract"), new("usedBy", "Used by", true), new("usedIn", "Used in other publications") },
            AuditKind.StructureGroups => new AuditColumn[] { new("directory", "Directory"), new("url", "URL path"), new("pages", "Pages", true), new("subGroups", "Sub-groups", true), new("active", "Active") },
            AuditKind.Pages => new AuditColumn[] { new("url", "URL"), new("fileName", "File name"), new("pageTemplate", "Page Template"), new("cps", "Component presentations", true) },
            _ => Array.Empty<AuditColumn>()
        };

    /// <summary>Columns filled only by a deep scan ("where used" per item). Only those the CMS has are marked.</summary>
    public static readonly (AuditKind Kind, string Column)[] DeepColumns =
    {
        (AuditKind.Components, "usedBy"), (AuditKind.Multimedia, "usedBy"), (AuditKind.Keywords, "usedBy"), (AuditKind.Categories, "schemas"),
        (AuditKind.Components, "usedIn"), (AuditKind.Multimedia, "usedIn"), (AuditKind.Keywords, "usedIn")
    };

    public const string DeepReason = "Filled by a deep scan – choose \"Run deep scan\".";

    public static string Origin(BluePrintOrigin o, string? cms = null) => AuditCms.IsAem(cms)
        ? o switch
        {
            BluePrintOrigin.Local => "Local",
            BluePrintOrigin.Localized => "Inheritance cancelled",
            BluePrintOrigin.Shared => "Live copy",
            _ => "Unknown"
        }
        : o switch
        {
            BluePrintOrigin.Local => "Local",
            BluePrintOrigin.Localized => "Localized",
            BluePrintOrigin.Shared => "Shared",
            _ => "Unknown"
        };

    /// <summary>Column heading for the origin column: BluePrint (Tridion) or MSM (AEM).</summary>
    public static string OriginLabel(string? cms) => AuditCms.IsAem(cms) ? "MSM" : "BluePrint";

    public static string Publish(PublishState p) => p switch
    {
        PublishState.Published => "Published",
        PublishState.Unpublished => "Not published",
        PublishState.Unknown => "Unknown",
        _ => ""
    };
}
