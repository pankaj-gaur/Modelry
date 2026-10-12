using Modelry.Core.Audit;
using Modelry.Core.Gateway;
using Modelry.Web.Services;

namespace Modelry.Web.Models;

public sealed class AuditIndexViewModel
{
    /// <summary>Publications (Tridion) or site roots (AEM).</summary>
    public IReadOnlyList<AuditRoot> Roots { get; set; } = Array.Empty<AuditRoot>();
    public string Cms { get; set; } = AuditCms.Tridion;
    public bool IsAem => AuditCms.IsAem(Cms);
    public IReadOnlyList<AuditJob> Recent { get; set; } = Array.Empty<AuditJob>();
    public IReadOnlyList<SnapshotMeta> Saved { get; set; } = Array.Empty<SnapshotMeta>();
    public bool HistoryEnabled { get; set; }
    public CmsCapabilities Capabilities { get; set; } = CmsCapabilities.From("");
    public string? Error { get; set; }
}

public sealed class AuditDashboardViewModel
{
    public required AuditJob Job { get; init; }
    public int PageSize { get; init; } = 20;
    /// <summary>Other saved audits of this publication (for Compare).</summary>
    public IReadOnlyList<SnapshotMeta> Saved { get; init; } = Array.Empty<SnapshotMeta>();
    /// <summary>The AI executive summary can be generated (Orchestrator:Enabled).</summary>
    public bool AiSummaryEnabled { get; init; }
    public AuditSnapshot? Snapshot => Job.Snapshot;

    /// <summary>Table definitions for the page script: columns, unavailable reasons, publish targets, rule titles.</summary>
    public object ClientModel()
    {
        var s = Snapshot!;
        var cms = s.Cms;
        return new
        {
            jobId = Job.Id,
            cms,
            originLabel = AuditCatalog.OriginLabel(cms),
            targets = s.PublishTargets,
            rules = s.Rules.ToDictionary(r => r.Id, r => new { title = r.Title, severity = r.Severity.ToString() }),
            kinds = Enum.GetValues<AuditKind>().Select(k => new
            {
                key = k.ToString(), title = AuditCatalog.Title(k, cms), singular = AuditCatalog.Singular(k, cms),
                publishable = AuditCatalog.IsPublishable(k), pathLabel = AuditCatalog.PathLabel(k, cms),
                columns = AuditCatalog.Columns(k, cms).Select(c => new { key = c.Key, label = c.Label, numeric = c.Numeric, na = s.UnavailableReason(k, c.Key) }),
                na = new
                {
                    publish = s.UnavailableReason(k, "publish"), modified = s.UnavailableReason(k, "modified"),
                    modifiedBy = s.UnavailableReason(k, "modifiedBy"), origin = s.UnavailableReason(k, "origin"),
                    created = s.UnavailableReason(k, "created"), createdBy = s.UnavailableReason(k, "createdBy")
                }
            })
        };
    }
}

public sealed class AuditCompareViewModel
{
    public required string JobId { get; init; }
    public required AuditComparison Comparison { get; init; }
    /// <summary>True when the open audit is the later of the two.</summary>
    public bool CurrentIsLater { get; init; }
    /// <summary>All saved audits of the publication, oldest first (score and size over time).</summary>
    public IReadOnlyList<SnapshotMeta> Trend { get; init; } = Array.Empty<SnapshotMeta>();
}

/// <summary>The printable audit report (Export to PDF).</summary>
public sealed class AuditReportViewModel
{
    public required string JobId { get; init; }
    public required AuditSnapshot Snapshot { get; init; }
    public AuditAiSummary? Summary { get; init; }
    /// <summary>Open the browser's print dialog ("Save as PDF") as soon as the page has loaded.</summary>
    public bool Print { get; init; }
    /// <summary>Items listed per check; the rest are counted and left to the Excel export.</summary>
    public int ItemsPerCheck { get; init; } = 25;
}
