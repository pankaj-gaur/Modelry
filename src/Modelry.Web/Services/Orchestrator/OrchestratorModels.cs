using System.Text.Json;

namespace Modelry.Web.Services.Orchestrator;

/// <summary>POST /orchestrate body. Null members are left out of the JSON.</summary>
public sealed record OrchestrateRequest
{
    public required string Prompt { get; init; }
    public string? SystemPrompt { get; init; }
    /// <summary>Null or empty: every active provider.</summary>
    public IReadOnlyList<string>? Providers { get; init; }
    /// <summary>Null: the server default ("hi") applies and the answer is translated.</summary>
    public string? TargetLanguage { get; init; }
    /// <summary>
    /// The answer's structure as text – a JSON schema or an example, e.g. the schema serialised to a string (the service
    /// rejects a JSON object here: "responseSchema must be a string"). Null for free-form prose.
    /// </summary>
    public string? ResponseSchema { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>The final answer: <see cref="Original"/> is JSON text when a response schema was sent.</summary>
public sealed record FinalAnswer(string? Original, string? Translated);

/// <summary>The pipeline artifact: the final answer typed, everything else kept as raw JSON.</summary>
public sealed record OrchestratorArtifact(FinalAnswer? FinalAnswer, JsonElement Raw);

/// <summary>POST /orchestrate → 200.</summary>
public sealed record OrchestrateResponse(string Id, OrchestratorArtifact Artifact);

/// <summary>GET /result/{id} → 200.</summary>
public sealed record OrchestratorResult(OrchestratorArtifact Artifact);

/// <summary>GET /health → 200.</summary>
public sealed record OrchestratorHealth(bool Ok, IReadOnlyList<string> ActiveProviders, int ActiveCount, string? JudgeProvider, string? FactCheckProvider);
