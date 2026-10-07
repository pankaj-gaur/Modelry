using System.Collections.Concurrent;
using Modelry.Core.Import;
using Modelry.Core.Model;
using Modelry.Web.Models;

namespace Modelry.Web.Services;

/// <summary>What a finished schema check knows – reused by Create so the publication is not read twice.</summary>
public sealed record SchemaCheckSnapshot(IaWorkbook Workbook, ImportContext Context, ImportPlan Plan, string UploadId, string FolderId);

/// <summary>What a finished template check knows – reused by Create.</summary>
public sealed record TemplateCheckSnapshot(Modelry.Core.Templates.TemplatePlan Plan, Modelry.Core.Templates.TemplateContext Context,
    Modelry.Core.Templates.TemplateImportOptions Options, string UploadId, string FolderId);

public enum JobState { Running, Completed, Cancelled, Failed, PlanErrors, Checked }

/// <summary>One running / finished import. Receives progress synchronously from the executor (thread-safe).</summary>
public sealed class ImportJob : IProgress<ImportProgress>
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _recent = new();
    private ImportProgress _latest = ImportProgress.Preparing(ImportSteps.Names);
    /// <summary>Step labels shown on the progress page (schema import or template import).</summary>
    public string[] StepNames { get; init; } = ImportSteps.Names;
    /// <summary>"Schemas" or "Templates" – used in titles.</summary>
    public string What { get; init; } = "Schemas";
    /// <summary>"Check" (dry run – changes nothing) or "Create".</summary>
    public string Mode { get; init; } = "Create";
    public bool IsCheck => Mode == "Check";
    private int _created, _errors, _warnings;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Owner { get; init; }
    public required string FileName { get; init; }
    public required string FolderId { get; init; }
    public string FolderTitle { get; set; } = "";
    public string PublicationTitle { get; set; } = "";
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; private set; }
    public JobState State { get; private set; } = JobState.Running;
    public string? Error { get; private set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public ImportResult? Result { get; private set; }
    public string? ResultId { get; private set; }
    public object? PlanErrorsModel { get; private set; }
    /// <summary>Check jobs: the snapshot Create can reuse (cleared once used).</summary>
    public object? CheckSnapshot { get; set; }
    public string PlanErrorsView { get; private set; } = "DryRun";

    public void Report(ImportProgress value)
    {
        lock (_lock)
        {
            _latest = value;
            if (value.LastEntry is { } e)
            {
                _recent.Add(e);
                if (_recent.Count > 50) _recent.RemoveAt(0);
                if (e.Level == IssueLevel.Error) _errors++;
                else if (e.Level == IssueLevel.Warning) _warnings++;
                else if (e.Message.Contains("created", StringComparison.OrdinalIgnoreCase) && e.Step != "Done") _created++;
            }
        }
    }

    public void Complete(ImportResult result, string resultId) =>
        Finish(result.Cancelled ? JobState.Cancelled : JobState.Completed, () => { Result = result; ResultId = resultId; });
    public void Fail(string error) => Finish(JobState.Failed, () => Error = error);
    public void PlanFailed(object model, string viewName = "DryRun") =>
        Finish(JobState.PlanErrors, () => { PlanErrorsModel = model; PlanErrorsView = viewName; });

    public void Start() => Report(ImportProgress.Preparing(StepNames));

    /// <summary>Check jobs: move to a step (progress = completed steps / all steps).</summary>
    public void ReportStep(int step, string? activity = null) =>
        Report(new ImportProgress(step, StepNames.Length, StepNames[step - 1], 0, 0, step - 1, StepNames.Length, activity, null));

    /// <summary>Live activity text for the current step (e.g. "folder 12 of 140").</summary>
    /// <summary>
    /// Live activity while reading the publication. Also shown in "Latest activity": one row per phase
    /// (e.g. "Looking for existing schemas"), updated in place as its counts change.
    /// </summary>
    public void ReportActivity(string activity)
    {
        lock (_lock)
        {
            _latest = _latest with { CurrentItem = activity };
            var phase = activity.Split(" – ")[0];
            var entry = new LogEntry(DateTime.UtcNow, IssueLevel.Info, "Reading", phase, activity);
            if (_recent.Count > 0 && _recent[^1].Step == "Reading" && _recent[^1].Item == phase) _recent[^1] = entry;
            else
            {
                _recent.Add(entry);
                if (_recent.Count > 50) _recent.RemoveAt(0);
            }
        }
    }

    /// <summary>Check finished: the findings page is ready.</summary>
    public void CheckDone(object model, string viewName) =>
        Finish(JobState.Checked, () => { PlanErrorsModel = model; PlanErrorsView = viewName; });

    private void Finish(JobState state, Action set)
    {
        lock (_lock) { set(); State = state; FinishedUtc = DateTime.UtcNow; }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            var p = _latest;
            var finished = State != JobState.Running;
            var percent = finished && State is JobState.Completed or JobState.Checked ? 100
                : p.OverallTotal == 0 ? 0 : (int)Math.Floor(100.0 * p.OverallDone / p.OverallTotal);
            return new
            {
                state = State.ToString(),
                mode = Mode,
                step = p.Step, stepCount = p.StepCount, stepName = p.StepName,
                stepDone = p.StepDone, stepTotal = p.StepTotal,
                overallDone = p.OverallDone, overallTotal = p.OverallTotal, percent,
                currentItem = p.CurrentItem,
                elapsedSeconds = (int)((FinishedUtc ?? DateTime.UtcNow) - StartedUtc).TotalSeconds,
                created = _created, errors = _errors, warnings = _warnings,
                error = Error,
                recent = _recent.TakeLast(8).Reverse().Select(e => new
                {
                    time = e.TimeUtc.ToString("HH:mm:ss"), level = e.Level.ToString(), step = e.Step, item = e.Item, message = e.Message
                })
            };
        }
    }
}

/// <summary>In-memory registry of import jobs (per server instance). Jobs are kept for 2 hours.</summary>
public sealed class ImportJobStore
{
    private readonly ConcurrentDictionary<string, ImportJob> _jobs = new();

    public ImportJob Add(ImportJob job)
    {
        foreach (var old in _jobs.Values.Where(j => j.FinishedUtc < DateTime.UtcNow.AddHours(-2)))
            _jobs.TryRemove(old.Id, out _);
        _jobs[job.Id] = job;
        return job;
    }

    /// <summary>Returns the job only to the session that started it.</summary>
    public ImportJob? Get(string id, string owner) =>
        _jobs.TryGetValue(id, out var job) && job.Owner == owner ? job : null;
}
