using System.Globalization;
using System.Text.RegularExpressions;

namespace Modelry.Core.Audit;

public enum FindingSeverity { Info, Warning, Error }
public enum FindingCategory { ContentModel, Publishing, SiteStructure, Multimedia, Usage }

/// <summary>One item a rule flagged.</summary>
public sealed record AuditFinding(string RuleId, string ItemId, AuditKind Kind, string Title, string Path, string Message);

/// <summary>Outcome of one rule: how many items it looked at and how many it flagged – or why it could not run.</summary>
public sealed class RuleResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required FindingCategory Category { get; init; }
    public required FindingSeverity Severity { get; init; }
    public bool Evaluated { get; set; }
    /// <summary>Why the rule did not run (data not available on this CMS, limit hit, needs a deep scan …).</summary>
    public string? NotEvaluatedReason { get; set; }
    public bool NeedsDeepScan { get; set; }
    public int Population { get; set; }
    public int Affected { get; set; }
    /// <summary>
    /// False for checks that run and list items for review but do not count towards the health score: usage checks in a
    /// standard scan (links between items are not read, so "unused" cannot be decided) and review-only lists.
    /// </summary>
    public bool Scored { get; set; } = true;
    public string? NotScoredReason { get; set; }
    /// <summary>1 = nothing flagged, 0 = everything flagged.</summary>
    public double Score => Population == 0 ? 1 : 1 - Math.Min(1.0, (double)Affected / Population);
    public double Weight => WeightOf(Severity);
    /// <summary>True when this check is part of the health score.</summary>
    public bool CountsTowardsScore => Evaluated && Scored && Population > 0;

    public static double WeightOf(FindingSeverity s) => s switch { FindingSeverity.Error => 3, FindingSeverity.Warning => 2, _ => 1 };
}

/// <summary>0–100 overall and per category; a category is null when none of its rules could run.</summary>
public sealed class HealthScore
{
    public int Overall { get; init; }
    public string Grade { get; init; } = "";
    public Dictionary<FindingCategory, int?> Categories { get; init; } = new();

    public static string GradeOf(int score) => score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : score >= 40 ? "D" : "E";

    /// <summary>Grade bands, best first: grade, lowest score, what it means.</summary>
    public static readonly (string Grade, int From, string Meaning)[] Bands =
    {
        ("A", 90, "Well kept. Only minor housekeeping."),
        ("B", 75, "Healthy, with some housekeeping to plan."),
        ("C", 60, "Noticeable clean-up debt. Plan a clean-up before the next big change."),
        ("D", 40, "Significant issues that editors and visitors are likely to notice."),
        ("E", 0, "Needs attention before a migration, upgrade or redesign."),
    };

    /// <summary>Rounds a 0–100 score the way people expect (72.5 → 73).</summary>
    public static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Audit checks (phase 2): usage and orphans, publish health, content-model hygiene, site structure and multimedia, plus the
/// health score. Each rule declares the columns it needs; when one is not available on this CMS (or needs a deep scan) the
/// rule is reported as "not checked" with the reason and left out of the score – it never counts as a pass or a fail.
/// </summary>
public static partial class AuditRules
{
    public static string CategoryTitle(FindingCategory c) => c switch
    {
        FindingCategory.ContentModel => "Content model",
        FindingCategory.Publishing => "Publishing",
        FindingCategory.SiteStructure => "Site structure",
        FindingCategory.Multimedia => "Multimedia",
        FindingCategory.Usage => "Usage",
        _ => c.ToString()
    };

    private enum RuleCms { Both, Tridion, Aem }

    /// <summary>A check. Applies to Tridion, AEM or both; AemTitle / AemDescription give the AEM wording.</summary>
    private sealed record Rule(string Id, FindingCategory Category, FindingSeverity Severity, string Title, string Description,
        Action<RuleRun> Check, RuleCms Cms = RuleCms.Both, string? AemTitle = null, string? AemDescription = null, string? ReviewOnly = null)
    {
        public bool AppliesTo(string cms) => Cms == RuleCms.Both || (Cms == RuleCms.Aem) == AuditCms.IsAem(cms);
    }

    /// <summary>State handed to a rule while it runs.</summary>
    private sealed class RuleRun
    {
        public required AuditSnapshot S { get; init; }
        public required AuditOptions O { get; init; }
        public required RuleResult Result { get; init; }
        public List<AuditFinding> Findings { get; } = new();

