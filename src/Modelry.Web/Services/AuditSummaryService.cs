using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Modelry.Core.Audit;
using Modelry.Web.Services.Orchestrator;

namespace Modelry.Web.Services;

/// <summary>
/// Reads ai-summary.json (prompts, language, providers, answer schema) from the content root. The file is read again when it
/// changes, so prompts can be tuned on the server without a rebuild or restart.
/// </summary>
public sealed class AuditSummarySettingsProvider
{
    public const string FileName = "ai-summary.json";
    private readonly string _path;
    private readonly ILogger<AuditSummarySettingsProvider> _log;
    private readonly object _lock = new();
    private DateTime _stamp;
    private AuditSummarySettings? _settings;
    private string? _error;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    private sealed class FileShape { public AuditSummarySettings? AuditSummary { get; set; } }

    public AuditSummarySettingsProvider(IWebHostEnvironment env, ILogger<AuditSummarySettingsProvider> log)
    {
        _path = Path.Combine(env.ContentRootPath, FileName);
        _log = log;
    }

    /// <summary>The current settings, or an error describing what is wrong with the file.</summary>
    public (AuditSummarySettings? Settings, string? Error) Get()
    {
        lock (_lock)
        {
            var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            if (stamp == _stamp && (_settings is not null || _error is not null)) return (_settings, _error);
            _stamp = stamp;
            _settings = null;
            _error = null;
            try
            {
                if (stamp == DateTime.MinValue) throw new FileNotFoundException($"{FileName} was not found in {Path.GetDirectoryName(_path)}.");
                var s = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Options)?.AuditSummary
                        ?? throw new InvalidDataException($"{FileName} has no \"AuditSummary\" section.");
                var problems = s.Validate().ToList();
                if (problems.Count > 0) throw new InvalidDataException($"{FileName}: {string.Join(" ", problems)}");
                _settings = s;
                _log.LogInformation("AI summary settings loaded from {File} (max {Words} words, translation: {Language})", FileName, s.MaxWords,
                    s.HasTranslation ? s.TargetLanguage : "none");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                _error = ex.Message;
                _log.LogWarning("AI summary settings could not be loaded: {Error}", ex.Message);
            }
            return (_settings, _error);
        }
    }
}

public enum AuditSummaryState { Running, Completed, Failed }

/// <summary>One AI summary request for one open audit.</summary>
public sealed class AuditSummaryJob
{
    public required string AuditJobId { get; init; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public AuditSummaryState State { get; set; } = AuditSummaryState.Running;
    public AuditAiSummary? Result { get; set; }
    public string? Error { get; set; }
    public bool RateLimited { get; set; }
    /// <summary>Skipped with "Skip the summary" during the audit – not an error.</summary>
    public bool Skipped { get; set; }

    public object Status() => new
    {
        state = State.ToString().ToLowerInvariant(),
        elapsed = (int)(DateTime.UtcNow - StartedUtc).TotalSeconds,
        error = Error,
        rateLimited = RateLimited,
        skipped = Skipped,
        summary = Result
    };
}

/// <summary>
/// Generates AI executive summaries of audits. The call takes 1.5–3 minutes, so it runs in the background and the dashboard
/// polls for the result – no browser request waits on it. Summaries are saved beside saved audits and reused, because the
/// service is paid and rate limited (5 calls an hour per IP: the Modelry server's IP, shared by all its users).
/// </summary>
public sealed class AuditSummaryService
{
    private readonly ConcurrentDictionary<string, AuditSummaryJob> _jobs = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly OrchestratorOptions _o;
    private readonly AuditSummarySettingsProvider _settings;
    private readonly AuditSnapshotStore _history;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AuditSummaryService> _log;

    public AuditSummaryService(IServiceScopeFactory scopes, IOptions<OrchestratorOptions> o, AuditSummarySettingsProvider settings,
        AuditSnapshotStore history, IHostApplicationLifetime lifetime, ILogger<AuditSummaryService> log)
    {
        _scopes = scopes; _o = o.Value; _settings = settings; _history = history; _lifetime = lifetime; _log = log;
    }

    /// <summary>On when Orchestrator:Enabled is true (the key and URL are then validated at start-up).</summary>
    public bool Enabled => _o.Enabled;

    /// <summary>The summary request of an open audit; for a saved audit, a summary saved earlier is loaded once.</summary>
    public AuditSummaryJob? Get(string auditJobId, string cms, AuditSnapshot snap)
    {
        Prune();
        if (_jobs.TryGetValue(auditJobId, out var job)) return job;
        if (snap.SavedId is { } savedId && _history.LoadSummary(cms, snap.PublicationId, savedId) is { } saved)
            return _jobs.GetOrAdd(auditJobId, _ => new AuditSummaryJob { AuditJobId = auditJobId, State = AuditSummaryState.Completed, Result = saved });
        return null;
    }

    /// <summary>
    /// Starts a summary in the background (dashboard button) unless one is already running for this audit.
    /// The caller has checked <see cref="Enabled"/>.
    /// </summary>
    public AuditSummaryJob Start(string auditJobId, string cms, AuditSnapshot snap, AuditSummaryPrompt.Previous? previous, string who)
    {
        var (job, work) = Prepare(auditJobId, cms, snap, previous, who);
        if (work is not null) _ = Task.Run(() => work(_lifetime.ApplicationStopping));
        return job;
    }

