using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Modelry.Core.Model;

namespace Modelry.Core.Pages;

public enum BindKind
{
    /// <summary>Element text.</summary>
    Text,
    /// <summary>Inner HTML of a container, or a run of sibling paragraphs.</summary>
    RichText,
    /// <summary>src of an img (or url() of an inline background image).</summary>
    ImageSrc,
    /// <summary>alt attribute of the image bound by a sibling field.</summary>
    ImageAlt,
    VideoSrc,
    /// <summary>src of an iframe (YouTube, Vimeo, maps).</summary>
    EmbedSrc,
    /// <summary>href of an anchor.</summary>
    LinkHref,
    /// <summary>aria-label / title of an anchor.</summary>
    LinkTitle,
    Date,
    Number,
    /// <summary>An embedded schema bound to one element (link, media) or a sub-scope.</summary>
    Embedded,
    /// <summary>A repeated embedded field or a list of linked components: one item per repeated element.</summary>
    Items
}

public sealed class FieldBinding
{
    public required IaField Field { get; init; }
    public BindKind Kind { get; init; }
    /// <summary>The element that holds the value (first element of a run, the anchor, the img…).</summary>
    public IElement? Element { get; init; }
    /// <summary>For RichText runs and link groups: every element the value spans.</summary>
    public List<IElement> Elements { get; init; } = new();
    /// <summary>RichText: true when Element is a container whose whole inner HTML is the value.</summary>
    public bool Container { get; init; }
    public string? Preview { get; init; }
    /// <summary>Asset URL as written in the HTML (img src, video src, document href).</summary>
    public string? AssetUrl { get; init; }
    public SchemaBinding? Embedded { get; init; }
    public List<SchemaBinding> Items { get; init; } = new();
    /// <summary>Schema each item was bound against (Items).</summary>
    public string? ItemSchema { get; init; }
    public string Source { get; init; } = "";
}

public sealed class SchemaBinding
{
    public required IaSchema Schema { get; init; }
    public required IElement Scope { get; init; }
    public List<FieldBinding> Fields { get; } = new();
    /// <summary>Mandatory fields with nothing in the HTML (they get placeholders when content is created).</summary>
    public List<IaField> MissingMandatory { get; } = new();
    public IEnumerable<FieldBinding> All() => Fields.Concat(Fields.SelectMany(f => (f.Embedded?.All() ?? Enumerable.Empty<FieldBinding>())
        .Concat(f.Items.SelectMany(i => i.All()))));
}

/// <summary>
/// Finds, inside a section, the element that holds each field of the matched schema. Explicit data-cms-field markers win;
/// otherwise class names, element roles (headings, paragraphs, links, images, repeated items) and field names decide.
/// The result drives both the validation screen and the generated Razor view.
/// </summary>
public sealed class FieldBinder
{
    private readonly IaWorkbook _wb;
    private readonly HashSet<IElement> _claimed = new();
    /// <summary>Item schemas the mapping names for this placement (e.g. Industry Profile cards in a Content List).</summary>
    private readonly HashSet<string> _preferred;

    public FieldBinder(IaWorkbook wb, IEnumerable<string>? preferredItemSchemas = null)
    {
        _wb = wb;
        _preferred = new HashSet<string>(preferredItemSchemas ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public static SchemaBinding? Bind(IaWorkbook wb, HtmlSection section, string schemaTitle, IEnumerable<string>? itemSchemas = null)
    {
        var schema = wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, schemaTitle));
        return schema is null ? null : new FieldBinder(wb, itemSchemas).BindSchema(schema, section.Element, topLevel: true);
    }

