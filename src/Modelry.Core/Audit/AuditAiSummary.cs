using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Modelry.Core.Audit;

/// <summary>Settings for the AI executive summary, read from ai-summary.json ("AuditSummary").</summary>
public sealed class AuditSummarySettings
{
    /// <summary>Upper limit for the whole summary (every field together). Longer answers are trimmed.</summary>
    public int MaxWords { get; set; } = 100;
    /// <summary>Second language shown in its own tab (e.g. "ar", "hi"); empty = English only.</summary>
    public string TargetLanguage { get; set; } = "";
    /// <summary>
    /// Sent as targetLanguage when TargetLanguage is empty, so the service does not translate into its default (Hindi).
    /// </summary>
    public string NoTranslationLanguage { get; set; } = "en";
    /// <summary>Empty = every active provider.</summary>
    public List<string> Providers { get; set; } = new();
    /// <summary>At most this many checks (those that flagged items, most severe first) go into the prompt.</summary>
    public int MaxFindings { get; set; } = 15;
    public string SystemPrompt { get; set; } = "";
    /// <summary>Placeholders: {{auditData}}, {{maxWords}}.</summary>
    public string PromptTemplate { get; set; } = "";
    /// <summary>
    /// The answer's structure: a JSON schema written as an object (sent to the service as compact JSON text) or as a string
    /// (sent as it is); null = free-form prose.
    /// </summary>
    public JsonElement? ResponseSchema { get; set; }

    /// <summary>The schema as the text the service expects, or null.</summary>
    public string? ResponseSchemaText => ResponseSchema switch
    {
        { ValueKind: JsonValueKind.Object or JsonValueKind.Array } s => JsonSerializer.Serialize(s),   // compact
        { ValueKind: JsonValueKind.String } s when !string.IsNullOrWhiteSpace(s.GetString()) => s.GetString(),
        _ => null
    };

    public bool HasTranslation => !string.IsNullOrWhiteSpace(TargetLanguage);

    /// <summary>The targetLanguage to send: the configured language, or the "no translation" language.</summary>
    public string? RequestLanguage => HasTranslation ? TargetLanguage.Trim()
        : string.IsNullOrWhiteSpace(NoTranslationLanguage) ? null : NoTranslationLanguage.Trim();

    public IEnumerable<string> Validate()
    {
        if (MaxWords is < 20 or > 1000) yield return "MaxWords must be between 20 and 1000.";
        if (string.IsNullOrWhiteSpace(PromptTemplate)) yield return "PromptTemplate is empty.";
        else if (!PromptTemplate.Contains("{{auditData}}", StringComparison.Ordinal)) yield return "PromptTemplate must contain {{auditData}}.";
        if (ResponseSchema is { } s && s.ValueKind is not (JsonValueKind.Object or JsonValueKind.String or JsonValueKind.Null))
            yield return "ResponseSchema must be a JSON object or a string.";
        if (HasTranslation && !Regex.IsMatch(TargetLanguage.Trim(), "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$")) yield return "TargetLanguage must be a language code such as \"ar\" or \"hi\".";
    }
}

/// <summary>The structured answer: headline, status, a short summary, up to three actions and one decision.</summary>
public sealed class ExecutiveSummary
{
    public string Headline { get; set; } = "";
    /// <summary>"Healthy", "Needs attention" or "At risk".</summary>
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<SummaryAction> Actions { get; set; } = new();
    public string Decision { get; set; } = "";

    public int Words => AuditAiSummary.CountWords(Headline) + AuditAiSummary.CountWords(Status) + AuditAiSummary.CountWords(Summary)
                        + Actions.Sum(a => AuditAiSummary.CountWords(a.Priority) + AuditAiSummary.CountWords(a.Action)) + AuditAiSummary.CountWords(Decision);

    public bool IsEmpty => Headline.Length == 0 && Summary.Length == 0 && Actions.Count == 0 && Decision.Length == 0;
}

public sealed class SummaryAction
{
    /// <summary>"Now", "Next" or "Later".</summary>
    public string Priority { get; set; } = "";
    public string Action { get; set; } = "";
}

