using Modelry.Core.Gateway;
using Modelry.Core.Model;

namespace Modelry.Core.Import;

public enum PlanAction { Create, Skip, UseExisting }

public sealed record LogEntry(DateTime TimeUtc, IssueLevel Level, string Step, string Item, string Message, string? TcmUri = null);

/// <summary>Snapshot of the target publication used for planning (read once per dry run / execution).</summary>
public sealed class ImportContext
{
    public required FolderInfo Target { get; init; }
    /// <summary>Path of the target folder relative to the publication root folder ('.' = root).</summary>
    public required string TargetPathFromRoot { get; init; }
    public required string RootFolderId { get; init; }
    public Dictionary<string, string> FolderIdsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SchemaLocation> ExistingSchemas { get; } = new();
    public List<NamedItem> Categories { get; } = new();
    public Dictionary<string, List<NamedItem>> KeywordsByCategoryId { get; } = new();
    public List<MultimediaTypeInfo> MultimediaTypes { get; } = new();
    public List<NamedItem> ComponentTemplates { get; } = new();

    public string FullPath(string relativePath) =>
        relativePath == "." ? TargetPathFromRoot : FolderTraversal.Combine(TargetPathFromRoot, relativePath);

    /// <summary>
    /// Loads only what the plan needs:
    ///  - the target folder and its sub-folders (to decide create vs skip),
    ///  - one publication-wide list of schema titles (to resolve references to existing schemas),
    ///  - type and folder only for existing schemas the workbook actually refers to,
    ///  - categories, keywords of the workbook's categories, multimedia types, Component Templates.
    /// </summary>
    public static async Task<ImportContext> LoadAsync(ITridionGateway gw, string targetFolderId, IaWorkbook wb, IProgress<string>? activity = null)
    {
        activity?.Report("Opening the selected folder");
        var target = await gw.GetFolderAsync(targetFolderId);
        var root = await gw.GetPublicationRootFolderAsync(target.PublicationId);
        var ctx = new ImportContext { Target = target, TargetPathFromRoot = target.PathFromRoot, RootFolderId = root.Id };

        // Ancestors of the target exist by definition; their ids are never needed (folders are only created below the target).
        if (target.PathFromRoot != ".")
        {
            ctx.FolderIdsByPath["."] = root.Id;
            var parts = target.PathFromRoot.Split('/');
            for (var i = 1; i < parts.Length; i++) ctx.FolderIdsByPath[string.Join("/", parts.Take(i))] = "";
        }

        // 1. target subtree: folders + schema titles (no schema reads)
        activity?.Report($"Reading folders below {target.Title}");
        var subtree = await FolderTraversal.GetFoldersAsync(gw, target.Id, target.Title, recursive: true,
            n => activity?.Report($"Reading folders below {target.Title} – {n} folders so far"));
        var inSubtree = new HashSet<string>();
        for (var i = 0; i < subtree.Count; i++)
        {
            var f = subtree[i];
            var path = ctx.FullPath(f.RelativePath);
            ctx.FolderIdsByPath[path] = f.Id;
            foreach (var s in await gw.ListSchemasInFolderAsync(f.Id))
            {
                ctx.ExistingSchemas.Add(new SchemaLocation(s.Id, s.Title, "", f.Id, path));
                inSubtree.Add(s.Id);
            }
            activity?.Report($"Looking for existing schemas in the target folder – folder {i + 1} of {subtree.Count}, {ctx.ExistingSchemas.Count} schemas found");
        }

        // 2. every schema in the publication, one call (titles only) – for references outside the workbook
        activity?.Report("Listing all schemas in the publication");
        foreach (var s in await gw.ListPublicationSchemasAsync(target.PublicationId))
            if (!inSubtree.Contains(s.Id)) ctx.ExistingSchemas.Add(new SchemaLocation(s.Id, s.Title, "", "", "?"));

        // 3. type + folder only for existing schemas the workbook refers to
        var referenced = ReferencedTitles(wb);
        var toRead = ctx.ExistingSchemas.Where(s => referenced.Contains(s.Title)).ToList();
        for (var i = 0; i < toRead.Count; i++)
        {
            activity?.Report($"Reading referenced schemas – {i + 1} of {toRead.Count}: {toRead[i].Title}");
            var info = await gw.ReadSchemaInfoAsync(toRead[i].Id);
            var idx = ctx.ExistingSchemas.IndexOf(toRead[i]);
            ctx.ExistingSchemas[idx] = toRead[i] with
            {
                Purpose = info.Purpose,
                RelativePath = toRead[i].RelativePath == "?" ? info.PathFromRoot : toRead[i].RelativePath
            };
        }

        // 4. taxonomy, multimedia types, templates
        activity?.Report("Reading categories");
        ctx.Categories.AddRange(await gw.GetCategoriesAsync(target.PublicationId));
        var wanted = new HashSet<string>(wb.Categories.Select(c => c.Title).Concat(wb.Keywords.Select(k => k.CategoryTitle)), StringComparer.OrdinalIgnoreCase);
        foreach (var c in ctx.Categories.Where(c => wanted.Contains(c.Title)))
        {
            activity?.Report($"Reading keywords of category '{c.Title}'");
            ctx.KeywordsByCategoryId[c.Id] = (await gw.GetKeywordsAsync(c.Id)).ToList();
        }
        activity?.Report("Reading multimedia types");
        ctx.MultimediaTypes.AddRange(await gw.GetMultimediaTypesAsync());
        if (wb.Regions.Any(r => r.AllowedComponentTemplates.Count > 0))
        {
            activity?.Report("Reading Component Templates");
            ctx.ComponentTemplates.AddRange(await gw.GetComponentTemplatesAsync(target.PublicationId));
        }
        return ctx;
    }