    private SchemaBinding BindSchema(IaSchema schema, IElement scope, bool topLevel)
    {
        var b = new SchemaBinding { Schema = schema, Scope = scope };
        var fields = _wb.FieldsOf(schema.Title, IaFieldSection.Content).ToList();
        var done = new HashSet<IaField>();
        void Add(FieldBinding? fb) { if (fb is null) return; b.Fields.Add(fb); done.Add(fb.Field); }

        // 0. Explicit markers.
        foreach (var f in fields)
            if (First(scope, e => e.GetAttribute("data-cms-field") == f.XmlName) is { } el) Add(Explicit(f, el));
        // 1. Repeated items (cards, panels, slides, linked components) – before headings, so item headings stay with their item.
        foreach (var f in fields.Where(f => !done.Contains(f) && IsRepeated(f) && !IsLinkLike(TargetSchema(f)))) Add(BindItems(f, scope, topLevel));
        // 2. Media and images.
        foreach (var f in fields.Where(f => !done.Contains(f) && (IsMediaLike(TargetSchema(f)) || f.Type == IaFieldType.MultimediaLink && TargetsImage(f))))
            Add(BindMedia(f, scope));
        // 3. Headings, then other simple values.
        foreach (var f in fields.Where(f => !done.Contains(f) && f.Type == IaFieldType.Text && Role(f) == "heading")) Add(BindText(f, scope, topLevel, b));
        // Dates before other text: a standfirst paragraph must not swallow the <time> beside it.
        foreach (var f in fields.Where(f => !done.Contains(f) && f.Type == IaFieldType.Date)) Add(BindText(f, scope, topLevel, b));
        foreach (var f in fields.Where(f => !done.Contains(f) && f.Type is IaFieldType.Text or IaFieldType.MultiLineText or IaFieldType.Number
                                          or IaFieldType.Date or IaFieldType.ExternalLink or IaFieldType.MultimediaLink))
            Add(BindText(f, scope, topLevel, b));
        // 4. Rich text: what is left of the running text.
        foreach (var f in fields.Where(f => !done.Contains(f) && f.Type == IaFieldType.RichText)) Add(BindRichText(f, scope));
        // 5. Links last: whatever anchors are left are the calls to action.
        foreach (var f in fields.Where(f => !done.Contains(f) && IsLinkLike(TargetSchema(f)))) Add(BindLinks(f, scope));

        b.Fields.Sort((x, y) => fields.IndexOf(x.Field).CompareTo(fields.IndexOf(y.Field)));
        b.MissingMandatory.AddRange(fields.Where(f => f.Mandatory && !done.Contains(f)));
        return b;
    }

    // ---------------------------------------------------------------- field kinds

    private FieldBinding Explicit(IaField f, IElement el)
    {
        Claim(el);
        if (f.Type == IaFieldType.EmbeddedSchema && TargetSchema(f) is { } t)
            return new FieldBinding { Field = f, Kind = BindKind.Embedded, Element = el, Embedded = BindEmbeddedOn(t, el), Source = "data-cms-field", Preview = Short(el.TextContent) };
        var kind = f.Type switch
        {
            IaFieldType.RichText => BindKind.RichText, IaFieldType.Date => BindKind.Date, IaFieldType.Number => BindKind.Number,
            IaFieldType.ExternalLink => BindKind.LinkHref,
            IaFieldType.MultimediaLink => el.LocalName == "a" ? BindKind.LinkHref : BindKind.ImageSrc,
            _ => BindKind.Text
        };
        return new FieldBinding
        {
            Field = f, Kind = kind, Element = el, Container = kind == BindKind.RichText, Source = "data-cms-field",
            Preview = kind is BindKind.ImageSrc or BindKind.LinkHref ? Src(el) : Short(el.TextContent), AssetUrl = kind == BindKind.ImageSrc ? Src(el) : null
        };
    }