/// <summary>
/// An AI executive summary of one audit, saved next to the audit ({id}.summary.json). English is always present – structured
/// when the answer matched the schema, otherwise as plain text; the translation only when a target language is configured.
/// </summary>
public sealed class AuditAiSummary
{
    public ExecutiveSummary? English { get; set; }
    /// <summary>Plain-text English answer, when it did not match the schema.</summary>
    public string? EnglishText { get; set; }
    /// <summary>Target language code, when a translation tab is shown.</summary>
    public string? Language { get; set; }
    public ExecutiveSummary? Translated { get; set; }
    public string? TranslatedText { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    /// <summary>The orchestrator's result id (GET /result/{id}).</summary>
    public string? ResultId { get; set; }
    /// <summary>The answer was longer than MaxWords and was shortened.</summary>
    public bool Trimmed { get; set; }
    public int Words { get; set; }
    public int Seconds { get; set; }
    /// <summary>Score of the audit it summarises (shown with the summary).</summary>
    public int Score { get; set; }

    public bool HasTranslation => Language is not null && (Translated is not null || !string.IsNullOrWhiteSpace(TranslatedText));

    /// <summary>Display name of the translation, e.g. "العربية (Arabic)".</summary>
    public string LanguageName
    {
        get
        {
            if (Language is null) return "";
            try
            {
                var c = CultureInfo.GetCultureInfo(Language);
                return c.NativeName.Equals(c.EnglishName, StringComparison.OrdinalIgnoreCase) ? c.EnglishName : $"{c.NativeName} ({c.EnglishName})";
            }
            catch (CultureNotFoundException) { return Language; }
        }
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true
    };

    public static int CountWords(string? s) => string.IsNullOrWhiteSpace(s) ? 0 : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Builds the summary from the service's final answer: parses the structured answer (also when wrapped in a ```json
    /// fence), falls back to plain text, and keeps the English answer within <paramref name="maxWords"/>.
    /// </summary>
    public static AuditAiSummary From(string? original, string? translated, string? language, int maxWords)
    {
        var s = new AuditAiSummary();
        if (Parse(original) is { } en)
        {
            s.Trimmed = Trim(en, maxWords);
            s.English = en;
            s.Words = en.Words;
        }
        else
        {
            var text = (original ?? "").Trim();
            if (text.Length == 0) throw new InvalidDataException("The AI service returned an empty answer.");
            (s.EnglishText, s.Trimmed) = TrimText(text, maxWords);
            s.Words = CountWords(s.EnglishText);
        }
        if (!string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(translated))
        {
            s.Language = language.Trim();
            if (Parse(translated) is { } tr)
            {
                // Keep the same actions as the (possibly trimmed) English summary.
                if (s.English is not null && tr.Actions.Count > s.English.Actions.Count) tr.Actions.RemoveRange(s.English.Actions.Count, tr.Actions.Count - s.English.Actions.Count);
                s.Translated = tr;
            }
            else s.TranslatedText = translated.Trim();
        }
        return s;
    }

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>The structured answer, or null when the text is not a JSON object with a headline or summary.</summary>
    public static ExecutiveSummary? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var fence = Regex.Match(t, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline);
        if (fence.Success) t = fence.Groups[1].Value;
        if (!t.StartsWith('{')) return null;
        try
        {
            var e = JsonSerializer.Deserialize<ExecutiveSummary>(t, ParseOptions);
            if (e is null || e.IsEmpty) return null;
            e.Headline = e.Headline.Trim(); e.Summary = e.Summary.Trim(); e.Decision = e.Decision.Trim(); e.Status = e.Status.Trim();
            e.Actions = e.Actions.Where(a => !string.IsNullOrWhiteSpace(a.Action)).Take(3).ToList();
            return e;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Shortens a structured summary to <paramref name="max"/> words: drops trailing actions, then shortens the summary, decision and headline.</summary>
    public static bool Trim(ExecutiveSummary e, int max)
    {
        if (e.Words <= max) return false;
        while (e.Words > max && e.Actions.Count > 1) e.Actions.RemoveAt(e.Actions.Count - 1);
        void Cut(Func<string> get, Action<string> set)
        {
            if (e.Words <= max) return;
            var value = get();
            set(TrimText(value, Math.Max(3, CountWords(value) - (e.Words - max))).Text);
        }
        Cut(() => e.Summary, v => e.Summary = v);
        Cut(() => e.Decision, v => e.Decision = v);
        Cut(() => e.Headline, v => e.Headline = v);
        foreach (var a in e.Actions) { var action = a; Cut(() => action.Action, v => action.Action = v); }
        return true;
    }

    public static (string Text, bool Trimmed) TrimText(string text, int max)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= max ? (text.Trim(), false) : (string.Join(' ', words.Take(max)).TrimEnd(',', ';', ':', '-', '–') + "…", true);
    }
}

/// <summary>
/// Turns an audit into the compact data block for the prompt. Only numbers and check names leave the server: no item titles,
/// paths, IDs, user names, CMS host or publication/site name.
/// </summary>
public static class AuditSummaryPrompt
{
    /// <summary>The previous saved audit of the same publication, for the "change since" line.</summary>
    public sealed record Previous(DateTime FinishedUtc, int Score, int Findings);