        public List<AuditRow> Rows(params AuditKind[] kinds) => kinds.SelectMany(k => S.Items[k]).ToList();

        /// <summary>False (and marks the rule not checked) when a needed column is not available.</summary>
        public bool Need(AuditKind kind, string column)
        {
            if (Result.NotEvaluatedReason is not null) return false;
            if (S.UnavailableReason(kind, column) is not { } reason) return true;
            Result.NotEvaluatedReason = reason;
            Result.NeedsDeepScan = reason == AuditCatalog.DeepReason;
            return false;
        }

        public void Skip(string reason) => Result.NotEvaluatedReason ??= reason;

        public void Flag(AuditRow r, string message) =>
            Findings.Add(new AuditFinding(Result.Id, r.Id, r.Kind, r.Title, r.Path, message));

        public void Evaluate(IReadOnlyCollection<AuditRow> population, Func<AuditRow, string?> check)
        {
            Result.Population = population.Count;
            foreach (var r in population) if (check(r) is { } message) Flag(r, message);
        }
    }

    private static int Num(AuditRow r, string key) => int.TryParse(r.Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static bool IsComponentSchema(AuditRow r) => r.Value("purpose") is "Component" or "Multimedia";

    /// <summary>Published to a target of this publication, or (Tridion BluePrint) from a child publication.</summary>
    private static bool IsPublishedAnywhere(AuditRow r) => r.Publish == PublishState.Published || r.Value("_publishedElsewhere") == "1";

    /// <summary>Rows whose where-used count and publish status are both known – the population of the "unused" checks.</summary>
    private static List<AuditRow> UsageKnown(IEnumerable<AuditRow> rows) =>
        rows.Where(x => x.Value("usedBy").Length > 0 && (x.Publish is PublishState.Published or PublishState.Unpublished || x.Value("_publishedElsewhere") == "1")).ToList();

    /// <summary>Truly unused: not published anywhere, not on a page and not linked from anything (where-used is empty).</summary>
    private static bool IsUnused(AuditRow r) => Num(r, "usedBy") == 0 && !IsPublishedAnywhere(r);

    private static string UnusedMessage(AuditSnapshot s) => AuditCms.IsAem(s.Cms)
        ? "Not published and not referenced by any page or fragment."
        : "Not published, not on any page and not linked from any component, page or keyword.";

    public const string UsageStandardReason = "Usage is scored only in a deep scan: a standard scan does not read links between items, so it cannot tell whether an item is really unused.";

    [GeneratedRegex(@"\b(copy( of)?|test|testing|temp|tmp|old|backup|bak|delete|deleted|do not use|dummy|xxx|lorem)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SuspiciousWords();
    [GeneratedRegex(@"^\s|\s$|\s{2,}")]
    private static partial Regex StraySpaces();
    [GeneratedRegex(@"[^a-z0-9\-_.]")]
    private static partial Regex UnsafeFileName();

    private static readonly Rule[] All =
    {
        // ------------------------------------------------------------------ content model
        new("M01", FindingCategory.ContentModel, FindingSeverity.Warning, "Component schemas without a Component Template",
            "Content on these schemas cannot be rendered by any template in this publication.", r =>
            {
                if (!r.Need(AuditKind.Schemas, "linkedCts")) return;
                r.Evaluate(r.Rows(AuditKind.Schemas).Where(x => x.Value("purpose") == "Component").ToList(),
                    x => Num(x, "linkedCts") == 0 ? "No Component Template links this schema." : null);
            }, RuleCms.Tridion),
        new("M02", FindingCategory.ContentModel, FindingSeverity.Warning, "Component Templates without a DXA view",
            "DXA cannot render a component presentation whose template names no view.", r =>
            {
                if (!r.Need(AuditKind.ComponentTemplates, "view")) return;
                r.Evaluate(r.Rows(AuditKind.ComponentTemplates), x => x.Value("view").Length == 0 ? "The view metadata field is empty." : null);
            }, RuleCms.Tridion),
        new("M03", FindingCategory.ContentModel, FindingSeverity.Warning, "Component Templates not linked to any schema",
            "A template with no linked schemas cannot be used for any component.", r =>
            {
                if (!r.Need(AuditKind.ComponentTemplates, "linkedSchemas")) return;
                r.Evaluate(r.Rows(AuditKind.ComponentTemplates), x => x.Value("linkedSchemas").Length == 0 ? "No linked schemas." : null);
            }, RuleCms.Tridion),
        new("M04", FindingCategory.ContentModel, FindingSeverity.Info, "Page Templates without includes",
            "DXA pages normally include the header and footer; a Page Template without includes renders a bare page.", r =>
            {
                if (!r.Need(AuditKind.PageTemplates, "includes")) return;
                r.Evaluate(r.Rows(AuditKind.PageTemplates), x => x.Value("includes").Length == 0 ? "The includes metadata field is empty." : null);
            }, RuleCms.Tridion),
        new("M05", FindingCategory.ContentModel, FindingSeverity.Warning, "Duplicate titles",
            "Schemas or templates of the same kind with the same title are easy to confuse when editors pick one.", r =>
            {
                var rows = r.Rows(AuditKind.Schemas, AuditKind.ComponentTemplates, AuditKind.PageTemplates);
                var dup = rows.GroupBy(x => (x.Kind, Title: x.Title.Trim().ToLowerInvariant())).Where(g => g.Count() > 1)
                    .SelectMany(g => g.Select(x => (Row: x, Others: g.Where(o => o.Id != x.Id).Select(o => o.Id).ToList()))).ToDictionary(x => x.Row.Id, x => x.Others);
                r.Evaluate(rows, x => dup.TryGetValue(x.Id, out var others) ? $"Same title as {string.Join(", ", others)}." : null);
            }, AemTitle: "Duplicate titles", AemDescription: "Content Fragment Models, components or templates with the same title are easy to confuse."),
        new("M06", FindingCategory.ContentModel, FindingSeverity.Info, "Suspicious names",
            "Titles that look temporary (copy, test, old, backup, delete …) or have stray spaces.", r =>
            {
                r.Evaluate(r.Rows(AuditKind.Schemas, AuditKind.ComponentTemplates, AuditKind.PageTemplates, AuditKind.Components,
                    AuditKind.Multimedia, AuditKind.Pages, AuditKind.StructureGroups, AuditKind.Categories, AuditKind.Keywords), x =>
                    SuspiciousWords().Match(x.Title) is { Success: true } m ? $"Title contains \"{m.Value}\"."
                    : StraySpaces().IsMatch(x.Title) ? "Title has leading, trailing or double spaces." : null);
            }, AemTitle: "Suspicious names", AemDescription: "Titles that look temporary (copy, test, old, backup, delete …) or have stray spaces."),
        new("M07", FindingCategory.ContentModel, FindingSeverity.Info, "Naming convention",
            "Titles that do not match the naming convention configured for their building block (Audit:Naming).", r =>
            {
                var rules = r.O.Naming.Where(kv => Enum.TryParse<AuditKind>(kv.Key, true, out _) && kv.Value.Length > 0)
                    .Select(kv => (Kind: Enum.Parse<AuditKind>(kv.Key, true), Pattern: kv.Value)).ToList();
                if (rules.Count == 0) { r.Skip("No naming conventions configured (appsettings Audit:Naming)."); return; }
                var regex = new Dictionary<AuditKind, Regex>();
                foreach (var (kind, pattern) in rules)
                {
                    try { regex[kind] = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
                    catch (ArgumentException) { r.S.Note($"Naming convention for {kind} is not a valid regular expression: {pattern}"); }
                }
                r.Evaluate(r.Rows(regex.Keys.ToArray()), x => regex[x.Kind].IsMatch(x.Title) ? null : $"Does not match {regex[x.Kind]}.");
            }, AemTitle: "Naming convention", AemDescription: "Titles that do not match the naming convention configured for their type (Audit:Naming)."),

        // ------------------------------------------------------------------ publishing
        new("P01", FindingCategory.Publishing, FindingSeverity.Warning, "Changed since last publish",
            "Published items edited after they were last published – the site shows an older version.", r =>
            {
                if (!r.Need(AuditKind.Pages, "publish") || !r.Need(AuditKind.Pages, "modified")) return;
                var rows = r.Rows(AuditKind.Components, AuditKind.Multimedia, AuditKind.Pages)
                    .Where(x => x.Modified is not null && x.PublishedTo.Any(t => t.PublishedAt is not null)).ToList();
                if (rows.Count == 0 && r.Rows(AuditKind.Pages).Any(x => x.Publish == PublishState.Published))
                { r.Skip("Publish dates are not available (publish status came from the list flag)."); return; }
                r.Evaluate(rows, x =>
                {
                    var last = x.PublishedTo.Max(t => t.PublishedAt)!.Value;
                    return x.Modified > last.AddMinutes(1) ? $"Modified {x.Modified:yyyy-MM-dd HH:mm}, last published {last:yyyy-MM-dd HH:mm}." : null;
                });
            }, AemTitle: "Changed since last publish", AemDescription: "Published pages, fragments or assets edited after they were last published – the site shows an older version."),
        new("P02", FindingCategory.Publishing, FindingSeverity.Info, "Published to some targets only",
            "Items published to one target (e.g. Staging) but not another (e.g. Live).", r =>
            {
                if (!r.Need(AuditKind.Pages, "publish")) return;
                var targets = r.S.PublishTargets.ToList();
                if (targets.Count < 2) { r.Skip(targets.Count == 0 ? "No publish targets found." : $"Only one publish target ({targets[0]})."); return; }
                // AEM: Preview is a page tier – fragments and assets are normally only published to Publish.
                var kinds = AuditCms.IsAem(r.S.Cms) ? new[] { AuditKind.Pages } : new[] { AuditKind.Components, AuditKind.Multimedia, AuditKind.Pages };
                r.Evaluate(r.Rows(kinds).Where(x => x.Publish == PublishState.Published).ToList(), x =>
                {
                    var missing = targets.Where(t => !x.PublishedTo.Any(p => p.Target.Equals(t, StringComparison.OrdinalIgnoreCase))).ToList();
                    return missing.Count > 0 ? $"Not published to {string.Join(", ", missing)}." : null;
                });
            }, AemTitle: "Published to some tiers only", AemDescription: "Pages published to one tier (e.g. Preview) but not another (e.g. Publish)."),
        new("P03", FindingCategory.Publishing, FindingSeverity.Info, "Pages never published",
            "Pages that exist in the CMS but are not on any target.", r =>
            {
                if (!r.Need(AuditKind.Pages, "publish")) return;
                r.Evaluate(r.Rows(AuditKind.Pages).Where(x => x.Publish != PublishState.Unknown).ToList(),
                    x => x.Publish == PublishState.Unpublished ? "Not published." : null);
            }, AemTitle: "Pages never published", AemDescription: "Pages that exist on author but were never published, or were unpublished."),
        new("P04", FindingCategory.Publishing, FindingSeverity.Info, "Published pages not updated for a long time",
            "Published pages whose last change is older than the configured number of months (Audit:StaleMonths).", r =>
            {
                if (!r.Need(AuditKind.Pages, "publish") || !r.Need(AuditKind.Pages, "modified")) return;
                var cutoff = DateTime.UtcNow.AddMonths(-Math.Max(1, r.O.StaleMonths));
                r.Evaluate(r.Rows(AuditKind.Pages).Where(x => x.Publish == PublishState.Published && x.Modified is not null).ToList(),
                    x => x.Modified < cutoff ? $"Last changed {x.Modified:yyyy-MM-dd} (over {r.O.StaleMonths} months ago)." : null);
            }, AemTitle: "Published pages not updated for a long time", AemDescription: "Published pages whose last change is older than the configured number of months (Audit:StaleMonths)."),

        // ------------------------------------------------------------------ site structure
        new("S01", FindingCategory.SiteStructure, FindingSeverity.Info, "Empty Structure Groups",
            "Structure Groups with no pages and no sub-groups.", r =>
            {
                r.Evaluate(r.Rows(AuditKind.StructureGroups).Where(x => x.Path.Length > 0).ToList(),
                    x => Num(x, "pages") == 0 && Num(x, "subGroups") == 0 ? "No pages and no sub-groups." : null);
            }, RuleCms.Tridion),
        new("S02", FindingCategory.SiteStructure, FindingSeverity.Info, "Inactive Structure Groups",
            "Pages in an inactive Structure Group are not published.", r =>
            {
                if (!r.Need(AuditKind.StructureGroups, "active")) return;
                r.Evaluate(r.Rows(AuditKind.StructureGroups), x => x.Value("active") == "No" ? $"Inactive, holds {Num(x, "pages")} pages." : null);
            }, RuleCms.Tridion),
        new("S03", FindingCategory.SiteStructure, FindingSeverity.Warning, "Pages without component presentations",
            "Pages that would render with no content.", r =>
            {
                if (!r.Need(AuditKind.Pages, "cps")) return;
                r.Evaluate(r.Rows(AuditKind.Pages), x => Num(x, "cps") == 0 ? "No component presentations." : null);
            }, AemTitle: "Pages without components", AemDescription: "Pages whose content holds no components."),
        new("S04", FindingCategory.SiteStructure, FindingSeverity.Error, "Duplicate page file names",
            "Two pages with the same file name in one Structure Group publish to the same URL – one overwrites the other.", r =>
            {
                if (!r.Need(AuditKind.Pages, "fileName")) return;
                var rows = r.Rows(AuditKind.Pages);
                var dup = rows.Where(x => x.Value("fileName").Length > 0)
                    .GroupBy(x => (x.Value("_sgId"), x.Value("fileName").ToLowerInvariant())).Where(g => g.Count() > 1)
                    .SelectMany(g => g).Select(x => x.Id).ToHashSet();
                r.Evaluate(rows, x => dup.Contains(x.Id) ? $"File name \"{x.Value("fileName")}\" is used by another page in {(x.Path.Length == 0 ? "the root" : x.Path)}." : null);
            }, RuleCms.Tridion),
        new("S05", FindingCategory.SiteStructure, FindingSeverity.Info, "File names with capitals, spaces or special characters",
            "Lower-case letters, digits, hyphens and underscores make predictable, shareable URLs.", r =>
            {
                if (!r.Need(AuditKind.Pages, "fileName")) return;
                r.Evaluate(r.Rows(AuditKind.Pages).Where(x => x.Value("fileName").Length > 0).ToList(),
                    x => UnsafeFileName().IsMatch(x.Value("fileName")) ? $"File name \"{x.Value("fileName")}\"." : null);
            }, AemTitle: "Page names with capitals, spaces or special characters", AemDescription: "Lower-case letters, digits and hyphens make predictable, shareable URLs."),

        // ------------------------------------------------------------------ multimedia
        new("MM01", FindingCategory.Multimedia, FindingSeverity.Warning, "Large multimedia files",
            "Files above the configured size (Audit:LargeFileKb) slow pages down.", r =>
            {
                if (!r.Need(AuditKind.Multimedia, "sizeKb")) return;
                r.Evaluate(r.Rows(AuditKind.Multimedia).Where(x => x.Value("sizeKb").Length > 0).ToList(),
                    x => Num(x, "sizeKb") > r.O.LargeFileKb ? $"{Num(x, "sizeKb"):N0} KB (limit {r.O.LargeFileKb:N0} KB)." : null);
            }, AemTitle: "Large assets", AemDescription: "Original files above the configured size (Audit:LargeFileKb) slow pages and downloads."),

        // ------------------------------------------------------------------ usage and orphans
        new("U01", FindingCategory.Usage, FindingSeverity.Warning, "Schemas with no components",
            "Component and multimedia schemas that no component in this publication uses.", r =>
            {
                if (!r.Need(AuditKind.Schemas, "components")) return;
                r.Evaluate(r.Rows(AuditKind.Schemas).Where(IsComponentSchema).ToList(),
                    x => Num(x, "components") == 0 ? "No components in this publication use this schema." : null);
            }, AemTitle: "Content Fragment Models with no fragments", AemDescription: "Models no Content Fragment under the site's DAM folder uses."),
        new("U02", FindingCategory.Usage, FindingSeverity.Info, "Component Templates not used on any page",
            "Templates no page in this publication uses (they may be used for dynamic presentations).", r =>
            {
                if (!r.Need(AuditKind.ComponentTemplates, "pages")) return;
                r.Evaluate(r.Rows(AuditKind.ComponentTemplates), x => Num(x, "pages") == 0 ? "Not on any page." : null);
            }, AemTitle: "Components not used on any page", AemDescription: "Components in /apps that no page of this site uses (they may be used by other sites or templates)."),
        new("U03", FindingCategory.Usage, FindingSeverity.Info, "Page Templates not used by any page",
            "Page Templates no page in this publication uses.", r =>
            {
                if (!r.Need(AuditKind.PageTemplates, "pages")) return;
                r.Evaluate(r.Rows(AuditKind.PageTemplates), x => Num(x, "pages") == 0 ? "No pages use this template." : null);
            }, AemTitle: "Templates not used by any page", AemDescription: "Editable templates no page of this site uses."),
        new("U04", FindingCategory.Usage, FindingSeverity.Warning, "Components not used anywhere",
            "Truly unused: not published, not on any page and not linked from any other component, page or keyword – in this or any " +
            "child publication (deep scan). Components whose publish status is unknown are left out.", r =>
            {
                if (!r.Need(AuditKind.Components, "usedBy") || !r.Need(AuditKind.Components, "publish")) return;
                r.Evaluate(UsageKnown(r.Rows(AuditKind.Components)), x => IsUnused(x) ? UnusedMessage(r.S) : null);
            }, AemTitle: "Fragments not used anywhere",
            AemDescription: "Truly unused: not published and not referenced by any page or other fragment of this site (deep scan). Fragments whose publish status is unknown are left out."),
        new("U05", FindingCategory.Usage, FindingSeverity.Info, "Components not placed on any page",
            "Components not on any page directly – a review list. Many are linked from other components or published as dynamic " +
            "content, so this is not counted in the score; a deep scan finds the truly unused ones.", r =>
            {
                if (r.S.Deep) { r.Skip("Replaced by the deep-scan usage check (U04)."); return; }
                if (!r.Need(AuditKind.Components, "onPages")) return;
                r.Evaluate(r.Rows(AuditKind.Components), x => Num(x, "onPages") == 0 ? "Not placed on a page (may be linked from another component)." : null);
            }, AemTitle: "Fragments not placed on any page", AemDescription: "Fragments not referenced from a page directly – a review list. Some are referenced from other fragments or delivered headless, so this is not counted in the score; a deep scan finds the truly unused ones."),
        new("U06", FindingCategory.Usage, FindingSeverity.Warning, "Unused multimedia",
            "Truly unused images and documents: not published, not on any page and not linked from any component, page or keyword – " +
            "in this or any child publication (deep scan). Items whose publish status is unknown are left out.", r =>
            {
                if (!r.Need(AuditKind.Multimedia, "usedBy") || !r.Need(AuditKind.Multimedia, "publish")) return;
                r.Evaluate(UsageKnown(r.Rows(AuditKind.Multimedia)), x => IsUnused(x) ? UnusedMessage(r.S) : null);
            }, AemTitle: "Unused assets",
            AemDescription: "Truly unused: not published and not referenced by any page or fragment of this site (deep scan). Assets whose publish status is unknown are left out."),
        new("U07", FindingCategory.Usage, FindingSeverity.Info, "Unused keywords",
            "Keywords no item is classified with (deep scan).", r =>
            {
                if (!r.Need(AuditKind.Keywords, "usedBy")) return;
                r.Evaluate(r.Rows(AuditKind.Keywords).Where(x => x.Value("abstract") != "Yes" && x.Value("usedBy").Length > 0).ToList(),
                    x => Num(x, "usedBy") == 0 ? (AuditCms.IsAem(r.S.Cms) ? "No page, fragment or asset is tagged with it." : "No item uses this keyword.") : null);
            }, AemTitle: "Unused tags", AemDescription: "Tags no page, fragment or asset of this site is tagged with (deep scan)."),
        new("U08", FindingCategory.Usage, FindingSeverity.Info, "Categories not used in any schema",
            "Categories no schema field or metadata field refers to (deep scan).", r =>
            {
                if (!r.Need(AuditKind.Categories, "schemas")) return;
                r.Evaluate(r.Rows(AuditKind.Categories).Where(x => x.Value("schemas").Length > 0).ToList(), x => Num(x, "schemas") == 0 ? "No schema uses this category." : null);
            }, RuleCms.Tridion),
        new("U09", FindingCategory.Usage, FindingSeverity.Info, "Published but not placed or linked",
            "Published components and multimedia that no page, component or keyword uses. Not counted as unused: they may be " +
            "delivered as dynamic content (DCPs, Content Service / GraphQL) or linked directly. Review whether they are still needed.", r =>
            {
                if (!r.Need(AuditKind.Components, "usedBy") || !r.Need(AuditKind.Components, "publish")) return;
                r.Evaluate(UsageKnown(r.Rows(AuditKind.Components, AuditKind.Multimedia)), x =>
                    Num(x, "usedBy") == 0 && IsPublishedAnywhere(x)
                        ? AuditCms.IsAem(r.S.Cms)
                            ? "Published, but no page or fragment refers to it – it may be delivered headless or linked directly."
                            : x.Publish == PublishState.Published
                                ? "Published, but no page or component uses it – it may be delivered as dynamic content."
                                : "Published from a child publication, but no page or component uses it."
                        : null);
            }, AemTitle: "Published but not referenced",
            AemDescription: "Published fragments and assets that no page or fragment of this site refers to. Not counted as unused: they may be " +
                            "delivered headless (GraphQL, Assets HTTP API) or linked directly. Review whether they are still needed.",
            ReviewOnly: "A review list, not counted in the score: published content may be delivered dynamically."),

        // ------------------------------------------------------------------ AEM only
        new("A01", FindingCategory.SiteStructure, FindingSeverity.Warning, "Pages on a disabled or missing template",
            "Pages whose editable template is disabled, or is missing from the site's /conf folder.", r =>
            {
                if (!r.Need(AuditKind.Pages, "pageTemplate")) return;
                var templates = r.Rows(AuditKind.PageTemplates).ToDictionary(t => AuditFinalizer.ItemKey(t.Id), t => t);
                var confRoots = templates.Keys.Select(k => k.Split("/settings/")[0]).Distinct().ToList();
                r.Evaluate(r.Rows(AuditKind.Pages).Where(x => x.Value("_pageTemplateId").Length > 0).ToList(), x =>
                {
                    var key = AuditFinalizer.ItemKey(x.Value("_pageTemplateId"));
                    if (templates.TryGetValue(key, out var t)) return t.Value("status") == "disabled" ? $"Template \"{t.Title}\" is disabled." : null;
                    return confRoots.Any(c => key.StartsWith(c + "/", StringComparison.Ordinal)) ? $"Template {x.Value("_pageTemplateId")} no longer exists." : null;
                });
            }, RuleCms.Aem),
        new("A02", FindingCategory.Multimedia, FindingSeverity.Info, "Assets without a title or description",
            "Images without dc:title or dc:description often end up without meaningful alt text.", r =>
            {
                if (!r.Need(AuditKind.Multimedia, "assetTitle")) return;
                r.Evaluate(r.Rows(AuditKind.Multimedia).Where(x => x.Value("mmType").StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToList(),
                    x => x.Value("assetTitle").Length == 0 && x.Value("_assetDescription").Length == 0 ? "No dc:title and no dc:description." : null);
            }, RuleCms.Aem),
        new("A03", FindingCategory.Multimedia, FindingSeverity.Warning, "Assets not fully processed",
            "Assets whose processing (renditions, metadata extraction) did not finish.", r =>
            {
                if (!r.Need(AuditKind.Multimedia, "assetState")) return;
                r.Evaluate(r.Rows(AuditKind.Multimedia).Where(x => x.Value("assetState").Length > 0).ToList(),
                    x => x.Value("assetState").Equals("processed", StringComparison.OrdinalIgnoreCase) ? null : $"Processing state: {x.Value("assetState")}.");
            }, RuleCms.Aem),
        new("A04", FindingCategory.Publishing, FindingSeverity.Warning, "Unpublished pages still linked from published pages",
            "Visitors following these links from the live site get a 404.", r =>
            {
                if (!r.Need(AuditKind.Pages, "publish") || !r.Need(AuditKind.Pages, "cps")) return;
                var pages = r.Rows(AuditKind.Pages);
                var linkedFrom = new Dictionary<string, List<string>>();
                foreach (var p in pages.Where(p => p.Publish == PublishState.Published))
                    foreach (var target in p.Value("_pageLinks").Split(AuditFinalizer.ListSeparator, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var key = AuditFinalizer.ItemKey(target);
                        if (!linkedFrom.TryGetValue(key, out var list)) linkedFrom[key] = list = new List<string>();
                        list.Add(p.Title);
                    }
                r.Evaluate(pages.Where(p => p.Publish == PublishState.Unpublished).ToList(), x =>
                    linkedFrom.TryGetValue(AuditFinalizer.ItemKey(x.Id), out var from) ? $"Linked from {from.Count} published page(s), e.g. \"{from[0]}\"." : null);
            }, RuleCms.Aem),
        new("A05", FindingCategory.SiteStructure, FindingSeverity.Info, "Live copies with cancelled inheritance",
            "Pages and fragments that no longer receive changes from their blueprint (MSM).", r =>
            {
                var rows = r.Rows(AuditKind.Pages, AuditKind.Components).Where(x => x.Origin is BluePrintOrigin.Shared or BluePrintOrigin.Localized).ToList();
                if (rows.Count == 0) { r.Skip("This site has no live copies."); return; }
                r.Evaluate(rows, x => x.Origin == BluePrintOrigin.Localized ? "Inheritance cancelled." : null);
            }, RuleCms.Aem),
    };

    public static IReadOnlyList<(string Id, string Title)> Catalog => All.Select(r => (r.Id, r.Title)).ToList();

    /// <summary>Runs every rule, fills Findings, Rules and Health on the snapshot (replacing any earlier result).</summary>
    public static void Evaluate(AuditSnapshot s, AuditOptions options)
    {
        s.Findings.Clear();
        s.Rules.Clear();
        var aem = AuditCms.IsAem(s.Cms);
        foreach (var rule in All.Where(r => r.AppliesTo(s.Cms)))
        {
            var result = new RuleResult
            {
                Id = rule.Id, Title = aem ? rule.AemTitle ?? rule.Title : rule.Title,
                Description = aem ? rule.AemDescription ?? rule.Description : rule.Description, Category = rule.Category, Severity = rule.Severity
            };
            var run = new RuleRun { S = s, O = options, Result = result };
            try { rule.Check(run); }
            catch (Exception ex) { result.NotEvaluatedReason = "The check failed: " + ex.Message; }
            result.Evaluated = result.NotEvaluatedReason is null;
            if (result.Evaluated)
            {
                result.Affected = run.Findings.Select(f => f.ItemId).Distinct().Count();
                s.Findings.AddRange(run.Findings);
            }
            if (rule.ReviewOnly is not null) { result.Scored = false; result.NotScoredReason = rule.ReviewOnly; }
            else if (rule.Category == FindingCategory.Usage && !s.Deep) { result.Scored = false; result.NotScoredReason = UsageStandardReason; }
            s.Rules.Add(result);
        }
        s.Health = Score(s.Rules);
    }

    /// <summary>
    /// Health score. Check score = items not flagged ÷ items checked. Area score = weighted average of its check scores
    /// (Error 3, Warning 2, Info 1) × 100. Overall = plain average of the area scores. Checks that could not run, review-only
    /// checks and (in a standard scan) the usage checks are left out; an area with no scored check is null and left out.
    /// </summary>
    public static HealthScore Score(IEnumerable<RuleResult> rules)
    {
        var list = rules.ToList();
        var categories = Enum.GetValues<FindingCategory>().ToDictionary(c => c, c =>
        {
            var scored = list.Where(r => r.Category == c && r.CountsTowardsScore).ToList();
            return scored.Count == 0 ? (int?)null : HealthScore.Round(100 * scored.Sum(r => r.Weight * r.Score) / scored.Sum(r => r.Weight));
        });
        var values = categories.Values.Where(v => v is not null).Select(v => v!.Value).ToList();
        var overall = values.Count == 0 ? 100 : HealthScore.Round(values.Average());
        return new HealthScore { Overall = overall, Grade = HealthScore.GradeOf(overall), Categories = categories };
    }

    /// <summary>True when the area is left out of the score because the scan was a standard one (Usage).</summary>
    public static bool NotScoredInThisScan(AuditSnapshot s, FindingCategory c) => c == FindingCategory.Usage && !s.Deep;

    /// <summary>Item id → titles of the rules that flagged it (for the Findings column of the detail tables).</summary>
    public static Dictionary<string, List<string>> ByItem(AuditSnapshot s, AuditKind? kind = null) =>
        s.Findings.Where(f => kind is null || f.Kind == kind).GroupBy(f => f.ItemId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.RuleId).Distinct().ToList());
}
