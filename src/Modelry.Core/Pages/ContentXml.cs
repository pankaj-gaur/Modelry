using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using Modelry.Core.Model;

namespace Modelry.Core.Pages;

/// <summary>
/// Builds Tridion component content / metadata XML for a schema from what was read out of the HTML. Fields follow the
/// schema's order; embedded fields use the parent's namespace; links are xlink. A mandatory field that the HTML does not
/// provide gets a placeholder ("[TBC]", the first list value, today's date, the category's first keyword…) and is listed
/// in Placeholders; a mandatory link that cannot be filled is listed in Errors (the component cannot be saved without it).
/// </summary>
public sealed class ContentXml
{
    public const string Tbc = "[TBC]";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    private readonly IaWorkbook _wb;
    private readonly Func<string, string?> _namespaceOf;
    private readonly Func<FieldBinding, string?> _assetId;
    private readonly Func<SchemaBinding, string?> _componentId;
    private readonly Func<string, string?, (string Id, string Title)?> _keyword;

    public List<string> Placeholders { get; } = new();
    public List<string> Errors { get; } = new();
    private readonly HashSet<string> _reported = new();

    /// <param name="namespaceOf">Schema title → namespace URI (as in the CMS).</param>
    /// <param name="assetId">Image / video / document binding → its multimedia component id (null when there is none).</param>
    /// <param name="componentId">Item binding (slide, list item) → the id of the component created for it.</param>
    /// <param name="keyword">(category, text to match) → the matching keyword, else the category's first one; for mandatory keyword placeholders.</param>
    public ContentXml(IaWorkbook wb, Func<string, string?> namespaceOf, Func<FieldBinding, string?> assetId,
        Func<SchemaBinding, string?> componentId, Func<string, string?, (string Id, string Title)?> keyword)
    {
        _wb = wb; _namespaceOf = namespaceOf; _assetId = assetId; _componentId = componentId; _keyword = keyword;
    }