    public static string BuildData(AuditSnapshot s, AuditSummarySettings settings, Previous? previous = null)
    {
        var sb = new StringBuilder();
        var aem = AuditCms.IsAem(s.Cms);
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"CMS: {(aem ? "Adobe Experience Manager" : "Tridion Sites")} (the company's website content system)");
        sb.AppendLine($"Audit date: {s.FinishedUtc.ToString("yyyy-MM-dd", inv)}; scan: {(s.Deep ? "deep (includes unused content)" : "standard (unused content not checked)")}");
        if (s.Health is { } h)
        {
            sb.AppendLine($"Health score: {h.Overall}/100, grade {h.Grade} (A 90+, B 75-89, C 60-74, D 40-59, E below 40)");
            sb.AppendLine("Area scores: " + string.Join("; ", h.Categories.Select(kv =>
                $"{AuditRules.CategoryTitle(kv.Key)} {(AuditRules.NotScoredInThisScan(s, kv.Key) ? "not scored" : kv.Value?.ToString(inv) ?? "not checked")}")));
        }
        sb.AppendLine("Inventory: " + string.Join("; ", Enum.GetValues<AuditKind>().Select(k =>
        {
            var sum = s.Summarize(k);
            var pub = AuditCatalog.IsPublishable(k) && s.UnavailableReason(k, "publish") is null && sum.Total > 0
                ? $" ({sum.Published} live, {sum.Unpublished} not live)" : "";
            return $"{AuditCatalog.Title(k, s.Cms)} {sum.Total}{pub}";
        })));

        var flagged = s.Rules.Where(r => r.Evaluated && r.Affected > 0)
            .OrderByDescending(r => r.Severity).ThenByDescending(r => r.Scored).ThenByDescending(r => (double)r.Affected / Math.Max(1, r.Population))
            .Take(Math.Max(1, settings.MaxFindings)).ToList();
        sb.AppendLine("Issues found (severity | check | items affected of items checked):");
        if (flagged.Count == 0) sb.AppendLine("- none");
        foreach (var r in flagged)
            sb.AppendLine($"- {r.Severity} | {r.Title} | {r.Affected} of {r.Population}{(r.Scored ? "" : " | for review only")}");
        var more = s.Rules.Count(r => r.Evaluated && r.Affected > 0) - flagged.Count;
        if (more > 0) sb.AppendLine($"- and {more} smaller housekeeping checks with issues");
        var clean = s.Rules.Count(r => r.Evaluated && r.Affected == 0);
        var notRun = s.Rules.Count(r => !r.Evaluated);
        sb.AppendLine($"Checks passed with no issues: {clean}; checks not run: {notRun}");
        if (previous is not null)
            sb.AppendLine($"Previous audit {previous.FinishedUtc.ToString("yyyy-MM-dd", inv)}: score {previous.Score}/100, {previous.Findings} issues " +
                          $"(now {s.Health?.Overall}/100, {s.Rules.Where(r => r.Evaluated).Sum(r => r.Affected)} issues)");
        return sb.ToString().TrimEnd();
    }

    public static string Fill(string template, string auditData, int maxWords) =>
        template.Replace("{{auditData}}", auditData, StringComparison.Ordinal)
                .Replace("{{maxWords}}", maxWords.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
