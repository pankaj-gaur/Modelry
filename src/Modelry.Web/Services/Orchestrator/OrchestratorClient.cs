using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modelry.Web.Services.Orchestrator;

public interface IOrchestratorClient
{
    /// <summary>Runs the pipeline (1.5–3 minutes). Paid and rate limited – never retried.</summary>
    Task<OrchestrateResponse> OrchestrateAsync(OrchestrateRequest request, CancellationToken ct = default);
    /// <summary>A stored result, or null when the id is unknown (404).</summary>
    Task<OrchestratorResult?> GetResultAsync(string id, CancellationToken ct = default);
    Task<OrchestratorHealth> GetHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Typed client for the AI Orchestration Engine. Server-side only; the API key is sent as x-api-key and never logged, and
/// neither is the prompt. Base address, key header and timeout are set where the client is registered (AddOrchestrator).
/// </summary>
public sealed class OrchestratorClient : IOrchestratorClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger<OrchestratorClient> _log;

    public OrchestratorClient(HttpClient http, ILogger<OrchestratorClient> log)
    {
        _http = http; _log = log;
    }

    public async Task<OrchestrateResponse> OrchestrateAsync(OrchestrateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("The prompt is empty.", nameof(request));
        var body = request with { Providers = request.Providers is { Count: > 0 } ? request.Providers : null };
        using var msg = new HttpRequestMessage(HttpMethod.Post, "orchestrate")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json")
        };
        var (root, _) = await SendAsync(msg, ct, allowNotFound: false);
        var id = root!.Value.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()! : "";
        var artifact = Artifact(root.Value);
        _log.LogInformation("Orchestrator result {ResultId}", id);
        return new OrchestrateResponse(id, artifact);
    }

    public async Task<OrchestratorResult?> GetResultAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("The result id is empty.", nameof(id));
        using var msg = new HttpRequestMessage(HttpMethod.Get, "result/" + Uri.EscapeDataString(id));
        var (root, notFound) = await SendAsync(msg, ct, allowNotFound: true);
        return notFound ? null : new OrchestratorResult(Artifact(root!.Value));
    }

    public async Task<OrchestratorHealth> GetHealthAsync(CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "health");
        var (root, _) = await SendAsync(msg, ct, allowNotFound: false);
        try
        {
            return root!.Value.Deserialize<OrchestratorHealth>(Json) ?? throw new JsonException("Empty health response.");
        }
        catch (JsonException ex)
        {
            throw new OrchestratorException(HttpStatusCode.OK, "The health response was not in the expected format.", inner: ex);
        }
    }

    /// <summary>
    /// Sends one request: logs method, path, status and duration; turns every failure into an <see cref="OrchestratorException"/>.
    /// Returns the parsed body of a successful ("ok": true) response.
    /// </summary>
    private async Task<(JsonElement? Root, bool NotFound)> SendAsync(HttpRequestMessage msg, CancellationToken ct, bool allowNotFound)
    {
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var path = msg.RequestUri?.OriginalString ?? "";
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed (the caller did not cancel).
            _log.LogWarning("Orchestrator {Method} /{Path} timed out after {Seconds:0} s", msg.Method, path, watch.Elapsed.TotalSeconds);
            throw new OrchestratorException(null, $"no answer after {_http.Timeout.TotalSeconds:0} seconds (Orchestrator:TimeoutSeconds).", isTimeout: true, inner: ex);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Orchestrator {Method} /{Path} failed after {Ms} ms: {Error}", msg.Method, path, watch.ElapsedMilliseconds, ex.Message);
            throw new OrchestratorException(null, ex.Message, inner: ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            _log.LogInformation("Orchestrator {Method} /{Path} → {Status} in {Ms} ms", msg.Method, path, (int)response.StatusCode, watch.ElapsedMilliseconds);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return (null, true);

            JsonElement? root = null;
            try { if (text.Length > 0) root = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { /* not JSON – handled below */ }

            var ok = root is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (response.IsSuccessStatusCode && ok) return (root, false);

            string error;
            string? code = null;
            if (root is { ValueKind: JsonValueKind.Object } e)
            {
                error = e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString()! : "no error message in the response";
                code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            }
            else
                error = text.Length == 0 ? $"empty response ({response.ReasonPhrase})"
                    : $"the response was not JSON ({response.Content.Headers.ContentType?.MediaType ?? "no content type"}): {Shorten(text)}";
            if (response.IsSuccessStatusCode) error = "the response did not report success: " + error;
            throw new OrchestratorException(response.StatusCode, error, code);
        }
    }

    private static OrchestratorArtifact Artifact(JsonElement root)
    {
        if (!root.TryGetProperty("artifact", out var artifact) || artifact.ValueKind != JsonValueKind.Object)
            throw new OrchestratorException(HttpStatusCode.OK, "the response has no artifact.");
        FinalAnswer? final = null;
        if (artifact.TryGetProperty("finalAnswer", out var fa) && fa.ValueKind == JsonValueKind.Object)
            final = new FinalAnswer(Text(fa, "original"), Text(fa, "translated"));
        return new OrchestratorArtifact(final, artifact.Clone());
    }

    /// <summary>A string member as text; an object or array member (structured answer) as its JSON.</summary>
    private static string? Text(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) ? null
        : v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Object or JsonValueKind.Array => v.GetRawText(), _ => null };

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= 200 ? s : s[..200] + "…";
    }
}
