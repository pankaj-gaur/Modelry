using ClosedXML.Excel;
using Modelry.Core.Model;

namespace Modelry.Core.Excel;

/// <summary>Reads an IA workbook. Unknown sheets/columns are ignored. Structural problems are returned as issues.</summary>
public static class IaExcelReader
{
    /// <summary>Reads the full workbook (Schemas sheet required).</summary>
    public static (IaWorkbook Workbook, List<Issue> Issues) Read(Stream stream) => Read(stream, schemasRequired: true);

    /// <summary>Reads the workbook; templates-only workbooks may omit the Schemas sheet.</summary>
    public static (IaWorkbook Workbook, List<Issue> Issues) Read(Stream stream, bool schemasRequired)
    {
        var wb = new IaWorkbook();
        var issues = new List<Issue>();
        using var xl = Open(stream);

        foreach (var row in Rows(xl, IaFormat.SchemasSheet, IaFormat.Schemas.All, issues, required: schemasRequired))
        {
            var title = row[IaFormat.Schemas.Title];
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (!IaVocabulary.TryParsePurpose(row[IaFormat.Schemas.Purpose], out var purpose))
            {
                issues.Add(new Issue(IssueLevel.Error, IaFormat.SchemasSheet, row.Number, title, $"Unknown Schema Purpose '{row[IaFormat.Schemas.Purpose]}'."));
                continue;
            }
            wb.Schemas.Add(new IaSchema
            {
                Title = title.Trim(), Purpose = purpose,
                RelativePath = NormalizePath(row[IaFormat.Schemas.RelativePath]),
                RootElementName = NullIfEmpty(row[IaFormat.Schemas.RootElement]),
                NamespaceUri = NullIfEmpty(row[IaFormat.Schemas.Namespace]),
                Description = NullIfEmpty(row[IaFormat.Schemas.Description]),
                AllowedMultimediaTypes = IaVocabulary.SplitList(row[IaFormat.Schemas.MultimediaTypes]).Select(x => x.TrimStart('.').ToLowerInvariant()).ToList(),
                Notes = NullIfEmpty(row[IaFormat.Schemas.Notes]),
                SourceId = NullIfEmpty(row[IaFormat.Schemas.SourceId]),
                Row = row.Number
            });
        }

        foreach (var row in Rows(xl, IaFormat.FieldsSheet, IaFormat.Fields.All, issues, required: false))
        {
            var schema = row[IaFormat.Fields.SchemaTitle];
            var xml = row[IaFormat.Fields.XmlName];
            if (string.IsNullOrWhiteSpace(schema) && string.IsNullOrWhiteSpace(xml)) continue;
            var item = $"{schema}.{xml}";
            if (!IaVocabulary.TryParseFieldType(row[IaFormat.Fields.Type], out var type))
            {
                issues.Add(new Issue(IssueLevel.Error, IaFormat.FieldsSheet, row.Number, item, $"Unknown Field Type '{row[IaFormat.Fields.Type]}'."));
                continue;
            }
            var sectionText = row[IaFormat.Fields.Section];
            var section = sectionText.Trim().Equals("Metadata", StringComparison.OrdinalIgnoreCase) ? IaFieldSection.Metadata : IaFieldSection.Content;
            var mandatory = IaVocabulary.ParseYesNo(row[IaFormat.Fields.Mandatory]) ?? false;
            var min = int.TryParse(row[IaFormat.Fields.MinOccurs], out var m) ? m : (mandatory ? 1 : 0);
            var max = IaVocabulary.ParseMaxOccurs(row[IaFormat.Fields.MaxOccurs]) ?? 1;
            var listType = NullIfEmpty(row[IaFormat.Fields.ListType]);
            var rawType = row[IaFormat.Fields.Type].Trim();
            if (rawType.Equals("Text (Checkbox)", StringComparison.OrdinalIgnoreCase) && listType is null) listType = "Checkbox";
            if (rawType.Equals("Text (List)", StringComparison.OrdinalIgnoreCase) && listType is null) listType = "Select";
            wb.Fields.Add(new IaField
            {
                SchemaTitle = schema.Trim(), Section = section,
                Order = int.TryParse(row[IaFormat.Fields.Order], out var o) ? o : 0,
                XmlName = xml.Trim(), Label = row[IaFormat.Fields.Label].Trim(), Type = type,
                Mandatory = mandatory, MinOccurs = min, MaxOccurs = max,
                EmbeddedSchema = NullIfEmpty(row[IaFormat.Fields.EmbeddedSchema]),
                AllowedTargetSchemas = IaVocabulary.SplitList(row[IaFormat.Fields.AllowedTargets])
                    .Where(t => t != "(any)").ToList(),
                AllowMultimediaLinks = IaVocabulary.ParseYesNo(row[IaFormat.Fields.AllowMultimedia]) ?? false,
                Category = NullIfEmpty(row[IaFormat.Fields.Category]),
                ListType = listType,
                ListValues = IaVocabulary.SplitList(row[IaFormat.Fields.ListValues]),
                DefaultValue = NullIfEmpty(row[IaFormat.Fields.DefaultValue]),
                Height = int.TryParse(row[IaFormat.Fields.Height], out var h) ? h : null,
                FormatArea = NullIfEmpty(row[IaFormat.Fields.FormatArea]),
                MaxLength = int.TryParse(row[IaFormat.Fields.MaxLength], out var ml) ? ml : null,
                HelpText = NullIfEmpty(row[IaFormat.Fields.HelpText]),
                Row = row.Number
            });
        }

        foreach (var row in Rows(xl, IaFormat.RegionsSheet, IaFormat.Regions.All, issues, required: false))
        {
            var schema = row[IaFormat.Regions.RegionSchema];
            if (string.IsNullOrWhiteSpace(schema)) continue;
            var rt = row[IaFormat.Regions.RowType].Replace(" ", "");
            if (!Enum.TryParse<IaRegionRowType>(rt, true, out var rowType))
            {
                issues.Add(new Issue(IssueLevel.Error, IaFormat.RegionsSheet, row.Number, schema, $"Row Type must be 'Constraint' or 'Nested Region' (found '{row[IaFormat.Regions.RowType]}')."));
                continue;
            }
            wb.Regions.Add(new IaRegionRow
            {
                RegionSchemaTitle = schema.Trim(), RowType = rowType,
                NestedRegionName = NullIfEmpty(row[IaFormat.Regions.NestedName]),
                NestedRegionSchema = NullIfEmpty(row[IaFormat.Regions.NestedSchema]),
                Mandatory = IaVocabulary.ParseYesNo(row[IaFormat.Regions.Mandatory]) ?? false,
                MinOccurs = int.TryParse(row[IaFormat.Regions.MinOccurs], out var mi) ? mi : null,
                MaxOccurs = IaVocabulary.ParseMaxOccurs(row[IaFormat.Regions.MaxOccurs]),
                AllowedComponentSchemas = IaVocabulary.SplitList(row[IaFormat.Regions.AllowedSchemas]),
                AllowedComponentTemplates = IaVocabulary.SplitList(row[IaFormat.Regions.AllowedTemplates]),
                Notes = NullIfEmpty(row[IaFormat.Regions.Notes]),
                Row = row.Number
            });
        }

        foreach (var row in Rows(xl, IaFormat.CategoriesSheet, IaFormat.Categories.All, issues, required: false))
        {
            var title = row[IaFormat.Categories.Title];
            if (string.IsNullOrWhiteSpace(title)) continue;
            wb.Categories.Add(new IaCategory
            {
                Title = title.Trim(),
                XmlName = string.IsNullOrWhiteSpace(row[IaFormat.Categories.XmlName]) ? new string(title.Where(char.IsLetterOrDigit).ToArray()) : row[IaFormat.Categories.XmlName].Trim(),
                Description = NullIfEmpty(row[IaFormat.Categories.Description]),
                Publishable = IaVocabulary.ParseYesNo(row[IaFormat.Categories.Publishable]) ?? true,
                UseForIdentification = IaVocabulary.ParseYesNo(row[IaFormat.Categories.UseForIdentification]) ?? false,
                KeywordMetadataSchema = NullIfEmpty(row[IaFormat.Categories.KeywordMetadataSchema]),
                SourceId = NullIfEmpty(row[IaFormat.Categories.SourceId]),
                Row = row.Number
            });
        }

        wb.SheetNames.AddRange(xl.Worksheets.Select(w => w.Name));
        if (xl.Worksheets.Contains(IaFormat.TemplatesSheet)) wb.TemplatesSource = IaFormat.TemplatesSheet;
        foreach (var row in Rows(xl, IaFormat.TemplatesSheet, IaFormat.Templates.All, issues, required: false))
        {
            var title = row[IaFormat.Templates.Title];
            if (string.IsNullOrWhiteSpace(title)) continue;
            // Reference-only rows (e.g. "(reference – not created)") document templates the utility must not create.
            // Region view rows are kept for model generation (view registration).
            if (row[IaFormat.Templates.Type].TrimStart().StartsWith("("))
            {
                var view = NullIfEmpty(row[IaFormat.Templates.View]);
                if (row[IaFormat.Templates.Type].Contains("region", StringComparison.OrdinalIgnoreCase) && view is not null && !view.StartsWith("("))
                    wb.RegionViews.Add(new IaRegionView
                    {
                        Title = title.Trim(), View = view, RegionSchema = NullIfEmpty(row[IaFormat.Templates.PageSchema]),
                        ViewModelType = NullIfEmpty(row[IaFormat.Templates.ViewModelType]), Row = row.Number
                    });
                continue;
            }
            var typeText = row[IaFormat.Templates.Type].Replace(" ", "").ToLowerInvariant();
            IaTemplateKind kind;
            if (typeText is "componenttemplate" or "ct" or "component") kind = IaTemplateKind.ComponentTemplate;
            else if (typeText is "pagetemplate" or "pt" or "page") kind = IaTemplateKind.PageTemplate;
            else
            {
                issues.Add(new Issue(IssueLevel.Error, IaFormat.TemplatesSheet, row.Number, title, $"Template Type must be 'Component Template' or 'Page Template' (found '{row[IaFormat.Templates.Type]}')."));
                continue;
            }
            var includes = IaVocabulary.SplitList(row[IaFormat.Templates.Includes]);
            var none = includes.Count == 1 && includes[0].Equals("(none)", StringComparison.OrdinalIgnoreCase);
            wb.Templates.Add(new IaTemplate
            {
                Kind = kind, Title = title.Trim(),
                RelativePath = NormalizePath(row[IaFormat.Templates.RelativePath]),
                View = NullIfEmpty(row[IaFormat.Templates.View]),
                Controller = NullIfEmpty(row[IaFormat.Templates.Controller]),
                Action = NullIfEmpty(row[IaFormat.Templates.Action]),
                RouteValues = NullIfEmpty(row[IaFormat.Templates.RouteValues]),
                HtmlClasses = NullIfEmpty(row[IaFormat.Templates.HtmlClasses]),
                ViewModelType = NullIfEmpty(row[IaFormat.Templates.ViewModelType]),
                LinkedSchemas = IaVocabulary.SplitList(row[IaFormat.Templates.LinkedSchemas]),
                PageSchema = NullIfEmpty(row[IaFormat.Templates.PageSchema]),
                Dynamic = IaVocabulary.ParseYesNo(row[IaFormat.Templates.Dynamic]),
                Priority = NullIfEmpty(row[IaFormat.Templates.Priority]),
                BaseTemplate = NullIfEmpty(row[IaFormat.Templates.BaseTemplate]),
                Includes = none ? new List<string>() : includes,
                NoIncludes = none,
                Description = NullIfEmpty(row[IaFormat.Templates.Description]),
                SourceId = NullIfEmpty(row[IaFormat.Templates.SourceId]),
                Row = row.Number
            });
        }

        if (wb.TemplatesSource is null) ReadLegacyTemplates(xl, wb, issues);

        foreach (var row in Rows(xl, IaFormat.KeywordsSheet, IaFormat.Keywords.All, issues, required: false))
        {
            var cat = row[IaFormat.Keywords.Category];
            var title = row[IaFormat.Keywords.Title];
            if (string.IsNullOrWhiteSpace(cat) || string.IsNullOrWhiteSpace(title)) continue;
            wb.Keywords.Add(new IaKeyword
            {
                CategoryTitle = cat.Trim(), Title = title.Trim(),
                Key = NullIfEmpty(row[IaFormat.Keywords.Key]),
                ParentKeyword = NullIfEmpty(row[IaFormat.Keywords.Parent]),
                Description = NullIfEmpty(row[IaFormat.Keywords.Description]),
                IsAbstract = IaVocabulary.ParseYesNo(row[IaFormat.Keywords.IsAbstract]) ?? false,
                Row = row.Number
            });
        }
        return (wb, issues);
    }