    /// <summary>Titles of every schema the workbook refers to (last path segment of path references).</summary>
    public static HashSet<string> ReferencedTitles(IaWorkbook wb)
    {
        var refs = new List<string?>();
        foreach (var f in wb.Fields) { refs.Add(f.EmbeddedSchema); refs.AddRange(f.AllowedTargetSchemas); }
        foreach (var r in wb.Regions) { refs.Add(r.NestedRegionSchema); refs.AddRange(r.AllowedComponentSchemas); }
        foreach (var c in wb.Categories) refs.Add(c.KeywordMetadataSchema);
        foreach (var t in wb.Templates) { refs.AddRange(t.LinkedSchemas); refs.Add(t.PageSchema); }
        foreach (var r in wb.Regions) refs.Add(r.RegionSchemaTitle);
        return new HashSet<string>(refs.Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Last().Trim()), StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class SchemaPlan
{
    public required IaSchema Schema { get; init; }
    public required string FolderPath { get; init; }   // from publication root
    public PlanAction Action { get; set; }
    public string? ExistingId { get; set; }
    public bool NeedsSecondPass { get; set; }
}

public sealed class CategoryPlan
{
    public required IaCategory Category { get; init; }
    public PlanAction Action { get; set; }
    public string? ExistingId { get; set; }
}

public sealed class KeywordPlan
{
    public required IaKeyword Keyword { get; init; }
    public PlanAction Action { get; set; }
    public string? ExistingId { get; set; }
    public int Depth { get; set; }
}

public sealed class ImportPlan
{
    public List<Issue> Issues { get; } = new();
    public List<string> FoldersToCreate { get; } = new();
    public List<CategoryPlan> Categories { get; } = new();
    public List<KeywordPlan> Keywords { get; } = new();
    public List<SchemaPlan> Schemas { get; } = new();
    /// <summary>Categories, keywords and schemas to create, each after everything it references.</summary>
    public List<CreationStep> CreationOrder { get; } = new();
    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);
}

public sealed class ImportResult
{
    public List<LogEntry> Log { get; } = new();
    public int Created { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
}
