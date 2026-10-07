namespace Modelry.Core.Import;

/// <summary>Step labels shown in the progress UI (step 1 is performed by the host before execution starts).</summary>
/// <summary>Step labels for the check (dry run) jobs.</summary>
public static class CheckSteps
{
    public static readonly string[] Schemas =
    {
        "Reading the IA workbook",
        "Reading the publication – folders, existing schemas, taxonomy and multimedia types",
        "Validating the workbook and planning the creation order",
    };
    public static readonly string[] Templates =
    {
        "Reading the IA workbook",
        "Reading the publication – folders, existing templates, schemas and base templates",
        "Validating the templates and region constraints",
    };
}

/// <summary>Adapts a callback to IProgress without a synchronisation context (reports run inline).</summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public InlineProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}

public static class ImportSteps
{
    public static readonly string[] Names =
    {
        "Preparing – reading workbook and loading publication snapshot",
        "Creating folders",
        "Creating categories, keywords and schemas in dependency order",
        "Applying deferred references (circular links, keyword metadata schemas)",
    };
    public static int Count => Names.Length;
}

/// <summary>Snapshot of import progress. StepDone / OverallDone count items already finished.</summary>
public sealed record ImportProgress(
    int Step, int StepCount, string StepName, int StepDone, int StepTotal,
    int OverallDone, int OverallTotal, string? CurrentItem, LogEntry? LastEntry)
{
    public static ImportProgress Preparing() => Preparing(ImportSteps.Names);
    public static ImportProgress Preparing(string[] stepNames) => new(1, stepNames.Length, stepNames[0], 0, 0, 0, 0, null, null);
}

/// <summary>Tracks per-step and overall counters and pushes snapshots to an IProgress sink (synchronously).</summary>
internal sealed class ProgressTracker
{
    private readonly IProgress<ImportProgress>? _sink;
    private int _overallTotal;
    private int _step, _stepDone, _stepTotal, _stepBase, _overall;
    private string _name = "";
    private string? _item;

    private readonly string[] _names;

    public ProgressTracker(IProgress<ImportProgress>? sink, int overallTotal, string[]? stepNames = null)
    { _sink = sink; _overallTotal = overallTotal; _names = stepNames ?? ImportSteps.Names; }

    /// <summary>Adds items discovered during execution (e.g. deferred updates) to the overall total.</summary>
    public void AddToTotal(int count) => _overallTotal += count;

    public void Step(int step, int total)
    {
        _step = step; _name = _names[step - 1]; _stepTotal = total; _stepDone = 0; _stepBase = _overall; _item = null;
        Send(null);
    }

    /// <summary>Call before processing an item; the previous item (if any) counts as done.</summary>
    public void Item(string item)
    {
        if (_item is not null) { _stepDone++; _overall++; }
        _item = item;
        Send(null);
    }

    public void EndStep()
    {
        _stepDone = _stepTotal; _overall = _stepBase + _stepTotal; _item = null;
        Send(null);
    }

    public void Entry(LogEntry e) => Send(e);

    private void Send(LogEntry? e) =>
        _sink?.Report(new ImportProgress(_step, _names.Length, _name, _stepDone, _stepTotal, _overall, _overallTotal, _item, e));
}
