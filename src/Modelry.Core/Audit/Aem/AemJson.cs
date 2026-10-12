using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modelry.Core.Audit.Aem;

/// <summary>How to reach an AEM author instance: Basic (user + password) or Bearer (access token).</summary>
public sealed record AemConnection(string BaseUrl, bool UseToken, string? User, string Secret);

public sealed class AemHttpException : Exception
{
    public HttpStatusCode Status { get; }
    public AemHttpException(HttpStatusCode status, string url) : base($"AEM answered HTTP {(int)status} for {url}") => Status = status;
}

/// <summary>Small JSON client for AEM's read APIs (Sling GET servlet and QueryBuilder), plus helpers to read the results.</summary>
public sealed partial class AemJsonClient
{
    private readonly HttpClient _http;
    private readonly AemConnection _c;

    public AemJsonClient(HttpClient http, AemConnection connection) { _http = http; _c = connection; }

    /// <summary>Encodes each path segment but keeps '/' and ':' (jcr:content, cq:tags) as AEM expects.</summary>
    public static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(s => Uri.EscapeDataString(s).Replace("%3A", ":", StringComparison.OrdinalIgnoreCase)));

    public async Task<JsonElement> GetAsync(string pathAndQuery, CancellationToken ct) =>
        await TryGetAsync(pathAndQuery, ct) ?? throw new AemHttpException(HttpStatusCode.NotFound, pathAndQuery);

    /// <summary>GET returning the JSON root, or null on 404. Sling's "300 Multiple Choices" (tree too deep) is returned as null too.</summary>
    public async Task<JsonElement?> TryGetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _c.BaseUrl.TrimEnd('/') + pathAndQuery);
        req.Headers.Authorization = _c.UseToken
            ? new AuthenticationHeaderValue("Bearer", _c.Secret)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_c.User}:{_c.Secret}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MultipleChoices) return null;
        if (!res.IsSuccessStatusCode) throw new AemHttpException(res.StatusCode, pathAndQuery);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Runs a QueryBuilder query page by page (p.hits=full) and yields every hit. Each hit holds the node's properties and,
    /// down to <paramref name="depth"/>, its child nodes as nested objects; "jcr:path" holds the node path.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> QueryAsync(IReadOnlyDictionary<string, string> predicates, int depth,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, int pageSize = 500)
    {
        var offset = 0;
        while (true)
        {
            var q = string.Join("&", predicates.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            var url = $"/bin/querybuilder.json?{q}&orderby=path&p.hits=full&p.nodedepth={depth}&p.limit={pageSize}&p.offset={offset}&p.guessTotal=true";
            var page = await GetAsync(url, ct);
            var hits = page.TryGetProperty("hits", out var h) && h.ValueKind == JsonValueKind.Array ? h.EnumerateArray().ToList() : new List<JsonElement>();
            foreach (var hit in hits) yield return hit;
            var more = page.TryGetProperty("more", out var m) && m.ValueKind == JsonValueKind.True;
            if (hits.Count < pageSize && !more) yield break;
            if (hits.Count == 0) yield break;
            offset += hits.Count;
        }
    }

    // ------------------------------------------------------------------ JSON helpers

    /// <summary>A property as text: strings as is, the first entry of a string array, numbers / booleans as their JSON text.</summary>
    public static string? Str(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Array => v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString()).FirstOrDefault(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null
        };
    }

    public static List<string> Strs(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(name, out var v)) return new List<string>();
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList(),
            JsonValueKind.String => new List<string> { v.GetString()! },
            _ => new List<string>()
        };
    }

    /// <summary>A child node, following a '/'-separated relative path ("jcr:content/metadata").</summary>
    public static JsonElement? Child(JsonElement? e, string relPath)
    {
        var cur = e;
        foreach (var seg in relPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(seg, out var next) || next.ValueKind != JsonValueKind.Object) return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>Child nodes of an object (properties whose value is an object), excluding jcr:content unless asked.</summary>
    public static IEnumerable<(string Name, JsonElement Node)> Children(JsonElement? e, bool includeContent = false)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) yield break;
        foreach (var p in o.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && (includeContent || p.Name != "jcr:content")) yield return (p.Name, p.Value);
    }

    [GeneratedRegex(@"^\w{3} (\w{3}) (\d{2}) (\d{4}) (\d{2}):(\d{2}):(\d{2}) GMT([+-]\d{2})(\d{2})")]
    private static partial Regex EcmaDate();

    /// <summary>AEM dates: ISO 8601 (QueryBuilder, some APIs) or the Sling/ECMA form "Fri Oct 09 2026 10:11:12 GMT+0200".</summary>
    public static DateTime? Date(JsonElement? e, string name)
    {
        var s = Str(e, name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = EcmaDate().Match(s);
        if (!m.Success)
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso) ? iso.UtcDateTime : null;
        var text = $"{m.Groups[3].Value}-{m.Groups[1].Value}-{m.Groups[2].Value} {m.Groups[4].Value}:{m.Groups[5].Value}:{m.Groups[6].Value} {m.Groups[7].Value}:{m.Groups[8].Value}";
        return DateTimeOffset.TryParseExact(text, "yyyy-MMM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.UtcDateTime : null;
    }

    /// <summary>Every string value anywhere in a JSON tree (for reference crawling), with its property name.</summary>
    public static IEnumerable<(string Name, string Value)> AllStrings(JsonElement e, string name = "")
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: yield return (name, e.GetString()!); break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) foreach (var v in AllStrings(x, name)) yield return v;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) foreach (var v in AllStrings(p.Value, p.Name)) yield return v;
                break;
        }
    }
}
