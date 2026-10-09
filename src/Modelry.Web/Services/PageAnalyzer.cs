using AngleSharp.Html.Dom;
using Modelry.Core.Model;
using Modelry.Core.Pages;

namespace Modelry.Web.Services;

/// <summary>Result of reading one page run: sections, their match to the page's mapping and the field bindings.</summary>
public sealed class PageAnalysis
{
    public required IaWorkbook Workbook { get; init; }
    public required IaPage Page { get; init; }
    public required IHtmlDocument Document { get; init; }
    public required List<HtmlSection> Sections { get; init; }
    public required List<IgnoredElement> Ignored { get; init; }
    public required PageMatch Match { get; init; }
    public Dictionary<int, SchemaBinding> Bindings { get; } = new();
    /// <summary>Mapping rows chosen for more than one section.</summary>
    public List<string> DuplicateRows { get; } = new();
}

/// <summary>Reads the run's entry HTML, splits and matches it, applies the validation-screen choices and binds fields.</summary>
public static class PageAnalyzer
{
    public static PageAnalysis Analyze(IaWorkbook wb, IaPage page, PageRun run)
    {
        var doc = HtmlSections.Parse(File.ReadAllText(run.EntryFullPath));
        var (found, ignored) = HtmlSections.Split(doc);
        var match = SectionMatcher.Match(wb, page, found);
        var sections = match.FinalSections;
        run.SectionPaths = sections.Select(s => HtmlSections.PathOf(s.Element)).ToList();
        var rows = wb.Mapping.Where(r => IaWorkbook.Same(r.PageId, page.PageId)).ToList();

        foreach (var sm in match.Sections)
        {
            if (!run.Choices.TryGetValue(sm.Section.Index, out var choice)) continue;
            sm.Overridden = true;
            if (choice == "exclude") { sm.Excluded = true; continue; }
            if (choice.StartsWith("map:", StringComparison.Ordinal) && rows.FirstOrDefault(r => r.MapId == choice[4..]) is { } row)
            {
                sm.Row = row; sm.SchemaTitle = row.SchemaTitle; sm.TemplateTitle = row.ComponentTemplate; sm.Region = row.Region; sm.Excluded = false;
                sm.ItemSchemas.Clear();
                if (match.Children.TryGetValue(row.MapId, out var kids)) sm.ItemSchemas.AddRange(kids.Select(k => k.SchemaTitle!).Distinct());
            }
            else if (choice.StartsWith("ct:", StringComparison.Ordinal) && wb.Templates.FirstOrDefault(t => IaWorkbook.Same(t.Title, choice[3..])) is { } ct)
            {
                sm.Row = null; sm.ItemSchemas.Clear(); sm.TemplateTitle = ct.Title; sm.SchemaTitle = ct.LinkedSchemas.FirstOrDefault(); sm.Region = RegionFor(wb, page, ct.Title); sm.Excluded = sm.SchemaTitle is null;
            }
        }
        var analysis = new PageAnalysis { Workbook = wb, Page = page, Document = doc, Sections = sections, Ignored = ignored, Match = match };
        // Rows used by a choice are no longer "not found"; rows chosen twice are flagged.
        var used = match.Sections.Where(s => !s.Excluded && s.Row is not null).GroupBy(s => s.Row!.MapId).ToList();
        analysis.DuplicateRows.AddRange(used.Where(g => g.Count() > 1).Select(g => g.Key));
        match.Unmatched.Clear();
        match.Unmatched.AddRange(rows.Where(r => SectionMatcher.ExpectsSection(wb, r)).Where(r => used.All(g => g.Key != r.MapId)));

        foreach (var sm in match.Sections.Where(s => !s.Excluded && s.SchemaTitle is not null))
            if (FieldBinder.Bind(wb, sm.Section, sm.SchemaTitle!, sm.ItemSchemas) is { } b) analysis.Bindings[sm.Section.Index] = b;
        return analysis;
    }

    /// <summary>Region of the page schema whose region schema allows this Component Template; Main when none says so.</summary>
    public static string RegionFor(IaWorkbook wb, IaPage page, string templateTitle)
    {
        var nested = wb.Regions.Where(r => r.RowType == IaRegionRowType.NestedRegion && IaWorkbook.Same(r.RegionSchemaTitle, page.PageSchema)).ToList();
        foreach (var n in nested)
            if (wb.Regions.Any(c => c.RowType == IaRegionRowType.Constraint && IaWorkbook.Same(c.RegionSchemaTitle, n.NestedRegionSchema)
                                    && c.AllowedComponentTemplates.Any(t => IaWorkbook.Same(t, templateTitle))))
                return n.NestedRegionName ?? "Main";
        return nested.FirstOrDefault(n => IaWorkbook.Same(n.NestedRegionName, "Main"))?.NestedRegionName ?? nested.FirstOrDefault()?.NestedRegionName ?? "Main";
    }
}
