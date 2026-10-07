using Modelry.Core.Model;

namespace Modelry.Core.CodeGen;

/// <summary>What a generated class represents. Decides its base type and folder.</summary>
public enum ModelKind { Entity, Embedded, Media, Keyword, Page, Region }

/// <summary>How a property's value is shaped, independent of the target language (DXA C# today; Sling Models later).</summary>
public enum ModelValueKind
{
    Text, RichText, Number, Date, Boolean,
    /// <summary>A resolved link (DXA Link) – component link without a specific target schema.</summary>
    Link,
    /// <summary>A keyword without its own model (DXA Tag).</summary>
    Tag,
    /// <summary>Another generated class (embedded schema, linked component, multimedia or keyword model).</summary>
    Model,
    /// <summary>Any entity (component link to several schemas).</summary>
    AnyEntity,
    /// <summary>Any multimedia item.</summary>
    AnyMedia
}

/// <summary>Options chosen on the Models step.</summary>
public sealed class ModelGenerationOptions
{
    /// <summary>Namespace of every model class, e.g. "Sabic.Web.Models".</summary>
    public string RootNamespace { get; set; } = "";
    /// <summary>Area used for views without an area prefix (and for the registration class when there are no views).</summary>
    public string? DefaultArea { get; set; }
    /// <summary>Metadata schema whose fields become the page model's properties; null = DXA's PageModel.</summary>
    public string? PageMetadataSchema { get; set; }
    /// <summary>Semantic prefix used in [SemanticEntity]/[SemanticProperty]; empty = derived from the schema titles.</summary>
    public string? SemanticPrefix { get; set; }
    /// <summary>Workbook file name, written into file headers and the README.</summary>
    public string SourceFileName { get; set; } = "";
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ModelProperty
{
    public required string Name { get; init; }
    public required IaField Field { get; init; }
    public ModelValueKind Kind { get; init; }
    /// <summary>Target class name when Kind is Model.</summary>
    public string? ModelName { get; init; }
    public bool IsList { get; init; }
    /// <summary>For Checkbox fields with a single value: a read-only boolean helper is generated beside the mapped text.</summary>
    public string? BooleanHelperName { get; init; }
    public string? CheckedValue { get; init; }
    /// <summary>Why the name differs from the XML name (collision), for the report.</summary>
    public string? RenameReason { get; init; }
}

public sealed class ModelClass
{
    public required string Name { get; init; }
    public required IaSchema Schema { get; init; }
    public ModelKind Kind { get; init; }
    /// <summary>Base class: a DXA type (EntityModel, Image, Download, KeywordModel, PageModel, RegionModel) or another generated class.</summary>
    public required string BaseType { get; init; }
    public required string Folder { get; init; }
    public List<ModelProperty> Properties { get; } = new();
    public string FilePath => $"{Folder}/{Name}.cs";
}

/// <summary>One RegisterViewModel call in the area registration.</summary>
public sealed record ViewRegistration(string Area, string View, string? Controller, string ModelType, string Source)
{
    public string Key => $"{Area}:{Controller ?? "(default)"}:{View}";
}

/// <summary>A schema that gets no class, and why.</summary>
public sealed record SkippedSchema(IaSchema Schema, string Reason);

public sealed class ModelPlan
{
    public required ModelGenerationOptions Options { get; init; }
    public required string SemanticPrefix { get; init; }
    public List<ModelClass> Classes { get; } = new();
    public List<ViewRegistration> Registrations { get; } = new();
    /// <summary>Component models without a view, registered for semantic lookup (lists, links typed as EntityModel).</summary>
    public List<string> ModelOnlyRegistrations { get; } = new();
    public List<SkippedSchema> Skipped { get; } = new();
    public List<Issue> Issues { get; } = new();
    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);

    public IEnumerable<string> Areas => Registrations.Select(r => r.Area)
        .Concat(Options.DefaultArea is { Length: > 0 } a ? new[] { a } : Array.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public ModelClass? ClassFor(string schemaTitle) => Classes.FirstOrDefault(c => IaWorkbook.Same(c.Schema.Title, schemaTitle));
}
