using Modelry.Core.Gateway;
using Modelry.Core.Model;

namespace Modelry.Core.Import;

public enum ResolutionKind { Import, Existing, Ambiguous, Missing }

public sealed record Resolution(ResolutionKind Kind, SchemaPlan? Plan = null, SchemaLocation? Existing = null)
{
    public string Purpose => Plan?.Schema.Purpose.ToString() ?? Existing?.Purpose ?? "";
}

/// <summary>
/// Resolves a schema reference. A reference is a title, or a '/'-separated path ending in the title
/// (matched against the end of "folderPath/title"). Schemas in the workbook win over existing ones.
/// </summary>
public sealed class SchemaResolver
{
    private readonly IReadOnlyList<SchemaPlan> _plans;
    private readonly IReadOnlyList<SchemaLocation> _existing;

    public SchemaResolver(IReadOnlyList<SchemaPlan> plans, IReadOnlyList<SchemaLocation> existing)
    { _plans = plans; _existing = existing; }

    public Resolution Resolve(string reference)
    {
        var r = reference.Trim().Replace('\\', '/').Trim('/');
        var byPlan = _plans.Where(p => Matches(r, p.FolderPath, p.Schema.Title)).ToList();
        if (byPlan.Count == 1) return new Resolution(ResolutionKind.Import, Plan: byPlan[0]);
        if (byPlan.Count > 1) return new Resolution(ResolutionKind.Ambiguous);
        var byExisting = _existing.Where(e => Matches(r, e.RelativePath, e.Title)).ToList();
        if (byExisting.Count == 1) return new Resolution(ResolutionKind.Existing, Existing: byExisting[0]);
        return new Resolution(byExisting.Count > 1 ? ResolutionKind.Ambiguous : ResolutionKind.Missing);
    }

    private static bool Matches(string reference, string folderPath, string title)
    {
        if (!reference.Contains('/')) return IaWorkbook.Same(reference, title);
        var key = "/" + (folderPath == "." ? "" : folderPath + "/") + title;
        return key.EndsWith("/" + reference, StringComparison.OrdinalIgnoreCase);
    }
}
