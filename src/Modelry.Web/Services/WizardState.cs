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
