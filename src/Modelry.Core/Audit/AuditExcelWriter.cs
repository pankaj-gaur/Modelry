using ClosedXML.Excel;

namespace Modelry.Core.Audit;

/// <summary>Writes an audit to Excel: a Summary sheet, then one sheet per building block with every row and column.</summary>
public static class AuditExcelWriter
{
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#1F3864");
    private static readonly XLColor NaFill = XLColor.FromHtml("#EEECE6");

    public static byte[] Write(AuditSnapshot s, AuditAiSummary? ai = null)
    {
        using var xl = new XLWorkbook();
        if (ai is not null) Executive(xl, ai, "Executive summary", translated: false);
        if (ai is { HasTranslation: true }) Executive(xl, ai, $"Summary ({ai.Language})", translated: true);
        Summary(xl, s);
        Findings(xl, s);
        foreach (var kind in Enum.GetValues<AuditKind>()) Detail(xl, s, kind, AuditRules.ByItem(s, kind));
        using var ms = new MemoryStream();
        xl.SaveAs(ms);
        return ms.ToArray();
    }

    public static string FileName(AuditSnapshot s)
    {
        var safe = new string(s.PublicationTitle.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        return $"Modelry_Audit_{(safe.Length > 0 ? safe : "publication")}_{s.FinishedUtc:yyyyMMdd_HHmm}.xlsx";
    }

    private static void Summary(XLWorkbook xl, AuditSnapshot s)
    {
        var ws = xl.AddWorksheet("Summary");
        ws.Cell(1, 1).Value = "Modelry CMS audit";
        ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 16;
        var info = new (string, string)[]
        {
            (AuditCms.IsAem(s.Cms) ? "Site" : "Publication", s.PublicationTitle), (AuditCms.IsAem(s.Cms) ? "Site path" : "Publication ID", s.PublicationId),
            ("CMS version", $"{s.Capabilities.Display}{(s.Capabilities.Raw.Length > 0 && !s.Capabilities.IsDemo ? $" (API {s.Capabilities.Raw})" : "")}"),
            ("CMS host", s.CmsHost), ("Run by", s.RunBy),
            ("Started (UTC)", s.StartedUtc.ToString("yyyy-MM-dd HH:mm")),
            ("Duration", $"{(s.FinishedUtc - s.StartedUtc).TotalSeconds:0} seconds"),
            ("Scan", s.Deep ? "Deep (where used read for every item)" : "Standard"),
            ("Health score", s.Health is { } h ? $"{h.Overall} / 100 (grade {h.Grade})" : ""),
        };
        if (s.Health is { } health)
            info = info.Concat(health.Categories.Select(kv => ($"  {AuditRules.CategoryTitle(kv.Key)}",
                AuditRules.NotScoredInThisScan(s, kv.Key) ? "not scored – usage is scored only in a deep scan"
                : kv.Value is { } v ? $"{v} / 100" : "not checked"))).ToArray();
        var r = 3;
        foreach (var (label, value) in info)
        {
            ws.Cell(r, 1).Value = label; ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value;
            r++;
        }

        r++;
        var headers = new[] { "Building block", "Total", "Published", "Not published", "Publish unknown", "Local", "Localized", "Shared" };
        Header(ws, r, headers);
        var first = ++r;
        foreach (var kind in Enum.GetValues<AuditKind>())
        {
            var sum = s.Summarize(kind);
            ws.Cell(r, 1).Value = AuditCatalog.Title(kind, s.Cms);
            ws.Cell(r, 2).Value = sum.Total;
            if (AuditCatalog.IsPublishable(kind))
            {
                ws.Cell(r, 3).Value = sum.Published; ws.Cell(r, 4).Value = sum.Unpublished; ws.Cell(r, 5).Value = sum.PublishUnknown;
            }
            ws.Cell(r, 6).Value = sum.Local; ws.Cell(r, 7).Value = sum.Localized; ws.Cell(r, 8).Value = sum.Shared;
            r++;
        }
        ws.Range(first - 1, 1, r - 1, headers.Length).SetAutoFilter();

        if (s.Notes.Count > 0)
        {
            r++;
            ws.Cell(r++, 1).Value = "Notes";
            ws.Cell(r - 1, 1).Style.Font.Bold = true;
            foreach (var n in s.Notes) { ws.Cell(r, 1).Value = "•"; ws.Cell(r++, 2).Value = n; }
        }
        if (s.Unavailable.Count > 0)
        {
            r++;
            ws.Cell(r++, 1).Value = "Checks not available on this CMS";
            ws.Cell(r - 1, 1).Style.Font.Bold = true;
            foreach (var (column, reason) in s.Unavailable) { ws.Cell(r, 1).Value = column; ws.Cell(r++, 2).Value = reason; }
        }
        ws.Column(1).Width = 26;
        ws.Column(2).Width = 60;
        ws.Columns(3, headers.Length).Width = 14;
    }

    /// <summary>The AI executive summary as the first sheet (English, or the translation).</summary>
    private static void Executive(XLWorkbook xl, AuditAiSummary ai, string name, bool translated)
    {
        var ws = xl.AddWorksheet(name.Length > 31 ? name[..31] : name);
        ws.Cell(1, 1).Value = translated ? $"Executive summary – {ai.LanguageName}" : "Executive summary";
        ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 16;
        var r = 3;
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            ws.Cell(r, 1).Value = label; ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value; ws.Cell(r, 2).Style.Alignment.WrapText = true;
            r++;
        }
        var e = translated ? ai.Translated : ai.English;
        if (e is not null)
        {
            Row("Headline", e.Headline);
            Row("Status", e.Status);
            Row("Summary", e.Summary);
            foreach (var a in e.Actions) Row(a.Priority.Length > 0 ? a.Priority : "Action", a.Action);
            Row("Decision", e.Decision);
        }
        else Row("Summary", translated ? ai.TranslatedText : ai.EnglishText);
        r++;
        Row("Generated", $"{ai.CreatedUtc:yyyy-MM-dd HH:mm} UTC by {ai.CreatedBy} (AI-generated – check before sharing)");
        ws.Column(1).Width = 14;
        ws.Column(2).Width = 100;
    }