    /// <summary>Opens the workbook with a clear message for files that are not plain .xlsx packages.</summary>
    private static XLWorkbook Open(Stream stream)
    {
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }
        var header = new byte[8];
        var read = stream.Read(header, 0, header.Length);
        stream.Position = 0;
        var isZip = read >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;            // "PK.."
        var isOle = read >= 8 && header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });        // OLE2 container
        if (isOle)
            throw new IaWorkbookFormatException(
                "The file is an encrypted or protected Office document – typically a sensitivity label / Information Rights Management (IRM) " +
                "applied when the file was saved, a password, or an old .xls file. Open it in Excel, remove the label or protection " +
                "(File → Info, or Sensitivity → a non-encrypting label such as 'General' / 'Internal'), save as Excel Workbook (.xlsx), and upload that copy.");
        if (!isZip)
            throw new IaWorkbookFormatException("The file is not a valid .xlsx workbook (it may be truncated or another file type renamed to .xlsx).");
        try
        {
            return new XLWorkbook(stream);
        }
        catch (Exception ex)
        {
            throw new IaWorkbookFormatException(
                $"The workbook could not be read ({ex.Message}). Open it in Excel, choose File → Save As → Excel Workbook (.xlsx), and upload the new copy.", ex);
        }
    }

    /// <summary>Legacy reference sheets from earlier IA versions, read when there is no 'Templates' sheet.</summary>
    private static readonly string[] LegacyTemplateSheets = { "DXA Templates (Ref)", "Templates (Ref)" };

    /// <summary>
    /// Earlier IA workbooks documented templates in a reference sheet instead of the 'Templates' sheet.
    /// Component and Page Template rows are read from it so they can still be imported; region views,
    /// DXA built-ins and other reference rows are ignored.
    /// </summary>
    private static void ReadLegacyTemplates(XLWorkbook xl, IaWorkbook wb, List<Issue> issues)
    {
        var sheet = LegacyTemplateSheets.FirstOrDefault(s => xl.Worksheets.Contains(s));
        if (sheet is null) return;
        string[] cols = { "Template Type", "Template Title", "Linked Schema(s)", "Schema", "DXA View (CT metadata)", "DXA View", "DXA View Name",
                          "Suggested Folder", "Publishing", "Description" };
        string Get(SheetRow r, params string[] names) => names.Select(n => r[n]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        var count = 0;
        foreach (var row in Rows(xl, sheet, cols, issues, required: false))
        {
            var type = Get(row, "Template Type").Trim();
            var title = Get(row, "Template Title").Trim();
            if (title.Length == 0) continue;
            var ct = type.Equals("Component Template", StringComparison.OrdinalIgnoreCase);
            var pt = type.Equals("Page Template", StringComparison.OrdinalIgnoreCase);
            if (!ct && !pt) continue;   // region views, DXA built-ins, notes
            var linked = Get(row, "Linked Schema(s)", "Schema");
            var folder = Get(row, "Suggested Folder");
            if (folder.StartsWith("(") || folder.Length == 0) folder = ct ? "Templates/Component" : "Templates/Page";
            wb.Templates.Add(new IaTemplate
            {
                Kind = ct ? IaTemplateKind.ComponentTemplate : IaTemplateKind.PageTemplate,
                Title = title, RelativePath = NormalizePath(folder),
                View = NullIfEmpty(Get(row, "DXA View (CT metadata)", "DXA View", "DXA View Name")),
                LinkedSchemas = ct ? IaVocabulary.SplitList(linked) : new List<string>(),
                PageSchema = pt ? NullIfEmpty(IaVocabulary.SplitList(linked).FirstOrDefault()) : null,
                Dynamic = ct ? Get(row, "Publishing").Contains("Dynamic", StringComparison.OrdinalIgnoreCase) : null,
                NoIncludes = pt && title.Contains("Include", StringComparison.OrdinalIgnoreCase),
                Description = NullIfEmpty(Get(row, "Description")),
                Row = row.Number
            });
            count++;
        }
        if (count > 0)
        {
            wb.TemplatesSource = sheet;
            issues.Add(new Issue(IssueLevel.Warning, sheet, 0, "Templates",
                $"No 'Templates' sheet – {count} templates were read from the older '{sheet}' sheet. Check folders, dynamic flags and includes, or move to the current IA format."));
        }
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ".";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(p => p != ".").ToArray();
        return parts.Length == 0 ? "." : string.Join("/", parts);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class SheetRow
    {
        private readonly Dictionary<string, string> _values;
        public int Number { get; }
        public SheetRow(int number, Dictionary<string, string> values) { Number = number; _values = values; }
        public string this[string header] => _values.TryGetValue(IaFormat.Normalize(header), out var v) ? v : "";
    }

    private static IEnumerable<SheetRow> Rows(XLWorkbook xl, string sheet, string[] expected, List<Issue> issues, bool required)
    {
        if (!xl.Worksheets.TryGetWorksheet(sheet, out var ws))
        {
            if (required) issues.Add(new Issue(IssueLevel.Error, sheet, 0, sheet, $"Sheet '{sheet}' is missing."));
            yield break;
        }
        var headers = new Dictionary<int, string>();
        foreach (var cell in ws.Row(1).CellsUsed())
            headers[cell.Address.ColumnNumber] = IaFormat.Normalize(cell.GetFormattedString());
        foreach (var h in expected.Where(h => h.EndsWith('*')))
            if (!headers.ContainsValue(IaFormat.Normalize(h)))
                issues.Add(new Issue(IssueLevel.Error, sheet, 1, h, $"Mandatory column '{h}' is missing."));
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= last; r++)
        {
            var values = new Dictionary<string, string>();
            foreach (var (col, name) in headers)
                values[name] = ws.Cell(r, col).GetFormattedString().Trim();
            if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
            yield return new SheetRow(r, values);
        }
    }
}

/// <summary>The uploaded file is not a readable IA workbook (encrypted, wrong type, damaged).</summary>
public sealed class IaWorkbookFormatException : Exception
{
    public IaWorkbookFormatException(string message, Exception? inner = null) : base(message, inner) { }
}
