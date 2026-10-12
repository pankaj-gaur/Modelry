using System.Globalization;

namespace Modelry.Core.Audit;

/// <summary>Filter, sort and page one detail table on the server (publications can hold tens of thousands of items).</summary>
public sealed class AuditQuery
{
    public string? Search { get; set; }
    /// <summary>"Local", "Localized", "Shared" or empty for all.</summary>
    public string? Origin { get; set; }
    /// <summary>"Published", "Unpublished", "Unknown" or empty for all.</summary>
    public string? Publish { get; set; }
    /// <summary>Column key: title, id, path, origin, modified, modifiedBy, publish, lastPublished or a kind-specific key.</summary>
    public string? Sort { get; set; }
    public bool Descending { get; set; }
    /// <summary>Only these item ids (rows flagged by a finding), or null for all.</summary>
    public HashSet<string>? OnlyIds { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;

    public IEnumerable<AuditRow> Filter(IEnumerable<AuditRow> rows)
    {
        if (OnlyIds is not null) rows = rows.Where(r => OnlyIds.Contains(r.Id));
        if (!string.IsNullOrWhiteSpace(Origin) && Enum.TryParse<BluePrintOrigin>(Origin, true, out var o))
            rows = rows.Where(r => r.Origin == o);
        if (!string.IsNullOrWhiteSpace(Publish) && Enum.TryParse<PublishState>(Publish, true, out var p))
            rows = rows.Where(r => r.Publish == p);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var q = Search.Trim();
            rows = rows.Where(r => Has(r.Title, q) || Has(r.Id, q) || Has(r.Path, q) || Has(r.FromPublication, q) || Has(r.ModifiedBy, q) || Has(r.CreatedBy, q)
                                   || r.Values.Any(kv => !kv.Key.StartsWith('_') && Has(kv.Value, q)));
        }
        return rows;
    }

    private static bool Has(string? value, string q) => value?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    public IEnumerable<AuditRow> Order(IEnumerable<AuditRow> rows, AuditKind kind)
    {
        var key = string.IsNullOrWhiteSpace(Sort) ? "path" : Sort!;
        var numeric = AuditCatalog.Columns(kind).FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Numeric == true;
        IOrderedEnumerable<AuditRow> ordered = key.ToLowerInvariant() switch
        {
            "modified" => By(rows, r => r.Modified ?? DateTime.MinValue),
            "lastpublished" => By(rows, r => LastPublished(r) ?? DateTime.MinValue),
            _ when key.StartsWith("target:", StringComparison.OrdinalIgnoreCase) => By(rows, r =>
                r.PublishedTo.FirstOrDefault(t => t.Target.Equals(key[7..], StringComparison.OrdinalIgnoreCase)) is { } t ? t.PublishedAt ?? DateTime.MinValue.AddDays(1) : DateTime.MinValue),
            "origin" => By(rows, r => AuditCatalog.Origin(r.Origin), StringComparer.OrdinalIgnoreCase),
            "publish" => By(rows, r => (int)r.Publish),
            "title" => By(rows, r => r.Title, StringComparer.OrdinalIgnoreCase),
            "id" => By(rows, r => r.Id, StringComparer.OrdinalIgnoreCase),
            "modifiedby" => By(rows, r => r.ModifiedBy ?? "", StringComparer.OrdinalIgnoreCase),
            "created" => By(rows, r => r.Created ?? DateTime.MinValue),
            "createdby" => By(rows, r => r.CreatedBy ?? "", StringComparer.OrdinalIgnoreCase),
            "path" => By(rows, r => r.Path, StringComparer.OrdinalIgnoreCase),
            _ when numeric => By(rows, r => int.TryParse(r.Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1),
            _ => By(rows, r => r.Value(key), StringComparer.OrdinalIgnoreCase)
        };
        return ordered.ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase);
    }

    private IOrderedEnumerable<AuditRow> By<T>(IEnumerable<AuditRow> rows, Func<AuditRow, T> key, IComparer<T>? comparer = null) =>
        Descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    public static DateTime? LastPublished(AuditRow r) => r.PublishedTo.Count == 0 ? null : r.PublishedTo.Max(t => t.PublishedAt);

    /// <summary>Applies filter, sort and paging. Returns the page and the number of rows matching the filter.</summary>
    public (List<AuditRow> Rows, int Matching) Apply(IReadOnlyList<AuditRow> all, AuditKind kind)
    {
        var filtered = Filter(all).ToList();
        var size = Math.Clamp(PageSize, 10, 500);
        var page = Math.Max(1, Page);
        return (Order(filtered, kind).Skip((page - 1) * size).Take(size).ToList(), filtered.Count);
    }
}
