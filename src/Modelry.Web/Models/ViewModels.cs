using System.ComponentModel.DataAnnotations;
using Modelry.Core.Import;
using Modelry.Core.Model;

namespace Modelry.Web.Models;

public sealed class LoginViewModel
{
    /// <summary>"Windows" or "OAuth".</summary>
    public string AuthMode { get; set; } = "Windows";
    [Required, Display(Name = "Core Service URL")] public string CoreServiceUrl { get; set; } = "";
    // Windows mode
    [Display(Name = "User ID (DOMAIN\\user)")] public string? UserName { get; set; }
    [DataType(DataType.Password), Display(Name = "Password")] public string? Password { get; set; }
    // OAuth mode
    [Display(Name = "Access Management token URL")] public string? TokenUrl { get; set; }
    [Display(Name = "Client ID")] public string? ClientId { get; set; }
    [DataType(DataType.Password), Display(Name = "Client secret")] public string? ClientSecret { get; set; }
    [Display(Name = "Scope (optional)")] public string? Scope { get; set; }

    // AEM
    [Display(Name = "AEM author URL")] public string? AemUrl { get; set; }
    /// <summary>"basic" (user + password) or "token" (bearer access token).</summary>
    public string AemAuth { get; set; } = "basic";
    [Display(Name = "User name")] public string? AemUser { get; set; }
    [DataType(DataType.Password), Display(Name = "Password")] public string? AemPassword { get; set; }
    [DataType(DataType.Password), Display(Name = "Access token")] public string? AemToken { get; set; }

    public string Cms { get; set; } = "tridion";
    public bool Demo { get; set; }
    public bool DemoAllowed { get; set; }
    public bool ProxyAvailable { get; set; }
    public string? Error { get; set; }
    public bool Expired { get; set; }
}

public sealed class DryRunViewModel
{
    public string? CheckJobId { get; init; }
    public DateTime? CheckedUtc { get; init; }
    public required string UploadId { get; init; }
    public required string FileName { get; init; }
    public required string FolderId { get; init; }
    public required string FolderTitle { get; init; }
    public required string PublicationTitle { get; init; }
    public required ImportPlan Plan { get; init; }
}

public sealed class ImportResultViewModel
{
    public required string ResultId { get; init; }
    public required string FolderTitle { get; init; }
    public required ImportResult Result { get; init; }
    public string What { get; init; } = "Schemas";
}

public sealed class TemplateDryRunViewModel
{
    public string? CheckJobId { get; init; }
    public DateTime? CheckedUtc { get; init; }
    public required string UploadId { get; init; }
    public required string FileName { get; init; }
    public required string FolderId { get; init; }
    public required string FolderTitle { get; init; }
    public required string PublicationTitle { get; init; }
    public required Modelry.Core.Templates.TemplatePlan Plan { get; init; }
    public string? BaseComponentTemplateId { get; init; }
    public string? BasePageTemplateId { get; init; }
    public string? HeaderInclude { get; init; }
    public string? FooterInclude { get; init; }
    public bool ApplyRegionConstraints { get; init; }
}

public sealed class TreeNodeDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Type { get; init; }
    public bool HasChildren { get; init; }
    public int? SchemaCount { get; init; }
}

public sealed class CreateFolderRequest
{
    [Required] public string ParentId { get; set; } = "";
    [Required, StringLength(255)] public string Title { get; set; } = "";
}

public static class IssueCss
{
    public static string Of(IssueLevel l) => l switch { IssueLevel.Error => "err", IssueLevel.Warning => "warn", _ => "info" };
}

public sealed class ModelsViewModel
{
    public required Modelry.Web.Services.WizardData Wizard { get; init; }
    public bool IsAem { get; init; }
    public Modelry.Core.CodeGen.ModelPlan? Plan { get; set; }
    public string Namespace { get; set; } = "";
    public string? PageMetadataSchema { get; set; }
    public string? SemanticPrefix { get; set; }
    public List<string> MetadataSchemas { get; set; } = new();
    public string? ReadError { get; set; }
}
