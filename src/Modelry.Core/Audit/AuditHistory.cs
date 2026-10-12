using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modelry.Core.Audit;

/// <summary>Summary of a saved audit, kept beside it so history lists do not load whole snapshots.</summary>
public sealed record SnapshotMeta(string Id, string PublicationId, string PublicationTitle, DateTime FinishedUtc, string RunBy, bool Deep,
    int Score, string Grade, Dictionary<AuditKind, int> Counts, int Findings, string CmsVersion);

/// <summary>Saves audits as gzip-compressed JSON and reads them back.</summary>
public static class AuditSnapshotSerializer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,   // getter-only collections are filled, not replaced
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    public static SnapshotMeta Meta(AuditSnapshot s, string id) => new(id, s.PublicationId, s.PublicationTitle, s.FinishedUtc, s.RunBy, s.Deep,
        s.Health?.Overall ?? 0, s.Health?.Grade ?? "", s.Items.ToDictionary(kv => kv.Key, kv => kv.Value.Count),
        s.Rules.Where(r => r.Evaluated).Sum(r => r.Affected), s.Capabilities.Display);

    public static void Write(AuditSnapshot s, Stream target)
    {
        using var gz = new GZipStream(target, CompressionLevel.Optimal, leaveOpen: true);
        JsonSerializer.Serialize(gz, s, Json);
    }

    public static AuditSnapshot Read(Stream source)
    {
        using var gz = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        return JsonSerializer.Deserialize<AuditSnapshot>(gz, Json) ?? throw new InvalidDataException("Empty audit file.");
    }

    public static string WriteMeta(SnapshotMeta m) => JsonSerializer.Serialize(m, Json);
    public static SnapshotMeta? ReadMeta(string json) => JsonSerializer.Deserialize<SnapshotMeta>(json, Json);
}

public sealed record KindDelta(AuditKind Kind, int Before, int After, List<AuditRow> Added, List<AuditRow> Removed,
    int PublishedBefore, int PublishedAfter, int NewlyPublished, int NoLongerPublished);

public sealed record RuleDelta(string Id, string Title, FindingCategory Category, FindingSeverity Severity, int? Before, int? After);

public sealed class AuditComparison
{
    public required AuditSnapshot Before { get; init; }
    public required AuditSnapshot After { get; init; }
    public List<KindDelta> Kinds { get; } = new();
    public List<RuleDelta> Rules { get; } = new();

    /// <summary>
    /// Compares two audits of the same publication. Items are matched by item number and type (TCM URIs differ only in the
    /// publication part across BluePrint levels). Both audits' findings are re-evaluated with the current rules first, so a
    /// change in Modelry's rules does not show up as a change in the site.
    /// </summary>
    public static AuditComparison Compare(AuditSnapshot before, AuditSnapshot after, AuditOptions options)
    {
        AuditRules.Evaluate(before, options);
        AuditRules.Evaluate(after, options);
        var c = new AuditComparison { Before = before, After = after };
        foreach (var kind in Enum.GetValues<AuditKind>())
        {
            var b = before.Items[kind].GroupBy(r => AuditFinalizer.ItemKey(r.Id)).ToDictionary(g => g.Key, g => g.First());
            var a = after.Items[kind].GroupBy(r => AuditFinalizer.ItemKey(r.Id)).ToDictionary(g => g.Key, g => g.First());
            var both = a.Keys.Where(b.ContainsKey).ToList();
            c.Kinds.Add(new KindDelta(kind, b.Count, a.Count,
                a.Where(kv => !b.ContainsKey(kv.Key)).Select(kv => kv.Value).OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                b.Where(kv => !a.ContainsKey(kv.Key)).Select(kv => kv.Value).OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                b.Values.Count(r => r.Publish == PublishState.Published), a.Values.Count(r => r.Publish == PublishState.Published),
                both.Count(k => a[k].Publish == PublishState.Published && b[k].Publish == PublishState.Unpublished),
                both.Count(k => a[k].Publish == PublishState.Unpublished && b[k].Publish == PublishState.Published)));
        }
        foreach (var r in after.Rules)
        {
            var old = before.Rules.FirstOrDefault(x => x.Id == r.Id);
            c.Rules.Add(new RuleDelta(r.Id, r.Title, r.Category, r.Severity, old is { Evaluated: true } ? old.Affected : null, r.Evaluated ? r.Affected : null));
        }
        return c;
    }
}
