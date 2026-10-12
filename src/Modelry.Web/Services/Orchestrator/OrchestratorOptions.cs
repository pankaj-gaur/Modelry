using Microsoft.Extensions.Options;

namespace Modelry.Web.Services.Orchestrator;

/// <summary>
/// The AI Orchestration Engine, bound from the "Orchestrator" section.
/// ApiKey comes from user-secrets in development and from the environment variable Orchestrator__ApiKey in production –
/// never from appsettings.json.
/// </summary>
public sealed class OrchestratorOptions
{
    public const string Section = "Orchestrator";

    /// <summary>Off: no calls are made, the AI summary button is hidden, and the settings below are not validated.</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>One call takes 1.5–3 minutes (the judge step waits 60 s on purpose).</summary>
    public int TimeoutSeconds { get; set; } = 240;
    /// <summary>Outbound proxy for the call; empty uses Tridion:ProxyUrl, and when that is empty too, the system proxy.</summary>
    public string ProxyUrl { get; set; } = "";
}

/// <summary>Start-up validation (only when Enabled).</summary>
public sealed class OrchestratorOptionsValidator : IValidateOptions<OrchestratorOptions>
{
    public ValidateOptionsResult Validate(string? name, OrchestratorOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;
        var errors = new List<string>();
        if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !u.IsLoopback))
            errors.Add("Orchestrator:BaseUrl must be an absolute https URL.");
        if (string.IsNullOrWhiteSpace(o.ApiKey))
            errors.Add("Orchestrator:ApiKey is missing – set it with user-secrets (development) or the environment variable Orchestrator__ApiKey (production), or set Orchestrator:Enabled to false.");
        if (o.TimeoutSeconds is < 30 or > 900)
            errors.Add("Orchestrator:TimeoutSeconds must be between 30 and 900.");
        if (!string.IsNullOrWhiteSpace(o.ProxyUrl) && !Uri.TryCreate(o.ProxyUrl, UriKind.Absolute, out _))
            errors.Add("Orchestrator:ProxyUrl must be an absolute URL.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
