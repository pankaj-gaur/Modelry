using Modelry.Core.Excel;
using Modelry.Core.Gateway;
using Modelry.Core.Import;

namespace Modelry.Web.Services;

/// <summary>Seeds demo mode by importing the sample IA template into the demo schema publication.</summary>
public static class DemoSeeder
{
    public static async Task SeedAsync(InMemoryTridionGateway gw, string samplePath, ILogger logger)
    {
        if (!File.Exists(samplePath)) { logger.LogWarning("Demo seed skipped – {Path} not found", samplePath); return; }
        var pub = (await gw.GetPublicationsAsync()).First(p => p.Title.StartsWith("010"));
        var root = await gw.GetPublicationRootFolderAsync(pub.Id);
        var schemas = (await gw.GetSubFoldersAsync(root.Id)).First(f => f.Title == "Schemas");
        await using var fs = File.OpenRead(samplePath);
        var (wb, issues) = IaExcelReader.Read(fs);
        var ctx = await ImportContext.LoadAsync(gw, schemas.Id, wb);
        var plan = ImportPlanner.Build(wb, issues, ctx);
        var result = await new ImportExecutor(gw, "Demo seed").ExecuteAsync(wb, plan, ctx);
        logger.LogInformation("Demo seed: {Created} schemas created, plan errors: {Errors}", result.Created,
            plan.Issues.Count(i => i.Level == Modelry.Core.Model.IssueLevel.Error));
    }
}
