using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Modelry.Core.Audit;

namespace Modelry.Web.Services;

/// <summary>
/// Saved audits on disk: {SnapshotFolder}/{cms}/{publication}/{id}.json.gz with a small {id}.meta.json beside it.
/// Audits are grouped by Core Service URL, so everyone using this Modelry instance against the same CMS sees the same history.
/// </summary>
public sealed partial class AuditSnapshotStore
{
    private readonly AuditOptions _o;
    private readonly string _root;
    private readonly ILogger<AuditSnapshotStore> _log;
    private static readonly object WriteLock = new();

    public AuditSnapshotStore(IOptions<AuditOptions> o, IWebHostEnvironment env, ILogger<AuditSnapshotStore> log)
    {
        _o = o.Value; _log = log;
        _root = Path.IsPathRooted(_o.SnapshotFolder) ? _o.SnapshotFolder : Path.Combine(env.ContentRootPath, _o.SnapshotFolder);
    }

    public bool Enabled => _o.SaveSnapshots;

    [GeneratedRegex(@"^\d{8}-\d{6}-[a-f0-9]{8}$")]
    private static partial Regex IdPattern();

    private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>"demo", or the CMS host plus a short hash of the full Core Service URL.</summary>
    private static string CmsKey(string cms)
    {
        if (cms == "demo") return "demo";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cms.Trim().ToLowerInvariant())))[..10].ToLowerInvariant();
        var host = Uri.TryCreate(cms, UriKind.Absolute, out var u) ? u.Host : "cms";
        return $"{Safe(host)}_{hash}";
    }

    private string Folder(string cms, string publicationId) => Path.Combine(_root, CmsKey(cms), Safe(publicationId));

    /// <summary>Saves the audit and prunes old ones. Returns the saved id, or null if saving is off or failed (logged).</summary>
    public string? Save(string cms, AuditSnapshot snap)
    {
        if (!Enabled) return null;
        try
        {
            var dir = Folder(cms, snap.PublicationId);
            Directory.CreateDirectory(dir);
            var id = $"{snap.FinishedUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
            lock (WriteLock)
            {
                using (var fs = File.Create(Path.Combine(dir, id + ".json.gz"))) AuditSnapshotSerializer.Write(snap, fs);
                File.WriteAllText(Path.Combine(dir, id + ".meta.json"), AuditSnapshotSerializer.WriteMeta(AuditSnapshotSerializer.Meta(snap, id)));
                foreach (var old in List(cms, snap.PublicationId).Skip(Math.Max(1, _o.KeepSnapshots)))
                {
                    File.Delete(Path.Combine(dir, old.Id + ".json.gz"));
                    File.Delete(Path.Combine(dir, old.Id + ".meta.json"));
                    File.Delete(Path.Combine(dir, old.Id + SummarySuffix));
                }
            }
            return id;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audit could not be saved to {Folder} – check the app pool's write access or Audit:SnapshotFolder", _root);
            return null;
        }
    }

    /// <summary>Saved audits, newest first – of one publication, or of every publication on this CMS.</summary>
    public IReadOnlyList<SnapshotMeta> List(string cms, string? publicationId = null)
    {
        var cmsDir = Path.Combine(_root, CmsKey(cms));
        if (!Directory.Exists(cmsDir)) return Array.Empty<SnapshotMeta>();
        var dirs = publicationId is null ? Directory.GetDirectories(cmsDir) : new[] { Folder(cms, publicationId) }.Where(Directory.Exists).ToArray();
        var list = new List<SnapshotMeta>();
        foreach (var file in dirs.SelectMany(d => Directory.GetFiles(d, "*.meta.json")))
        {
            try { if (AuditSnapshotSerializer.ReadMeta(File.ReadAllText(file)) is { } m) list.Add(m); }
            catch (Exception ex) { _log.LogDebug(ex, "Unreadable audit meta {File}", file); }
        }
        return list.OrderByDescending(m => m.FinishedUtc).ToList();
    }

    private const string SummarySuffix = ".summary.json";

    /// <summary>Saves the AI executive summary beside a saved audit ({id}.summary.json). False when it could not be saved (logged).</summary>
    public bool SaveSummary(string cms, string publicationId, string id, AuditAiSummary summary)
    {
        if (!Enabled || !IdPattern().IsMatch(id)) return false;
        try
        {
            var dir = Folder(cms, publicationId);
            if (!File.Exists(Path.Combine(dir, id + ".json.gz"))) return false;
            lock (WriteLock) File.WriteAllText(Path.Combine(dir, id + SummarySuffix), System.Text.Json.JsonSerializer.Serialize(summary, AuditAiSummary.Json));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI summary could not be saved to {Folder}", _root);
            return false;
        }
    }

    /// <summary>The saved AI executive summary of a saved audit, or null.</summary>
    public AuditAiSummary? LoadSummary(string cms, string publicationId, string id)
    {
        if (!IdPattern().IsMatch(id)) return null;
        var path = Path.Combine(Folder(cms, publicationId), id + SummarySuffix);
        if (!File.Exists(path)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<AuditAiSummary>(File.ReadAllText(path), AuditAiSummary.Json); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unreadable AI summary {File}", path);
            return null;
        }
    }

    public AuditSnapshot? Load(string cms, string publicationId, string id)
    {
        if (!IdPattern().IsMatch(id)) return null;
        var path = Path.Combine(Folder(cms, publicationId), id + ".json.gz");
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        var snap = AuditSnapshotSerializer.Read(fs);
        snap.SavedId = id;
        return snap;
    }
}
