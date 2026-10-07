using Modelry.Core.Excel;
using Modelry.Core.Gateway;
using Modelry.Core.Model;

namespace Modelry.Core.Export;

public sealed class ExportOptions
{
    public bool Recursive { get; init; } = true;
    /// <summary>Also export Template Parameters and Bundle schemas.</summary>
    public bool IncludeParameterAndBundleSchemas { get; init; }
}

public sealed record ExportResult(byte[] Content, string FileName, int SchemaCount, int FieldCount, List<string> Warnings);

/// <summary>Reads all schemas under a folder and produces an IA workbook.</summary>
public sealed class ExportService
{
    private static readonly HashSet<string> DefaultPurposes = new(StringComparer.OrdinalIgnoreCase)
        { "Component", "Multimedia", "Embedded", "Metadata", "Region" };
    private static readonly HashSet<string> ExtraPurposes = new(StringComparer.OrdinalIgnoreCase)
        { "TemplateParameters", "Bundle" };

    private readonly ITridionGateway _gw;
    public ExportService(ITridionGateway gw) => _gw = gw;

    public async Task<ExportResult> ExportAsync(string folderId, ExportOptions options, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var folder = await _gw.GetFolderAsync(folderId);
        var folders = await FolderTraversal.GetFoldersAsync(_gw, folderId, folder.Title, options.Recursive);
        var schemas = await FolderTraversal.GetSchemasAsync(_gw, folders);

        var wb = new IaWorkbook();
        var categoryIds = new HashSet<string>();
        foreach (var s in schemas.OrderBy(x => x.RelativePath).ThenBy(x => x.Title))
        {
            ct.ThrowIfCancellationRequested();
            var include = DefaultPurposes.Contains(s.Purpose) || (options.IncludeParameterAndBundleSchemas && ExtraPurposes.Contains(s.Purpose));
            if (!include) continue;
            try
            {
                var d = await _gw.ReadSchemaAsync(s.Id);
                d.Schema.RelativePath = s.RelativePath;
                wb.Schemas.Add(d.Schema);
                wb.Fields.AddRange(d.Fields);
                wb.Regions.AddRange(d.RegionRows);
                categoryIds.UnionWith(d.CategoryIds);
            }
            catch (Exception ex)
            {
                warnings.Add($"{s.Title} ({s.Id}): {ex.Message}");
            }
        }
        foreach (var id in categoryIds)
        {
            try { wb.Categories.Add(await _gw.ReadCategoryAsync(id)); }
            catch (Exception ex) { warnings.Add($"Category {id}: {ex.Message}"); }
        }
        wb.Categories.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

        var source = $"{folder.PublicationTitle} / {folder.Title} ({folder.Id}){(options.Recursive ? " incl. subfolders" : "")}";
        var bytes = IaExcelWriter.Write(wb, source);
        var safe = string.Concat(folder.Title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Replace(' ', '_');
        return new ExportResult(bytes, $"IA_{safe}_{DateTime.UtcNow:yyyyMMdd_HHmm}.xlsx", wb.Schemas.Count, wb.Fields.Count, warnings);
    }
}