    private static void Findings(XLWorkbook xl, AuditSnapshot s)
    {
        var ws = xl.AddWorksheet("Findings");
        var headers = new[] { "Rule", "Check", "Category", "Severity", "Checked", "Items checked", "Items flagged", "In score", "Item", "Building block", "ID", "Path", "Detail" };
        Header(ws, 1, headers);
        var row = 2;
        foreach (var rule in s.Rules)
        {
            var items = s.Findings.Where(f => f.RuleId == rule.Id).ToList();
            void Rule(int r)
            {
                ws.Cell(r, 1).Value = rule.Id; ws.Cell(r, 2).Value = rule.Title; ws.Cell(r, 3).Value = AuditRules.CategoryTitle(rule.Category);
                ws.Cell(r, 4).Value = rule.Severity.ToString(); ws.Cell(r, 5).Value = rule.Evaluated ? "Yes" : "No – " + rule.NotEvaluatedReason;
                if (rule.Evaluated)
                {
                    ws.Cell(r, 6).Value = rule.Population; ws.Cell(r, 7).Value = rule.Affected;
                    ws.Cell(r, 8).Value = rule.Scored ? "Yes" : "No – " + rule.NotScoredReason;
                }
            }
            if (items.Count == 0) { Rule(row++); continue; }
            foreach (var f in items)
            {
                Rule(row);
                ws.Cell(row, 9).Value = f.Title; ws.Cell(row, 10).Value = AuditCatalog.Singular(f.Kind, s.Cms); ws.Cell(row, 11).Value = f.ItemId;
                ws.Cell(row, 12).Value = f.Path; ws.Cell(row, 13).Value = f.Message;
                row++;
            }
        }
        ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents(8, 60);
    }

    private static void Detail(XLWorkbook xl, AuditSnapshot s, AuditKind kind, Dictionary<string, List<string>> byItem)
    {
        var ws = xl.AddWorksheet(AuditCatalog.Title(kind, s.Cms));
        var columns = new List<(string Header, Func<AuditRow, object?> Value, string? Key)>
        {
            ("Title", r => r.Title, "title"),
            ("ID", r => r.Id, "id"),
        };
        if (AuditCatalog.PathLabel(kind, s.Cms) is { } pathLabel) columns.Add((pathLabel, r => r.Path.Length == 0 ? "(root)" : r.Path, "path"));
        foreach (var c in AuditCatalog.Columns(kind, s.Cms))
            columns.Add((c.Label, r => c.Numeric && int.TryParse(r.Value(c.Key), out var n) ? n : r.Value(c.Key), c.Key));
        if (AuditCatalog.IsPublishable(kind))
        {
            columns.Add(("Publish status", r => AuditCatalog.Publish(r.Publish), "publish"));
            foreach (var target in s.PublishTargets)
                columns.Add(($"Published to {target}", r => r.PublishedTo.FirstOrDefault(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase))?.PublishedAt
                    ?? (object?)(r.PublishedTo.Any(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) ? "Yes" : null), "publish"));
        }
        if (AuditCatalog.IsPublishable(kind)) columns.Add(("Last published", r => AuditQuery.LastPublished(r), "publish"));
        columns.Add(("Findings", r => byItem.TryGetValue(r.Id, out var rules) ? string.Join(", ", rules) : null, null));
        columns.Add((AuditCatalog.OriginLabel(s.Cms), r => AuditCatalog.Origin(r.Origin, s.Cms), "origin"));
        columns.Add((AuditCms.IsAem(s.Cms) ? "Blueprint" : "From publication", r => r.FromPublication, "origin"));
        columns.Add(("Created", r => r.Created, "created"));
        columns.Add(("Created by", r => r.CreatedBy, "createdBy"));
        columns.Add(("Last modified", r => r.Modified, "modified"));
        columns.Add(("Modified by", r => r.ModifiedBy, "modifiedBy"));

        Header(ws, 1, columns.Select(c => c.Header).ToArray());
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Key is { } key && s.UnavailableReason(kind, key) is { } reason)
            {
                var h = ws.Cell(1, i + 1);
                h.Value = columns[i].Header + " (n/a)";
                h.CreateComment().AddText(reason);
            }
        }

        var row = 2;
        foreach (var item in s.Items[kind])
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var cell = ws.Cell(row, i + 1);
                switch (columns[i].Value(item))
                {
                    case null: break;
                    case DateTime d: cell.Value = d; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
                    case int n: cell.Value = n; break;
                    case string str when str.Length > 0: cell.Value = str; break;
                }
                if (columns[i].Key is { } key && s.UnavailableReason(kind, key) is not null) cell.Style.Fill.BackgroundColor = NaFill;
            }
            row++;
        }
        ws.Range(1, 1, Math.Max(row - 1, 1), columns.Count).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, columns.Count).AdjustToContents(10, 60);
    }

    private static void Header(IXLWorksheet ws, int row, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(row, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true; c.Style.Font.FontColor = XLColor.White; c.Style.Fill.BackgroundColor = HeaderFill;
            c.Style.Alignment.WrapText = true;
        }
    }
}
