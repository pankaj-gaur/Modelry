using System.Globalization;
using System.Xml.Linq;
using Modelry.Core.Model;

namespace Modelry.Tridion;

/// <summary>
/// Makes sure every field in a schema XSD carries the Min/Max Occurs from the workbook.
/// Fields with a fixed maximum above 1 (e.g. 2) came out of schema creation without "Allow multiple values", while
/// unbounded (-1) worked. This sets minOccurs/maxOccurs directly on the field elements after ConvertSchemaFieldsToXsd,
/// so the saved XSD always carries the workbook's values whatever the conversion produced.
/// </summary>
public static class XsdOccurrence
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

    /// <summary>Returns the corrected XSD and a description of each field that had to be changed.</summary>
    public static (string Xsd, List<string> Changes) Apply(string xsd, string? rootElementName,
        IEnumerable<IaField> contentFields, IEnumerable<IaField> metadataFields)
    {
        var changes = new List<string>();
        if (string.IsNullOrWhiteSpace(xsd)) return (xsd, changes);
        XDocument doc;
        try { doc = XDocument.Parse(xsd, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return (xsd, changes); }
        var top = doc.Root?.Elements().ToList() ?? new List<XElement>();

        // Content fields: <xsd:element name="root"> (component, multimedia) or <xsd:complexType name="root"> (embedded).
        var root = top.FirstOrDefault(e => (e.Name == Xs + "element" || e.Name == Xs + "complexType")
                                           && (string?)e.Attribute("name") == rootElementName);
        Patch(root, contentFields, "content", changes);
        // Metadata fields: <xsd:element name="Metadata">.
        Patch(top.FirstOrDefault(e => e.Name == Xs + "element" && (string?)e.Attribute("name") == "Metadata"), metadataFields, "metadata", changes);

        return changes.Count == 0 ? (xsd, changes) : (doc.ToString(SaveOptions.DisableFormatting), changes);
    }

    private static void Patch(XElement? container, IEnumerable<IaField> fields, string section, List<string> changes)
    {
        var sequence = container?.Descendants(Xs + "sequence").FirstOrDefault();
        if (sequence is null) return;
        foreach (var f in fields)
        {
            var el = sequence.Elements(Xs + "element").FirstOrDefault(e => (string?)e.Attribute("name") == f.XmlName);
            if (el is null) continue;
            var wantMax = f.MaxOccurs < 0 ? "unbounded" : f.MaxOccurs.ToString(CultureInfo.InvariantCulture);
            var wantMin = Math.Max(0, f.MinOccurs).ToString(CultureInfo.InvariantCulture);
            var haveMax = (string?)el.Attribute("maxOccurs") ?? "1";
            var haveMin = (string?)el.Attribute("minOccurs") ?? "1";
            if (haveMax != wantMax)
            {
                el.SetAttributeValue("maxOccurs", wantMax);
                changes.Add($"{section} field '{f.XmlName}': maxOccurs {haveMax} → {wantMax}");
            }
            if (haveMin != wantMin)
            {
                el.SetAttributeValue("minOccurs", wantMin);
                changes.Add($"{section} field '{f.XmlName}': minOccurs {haveMin} → {wantMin}");
            }
        }
    }
}
