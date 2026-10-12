using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modelry.Web.Services.Orchestrator;
using Xunit;

namespace Modelry.Web.Tests;

/// <summary>Answers every request with the given function and records what was sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
    public List<(HttpRequestMessage Request, string? Body)> Sent { get; } = new();

    public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

    public static FakeHandler Returns(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Sent.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
        return await _answer(request, ct);
    }
}

/// <summary>Collects log messages, to check that neither the key nor the prompt is logged.</summary>
public sealed class ListLogger : ILoggerProvider, ILogger
{
    public List<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add(formatter(state, ex) + (state is IEnumerable<KeyValuePair<string, object?>> kv ? " " + string.Join(" ", kv.Select(p => p.Value)) : ""));
    }
}

public class OrchestratorClientTests
{
    private const string Key = "test-key-123";
    private const string Prompt = "PROMPT-TEXT-THAT-MUST-NOT-BE-LOGGED";

    private const string SuccessBody = """
        { "ok": true, "id": "run-42",
          "artifact": { "finalAnswer": { "original": "{\"headline\":\"Fine\"}", "translated": "ठीक" },
                        "judge": { "winner": "groq" }, "timings": [1, 2] } }
        """;

    /// <summary>The client exactly as the app registers it, with the network replaced by <paramref name="handler"/>.</summary>
    private static (IOrchestratorClient Client, ListLogger Log) Create(FakeHandler handler, int timeoutSeconds = 240)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:Enabled"] = "true",
            ["Orchestrator:BaseUrl"] = "https://orchestrator.example.test",
            ["Orchestrator:ApiKey"] = Key,
            ["Orchestrator:TimeoutSeconds"] = timeoutSeconds.ToString()
        }).Build();
        var log = new ListLogger();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));
        services.AddOrchestrator(config);
        services.AddHttpClient<IOrchestratorClient, OrchestratorClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IOrchestratorClient>(), log);
    }

    private static OrchestrateRequest Request() => new() { Prompt = Prompt, Metadata = new Dictionary<string, string> { ["source"] = "dotnet-app" } };

    [Fact]
    public async Task Success_returns_id_and_final_answer_and_keeps_the_rest_of_the_artifact()
    {
        var handler = FakeHandler.Returns(HttpStatusCode.OK, SuccessBody);
        var (client, log) = Create(handler);

        var result = await client.OrchestrateAsync(Request());

        Assert.Equal("run-42", result.Id);
        Assert.Equal("{\"headline\":\"Fine\"}", result.Artifact.FinalAnswer!.Original);
        Assert.Equal("ठीक", result.Artifact.FinalAnswer.Translated);
        Assert.Equal("groq", result.Artifact.Raw.GetProperty("judge").GetProperty("winner").GetString());

        var (request, body) = handler.Sent.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://orchestrator.example.test/orchestrate", request.RequestUri!.ToString());
        Assert.Equal(Key, request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(Prompt, json.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("dotnet-app", json.RootElement.GetProperty("metadata").GetProperty("source").GetString());
        // nulls are omitted (no systemPrompt, providers, targetLanguage or responseSchema)
        Assert.False(json.RootElement.TryGetProperty("systemPrompt", out _));
        Assert.False(json.RootElement.TryGetProperty("providers", out _));
        Assert.False(json.RootElement.TryGetProperty("targetLanguage", out _));
        Assert.False(json.RootElement.TryGetProperty("responseSchema", out _));

        Assert.Contains(log.Lines, l => l.Contains("run-42"));
        Assert.DoesNotContain(log.Lines, l => l.Contains(Key) || l.Contains(Prompt));
    }

    [Fact]
    public async Task Request_sends_optional_members_in_camel_case_and_drops_an_empty_provider_list()
    {
        var handler = FakeHandler.Returns(HttpStatusCode.OK, SuccessBody);
        var (client, _) = Create(handler);
        await client.OrchestrateAsync(Request() with { SystemPrompt = "be brief", TargetLanguage = "es", ResponseSchema = """{"type":"object"}""", Providers = Array.Empty<string>() });

        using var json = JsonDocument.Parse(handler.Sent.Single().Body!);
        Assert.Equal("be brief", json.RootElement.GetProperty("systemPrompt").GetString());
        Assert.Equal("es", json.RootElement.GetProperty("targetLanguage").GetString());
        // the service wants the schema as a string
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("responseSchema").ValueKind);
        Assert.Equal("""{"type":"object"}""", json.RootElement.GetProperty("responseSchema").GetString());
        Assert.False(json.RootElement.TryGetProperty("providers", out _));
    }

    [Fact]
    public async Task Unauthorized_401_throws_with_status_and_error()
    {
        var (client, _) = Create(FakeHandler.Returns(HttpStatusCode.Unauthorized, """{ "ok": false, "error": "Invalid API key", "code": "AUTH" }"""));

        var ex = await Assert.ThrowsAsync<OrchestratorException>(() => client.OrchestrateAsync(Request()));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("Invalid API key", ex.Error);
        Assert.Equal("AUTH", ex.Code);
        Assert.True(ex.IsUnauthorized);
        Assert.False(ex.IsRateLimited);
    }

    [Fact]
    public async Task Rate_limited_429_is_flagged_and_not_retried()
    {
        var handler = FakeHandler.Returns(HttpStatusCode.TooManyRequests, """{ "ok": false, "error": "Rate limit exceeded", "code": "RATE_LIMIT" }""");
        var (client, _) = Create(handler);

        var ex = await Assert.ThrowsAsync<OrchestratorException>(() => client.OrchestrateAsync(Request()));

        Assert.True(ex.IsRateLimited);
        Assert.Equal("RATE_LIMIT", ex.Code);
        Assert.Single(handler.Sent);   // exactly one call – paid and rate limited, never retried
    }

    [Fact]
    public async Task Server_error_500_with_json_body_throws_with_the_error()
    {
        var handler = FakeHandler.Returns(HttpStatusCode.InternalServerError, """{ "ok": false, "error": "Pipeline failed: judge unavailable" }""");
        var (client, _) = Create(handler);

        var ex = await Assert.ThrowsAsync<OrchestratorException>(() => client.OrchestrateAsync(Request()));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal("Pipeline failed: judge unavailable", ex.Error);
        Assert.Null(ex.Code);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Server_error_500_with_html_body_says_the_response_was_not_json()
    {
        var (client, _) = Create(FakeHandler.Returns(HttpStatusCode.InternalServerError, "<html><body>Worker threw exception</body></html>", "text/html"));

        var ex = await Assert.ThrowsAsync<OrchestratorException>(() => client.OrchestrateAsync(Request()));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("not JSON", ex.Error);
        Assert.Contains("text/html", ex.Error);
    }

    [Fact]
    public async Task Ok_status_with_ok_false_is_an_error()
    {
        var (client, _) = Create(FakeHandler.Returns(HttpStatusCode.OK, """{ "ok": false, "error": "No providers active" }"""));

        var ex = await Assert.ThrowsAsync<OrchestratorException>(() => client.OrchestrateAsync(Request()));

        Assert.Contains("No providers active", ex.Error);
    }

    [Fact]
    public async Task Timeout_throws_an_orchestrator_exception_marked_as_timeout()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://orchestrator.example.test/"), Timeout = TimeSpan.FromMilliseconds(200) };
        var client = new OrchestratorClient(http, new Logger(new ListLogger()));

        var ex = await Assert.ThrowsAsync<OrchestratorException>(() => client.OrchestrateAsync(Request()));

        Assert.True(ex.IsTimeout);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_reported_as_a_timeout()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var (client, _) = Create(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OrchestrateAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task Registered_client_uses_the_configured_timeout()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:Enabled"] = "false", ["Orchestrator:BaseUrl"] = "https://orchestrator.example.test", ["Orchestrator:TimeoutSeconds"] = "1"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddOrchestrator(config);
        services.AddHttpClient<IOrchestratorClient, OrchestratorClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        var client = services.BuildServiceProvider().GetRequiredService<IOrchestratorClient>();

        var ex = await Assert.ThrowsAsync<OrchestratorException>(() => client.OrchestrateAsync(Request()));

        Assert.True(ex.IsTimeout);
    }

    [Fact]
    public async Task GetResult_returns_null_for_404()
    {
        var (client, _) = Create(FakeHandler.Returns(HttpStatusCode.NotFound, """{ "ok": false, "error": "Not found" }"""));

        Assert.Null(await client.GetResultAsync("missing"));
    }

    [Fact]
    public async Task GetResult_returns_the_artifact()
    {
        var handler = FakeHandler.Returns(HttpStatusCode.OK, """{ "ok": true, "artifact": { "finalAnswer": { "original": "Hello", "translated": "Hola" } } }""");
        var (client, _) = Create(handler);

        var r = await client.GetResultAsync("run 42");

        Assert.Equal("Hello", r!.Artifact.FinalAnswer!.Original);
        Assert.Equal("https://orchestrator.example.test/result/run%2042", handler.Sent.Single().Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Health_is_parsed()
    {
        var (client, _) = Create(FakeHandler.Returns(HttpStatusCode.OK,
            """{ "ok": true, "activeProviders": ["gemini","cohere","groq"], "activeCount": 3, "judgeProvider": "groq", "factCheckProvider": "cohere" }"""));

        var h = await client.GetHealthAsync();

        Assert.True(h.Ok);
        Assert.Equal(3, h.ActiveCount);
        Assert.Equal("groq", h.JudgeProvider);
        Assert.Equal("gemini,cohere,groq", string.Join(",", h.ActiveProviders));
    }

    [Fact]
    public void Options_validation_fails_when_enabled_without_a_key()
    {
        var result = new OrchestratorOptionsValidator().Validate(null, new OrchestratorOptions { Enabled = true, BaseUrl = "https://x.example", ApiKey = "" });
        Assert.True(result.Failed);
        Assert.Contains("ApiKey", result.FailureMessage);
    }

    [Fact]
    public void Options_validation_rejects_plain_http_and_skips_everything_when_disabled()
    {
        Assert.True(new OrchestratorOptionsValidator().Validate(null, new OrchestratorOptions { Enabled = true, BaseUrl = "http://x.example", ApiKey = "k" }).Failed);
        Assert.True(new OrchestratorOptionsValidator().Validate(null, new OrchestratorOptions { Enabled = false }).Succeeded);
    }

    [Fact]
    public void Options_are_validated_when_read()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Orchestrator:Enabled"] = "true" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddOrchestrator(config);
        var sp = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value);
    }

    /// <summary>Adapts the list logger to ILogger&lt;OrchestratorClient&gt;.</summary>
    private sealed class Logger : ILogger<OrchestratorClient>
    {
        private readonly ILogger _inner;
        public Logger(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel l, EventId id, TState s, Exception? e, Func<TState, Exception?, string> f) => _inner.Log(l, id, s, e, f);
    }
}