    public string BuildContent(IaSchema schema, SchemaBinding? binding)
    {
        XNamespace ns = _namespaceOf(schema.Title) ?? schema.NamespaceUri ?? "";
        var root = new XElement(ns + (schema.RootElementName ?? "Content"));
        AddFields(root, ns, schema.Title, IaFieldSection.Content, binding, schema.Title);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Metadata XML, or null when the schema has no metadata fields that get a value.</summary>
    /// <param name="values">Known values by XML name (e.g. altText of an image).</param>
    public string? BuildMetadata(IaSchema schema, IReadOnlyDictionary<string, string>? values = null)
    {
        if (!_wb.FieldsOf(schema.Title, IaFieldSection.Metadata).Any()) return null;
        XNamespace ns = _namespaceOf(schema.Title) ?? schema.NamespaceUri ?? "";
        var root = new XElement(ns + "Metadata");
        AddFields(root, ns, schema.Title, IaFieldSection.Metadata, null, schema.Title, values);
        return root.HasElements ? root.ToString(SaveOptions.DisableFormatting) : null;
    }

    private void AddFields(XElement parent, XNamespace ns, string schemaTitle, IaFieldSection section, SchemaBinding? b, string path,
        IReadOnlyDictionary<string, string>? values = null)
    {
        foreach (var f in _wb.FieldsOf(schemaTitle, section))
        {
            var fb = b?.Fields.FirstOrDefault(x => x.Field.XmlName == f.XmlName);
            var els = Values(ns, f, fb, $"{path}.{f.XmlName}").ToList();
            if (els.Count == 0 && values is not null && values.TryGetValue(f.XmlName, out var v) && v.Length > 0)
                els.Add(new XElement(ns + f.XmlName, v));
            if (els.Count == 0 && f.Mandatory) els.AddRange(Placeholder(ns, f, $"{path}.{f.XmlName}", b));
            if (f.MaxOccurs > 0 && els.Count > f.MaxOccurs) els = els.Take(f.MaxOccurs).ToList();
            parent.Add(els);
        }
    }

    private IEnumerable<XElement> Values(XNamespace ns, IaField f, FieldBinding? fb, string path)
    {
        if (fb is null) yield break;
        switch (f.Type)
        {
            case IaFieldType.Text or IaFieldType.MultiLineText:
            {
                var text = fb.Kind switch
                {
                    BindKind.LinkTitle => fb.Element?.GetAttribute("aria-label") ?? fb.Element?.GetAttribute("title"),
                    BindKind.ImageAlt => fb.Element?.GetAttribute("alt"),
                    BindKind.EmbedSrc => fb.Element?.GetAttribute("src"),
                    BindKind.LinkHref => fb.Element?.GetAttribute("href"),
                    _ => fb.Element is null ? null : HtmlSections.Collapse(fb.Element.TextContent)
                };
                if (!string.IsNullOrWhiteSpace(text)) yield return new XElement(ns + f.XmlName, text.Trim());
                break;
            }
            case IaFieldType.RichText:
            {
                var nodes = fb.Container && fb.Element is not null ? fb.Element.ChildNodes.ToList()
                    : fb.Elements.Count > 0 ? fb.Elements.Cast<INode>().ToList() : fb.Element is null ? new List<INode>() : new List<INode> { fb.Element };
                var xhtml = ToXhtml(nodes);
                if (xhtml.Count > 0) yield return new XElement(ns + f.XmlName, xhtml);
                break;
            }
            case IaFieldType.Number:
            {
                var m = Regex.Match(fb.Element?.TextContent ?? "", @"-?\d[\d,]*(\.\d+)?");
                if (m.Success && double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    yield return new XElement(ns + f.XmlName, d.ToString(CultureInfo.InvariantCulture));
                break;
            }
            case IaFieldType.Date:
            {
                var raw = fb.Element?.GetAttribute("datetime") ?? fb.Element?.TextContent.Trim();
                if (raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
                    yield return new XElement(ns + f.XmlName, dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                break;
            }
            case IaFieldType.ExternalLink:
            {
                var url = fb.Kind == BindKind.EmbedSrc ? fb.Element?.GetAttribute("src") : fb.Element?.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    yield return Link(ns, f, url.Trim()); // Tridion stores External Link fields as xlink:href, not as text
                break;
            }
            case IaFieldType.MultimediaLink:
                if (_assetId(fb) is { } mm) yield return Link(ns, f, mm);
                break;
            case IaFieldType.ComponentLink:
                if (fb.Kind == BindKind.Items)
                {
                    var any = false;
                    foreach (var item in fb.Items)
                        if (_componentId(item) is { } id) { any = true; yield return Link(ns, f, id); }
                    if (!any && fb.Items.Count > 0 && f.Mandatory && _reported.Add(path))
                        Errors.Add($"{path}: the {fb.Items.Count} linked item(s) from the HTML could not be created – see their own errors above");
                }
                else if (_assetId(fb) is { } doc) yield return Link(ns, f, doc); // a link to a document in the zip
                break;
            case IaFieldType.EmbeddedSchema:
            {
                var sub = f.EmbeddedSchema;
                if (sub is null) break;
                var bindings = fb.Kind == BindKind.Items ? fb.Items : fb.Embedded is { } e ? new List<SchemaBinding> { e } : new List<SchemaBinding>();
                var i = 0;
                foreach (var item in bindings)
                {
                    var el = new XElement(ns + f.XmlName);
                    AddFields(el, ns, sub, IaFieldSection.Content, item, $"{path}[{++i}]");
                    if (el.HasElements) yield return el;
                }
                break;
            }
        }
    }

    private IEnumerable<XElement> Placeholder(XNamespace ns, IaField f, string path, SchemaBinding? b)
    {
        switch (f.Type)
        {
            case IaFieldType.Text when f.ListValues.Count > 0:
            {
                // A list value naming the type of the linked items (e.g. entityType "Industry Profile" for a list of cards) beats the first value.
                var itemTypes = b?.Fields.Where(x => x.Kind == BindKind.Items && x.Field.Type == IaFieldType.ComponentLink && x.Items.Count > 0)
                    .Select(x => HtmlSections.Words(Pages.SectionMatcher.StripPrefix(x.Items[0].Schema.Title))).ToList() ?? new();
                var value = f.ListValues.FirstOrDefault(v => itemTypes.Any(w => HtmlSections.Words(v).SetEquals(w)))
                            ?? f.ListValues.FirstOrDefault(v => itemTypes.Any(w => HtmlSections.Words(v).Overlaps(w)));
                Placeholders.Add(value is null ? $"{path}: first allowed value \"{f.ListValues[0]}\"" : $"{path}: \"{value}\" (the type of the linked items)");
                yield return new XElement(ns + f.XmlName, value ?? f.ListValues[0]);
                break;
            }
            case IaFieldType.Text or IaFieldType.MultiLineText:
                Placeholders.Add($"{path}: {Tbc}");
                yield return new XElement(ns + f.XmlName, Tbc);
                break;
            case IaFieldType.RichText:
                Placeholders.Add($"{path}: {Tbc}");
                yield return new XElement(ns + f.XmlName, new XElement(Xhtml + "p", Tbc));
                break;
            case IaFieldType.Number:
                Placeholders.Add($"{path}: 0");
                yield return new XElement(ns + f.XmlName, "0");
                break;
            case IaFieldType.Date:
                Placeholders.Add($"{path}: today's date");
                yield return new XElement(ns + f.XmlName, DateTime.Today.ToString("yyyy-MM-ddT00:00:00", CultureInfo.InvariantCulture));
                break;
            case IaFieldType.ExternalLink:
                Placeholders.Add($"{path}: #");
                yield return Link(ns, f, "#");
                break;
            case IaFieldType.Keyword:
                if (f.Category is not null && _keyword(f.Category, HintText(b)) is { } kw)
                {
                    Placeholders.Add($"{path}: keyword \"{kw.Title}\"");
                    yield return new XElement(ns + f.XmlName, new XAttribute(XNamespace.Xmlns + "xlink", Xlink), new XAttribute(Xlink + "type", "simple"),
                        new XAttribute(Xlink + "href", kw.Id), new XAttribute(Xlink + "title", kw.Title), kw.Title);
                }
                else Errors.Add($"{path}: mandatory keyword, but category '{f.Category}' has no keywords in the CMS");
                break;
            case IaFieldType.EmbeddedSchema when f.EmbeddedSchema is not null:
            {
                var el = new XElement(ns + f.XmlName);
                AddFields(el, ns, f.EmbeddedSchema, IaFieldSection.Content, null, path);
                yield return el;
                break;
            }
            default:
                if (!_reported.Add(path)) break; // already explained (e.g. its linked items failed)
                Errors.Add($"{path}: mandatory {(f.Type == IaFieldType.MultimediaLink ? "image or file" : "link to another component")}, and the HTML has nothing for it");
                break;
        }
    }

    /// <summary>Text of the binding's heading-like fields, to pick a matching keyword (a card titled "Consumer goods" → Industry "Consumer Goods").</summary>
    private static string? HintText(SchemaBinding? b) =>
        b is null ? null : string.Join(" ", b.Fields.Where(x => x.Kind == BindKind.Text && x.Element is not null).Select(x => HtmlSections.Collapse(x.Element!.TextContent)));

    private static XElement Link(XNamespace ns, IaField f, string id) =>
        new(ns + f.XmlName, new XAttribute(XNamespace.Xmlns + "xlink", Xlink), new XAttribute(Xlink + "type", "simple"), new XAttribute(Xlink + "href", id));

    // ---------------------------------------------------------------- rich text

    private static readonly HashSet<string> Keep = new(StringComparer.OrdinalIgnoreCase)
        { "p", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "a", "strong", "b", "em", "i", "u", "br", "sup", "sub", "blockquote", "table", "thead", "tbody", "tr", "th", "td", "span" };
    private static readonly HashSet<string> Drop = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "img", "picture", "svg", "iframe", "video", "audio", "button", "form", "input", "select", "textarea", "template" };

    /// <summary>HTML → clean XHTML for a Tridion rich text field: known formatting tags only, links keep href and title.</summary>
    public static List<XNode> ToXhtml(IEnumerable<INode> nodes)
    {
        var result = new List<XNode>();
        foreach (var n in nodes) result.AddRange(Convert(n));
        // Loose text at the top level becomes a paragraph.
        var grouped = new List<XNode>();
        XElement? p = null;
        foreach (var n in result)
        {
            var inline = n is XText || n is XElement e && e.Name.LocalName is "a" or "strong" or "b" or "em" or "i" or "u" or "span" or "sup" or "sub" or "br";
            if (inline)
            {
                if (n is XText t && string.IsNullOrWhiteSpace(t.Value) && p is null) continue;
                p ??= new XElement(Xhtml + "p");
                p.Add(n);
            }
            else
            {
                if (p is not null) { grouped.Add(p); p = null; }
                grouped.Add(n);
            }
        }
        if (p is not null) grouped.Add(p);
        return grouped;
    }

    private static IEnumerable<XNode> Convert(INode node)
    {
        if (node.NodeType == NodeType.Text) { yield return new XText(Regex.Replace(node.TextContent, @"\s+", " ")); yield break; }
        if (node is not IElement el) yield break;
        if (Drop.Contains(el.LocalName)) yield break;
        var kids = el.ChildNodes.SelectMany(Convert).ToList();
        if (!Keep.Contains(el.LocalName)) { foreach (var k in kids) yield return k; yield break; }
        var x = new XElement(Xhtml + el.LocalName.ToLowerInvariant(), kids);
        if (el.LocalName == "a")
        {
            if (el.GetAttribute("href") is { Length: > 0 } href) x.SetAttributeValue("href", href);
            if (el.GetAttribute("title") is { Length: > 0 } title) x.SetAttributeValue("title", title);
        }
        yield return x;
    }
}
