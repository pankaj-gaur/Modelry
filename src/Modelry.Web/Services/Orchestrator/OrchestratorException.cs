using System.Net;

namespace Modelry.Web.Services.Orchestrator;

/// <summary>
/// A failed orchestrator call: an error response (400 bad body, 401 bad key, 429 rate limited, 500 pipeline error), a body
/// that is not the expected JSON, a timeout, or a network failure (StatusCode null).
/// </summary>
public sealed class OrchestratorException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    /// <summary>The service's "error" text, or a short description when the body was not JSON.</summary>
    public string Error { get; }
    /// <summary>The service's optional "code".</summary>
    public string? Code { get; }
    public bool IsTimeout { get; }
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests;
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;

    public OrchestratorException(HttpStatusCode? status, string error, string? code = null, bool isTimeout = false, Exception? inner = null)
        : base(Describe(status, error, code, isTimeout), inner)
    {
        StatusCode = status; Error = error; Code = code; IsTimeout = isTimeout;
    }

    private static string Describe(HttpStatusCode? status, string error, string? code, bool timeout) =>
        timeout ? $"The AI service did not answer in time: {error}"
        : status is null ? $"The AI service could not be reached: {error}"
        : $"The AI service returned {(int)status.Value} {status}{(code is null ? "" : $" ({code})")}: {error}";
}
