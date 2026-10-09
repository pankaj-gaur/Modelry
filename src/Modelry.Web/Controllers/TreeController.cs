using Microsoft.AspNetCore.Mvc;
using Modelry.Core.Gateway;
using Modelry.Web.Models;
using Modelry.Web.Services;

namespace Modelry.Web.Controllers;

/// <summary>JSON API for the publication / folder tree.</summary>
[ApiController, Route("api/tree")]
public sealed class TreeController : ControllerBase
{
    private readonly GatewayAccessor _gw;
    private readonly TridionSession _session;
    private readonly ILogger<TreeController> _log;
    public TreeController(GatewayAccessor gw, TridionSession session, ILogger<TreeController> log) { _gw = gw; _session = session; _log = log; }

    [HttpGet("publications")]
    public Task<IActionResult> Publications() => Run(async () => (await _gw.Gateway.GetPublicationsAsync()).Select(Dto));

    /// <summary>Children of a publication (its root folder, or root Structure Group with mode=sg) or of a folder / Structure Group.</summary>
    [HttpGet("children")]
    public Task<IActionResult> Children([FromQuery] string id, [FromQuery] string? mode) => Run(async () =>
        mode == "sg"
            ? IsPublication(id)
                ? new[] { Dto(await _gw.Gateway.GetPublicationRootStructureGroupAsync(id)) }.AsEnumerable()
                : (await _gw.Gateway.GetSubStructureGroupsAsync(id)).Select(Dto)
            : IsPublication(id)
                ? new[] { Dto(await _gw.Gateway.GetPublicationRootFolderAsync(id)) }.AsEnumerable()
                : (await _gw.Gateway.GetSubFoldersAsync(id)).Select(Dto));

    [HttpGet("structure-group")]
    public Task<IActionResult> StructureGroup([FromQuery] string id) => Run(async () =>
    {
        var sg = await _gw.Gateway.GetStructureGroupAsync(id);
        return (object)new { sg.Id, sg.Title, sg.PublicationId, sg.PublicationTitle, SchemaCount = 0 };
    });

    [HttpPost("structure-groups"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateStructureGroup([FromBody] CreateFolderRequest req) => Run(async () =>
    {
        if (req.Title.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Structure Group title cannot contain '/' or '\\'.");
        var directory = System.Text.RegularExpressions.Regex.Replace(req.Title.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var node = await _gw.Gateway.CreateStructureGroupAsync(req.ParentId, req.Title, directory.Length == 0 ? "sg" : directory);
        _log.LogInformation("AUDIT {Who} created Structure Group '{Title}' ({Id}) in {Parent}", _session.Who, req.Title, node.Id, req.ParentId);
        return (object)Dto(node);
    });

    [HttpGet("folder")]
    public Task<IActionResult> Folder([FromQuery] string id) => Run(async () =>
    {
        var f = await _gw.Gateway.GetFolderAsync(id);
        var count = await _gw.Gateway.CountSchemasAsync(id);
        return (object)new { f.Id, f.Title, f.PublicationId, f.PublicationTitle, SchemaCount = count };
    });

    /// <summary>Component or Page Templates visible in a publication – for the base template pickers.</summary>
    [HttpGet("templates")]
    public Task<IActionResult> Templates([FromQuery] string publicationId, [FromQuery] string kind) => Run(async () =>
        (await _gw.Gateway.GetTemplatesAsync(publicationId, kind.Equals("page", StringComparison.OrdinalIgnoreCase) ? TemplateKind.Page : TemplateKind.Component))
            .Select(t => new { t.Id, t.Title }));

    [HttpPost("folders"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateFolder([FromBody] CreateFolderRequest req) => Run(async () =>
    {
        if (req.Title.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Folder title cannot contain '/' or '\\'.");
        var node = await _gw.Gateway.CreateFolderAsync(req.ParentId, req.Title);
        _log.LogInformation("AUDIT {Who} created folder '{Title}' ({Id}) in {Parent}", _session.Who, req.Title, node.Id, req.ParentId);
        return (object)Dto(node);
    });

    private static bool IsPublication(string id) => id.StartsWith("tcm:0-") && id.EndsWith("-1");

    private static TreeNodeDto Dto(TreeNode n) => new()
    { Id = n.Id, Title = n.Title, Type = n.Type.ToString(), HasChildren = n.HasChildren, SchemaCount = n.SchemaCount };

    private async Task<IActionResult> Run<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tree API error");
            return BadRequest(new { error = ex.Message });
        }
    }
}
