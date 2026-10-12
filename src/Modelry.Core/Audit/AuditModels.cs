using Modelry.Core.Gateway;

namespace Modelry.Core.Audit;

/// <summary>The building blocks an audit counts. Order = order of the dashboard tiles and the Excel sheets.</summary>
public enum AuditKind { Schemas, ComponentTemplates, PageTemplates, Components, Multimedia, Categories, Keywords, StructureGroups, Pages }

/// <summary>Where an item comes from in the BluePrint, seen from the audited publication.</summary>
public enum BluePrintOrigin { Unknown, Local, Localized, Shared }

/// <summary>Publish state of a component or page. NotApplicable for building blocks that are not published on their own.</summary>
public enum PublishState { NotApplicable, Unknown, Published, Unpublished }

/// <summary>One publish target (target type / purpose, or publication target on older versions) an item is published to.</summary>
public sealed record PublishTarget(string Target, DateTime? PublishedAt, string? User);

/// <summary>One item in a detail table.</summary>
public sealed class AuditRow
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required AuditKind Kind { get; init; }
    /// <summary>Folder or Structure Group path below the publication's root, '/'-separated ("" = the root itself).</summary>
    public string Path { get; set; } = "";
    public BluePrintOrigin Origin { get; set; }
    /// <summary>Publication the item is shared or localized from (null for local items or when unknown).</summary>
    public string? FromPublication { get; set; }
    public DateTime? Created { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? Modified { get; set; }
    public string? ModifiedBy { get; set; }
    public PublishState Publish { get; set; } = PublishState.NotApplicable;
    public List<PublishTarget> PublishedTo { get; } = new();
    /// <summary>Kind-specific column values, keyed by <see cref="AuditColumn.Key"/>.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Value(string key) => Values.TryGetValue(key, out var v) ? v : "";
}

/// <summary>Result of one audit run of one publication.</summary>
public sealed class AuditSnapshot
{
    public required string PublicationId { get; init; }
    public string PublicationTitle { get; set; } = "";
    /// <summary>"tridion" or "aem" (<see cref="AuditCms"/>) – decides labels, columns and which checks apply.</summary>
    public string Cms { get; set; } = AuditCms.Tridion;
    public string CmsHost { get; set; } = "";
    public string RunBy { get; set; } = "";
    public CmsCapabilities Capabilities { get; set; } = CmsCapabilities.From("");
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<AuditKind, List<AuditRow>> Items { get; } = Enum.GetValues<AuditKind>().ToDictionary(k => k, _ => new List<AuditRow>());
    /// <summary>Things the auditor should know: limits hit, fall-backs used, calls this CM did not support.</summary>
    public List<string> Notes { get; } = new();
    /// <summary>
    /// Columns that could not be filled on this CM ("Components.publish", "*.modifiedBy" …) with the reason shown as a tooltip.
    /// "*" applies to every kind.
    /// </summary>
    public Dictionary<string, string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Publish targets seen on any item (column headers of the publish breakdown).</summary>
    public SortedSet<string> PublishTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Deep scan: "where used" was read for every component, multimedia component, keyword and category.</summary>
    public bool Deep { get; set; }
    /// <summary>Saved-snapshot id once written to disk (null while only in memory).</summary>
    public string? SavedId { get; set; }
    /// <summary>Findings and health score, computed by <see cref="AuditRules.Evaluate"/> after the scan.</summary>
    public List<AuditFinding> Findings { get; } = new();
    public List<RuleResult> Rules { get; } = new();
    public HealthScore? Health { get; set; }

    public void MarkUnavailable(string column, string reason) => Unavailable.TryAdd(column, reason);

    public string? UnavailableReason(AuditKind kind, string column) =>
        Unavailable.TryGetValue($"{kind}.{column}", out var r) ? r : Unavailable.TryGetValue($"*.{column}", out r) ? r : null;

    public void Note(string note) { lock (Notes) if (!Notes.Contains(note)) Notes.Add(note); }

    public AuditSummary Summarize(AuditKind kind)
    {
        var rows = Items[kind];
        return new AuditSummary(kind, rows.Count,
            rows.Count(r => r.Publish == PublishState.Published), rows.Count(r => r.Publish == PublishState.Unpublished),
            rows.Count(r => r.Publish == PublishState.Unknown),
            rows.Count(r => r.Origin == BluePrintOrigin.Local), rows.Count(r => r.Origin == BluePrintOrigin.Localized),
            rows.Count(r => r.Origin == BluePrintOrigin.Shared));
    }
}

public sealed record AuditSummary(AuditKind Kind, int Total, int Published, int Unpublished, int PublishUnknown, int Local, int Localized, int Shared);

/// <summary>Full detail of one item, read live when a row is opened.</summary>
public sealed class AuditItemDetail
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public required AuditKind Kind { get; init; }
    public List<DetailSection> Sections { get; } = new();

    public DetailSection Section(string title)
    {
        var s = Sections.FirstOrDefault(x => x.Title == title);
        if (s is null) Sections.Add(s = new DetailSection { Title = title });
        return s;
    }
}

public sealed class DetailSection
{
    public required string Title { get; init; }
    /// <summary>Label / value pairs.</summary>
    public List<KeyValuePair<string, string>> Rows { get; } = new();
    /// <summary>Optional list (fields, keywords, component presentations …).</summary>
    public List<string> Items { get; } = new();
    /// <summary>Optional XML (content / metadata), shown in a scrollable block.</summary>
    public string? Xml { get; set; }

