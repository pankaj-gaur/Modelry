using System.Text.RegularExpressions;
using Modelry.Core.Model;

namespace Modelry.Core.Pages;

public enum MatchConfidence { None, Low, Medium, High, Explicit }

/// <summary>A section and the mapping row (component placement) it was matched to.</summary>
public sealed class SectionMatch
{
    public required HtmlSection Section { get; init; }
    public IaMappingRow? Row { get; set; }
    public string? SchemaTitle { get; set; }
    public string? TemplateTitle { get; set; }
    public string? Region { get; set; }
    public MatchConfidence Confidence { get; set; }
    public List<string> Reasons { get; } = new();
    public bool Excluded { get; set; }
    /// <summary>Schemas of the mapping rows rendered inside this one (list items, carousel slides).</summary>
    public List<string> ItemSchemas { get; } = new();
    /// <summary>True when the user changed the suggestion on the validation screen.</summary>
    public bool Overridden { get; set; }
}

public sealed class PageMatch
{
    public required IaPage Page { get; init; }
    public List<SectionMatch> Sections { get; } = new();
    /// <summary>Mapping rows for the page that expect an authored section, but no section was matched to them.</summary>
    public List<IaMappingRow> Unmatched { get; } = new();
    /// <summary>Mapping rows that never come from the page body (header, footer, navigation, SG-driven).</summary>
    public List<IaMappingRow> NotExpected { get; } = new();
    /// <summary>Rows rendered inside another row's component (slides, list items), keyed by the parent's Map ID.</summary>
    public Dictionary<string, List<IaMappingRow>> Children { get; } = new();
    /// <summary>The sections after any splitting (indices are final).</summary>
    public List<HtmlSection> FinalSections { get; } = new();
}

/// <summary>
/// Lines the page's sections up with the page's rows in the Page-Schema Mapping sheet. Both are in page order, so the
/// match is an alignment: each pairing is scored on structure (an H1 and a large image for a hero, repeated cards for a
/// collection, a table for a data table…), on class names, and on how the section's heading compares with the UI section
/// named in the specification. Sections or rows without a convincing partner stay unmatched rather than being guessed.
/// </summary>
public static class SectionMatcher
{
    private static readonly HashSet<string> ChromeRegions = new(StringComparer.OrdinalIgnoreCase) { "Header", "Footer", "Global" };

    /// <summary>
    /// A row is a section of its own when its Component Template is a real template in the Templates sheet. Rows whose
    /// template is DXA's built-in Data Presentation or a note such as "(slide – rendered by Hero Carousel CT)" are items
    /// rendered inside the component of the row before them.
    /// </summary>
    public static bool ExpectsSection(IaWorkbook wb, IaMappingRow r) =>
        !ChromeRegions.Contains(r.Region ?? "") && !string.Equals(r.ContentSource, "SG-driven", StringComparison.OrdinalIgnoreCase)
        && r.SchemaTitle is not null && IsRealTemplate(wb, r.ComponentTemplate);

    public static bool IsItemRow(IaWorkbook wb, IaMappingRow r) =>
        !ChromeRegions.Contains(r.Region ?? "") && r.SchemaTitle is not null && !IsRealTemplate(wb, r.ComponentTemplate);

    private static bool IsRealTemplate(IaWorkbook wb, string? title) =>
        title is not null && wb.Templates.Any(t => t.Kind == IaTemplateKind.ComponentTemplate && IaWorkbook.Same(t.Title, title));

