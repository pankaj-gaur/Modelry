using Microsoft.Extensions.Options;

namespace Modelry.Web.Services.Orchestrator;

public static class OrchestratorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IOrchestratorClient"/> as a typed HttpClient bound to the "Orchestrator" section, validated at
    /// start-up when enabled. No retry or resilience handler: /orchestrate is paid and rate limited (20 calls an hour in
    /// total, 5 an hour per IP), so a failed call is reported, never repeated automatically.
    /// </summary>
    public static IServiceCollection AddOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrchestratorOptions>()
            .Bind(configuration.GetSection(OrchestratorOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OrchestratorOptions>, OrchestratorOptionsValidator>();

        var fallbackProxy = configuration["Tridion:ProxyUrl"];
        services.AddHttpClient<IOrchestratorClient, OrchestratorClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
                if (Uri.TryCreate(o.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)) http.BaseAddress = baseUri;
                http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds > 0 ? o.TimeoutSeconds : 240);
                if (!string.IsNullOrWhiteSpace(o.ApiKey)) http.DefaultRequestHeaders.Add("x-api-key", o.ApiKey);
            })
            // HttpClient logs request headers at Trace level: hide the key's value there.
            .RedactLoggedHeaders(new[] { "x-api-key" })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var o = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
                var proxy = !string.IsNullOrWhiteSpace(o.ProxyUrl) ? o.ProxyUrl : fallbackProxy;
                return string.IsNullOrWhiteSpace(proxy)
                    ? new HttpClientHandler()                                         // system proxy settings
                    : new HttpClientHandler { Proxy = new System.Net.WebProxy(proxy, BypassOnLocal: true), UseProxy = true };
            });
        // HttpClient logging never includes bodies, so the prompt is not logged; OrchestratorClient adds its own line with the result id.
        return services;
    }
}
