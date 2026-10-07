using ClosedXML.Excel;
using Modelry.Core.Model;

namespace Modelry.Core.Excel;

/// <summary>Writes an IA workbook (used by Export and for the import results file).</summary>
public static class IaExcelWriter
{
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#1F3864");

    public static byte[] Write(IaWorkbook wb, string sourceDescription)
    {
        using var xl = new XLWorkbook();
        Readme(xl, sourceDescription);

        var ws = Sheet(xl, IaFormat.SchemasSheet, IaFormat.Schemas.All);
        var r = 2;
        foreach (var s in wb.Schemas)
        {
            Set(ws, r++, s.Title, s.Purpose.ToString(), s.RelativePath, s.RootElementName, s.NamespaceUri, s.Description,
                IaVocabulary.JoinList(s.AllowedMultimediaTypes), s.Notes, s.SourceId, s.BluePrintStatus, s.OwningPublication);
        }
        Finish(ws, IaFormat.Schemas.All.Length, ("B", "\"Component,Multimedia,Embedded,Metadata,Region,TemplateParameters,Bundle\""));

        ws = Sheet(xl, IaFormat.FieldsSheet, IaFormat.Fields.All);
        r = 2;
        foreach (var f in wb.Fields)
        {
            Set(ws, r++, f.SchemaTitle, f.Section.ToString(), f.Order.ToString(), f.XmlName, f.Label,
                IaVocabulary.FieldTypeNames[f.Type], IaVocabulary.YesNo(f.Mandatory), f.MinOccurs.ToString(),
                IaVocabulary.MaxOccursText(f.MaxOccurs), f.EmbeddedSchema, IaVocabulary.JoinList(f.AllowedTargetSchemas),
                f.Type == IaFieldType.ComponentLink ? IaVocabulary.YesNo(f.AllowMultimediaLinks) : null,
                f.Category, f.ListType, IaVocabulary.JoinList(f.ListValues), f.DefaultValue, f.Height?.ToString(),
                f.FormatArea, f.MaxLength?.ToString(), f.HelpText);
        }
        Finish(ws, IaFormat.Fields.All.Length,
            ("B", "\"Content,Metadata\""),
            ("F", "\"" + string.Join(",", IaVocabulary.FieldTypeNames.Values) + "\""),
            ("G", "\"Y,N\""), ("L", "\"Y,N\""), ("N", "\"Select,Radio,Checkbox,Tree\""));

        ws = Sheet(xl, IaFormat.RegionsSheet, IaFormat.Regions.All);
        r = 2;
        foreach (var g in wb.Regions)
        {
            Set(ws, r++, g.RegionSchemaTitle, g.RowType == IaRegionRowType.NestedRegion ? "Nested Region" : "Constraint",
                g.NestedRegionName, g.NestedRegionSchema, g.RowType == IaRegionRowType.NestedRegion ? IaVocabulary.YesNo(g.Mandatory) : null,
                g.MinOccurs?.ToString(), g.MaxOccurs is null ? null : IaVocabulary.MaxOccursText(g.MaxOccurs.Value),
                IaVocabulary.JoinList(g.AllowedComponentSchemas), IaVocabulary.JoinList(g.AllowedComponentTemplates), g.Notes);
        }
        Finish(ws, IaFormat.Regions.All.Length, ("B", "\"Constraint,Nested Region\""), ("E", "\"Y,N\""));

        ws = Sheet(xl, IaFormat.CategoriesSheet, IaFormat.Categories.All);
        r = 2;
        foreach (var c in wb.Categories)
            Set(ws, r++, c.Title, c.XmlName, c.Description, IaVocabulary.YesNo(c.Publishable), IaVocabulary.YesNo(c.UseForIdentification), c.KeywordMetadataSchema, c.SourceId);
        Finish(ws, IaFormat.Categories.All.Length, ("D", "\"Y,N\""), ("E", "\"Y,N\""));

        ws = Sheet(xl, IaFormat.KeywordsSheet, IaFormat.Keywords.All);
        r = 2;
        foreach (var k in wb.Keywords)
            Set(ws, r++, k.CategoryTitle, k.Title, k.Key, k.ParentKeyword, k.Description, IaVocabulary.YesNo(k.IsAbstract));
        Finish(ws, IaFormat.Keywords.All.Length, ("F", "\"Y,N\""));

        if (wb.Templates.Count > 0) WriteTemplatesSheet(xl, wb);
        return Save(xl);
    }

