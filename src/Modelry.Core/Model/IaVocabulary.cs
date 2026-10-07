namespace Modelry.Core.Model;

/// <summary>Text values used in the workbook and their mapping to enums (with aliases for older workbooks).</summary>
public static class IaVocabulary
{
    public static readonly IReadOnlyDictionary<IaFieldType, string> FieldTypeNames = new Dictionary<IaFieldType, string>
    {
        [IaFieldType.Text] = "Text",
        [IaFieldType.MultiLineText] = "Multi-line Text",
        [IaFieldType.RichText] = "Rich Text",
        [IaFieldType.Number] = "Number",
        [IaFieldType.Date] = "Date",
        [IaFieldType.ExternalLink] = "External Link",
        [IaFieldType.ComponentLink] = "Component Link",
        [IaFieldType.MultimediaLink] = "Multimedia Link",
        [IaFieldType.Keyword] = "Keyword",
        [IaFieldType.EmbeddedSchema] = "Embedded Schema",
    };

    private static readonly Dictionary<string, IaFieldType> FieldTypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Text (List)"] = IaFieldType.Text,
        ["Text (Checkbox)"] = IaFieldType.Text,
        ["SingleLineText"] = IaFieldType.Text,
        ["MultiLineText"] = IaFieldType.MultiLineText,
        ["Xhtml"] = IaFieldType.RichText,
        ["RichText"] = IaFieldType.RichText,
        ["Embedded"] = IaFieldType.EmbeddedSchema,
    };

    public static bool TryParseFieldType(string text, out IaFieldType type)
    {
        foreach (var kv in FieldTypeNames)
            if (string.Equals(kv.Value, text.Trim(), StringComparison.OrdinalIgnoreCase)) { type = kv.Key; return true; }
        return FieldTypeAliases.TryGetValue(text.Trim(), out type);
    }

    public static readonly string[] ListTypes = { "Select", "Radio", "Checkbox", "Tree" };

    public static bool TryParsePurpose(string text, out IaSchemaPurpose purpose)
    {
        var t = text.Replace(" ", "").Trim();
        if (t.Equals("Page", StringComparison.OrdinalIgnoreCase)) t = "Region";
        if (t.Equals("Parameters", StringComparison.OrdinalIgnoreCase) || t.Equals("TemplateParameter", StringComparison.OrdinalIgnoreCase)) t = "TemplateParameters";
        return Enum.TryParse(t, true, out purpose);
    }

    public static bool? ParseYesNo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToLowerInvariant();
        return t is "y" or "yes" or "true" or "1" ? true : t is "n" or "no" or "false" or "0" ? false : null;
    }

    public static string YesNo(bool value) => value ? "Y" : "N";

    public static int? ParseMaxOccurs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Equals("unbounded", StringComparison.OrdinalIgnoreCase) || t == "-1" || t == "*") return -1;
        return int.TryParse(t, out var n) ? n : null;
    }

    public static string MaxOccursText(int max) => max < 0 ? "unbounded" : max.ToString();

    public static List<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string JoinList(IEnumerable<string> values) => string.Join("; ", values);

    /// <summary>Which field sections a schema purpose may contain.</summary>
    public static IaFieldSection[] AllowedSections(IaSchemaPurpose purpose) => purpose switch
    {
        IaSchemaPurpose.Component => new[] { IaFieldSection.Content, IaFieldSection.Metadata },
        IaSchemaPurpose.Embedded => new[] { IaFieldSection.Content },
        IaSchemaPurpose.TemplateParameters => new[] { IaFieldSection.Content },
        _ => new[] { IaFieldSection.Metadata }, // Multimedia, Metadata, Bundle, Region
    };

    public static bool IsValidXmlName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_.-]*$") &&
        !name.StartsWith("xml", StringComparison.OrdinalIgnoreCase);
}