    public static PageMatch Match(IaWorkbook wb, IaPage page, IReadOnlyList<HtmlSection> sections)
    {
        var result = new PageMatch { Page = page };
        var all = wb.Mapping.Where(r => IaWorkbook.Same(r.PageId, page.PageId)).ToList();
        IaMappingRow? parent = null;
        foreach (var r in all)
        {
            if (ExpectsSection(wb, r)) { parent = r; continue; }
            if (IsItemRow(wb, r) && parent is not null)
            {
                if (!result.Children.TryGetValue(parent.MapId, out var list)) result.Children[parent.MapId] = list = new List<IaMappingRow>();
                list.Add(r);
            }
            else result.NotExpected.Add(r);
        }
        var rows = all.Where(r => ExpectsSection(wb, r)).ToList();

        // Align; then, while rows are still unmatched, split a section into its blocks if that matches more rows.
        var current = sections.ToList();
        var best = Align(wb, current, rows);
        for (var round = 0; round < 6 && best.Pairs.Count < rows.Count; round++)
        {
            (List<HtmlSection> List, Alignment A)? better = null;
            foreach (var s in current)
            {
                var blocks = HtmlSections.SubBlocks(s.Element);
                if (blocks.Count == 0) continue;
                var trial = current.SelectMany(x => x == s
                    ? blocks.Select(b => new HtmlSection { Index = -1, Element = b, Features = HtmlSections.Features(b), Label = HtmlSections.LabelOf(b), SplitFrom = s.Label })
                    : new[] { x }).ToList();
                var a = Align(wb, trial, rows);
                if (a.Pairs.Count > best.Pairs.Count && (better is null || a.Total > better.Value.A.Total)) better = (trial, a);
            }
            if (better is null) break;
            current = better.Value.List;
            best = better.Value.A;
        }

        // Final numbering (also written on the elements for the preview).
        foreach (var s in sections) s.Element.RemoveAttribute(HtmlSections.SectionAttribute);
        var final = current.Select((s, i) => HtmlSections.Create(s.Element, i, s.SplitFrom)).ToList();
        result.FinalSections.AddRange(final);
        if (!ReferenceEquals(current, sections)) best = Align(wb, final, rows);

        for (var i = 0; i < final.Count; i++)
        {
            var sm = new SectionMatch { Section = final[i] };
            if (best.Pairs.TryGetValue(i, out var j))
            {
                var r = rows[j];
                sm.Row = r; sm.SchemaTitle = r.SchemaTitle; sm.TemplateTitle = r.ComponentTemplate; sm.Region = r.Region;
                var sc = best.Scores[i, j];
                sm.Confidence = sc >= 100 ? MatchConfidence.Explicit : sc >= 8 ? MatchConfidence.High : sc >= 4 ? MatchConfidence.Medium : MatchConfidence.Low;
                sm.Reasons.AddRange(best.Why[i, j]);
                if (result.Children.TryGetValue(r.MapId, out var kids)) sm.ItemSchemas.AddRange(kids.Select(k => k.SchemaTitle!).Distinct());
            }
            else
            {
                sm.Reasons.Add("No component in this page's mapping fits this section. Choose one, or leave it excluded.");
                // Say which row came closest and why it was not used, so a wrong exclusion is easy to diagnose.
                if (rows.Count > 0)
                {
                    var k = Enumerable.Range(0, rows.Count).OrderByDescending(j2 => best.Scores[i, j2]).First();
                    var taken = best.Pairs.FirstOrDefault(p => p.Value == k);
                    var near = $"Closest: {rows[k].MapId} {StripPrefix(rows[k].ComponentTemplate ?? rows[k].SchemaTitle ?? "")} (score {best.Scores[i, k]:0.#}";
                    sm.Reasons.Add(best.Scores[i, k] < MinScore ? near + $", below the {MinScore} needed)."
                        : best.Pairs.ContainsValue(k) ? near + $"), but it fits section {taken.Key + 1} better." : near + "), but taking it would break the page order of the other matches.");
                }
            }
            if (final[i].SplitFrom is { } from) sm.Reasons.Insert(0, $"Split out of the section \"{from}\", which holds several components.");
            sm.Excluded = sm.Row is null;
            result.Sections.Add(sm);
        }
        var used = best.Pairs.Values.ToHashSet();
        result.Unmatched.AddRange(rows.Where((r, j) => !used.Contains(j)));
        return result;
    }

    private sealed record Alignment(Dictionary<int, int> Pairs, double Total, double[,] Scores, List<string>[,] Why);