    /// <summary>Workbook containing only README + Templates (template export).</summary>
    public static byte[] WriteTemplates(IaWorkbook wb, string sourceDescription)
    {
        using var xl = new XLWorkbook();
        Readme(xl, sourceDescription);
        WriteTemplatesSheet(xl, wb);
        return Save(xl);
    }

    private static void WriteTemplatesSheet(XLWorkbook xl, IaWorkbook wb)
    {
        var ws = Sheet(xl, IaFormat.TemplatesSheet, IaFormat.Templates.All);
        var r = 2;
        foreach (var t in wb.Templates)
        {
            var ct = t.Kind == IaTemplateKind.ComponentTemplate;
            Set(ws, r++, ct ? "Component Template" : "Page Template", t.Title, t.RelativePath, t.View,
                t.Controller, t.Action, t.RouteValues, t.HtmlClasses,
                ct ? IaVocabulary.JoinList(t.LinkedSchemas) : null, ct ? null : t.PageSchema,
                ct && t.Dynamic is not null ? IaVocabulary.YesNo(t.Dynamic.Value) : null, ct ? t.Priority : null, t.BaseTemplate,
                ct ? null : t.NoIncludes ? "(none)" : IaVocabulary.JoinList(t.Includes), t.Description, t.SourceId);
        }
        Finish(ws, IaFormat.Templates.All.Length, ("A", "\"Component Template,Page Template\""), ("K", "\"Y,N\""), ("L", "\"High,Medium,Low,Never Link\""));
    }

    /// <summary>Workbook with the import log.</summary>
    public static byte[] WriteResults(IEnumerable<Import.LogEntry> entries)
    {
        using var xl = new XLWorkbook();
        var headers = new[] { "Time (UTC)", "Level", "Step", "Item", "Message", "TCM URI" };
        var ws = Sheet(xl, IaFormat.ResultsSheet, headers);
        var r = 2;
        foreach (var e in entries)
            Set(ws, r++, e.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss"), e.Level.ToString(), e.Step, e.Item, e.Message, e.TcmUri);
        Finish(ws, headers.Length);
        return Save(xl);
    }

    private static void Readme(XLWorkbook xl, string source)
    {
        var ws = xl.AddWorksheet(IaFormat.ReadmeSheet);
        ws.Cell(1, 1).Value = "Tridion Information Architecture workbook";
        ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = "Format version"; ws.Cell(2, 2).Value = IaFormat.FormatVersion;
        ws.Cell(3, 1).Value = "Source"; ws.Cell(3, 2).Value = source;
        ws.Cell(4, 1).Value = "Generated (UTC)"; ws.Cell(4, 2).Value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
        ws.Cell(6, 1).Value = "Relative Folder Path is relative to the folder selected in the tool ('.' = that folder). See the sample template for full column rules.";
        ws.Column(1).Width = 22; ws.Column(2).Width = 80;
    }

    private static IXLWorksheet Sheet(XLWorkbook xl, string name, string[] headers)
    {
        var ws = xl.AddWorksheet(name);
        for (var i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true; c.Style.Font.FontColor = XLColor.White; c.Style.Fill.BackgroundColor = HeaderFill;
            c.Style.Alignment.WrapText = true;
        }
        ws.SheetView.FreezeRows(1);
        return ws;
    }

    private static void Set(IXLWorksheet ws, int row, params string?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            if (!string.IsNullOrEmpty(values[i])) ws.Cell(row, i + 1).Value = values[i];
    }

    private static void Finish(IXLWorksheet ws, int columns, params (string Column, string List)[] lists)
    {
        ws.Range(1, 1, Math.Max(ws.LastRowUsed()?.RowNumber() ?? 1, 1), columns).SetAutoFilter();
        ws.Columns(1, columns).AdjustToContents(10, 60);
        foreach (var (col, list) in lists)
            ws.Range($"{col}2:{col}2000").CreateDataValidation().List(list, true);
    }

    private static byte[] Save(XLWorkbook xl)
    {
        using var ms = new MemoryStream();
        xl.SaveAs(ms);
        return ms.ToArray();
    }
}