    public DetailSection Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Rows.Add(new(label, value));
        return this;
    }
}

/// <summary>Progress of a running audit: steps are the measure, activity is free text.</summary>
public sealed record AuditProgress(int Step, string Activity);

/// <summary>Limits and switches for an audit run (appsettings "Audit" section).</summary>
public sealed class AuditOptions
{
    /// <summary>Parallel Core Service calls while scanning (folders, publish info).</summary>
    public int MaxParallelCalls { get; set; } = 4;
    /// <summary>Component / Page Templates read one by one for view, controller and linked schemas; above this only titles are listed.</summary>
    public int MaxTemplateReads { get; set; } = 1000;
    /// <summary>Structure Groups read one by one for directory and active flag.</summary>
    public int MaxStructureGroupReads { get; set; } = 5000;
    /// <summary>Categories read one by one for publishable flag and XML name.</summary>
    public int MaxCategoryReads { get; set; } = 500;
    /// <summary>
    /// "Auto" (per-item publish info up to MaxPublishInfoItems, else the list's published flag), "PerItem", "List" or "Off".
    /// Per-item gives targets and dates; the list flag only says published / not.
    /// </summary>
    public string PublishInfoMode { get; set; } = "Auto";
    public int MaxPublishInfoItems { get; set; } = 10000;
    /// <summary>Pages read one by one for file name and component presentations (pages without content, duplicate file names, template use).</summary>
    public int MaxPageReads { get; set; } = 3000;
    /// <summary>No longer used: multimedia is read in the item-details step (MaxItemDetailReads). Kept so old settings still bind.</summary>
    public int MaxMultimediaReads { get; set; } = 5000;
    /// <summary>
    /// Tridion: read every item (in batches) for created / modified dates and users, the component's schema and the multimedia
    /// file name, size and type – details that list data does not carry. Off: those columns show n/a.
    /// </summary>
    public bool ReadItemDetails { get; set; } = true;
    /// <summary>Most items read in the item-details step, all building blocks together; above it the columns show n/a.</summary>
    public int MaxItemDetailReads { get; set; } = 50000;
    /// <summary>Items per Core Service ReadItems call in the item-details step.</summary>
    public int ItemReadBatchSize { get; set; } = 50;
    /// <summary>Deep scan: most items whose "where used" list is read (components, multimedia, keywords, categories together).</summary>
    public int MaxUsageItems { get; set; } = 20000;
    /// <summary>Multimedia larger than this (KB) is reported as a large file.</summary>
    public int LargeFileKb { get; set; } = 2048;
    /// <summary>Published pages not changed for this many months are reported as possibly stale.</summary>
    public int StaleMonths { get; set; } = 12;
    /// <summary>Optional naming conventions: regular expression per building block, e.g. "ComponentTemplates": "^[A-Z][A-Za-z ]+ CT$".</summary>
    public Dictionary<string, string> Naming { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Save every finished audit to disk so it can be reopened and compared later.</summary>
    public bool SaveSnapshots { get; set; } = true;
    /// <summary>Folder for saved audits (relative to the app's content root, or absolute). Needs write access for the app pool.</summary>
    public string SnapshotFolder { get; set; } = "App_Data/audit-snapshots";
    /// <summary>Saved audits kept per publication (oldest are deleted).</summary>
    public int KeepSnapshots { get; set; } = 20;
    /// <summary>AEM: root under /apps whose components are listed (null = all of /apps except AEM's own folders).</summary>
    public string? AemComponentsRoot { get; set; }
    /// <summary>Per run (set from the start form, not appsettings): AEM /conf folder and DAM folder of the site (null = derived).</summary>
    public string? ScopeConf { get; set; }
    public string? ScopeDam { get; set; }

    /// <summary>A copy for one run, so per-run scope settings do not leak into the shared options.</summary>
    public AuditOptions Clone()
    {
        var c = (AuditOptions)MemberwiseClone();
        c.Naming = new Dictionary<string, string>(Naming, StringComparer.OrdinalIgnoreCase);
        return c;
    }

    /// <summary>A finished audit of the same publication is reused for this many minutes unless "Run again" is chosen.</summary>
    public int ReuseMinutes { get; set; } = 30;
    /// <summary>Rows per page in the detail table.</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>Something that can be audited: a Tridion publication or an AEM site root.</summary>
public sealed record AuditRoot(string Id, string Title, string? Detail = null);

/// <summary>Reads a publication (Tridion) or a site (AEM) for an audit. Read-only: never creates, changes or publishes anything.</summary>
public interface IAuditGateway
{
    /// <summary>Publications (Tridion) or site roots (AEM) the signed-in user can audit.</summary>
    Task<IReadOnlyList<AuditRoot>> ListAuditRootsAsync(CancellationToken ct);

    /// <param name="progress">Step numbers follow <see cref="AuditCatalog.Steps"/>.</param>
    /// <param name="deep">Also read "where used" for every component, multimedia component, keyword and category (slower).</param>
    Task<AuditSnapshot> ScanPublicationAsync(string publicationId, AuditOptions options, TemplateFieldOptions fields,
        CmsCapabilities capabilities, bool deep, IProgress<AuditProgress> progress, CancellationToken ct);

    /// <summary>Everything about one item, read live (called when a row is opened).</summary>
    Task<AuditItemDetail> ReadAuditDetailAsync(string itemId, AuditKind kind, TemplateFieldOptions fields, CancellationToken ct);
}