    private static Alignment Align(IaWorkbook wb, IReadOnlyList<HtmlSection> sections, IReadOnlyList<IaMappingRow> rows)
    {
        int n = sections.Count, m = rows.Count;
        var score = new double[n, m];
        var why = new List<string>[n, m];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < m; j++)
            {
                why[i, j] = new List<string>();
                score[i, j] = Score(wb, sections[i], rows[j], i, n, why[i, j]);
            }
        // Both lists are in page order: maximise the total score of accepted pairs.
        var best = new double[n + 1, m + 1];
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var v = Math.Max(best[i - 1, j], best[i, j - 1]);
                if (score[i - 1, j - 1] >= MinScore) v = Math.Max(v, best[i - 1, j - 1] + score[i - 1, j - 1]);
                best[i, j] = v;
            }
        var pairs = new Dictionary<int, int>();
        for (int i = n, j = m; i > 0 && j > 0;)
        {
            if (score[i - 1, j - 1] >= MinScore && Math.Abs(best[i, j] - (best[i - 1, j - 1] + score[i - 1, j - 1])) < 1e-9) { pairs[i - 1] = j - 1; i--; j--; }
            else if (Math.Abs(best[i, j] - best[i - 1, j]) < 1e-9) i--;
            else j--;
        }
        return new Alignment(pairs, best[n, m], score, why);
    }

    private const double MinScore = 1.5;

    /// <summary>How well a section fits a mapping row. 100+ = named explicitly in the HTML.</summary>
    public static double Score(IaWorkbook wb, HtmlSection s, IaMappingRow row, int position, int total, List<string> why)
    {
        var f = s.Features;
        if (s.ExplicitComponent is { } ex)
        {
            var hit = new[] { row.MapId, row.ComponentTemplate, row.SchemaTitle, ViewName(wb, row.ComponentTemplate) }
                .Any(v => v is not null && (IaWorkbook.Same(v, ex) || IaWorkbook.Same(StripPrefix(v), ex)));
            if (hit) { why.Add($"Named in the HTML (data-cms-component=\"{ex}\")."); return 100; }
            why.Add($"The HTML names a different component (\"{ex}\")."); return -10;
        }

        double score = 0;
        var schemaWords = HtmlSections.Words(StripPrefix(row.SchemaTitle ?? ""));
        var templateWords = HtmlSections.Words(StripPrefix(row.ComponentTemplate ?? "")).Except(schemaWords).ToHashSet();
        var tokenHits = schemaWords.Where(f.Tokens.Contains).ToList();
        if (tokenHits.Count > 0) { score += 3 * tokenHits.Count; why.Add($"Class names mention {string.Join(", ", tokenHits)}."); }
        var presHits = templateWords.Where(f.Tokens.Contains).ToList();
        if (presHits.Count > 0) { score += 2 * presHits.Count; why.Add($"Class names mention the presentation ({string.Join(", ", presHits)})."); }
        var ui = row.UiSection is null ? new HashSet<string>() : HtmlSections.Words(row.UiSection);
        var headingHits = ui.Where(f.HeadingWords.Contains).ToList();
        if (headingHits.Count > 0)
        {
            score += Math.Min(6, 2 * headingHits.Count);
            why.Add($"Heading matches the spec's \"{row.UiSection}\".");
        }

        var (s2, reason) = Structure(row, f, position);
        score += s2;
        if (reason is not null) why.Add(reason);

        // Carousel markup (aria-roledescription="carousel", data-carousel, slider library classes) supports a carousel row.
        // Only a bonus: a section can hold a small slider without being a carousel component, so other rows lose nothing.
        if (Regex.IsMatch($"{row.SchemaTitle} {row.ComponentTemplate}", "carousel|slider|slideshow", RegexOptions.IgnoreCase)
            && HtmlSections.HasCarousel(s.Element))
        {
            score += 3; why.Add("Carousel markup in the HTML, as expected for a carousel.");
        }
        // The first section with the page's H1 is the hero when the mapping has a Hero row.
        if (position == 0 && f.H1 > 0 && string.Equals(row.Region, "Hero", StringComparison.OrdinalIgnoreCase)) score += 2;
        return score;
    }

    /// <summary>Structural fit of the section to the kind of component, from words in the schema title.</summary>
    private static (double Score, string? Reason) Structure(IaMappingRow row, SectionFeatures f, int position)
    {
        var t = (row.SchemaTitle ?? "").ToLowerInvariant();
        var hero = string.Equals(row.Region, "Hero", StringComparison.OrdinalIgnoreCase) || t.Contains("hero");
        bool Has(params string[] w) => w.Any(t.Contains);

        if (hero)
            return (f.H1 > 0 || position == 0) && (f.Images + f.Videos) > 0 ? (5, "Top-of-page H1 with an image or video, as expected for a hero.")
                 : f.H1 > 0 ? (3, "Contains the page's H1.") : position == 0 ? (1, "First section on the page.") : (-2, null);
        if (Has("accordion", "faq")) return f.Disclosures || f.RepeatedItems >= 2 ? (5, "Expandable items, as expected for an accordion.") : (-1, null);
        if (Has("tab")) return f.TabList ? (6, "Has a tab list.") : f.RepeatedItems >= 2 ? (1.5, "Repeated panels (no tab roles found).") : (-1, null);
        if (Has("table")) return f.Table ? (6, "Contains a table.") : (-3, null);
        if (Has("chart")) return f.Chart ? (6, "Contains a chart.") : (-2, null);
        if (Has("form")) return f.Form ? (6, "Contains form fields.") : (-3, null);
        if (Has("quote", "testimonial")) return f.Blockquote ? (6, "Contains a quotation.") : (-1, null);
        if (Has("map", "location finder")) return f.Map ? (5, "Contains a map.") : (-1, null);
        if (Has("timeline")) return f.YearTexts >= 2 ? (5, "Several years listed, as in a timeline.") : (-1, null);
        if (Has("statistic", "stat ", "kpi", "figure")) return f.NumberTexts >= 2 ? (5, "Several key figures.") : (-1, null);
        if (Has("people", "person", "team", "leadership")) return f.RepeatedItems >= 2 && f.Images >= 2 ? (4, "Repeated profiles with photos.") : (0, null);
        if (Has("card", "collection", "grid", "list", "carousel"))
            return f.RepeatedItems >= 2 ? (4 + Math.Min(2, f.Dates > 1 && Has("list") ? 2 : 0), $"{f.RepeatedItems} repeated items, as expected for a {(Has("list") ? "list" : "collection")}.") : (-1, null);
        if (Has("video")) return f.Videos > 0 ? (5, "Contains a video.") : (-1, null);
        if (Has("widget", "integration")) return f.Embed || f.Form ? (4, "Embedded widget.") : (0, null);
        if (Has("promo", "banner", "cta"))
            return f.Links > 0 && f.Headings > 0 && f.RepeatedItems < 3 ? (3, "Heading with a call to action, as expected for a banner.") : (0, null);
        if (Has("content", "text", "block", "intro", "rich"))
            return f.Paragraphs > 0 && f.RepeatedItems < 3 && !f.Table && !f.Form ? (3, "Heading and paragraphs, as expected for a content block.") : (0.5, null);
        return (0, null);
    }

    public static string StripPrefix(string title)
    {
        var i = title.IndexOf(" – ", StringComparison.Ordinal);
        if (i < 0) i = title.IndexOf(" - ", StringComparison.Ordinal);
        var rest = i >= 0 && i < 12 ? title[(i + 3)..] : title;
        return rest.EndsWith(" CT", StringComparison.Ordinal) ? rest[..^3] : rest;
    }

    private static string? ViewName(IaWorkbook wb, string? template)
    {
        var view = wb.Templates.FirstOrDefault(t => IaWorkbook.Same(t.Title, template))?.View;
        return view is null ? null : view.Contains(':') ? view[(view.IndexOf(':') + 1)..] : view;
    }
}