    /// <summary>
    /// Writes the summary and waits for it: the last step of an audit, before the dashboard opens. Never throws – a failed
    /// or skipped summary is recorded on the returned job and shown on the dashboard. <paramref name="ct"/> = the audit's
    /// Stop button (skips the summary) or the app shutting down.
    /// </summary>
    public async Task<AuditSummaryJob> RunAsync(string auditJobId, string cms, AuditSnapshot snap, AuditSummaryPrompt.Previous? previous, string who, CancellationToken ct)
    {
        var (job, work) = Prepare(auditJobId, cms, snap, previous, who);
        if (work is not null) await work(ct);
        return job;
    }

    private (AuditSummaryJob Job, Func<CancellationToken, Task>? Work) Prepare(string auditJobId, string cms, AuditSnapshot snap,
        AuditSummaryPrompt.Previous? previous, string who)
    {
        var job = new AuditSummaryJob { AuditJobId = auditJobId };
        var current = _jobs.AddOrUpdate(auditJobId, job, (_, existing) => existing.State == AuditSummaryState.Running ? existing : job);
        if (!ReferenceEquals(current, job)) return (current, null);

        var (settings, error) = _settings.Get();
        if (settings is null)
        {
            job.State = AuditSummaryState.Failed;
            job.Error = "The AI summary is not set up correctly: " + error;
            return (job, null);
        }

        // Everything the prompt needs is read now, on the request thread; the snapshot is not touched afterwards.
        var data = AuditSummaryPrompt.BuildData(snap, settings, previous);
        var request = new OrchestrateRequest
        {
            Prompt = AuditSummaryPrompt.Fill(settings.PromptTemplate, data, settings.MaxWords),
            SystemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt) ? null : AuditSummaryPrompt.Fill(settings.SystemPrompt, "", settings.MaxWords),
            Providers = settings.Providers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().ToLowerInvariant()).ToList(),
            TargetLanguage = settings.RequestLanguage,
            ResponseSchema = settings.ResponseSchemaText,
            Metadata = new Dictionary<string, string> { ["source"] = "modelry", ["feature"] = "audit-summary", ["cms"] = AuditCms.IsAem(snap.Cms) ? "aem" : "tridion" }
        };
        var publicationId = snap.PublicationId;
        var savedId = snap.SavedId;
        var score = snap.Health?.Overall ?? 0;
        var language = settings.HasTranslation ? settings.TargetLanguage.Trim() : null;
        var maxWords = settings.MaxWords;

        async Task Work(CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var scope = _scopes.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<IOrchestratorClient>();
                var response = await client.OrchestrateAsync(request, ct);
                var answer = response.Artifact.FinalAnswer ?? throw new InvalidDataException("The AI service returned no final answer.");
                var summary = AuditAiSummary.From(answer.Original, answer.Translated, language, maxWords);
                summary.CreatedUtc = DateTime.UtcNow;
                summary.CreatedBy = who;
                summary.ResultId = response.Id;
                summary.Seconds = (int)watch.Elapsed.TotalSeconds;
                summary.Score = score;
                if (savedId is not null) _history.SaveSummary(cms, publicationId, savedId, summary);
                job.Result = summary;
                job.State = AuditSummaryState.Completed;
                _log.LogInformation("AUDIT {Who} generated an AI summary of {Publication}: result {ResultId}, {Words} words{Trimmed} in {Seconds:0}s",
                    who, publicationId, response.Id, summary.Words, summary.Trimmed ? " (trimmed)" : "", watch.Elapsed.TotalSeconds);
            }
            catch (OrchestratorException ex)
            {
                job.RateLimited = ex.IsRateLimited;
                job.Error = ex.IsRateLimited ? "The AI service's hourly limit has been reached for this server. Try again later – the limit is shared by everyone using this Modelry server."
                    : ex.IsUnauthorized ? "The AI service did not accept Modelry's API key (Orchestrator:ApiKey). Ask the administrator to check it."
                    : ex.IsTimeout ? $"The AI service did not answer within {_o.TimeoutSeconds} seconds. Try again later."
                    : ex.Message;
                job.State = AuditSummaryState.Failed;
                _log.LogWarning("AI summary of {Publication} failed after {Seconds:0}s: {Status} {Code} {Error}", publicationId, watch.Elapsed.TotalSeconds,
                    ex.StatusCode is { } s ? (int)s : 0, ex.Code, ex.Error);
            }
            catch (OperationCanceledException)
            {
                job.Error = _lifetime.ApplicationStopping.IsCancellationRequested
                    ? "Modelry was restarted while the summary was being written. Generate it again."
                    : "The summary was skipped. Generate it when you need it.";
                job.Skipped = !_lifetime.ApplicationStopping.IsCancellationRequested;
                job.State = AuditSummaryState.Failed;
            }
            catch (Exception ex)
            {
                job.Error = "The AI summary could not be created: " + ex.GetBaseException().Message;
                job.State = AuditSummaryState.Failed;
                _log.LogWarning(ex, "AI summary of {Publication} failed", publicationId);
            }
        }
        return (job, Work);
    }

    private void Prune()
    {
        foreach (var (id, j) in _jobs)
            if (j.State != AuditSummaryState.Running && DateTime.UtcNow - j.StartedUtc > TimeSpan.FromHours(3)) _jobs.TryRemove(id, out _);
    }
}
