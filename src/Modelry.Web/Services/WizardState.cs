using System.Text.Json;

namespace Modelry.Web.Services;

/// <summary>Outcome of one creation run (schemas or templates) shown on the journey rail.</summary>
public sealed class RunSummary
{
    public string FolderPath { get; set; } = "";
    public int Created { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public string? ResultId { get; set; }
    public DateTime FinishedUtc { get; set; }
}

/// <summary>Outcome of a model generation run (DXA C# view models zip).</summary>
public sealed class ModelRunSummary
{
    public int Classes { get; set; }
    public int Registrations { get; set; }
    public int Files { get; set; }
    public int Warnings { get; set; }
    public string Namespace { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Cache key of the zip (kept for 2 hours).</summary>
    public string? ResultId { get; set; }
    public DateTime FinishedUtc { get; set; }
}

/// <summary>Choices made on the Models step, remembered for the session.</summary>
public sealed class ModelSettings
{
    public string? Namespace { get; set; }
    public string? PageMetadataSchema { get; set; }
    public string? SemanticPrefix { get; set; }
}

/// <summary>One page handled on the Pages step.</summary>
public sealed class PageRunSummary
{
    public string RunId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string PageName { get; set; } = "";
    public int Sections { get; set; }
    public int Mapped { get; set; }
    public int Views { get; set; }
    public int Findings { get; set; }
    public string? ViewsResultId { get; set; }
    public string FileName { get; set; } = "";
    public DateTime FinishedUtc { get; set; }
    /// <summary>Stage 2: what was created in Tridion for this page (null = not run).</summary>
    public RunSummary? Content { get; set; }
}

/// <summary>What the uploaded IA workbook contains.</summary>
public sealed class IaSummary
{
    public int Schemas { get; set; }
    public int Fields { get; set; }
    public int ComponentTemplates { get; set; }
    public int PageTemplates { get; set; }
    public int Categories { get; set; }
    public int Keywords { get; set; }
    public int RegionRows { get; set; }
    public Dictionary<string, int> SchemasByPurpose { get; set; } = new();
    public List<string> ReadErrors { get; set; } = new();
    /// <summary>'Templates', a legacy sheet name, or null when the workbook has no templates sheet.</summary>
    public string? TemplatesSource { get; set; }
    public List<string> SheetNames { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>The wizard's progress for this session: the IA uploaded once, and the runs done with it.</summary>
public sealed class WizardData
{
    public string? UploadId { get; set; }
    public string? FileName { get; set; }
    public DateTime? UploadedUtc { get; set; }
    public IaSummary? Summary { get; set; }
    public string? SchemaFolderId { get; set; }
    public string? TemplateFolderId { get; set; }
    public RunSummary? SchemaRun { get; set; }
    public RunSummary? TemplateRun { get; set; }
    public ModelRunSummary? ModelRun { get; set; }
    public ModelSettings? ModelSettings { get; set; }
    /// <summary>Pages done on the Pages step in this session (latest per page).</summary>
    public List<PageRunSummary> PageRuns { get; set; } = new();
    /// <summary>Views generated in this session → the page they came from (first sample wins for later pages).</summary>
    public Dictionary<string, string> ViewSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentPageRunId { get; set; }
    /// <summary>Index (in StepTrackingFilter.Steps) of the furthest step opened; earlier unfinished steps show as skipped.</summary>
    public int FurthestStep { get; set; }
    public bool HasIa => UploadId is not null;
}

/// <summary>Reads / writes WizardData in the session.</summary>
public sealed class WizardState
{
    private const string Key = "modelry.wizard";
    private readonly ISession _s;
    public WizardState(IHttpContextAccessor http) => _s = http.HttpContext!.Session;

    public WizardData Data
    {
        get
        {
            var json = _s.GetString(Key);
            return string.IsNullOrEmpty(json) ? new WizardData() : JsonSerializer.Deserialize<WizardData>(json) ?? new WizardData();
        }
    }

    public void Update(Action<WizardData> change)
    {
        var d = Data;
        change(d);
        _s.SetString(Key, JsonSerializer.Serialize(d));
    }

    public void Clear() => _s.Remove(Key);
}
