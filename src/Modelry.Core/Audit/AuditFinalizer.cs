namespace Modelry.Core.Audit;

/// <summary>
/// Cross-counts computed after a scan, the same for every gateway. Gateways fill link values (hidden keys start with "_"):
/// components "_schemaId", Component Templates "_linkedSchemaIds" ('|'-separated), pages "_pageTemplateId" and "_sgId",
/// keywords "_categoryId".
/// </summary>
public static class AuditFinalizer
{
    public const char ListSeparator = '|';

    public static void Finalize(AuditSnapshot s, AuditOptions? options = null)
    {
        if (!s.Deep)
            foreach (var (kind, column) in AuditCatalog.DeepColumns.Where(d => AuditCatalog.Columns(d.Kind, s.Cms).Any(c => c.Key == d.Column)))
                s.MarkUnavailable($"{kind}.{column}", AuditCatalog.DeepReason);
        Count(s, AuditKind.Schemas, "components", s.Items[AuditKind.Components].Concat(s.Items[AuditKind.Multimedia]), "_schemaId",
            AuditKind.Components, "schema");
        Count(s, AuditKind.Schemas, "linkedCts", s.Items[AuditKind.ComponentTemplates], "_linkedSchemaIds",
            AuditKind.ComponentTemplates, "linkedSchemas");
        Count(s, AuditKind.PageTemplates, "pages", s.Items[AuditKind.Pages], "_pageTemplateId", AuditKind.Pages, "pageTemplate");
        Count(s, AuditKind.StructureGroups, "pages", s.Items[AuditKind.Pages], "_sgId", null, null);
        Count(s, AuditKind.Categories, "keywords", s.Items[AuditKind.Keywords], "_categoryId", null, null);
        Count(s, AuditKind.StructureGroups, "subGroups", s.Items[AuditKind.StructureGroups], "_parentId", null, null);
        // From page reads (component presentations): which Component Templates and components are placed on pages.
        Count(s, AuditKind.ComponentTemplates, "pages", s.Items[AuditKind.Pages], "_ctIds", AuditKind.Pages, "cps");
        Count(s, AuditKind.Components, "onPages", s.Items[AuditKind.Pages], "_componentIds", AuditKind.Pages, "cps");

        foreach (var rows in s.Items.Values) rows.Sort((a, b) =>
        {
            var c = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });
        s.FinishedUtc = DateTime.UtcNow;
        AuditRules.Evaluate(s, options ?? new AuditOptions());
    }

    /// <summary>
    /// Writes the number of <paramref name="sources"/> that link to each target row into <paramref name="column"/>.
    /// When the source column itself is marked unavailable, the count is marked unavailable too (a 0 would be misleading).
    /// </summary>
    private static void Count(AuditSnapshot s, AuditKind target, string column, IEnumerable<AuditRow> sources, string linkKey,
        AuditKind? sourceKind, string? sourceColumn)
    {
        if (sourceKind is { } sk && sourceColumn is not null && s.UnavailableReason(sk, sourceColumn) is { } reason)
        {
            s.MarkUnavailable($"{target}.{column}", reason);
            return;
        }
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in sources)
            foreach (var id in r.Value(linkKey).Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries))
                counts[ItemKey(id)] = counts.GetValueOrDefault(ItemKey(id)) + 1;
        foreach (var t in s.Items[target])
            t.Values[column] = counts.GetValueOrDefault(ItemKey(t.Id)).ToString();
    }

    /// <summary>
    /// Compares TCM URIs by item number and type, ignoring the publication part and version: a schema linked as
    /// tcm:5-123-8 in the owning publication is tcm:9-123-8 in the audited one.
    /// </summary>
    public static string ItemKey(string tcmUri)
    {
        if (!tcmUri.StartsWith("tcm:", StringComparison.OrdinalIgnoreCase)) return tcmUri.Trim().TrimEnd('/').ToLowerInvariant();   // AEM paths
        var parts = tcmUri.Split(':').Last().Split('-');
        return parts.Length >= 2 ? $"{parts[1]}-{(parts.Length > 2 && !parts[2].StartsWith('v') ? parts[2] : "16")}" : tcmUri;
    }
}