    private FieldBinding? BindItems(IaField f, IElement scope, bool topLevel)
    {
        var target = TargetSchema(f);
        if (target is null) return null;
        // An element named after the field (class="accordion__items") narrows the search. The name can also match one item
        // rather than the container (class="swiper-slide" for "slides"), so its parent is tried next, then the whole section.
        var hint = ByTokens(scope, f.XmlName, e => e.Children.Length >= 1);
        var source = hint is not null ? "class name" : "repeated elements";
        List<IElement>? group = null;
        foreach (var where in new[] { hint, hint?.ParentElement, scope }.Where(w => w is not null && (w == scope || scope.Contains(w!))).Distinct())
        {
            group = HtmlSections.RepeatedGroups(where!).FirstOrDefault(g => g.All(Available) && IsItemGroup(g));
            if (group is not null) break;
        }
        var single = false;
        if (group is null)
        {
            // A carousel whose other slides are added by JavaScript has one item in the HTML: for a mandatory list, use the
            // named element (or the section) as that one item rather than nothing. Inside an item (an article card's body
            // sections) there is no such fallback: it would swallow the card's own headline, date and image.
            if (!f.Mandatory || !topLevel && hint is null) return null;
            group = new List<IElement> { hint ?? scope };
            single = true;
            source = "only one item in the HTML";
        }
        if (f.MaxOccurs > 0 && group.Count > f.MaxOccurs) group = group.Take(f.MaxOccurs).ToList();
        // Links to several schemas: the one the mapping names for this page, else the one whose fields fit the items best.
        if (f.Type == IaFieldType.ComponentLink && f.AllowedTargetSchemas.Count > 1 && !f.AllowedTargetSchemas.Any(_preferred.Contains))
            target = f.AllowedTargetSchemas.Select(t => _wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, t))).Where(s => s is not null)
                .OrderByDescending(s => new FieldBinder(_wb).BindSchema(s!, group[0], topLevel: false).Fields.Count)
                .ThenByDescending(s => HtmlSections.Words(SectionMatcher.StripPrefix(s!.Title)).Count(w => group[0].ClassList.Concat(scope.ClassList)
                    .SelectMany(HtmlSections.SplitWords).Contains(w))).First()!;
        foreach (var e in group) Claim(e);
        var items = group.Select(e => new FieldBinder(_wb).BindSchema(target, e, topLevel: false)).ToList(); // fresh claims inside each item
        return new FieldBinding
        {
            Field = f, Kind = BindKind.Items, Element = group[0], Elements = group, Items = items, ItemSchema = target.Title,
            Preview = $"{group.Count} × {SectionMatcher.StripPrefix(target.Title)}{(single ? " (only one found in the HTML – others may be added by JavaScript)" : "")}", Source = source
        };
    }

    private FieldBinding? BindMedia(IaField f, IElement scope)
    {
        // Not an element that wraps media already taken (a <picture> around the image another field has).
        bool Free(IElement e) => IsMediaElement(e) && !_claimed.Any(c => e.Contains(c));
        var iconField = f.XmlName.Contains("icon", StringComparison.OrdinalIgnoreCase) || f.XmlName.Contains("logo", StringComparison.OrdinalIgnoreCase);
        var el = ByTokens(scope, f.XmlName, Free) ?? First(scope, e => Free(e) && iconField == IsIcon(e))
                 ?? (iconField ? null : First(scope, Free));
        if (el is null) return null;
        Claim(el);
        if (f.Type == IaFieldType.MultimediaLink)
            return new FieldBinding { Field = f, Kind = BindKind.ImageSrc, Element = el, AssetUrl = Src(el), Preview = Src(el), Source = "image" };
        var target = TargetSchema(f)!;
        return new FieldBinding { Field = f, Kind = BindKind.Embedded, Element = el, Embedded = BindEmbeddedOn(target, el), Preview = Src(el), AssetUrl = Src(el), Source = "image / video" };
    }

    private FieldBinding? BindText(IaField f, IElement scope, bool topLevel, SchemaBinding b)
    {
        var role = Role(f);
        IElement? el = ByTokens(scope, f.XmlName, e => ShortText(e) || f.Type == IaFieldType.MultiLineText);
        var source = "class name";
        if (el is null)
        {
            source = role;
            el = f.Type switch
            {
                IaFieldType.Date => First(scope, e => e.LocalName == "time") ?? First(scope, e => IsLeaf(e) && Regex.IsMatch(e.TextContent, @"\b\d{4}\b") && DateTime.TryParse(e.TextContent.Trim(), out _)),
                IaFieldType.Number => First(scope, e => IsLeaf(e) && Regex.IsMatch(e.TextContent.Trim(), @"^\d[\d.,]*$")),
                IaFieldType.ExternalLink => null,
                IaFieldType.MultimediaLink => TargetsDocument(f) ? First(scope, e => e.LocalName == "a" && IsDocumentHref(e.GetAttribute("href"))) : null,
                IaFieldType.MultiLineText when role == "quote" => First(scope, e => e.LocalName == "blockquote") ?? First(scope, e => e.LocalName == "p"),
                IaFieldType.MultiLineText => First(scope, e => e.LocalName == "p" && e.TextContent.Trim().Length >= 25 && e.QuerySelector("time") is null),
                _ => role switch
                {
                    "heading" => (topLevel ? First(scope, e => e.LocalName == "h1") : null) ?? First(scope, e => Regex.IsMatch(e.LocalName, "^h[1-6]$")),
                    "eyebrow" => b.Fields.FirstOrDefault(x => Role(x.Field) == "heading")?.Element?.PreviousElementSibling is { } p && Available(p) && ShortText(p) ? p : null,
                    "subheading" => b.Fields.FirstOrDefault(x => Role(x.Field) == "heading")?.Element?.NextElementSibling is { } n && Available(n) && ShortText(n) && n.LocalName != "p" ? n : null,
                    "caption" => First(scope, e => e.LocalName == "figcaption"),
                    "value" => First(scope, e => IsLeaf(e) && Regex.IsMatch(e.TextContent.Trim(), @"^[\p{Sc}+\-~≈<>]?\s?\d")),
                    "label" when !topLevel => First(scope, e => IsLeaf(e) && ShortText(e) && !Regex.IsMatch(e.TextContent.Trim(), @"^[\p{Sc}+\-]?\d")),
                    _ => null
                }
            };
        }
        if (el is null) return null;
        // A label split over inline spans ("Year of " + "establishment") is the whole parent paragraph.
        if (f.Type is IaFieldType.Text or IaFieldType.MultiLineText && el.LocalName == "span" && el.ParentElement is { } par && par != scope
            && par.LocalName is "p" or "span" or "div" && par.Children.Length > 1 && par.Children.All(c => c.LocalName == "span" && c.Children.Length == 0)
            && par.ChildNodes.All(nd => nd.NodeType != NodeType.Text || nd.TextContent.Trim().Length == 0) && Available(par))
            el = par;
        Claim(el);
        var kind = f.Type switch
        {
            IaFieldType.Date => BindKind.Date, IaFieldType.Number => BindKind.Number,
            IaFieldType.ExternalLink => BindKind.LinkHref, IaFieldType.MultimediaLink => BindKind.LinkHref, _ => BindKind.Text
        };
        var preview = kind == BindKind.LinkHref ? el.GetAttribute("href") : Short(el.TextContent);
        return new FieldBinding
        {
            Field = f, Kind = kind, Element = el, Preview = preview, Source = source,
            AssetUrl = f.Type == IaFieldType.MultimediaLink ? el.GetAttribute("href") : null
        };
    }

    private FieldBinding? BindRichText(IaField f, IElement scope)
    {
        var hinted = ByTokens(scope, f.XmlName, e => e.TextContent.Trim().Length > 0 && !_claimed.Any(c => e.Contains(c)));
        if (hinted is not null) { Claim(hinted); return new FieldBinding { Field = f, Kind = BindKind.RichText, Element = hinted, Container = true, Preview = Short(hinted.TextContent), Source = "class name" }; }
        // Lists of tabs, tiles or cards (buttons, images, tab roles inside) are UI, not running text.
        bool Prose(IElement e) => e.LocalName == "p" || e.QuerySelector("button,img,picture,video,iframe,[role=tab],[role=tablist]") is null && e.GetAttribute("role") != "tablist";
        var first = First(scope, e => e.LocalName is "p" or "ul" or "ol" && e.TextContent.Trim().Length > 0 && Prose(e));
        if (first is null) return null;
        var parent = first.ParentElement!;
        var run = new List<IElement>();
        for (var e = first; e is not null && Available(e) && e.LocalName is "p" or "ul" or "ol" or "h3" or "h4" or "h5" or "h6" or "blockquote" && Prose(e); e = e.NextElementSibling)
            run.Add(e);
        var siblings = parent.Children.Where(c => c.LocalName is not ("script" or "style" or "template" or "noscript" or "br")).ToList();
        var whole = parent != scope && siblings.All(run.Contains);
        foreach (var e in run) Claim(e);
        if (whole) Claim(parent);
        return new FieldBinding
        {
            Field = f, Kind = BindKind.RichText, Element = whole ? parent : first, Elements = run, Container = whole,
            Preview = Short(string.Join(" ", run.Select(r => r.TextContent))), Source = whole ? "paragraph container" : "paragraphs"
        };
    }

    private FieldBinding? BindLinks(IaField f, IElement scope)
    {
        var target = TargetSchema(f)!;
        var hint = ByTokens(scope, f.XmlName, e => e.LocalName == "a" || e.QuerySelector("a") is not null);
        var anchors = (hint is null ? Descendants(scope) : Descendants(hint).Prepend(hint))
            .Where(e => e.LocalName == "a" && e.HasAttribute("href") && Available(e)).ToList();
        if (anchors.Count == 0) return null;
        var single = f.MaxOccurs == 1;
        if (single) anchors = anchors.Take(1).ToList();
        else if (f.MaxOccurs > 1) anchors = anchors.Take(f.MaxOccurs).ToList();
        foreach (var a in anchors) Claim(a);
        var items = anchors.Select(a => BindEmbeddedOn(target, a)).ToList();
        return single
            ? new FieldBinding { Field = f, Kind = BindKind.Embedded, Element = anchors[0], Embedded = items[0], Preview = LinkPreview(anchors[0]), Source = "link" }
            : new FieldBinding
            {
                Field = f, Kind = BindKind.Items, Element = anchors[0], Elements = anchors, Items = items, ItemSchema = target.Title,
                Preview = string.Join(" · ", anchors.Select(LinkPreview)), Source = "links"
            };
    }

    /// <summary>Binds a link-like or media-like embedded schema to one anchor / image / video element.</summary>
    private SchemaBinding BindEmbeddedOn(IaSchema schema, IElement el)
    {
        var b = new SchemaBinding { Schema = schema, Scope = el };
        var fields = _wb.FieldsOf(schema.Title, IaFieldSection.Content).ToList();
        if (!IsLinkLike(schema) && !IsMediaLike(schema)) return new FieldBinder(_wb).BindSchema(schema, el, topLevel: false);
        var media = MediaElement(el);
        foreach (var f in fields)
        {
            FieldBinding? fb = null;
            var name = f.XmlName.ToLowerInvariant();
            if (el.LocalName == "a" && IsLinkLike(schema))
            {
                if (f.Type is IaFieldType.ExternalLink && !name.Contains("image") && !name.Contains("video"))
                    fb = new FieldBinding { Field = f, Kind = BindKind.LinkHref, Element = el, Preview = el.GetAttribute("href"), Source = "href" };
                else if (f.Type == IaFieldType.ComponentLink)
                    fb = new FieldBinding { Field = f, Kind = BindKind.LinkHref, Element = el, Preview = "(set when content is created: internal link if the target is known)", Source = "href" };
                else if (f.Type == IaFieldType.Text && f.ListType is null && (name.Contains("text") || name.Contains("label")) && el.TextContent.Trim().Length > 0)
                    fb = new FieldBinding { Field = f, Kind = BindKind.Text, Element = el, Preview = Short(el.TextContent), Source = "link text" };
                else if (f.Type == IaFieldType.Text && f.ListType is null && name.Contains("title") && (el.GetAttribute("aria-label") ?? el.GetAttribute("title")) is { } t)
                    fb = new FieldBinding { Field = f, Kind = BindKind.LinkTitle, Element = el, Preview = t, Source = "aria-label" };
            }
            else if (media is not null)
            {
                var isVideo = media.LocalName == "video" || media.LocalName == "iframe";
                if (f.Type == IaFieldType.MultimediaLink && !isVideo && (TargetsImage(f) || f.AllowedTargetSchemas.Count == 0) && !name.Contains("mobile") && !b.Fields.Any(x => x.Kind == BindKind.ImageSrc))
                    fb = new FieldBinding { Field = f, Kind = BindKind.ImageSrc, Element = media, AssetUrl = Src(media), Preview = Src(media), Source = "img src" };
                else if (f.Type == IaFieldType.MultimediaLink && media.LocalName == "video" && !TargetsImage(f))
                    fb = new FieldBinding { Field = f, Kind = BindKind.VideoSrc, Element = media, AssetUrl = Src(media), Preview = Src(media), Source = "video src" };
                else if (f.Type == IaFieldType.ExternalLink && name.Contains("video") && media.LocalName == "iframe")
                    fb = new FieldBinding { Field = f, Kind = BindKind.EmbedSrc, Element = media, Preview = Src(media), Source = "iframe src" };
                else if (f.Type == IaFieldType.Text && name.Contains("alt") && media.LocalName == "img")
                    fb = new FieldBinding { Field = f, Kind = BindKind.ImageAlt, Element = media, Preview = media.GetAttribute("alt"), Source = "alt" };
            }
            if (fb is not null) b.Fields.Add(fb);
        }
        b.MissingMandatory.AddRange(fields.Where(f => f.Mandatory && b.Fields.All(x => x.Field != f)));
        return b;
    }

    // ---------------------------------------------------------------- helpers

    private IaSchema? TargetSchema(IaField f)
    {
        var title = f.Type == IaFieldType.EmbeddedSchema ? f.EmbeddedSchema
            : f.Type == IaFieldType.ComponentLink && f.AllowedTargetSchemas.Count >= 1
                ? f.AllowedTargetSchemas.FirstOrDefault(_preferred.Contains) ?? f.AllowedTargetSchemas[0] : null;
        return title is null ? null : _wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, title));
    }

    /// <summary>Repeated elements that can be content items (not inline links, table cells, image sources or plain link lists).</summary>
    private static bool IsItemGroup(List<IElement> g)
    {
        var tag = g[0].LocalName;
        if (tag is "a" or "option" or "span" or "br" or "source" or "img" or "td" or "th" or "tr" or "path" or "use") return false;
        if (tag == "li" && g.All(e => e.Children.Length == 1 && e.Children[0].LocalName == "a")) return false;
        return true;
    }

    private static bool IsRepeated(IaField f) => f.MaxOccurs != 1 && f.Type is IaFieldType.EmbeddedSchema or IaFieldType.ComponentLink;

    private bool IsLinkLike(IaSchema? s)
    {
        if (s is null || s.Purpose != IaSchemaPurpose.Embedded) return false;
        var fs = _wb.FieldsOf(s.Title, IaFieldSection.Content).ToList();
        return fs.Count <= 10 && fs.Any(f => f.Type is IaFieldType.ExternalLink or IaFieldType.ComponentLink)
               && fs.Any(f => f.Type == IaFieldType.Text && Regex.IsMatch(f.XmlName, "^(link)?(text|label)$|linktext|linklabel", RegexOptions.IgnoreCase))
               && !fs.Any(f => f.Type is IaFieldType.RichText or IaFieldType.EmbeddedSchema or IaFieldType.MultimediaLink);
    }

    private bool IsMediaLike(IaSchema? s)
    {
        if (s is null || s.Purpose != IaSchemaPurpose.Embedded) return false;
        var fs = _wb.FieldsOf(s.Title, IaFieldSection.Content).ToList();
        return fs.Any(f => f.Type == IaFieldType.MultimediaLink) && !fs.Any(f => f.Type is IaFieldType.RichText or IaFieldType.EmbeddedSchema)
               && fs.All(f => f.Type is IaFieldType.MultimediaLink or IaFieldType.ExternalLink || f.Type == IaFieldType.Text && (f.ListType is not null || Regex.IsMatch(f.XmlName, "alt|caption", RegexOptions.IgnoreCase)));
    }

    private bool TargetsImage(IaField f) => f.AllowedTargetSchemas.Any(t =>
        _wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, t)) is { } s && s.AllowedMultimediaTypes.Any(x => x is "jpg" or "jpeg" or "png" or "svg" or "webp" or "gif"));

    private bool TargetsDocument(IaField f) => f.AllowedTargetSchemas.Any(t =>
        _wb.Schemas.FirstOrDefault(s => IaWorkbook.Same(s.Title, t)) is { } s && s.AllowedMultimediaTypes.Any(x => x is "pdf" or "docx" or "xlsx" or "pptx" or "zip" or "csv"));

    public static string Role(IaField f)
    {
        var n = f.XmlName.ToLowerInvariant();
        if (Regex.IsMatch(n, "^(heading|title|headline|fullname|name|tablabel|grouplabel|steplabel|yearlabel|seriesname)$")) return "heading";
        if (n.Contains("eyebrow") || n.Contains("badge") || n.Contains("kicker")) return "eyebrow";
        if (n.Contains("subheading") || n.Contains("subtitle") || n.Contains("sublabel") || n == "jobtitle" || n == "attributionrole") return "subheading";
        if (n.Contains("quote")) return "quote";
        if (n.Contains("caption")) return "caption";
        if (n == "value") return "value";
        if (n == "label") return "label";
        return "other";
    }

    private bool Available(IElement e) => !_claimed.Any(c => c == e || c.Contains(e));
    private void Claim(IElement e) => _claimed.Add(e);

    private static IEnumerable<IElement> Descendants(IElement scope) => scope.QuerySelectorAll("*");

    private IElement? First(IElement scope, Func<IElement, bool> test) =>
        Descendants(scope).FirstOrDefault(e => Available(e) && HtmlSections.SkipReason(e) is null or "markup only" && test(e));

    /// <summary>First available element whose class/id/data words contain all words of the field name.</summary>
    private IElement? ByTokens(IElement scope, string xmlName, Func<IElement, bool> test)
    {
        var words = HtmlSections.SplitWords(xmlName).ToList();
        if (words.Count == 0) return null;
        return Descendants(scope).FirstOrDefault(e => Available(e) && test(e) && HtmlSections.OwnTokens(e) is var t && words.All(t.Contains));
    }

    private static bool IsLeaf(IElement e) => e.Children.Length == 0 && e.TextContent.Trim().Length > 0;
    private static bool ShortText(IElement e) { var t = e.TextContent.Trim(); return t.Length is > 0 and <= 160 && e.QuerySelector("p,div,ul,ol,img") is null; }

    private static bool IsMediaElement(IElement e) =>
        e.LocalName is "img" or "video" || e.LocalName == "picture" || e.LocalName == "iframe" && HtmlSections.IsVideoUrl(e.GetAttribute("src"))
        || HtmlSections.HasBackgroundImage(e);

    private static bool IsIcon(IElement e) =>
        HtmlSections.OwnTokens(e).Any(t => t is "icon" or "logo") || (e.GetAttribute("src") ?? "").EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        || int.TryParse(e.GetAttribute("width"), out var w) && w <= 64;

    private static IElement? MediaElement(IElement el) =>
        el.LocalName is "img" or "video" or "iframe" ? el : el.LocalName == "picture" ? el.QuerySelector("img") ?? el
        : HtmlSections.HasBackgroundImage(el) ? el : el.QuerySelector("img,video,iframe");

    public static string? Src(IElement el)
    {
        var m = MediaElement(el) ?? el;
        if (m.LocalName == "video") return m.GetAttribute("src") ?? m.QuerySelector("source")?.GetAttribute("src");
        if (m.LocalName is "img" or "iframe") return m.GetAttribute("src") ?? m.GetAttribute("data-src");
        var style = m.GetAttribute("style") ?? "";
        var match = Regex.Match(style, @"url\(\s*['""]?([^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : m.GetAttribute("href");
    }

    public static bool IsDocumentHref(string? href) => href is not null && Regex.IsMatch(href, @"\.(pdf|docx?|xlsx?|pptx?|zip|csv)(\?|#|$)", RegexOptions.IgnoreCase);

    private static string LinkPreview(IElement a) => $"{Short(a.TextContent, 40)} → {a.GetAttribute("href")}";

    public static string Short(string? text, int max = 100)
    {
        var t = HtmlSections.Collapse(text ?? "");
        return t.Length > max ? t[..(max - 1)] + "…" : t;
    }
}
