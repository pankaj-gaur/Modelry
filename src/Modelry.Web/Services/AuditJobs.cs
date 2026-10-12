using System.Collections.Concurrent;
using Modelry.Core.Audit;

namespace Modelry.Web.Services;

public enum AuditJobState { Running, Completed, Cancelled, Failed }

/// <summary>One audit run of one publication, scanned in the background. Holds the snapshot once finished.</summary>
public sealed class AuditJob : IProgress<AuditProgress>
{
    private readonly object _lock = new();
    private readonly List<string> _recent = new();

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Owner { get; init; }
    public required string PublicationId { get; init; }
    public string PublicationTitle { get; set; } = "";
    /// <summary>CMS the audit ran against (host, or "demo") – a snapshot is only reused for the same CMS.</summary>
    public required string Cms { get; init; }
    /// <summary>"tridion" or "aem" (step names).</summary>
    public string CmsKind { get; init; } = Modelry.Core.Audit.AuditCms.Tridion;
    /// <summary>Deep scan ("where used" per item).</summary>
    public bool Deep { get; init; }
    /// <summary>Opened from the saved history rather than scanned in this session.</summary>
    public bool FromHistory { get; init; }
    /// <summary>The AI executive summary is written as the last step, before the dashboard opens.</summary>
    public bool WithSummary { get; init; }
    public const string SummaryStep = "Writing the AI executive summary";
    /// <summary>Progress steps: the CMS's scan steps, plus the AI summary when it is on.</summary>
    public string[] StepNames => WithSummary ? AuditCatalog.StepsFor(CmsKind).Append(SummaryStep).ToArray() : AuditCatalog.StepsFor(CmsKind);
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; private set; }
    public AuditJobState State { get; private set; } = AuditJobState.Running;
    public int Step { get; private set; } = 1;
    public string Activity { get; private set; } = "Starting";
    public string? Error { get; private set; }
    public AuditSnapshot? Snapshot { get; private set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public void Report(AuditProgress value)
    {
        lock (_lock)
        {
            Step = Math.Clamp(value.Step, 1, StepNames.Length);
            Activity = value.Activity;
            if (_recent.Count == 0 || _recent[^1] != value.Activity)
            {
                _recent.Add(value.Activity);
                if (_recent.Count > 6) _recent.RemoveAt(0);
            }
        }
    }

    public void Complete(AuditSnapshot snapshot) => Finish(AuditJobState.Completed, () => { Snapshot = snapshot; PublicationTitle = snapshot.PublicationTitle; });
    public void Cancel() => Finish(AuditJobState.Cancelled, () => Error = "The audit was stopped.");
    public void Fail(string error) => Finish(AuditJobState.Failed, () => Error = error);

    private void Finish(AuditJobState state, Action set)
    {
        lock (_lock) { set(); State = state; FinishedUtc = DateTime.UtcNow; }
    }

    public object Status()
    {
        lock (_lock)
        {
            var stepNames = StepNames;
            var steps = stepNames.Length;
            var percent = State == AuditJobState.Completed ? 100 : (int)Math.Floor(100.0 * (Step - 1) / steps);
            return new
            {
                state = State.ToString(), step = Step, stepCount = steps, stepName = stepNames[Step - 1], activity = Activity,
                summaryStep = WithSummary && Step == steps,
                percent, elapsedSeconds = (int)((FinishedUtc ?? DateTime.UtcNow) - StartedUtc).TotalSeconds,
                error = Error, recent = _recent.AsEnumerable().Reverse().ToList()
            };
        }
    }
}

/// <summary>In-memory registry of audit runs (per server instance). Finished runs are kept for 2 hours.</summary>
public sealed class AuditJobStore
{
    private readonly ConcurrentDictionary<string, AuditJob> _jobs = new();

    public AuditJob Add(AuditJob job)
    {
        foreach (var old in _jobs.Values.Where(j => j.FinishedUtc < DateTime.UtcNow.AddHours(-2)))
            _jobs.TryRemove(old.Id, out _);
        _jobs[job.Id] = job;
        return job;
    }

    /// <summary>Returns the job only to the session that started it.</summary>
    public AuditJob? Get(string id, string owner) => _jobs.TryGetValue(id, out var job) && job.Owner == owner ? job : null;

    /// <summary>A finished (or running) audit of the same publication on the same CMS, started within <paramref name="maxAge"/>.</summary>
    public AuditJob? Recent(string owner, string cms, string publicationId, TimeSpan maxAge, bool deep) =>
        _jobs.Values.Where(j => j.Owner == owner && j.Cms == cms && j.PublicationId == publicationId && !j.FromHistory && (j.Deep || !deep)
                                && j.State is AuditJobState.Running or AuditJobState.Completed && j.StartedUtc > DateTime.UtcNow - maxAge)
            .OrderByDescending(j => j.StartedUtc).FirstOrDefault();

    /// <summary>Audits of this session, newest first.</summary>
    public IReadOnlyList<AuditJob> ForOwner(string owner, string cms) =>
        _jobs.Values.Where(j => j.Owner == owner && j.Cms == cms && !j.FromHistory).OrderByDescending(j => j.StartedUtc).ToList();

    /// <summary>Drops older completed runs of the same publication once a new one has finished (snapshots can be large).</summary>
    public void Supersede(AuditJob latest)
    {
        foreach (var old in _jobs.Values.Where(j => j.Id != latest.Id && j.Owner == latest.Owner && j.Cms == latest.Cms && !j.FromHistory
                                                    && j.PublicationId == latest.PublicationId && j.State != AuditJobState.Running))
            _jobs.TryRemove(old.Id, out _);
    }
}
