using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Modelry.Core.Pages;

/// <summary>Structural facts about one section, used to match it to a schema.</summary>
public sealed class SectionFeatures
{
    public int H1 { get; init; }
    public int Headings { get; init; }
    public int Images { get; init; }
    public int Videos { get; init; }
    public int Links { get; init; }
    public int Paragraphs { get; init; }
    public bool Table { get; init; }
    public bool Form { get; init; }
    public bool Blockquote { get; init; }
    public bool TabList { get; init; }
    public bool Disclosures { get; init; }
    public bool Map { get; init; }
    public bool Chart { get; init; }
    public bool Embed { get; init; }
    /// <summary>Largest group of sibling elements with the same tag and classes (cards, items, slides).</summary>
    public int RepeatedItems { get; init; }
    public int NumberTexts { get; init; }
    public int YearTexts { get; init; }
    public int Dates { get; init; }
    public int TextLength { get; init; }
    /// <summary>Words from class, id, role, aria-label and data-* attributes of the section and its descendants (lower case).</summary>
    public HashSet<string> Tokens { get; init; } = new();
    /// <summary>Words of the section's first heading.</summary>
    public HashSet<string> HeadingWords { get; init; } = new();
}

public sealed class HtmlSection
{
    public int Index { get; init; }
    public required IElement Element { get; init; }
    /// <summary>First heading text, or a description of the element, for display.</summary>
    public required string Label { get; init; }
    public required SectionFeatures Features { get; init; }
    /// <summary>Label of the section this block was split out of, when it was split.</summary>
    public string? SplitFrom { get; init; }
    /// <summary>Value of an explicit data-cms-component / data-component attribute, if the front-end team added one.</summary>
    public string? ExplicitComponent { get; init; }
}

public sealed record IgnoredElement(string Description, string Reason);

/// <summary>Splits a page's HTML into its top-level content sections, leaving out header, footer and navigation.</summary>
public static class HtmlSections
{
    public const string SectionAttribute = "data-modelry-section";

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "template", "link", "meta", "base", "br", "hr" };
    private static readonly HashSet<string> SkipRoles = new(StringComparer.OrdinalIgnoreCase) { "banner", "contentinfo", "navigation" };
    private static readonly string[] SkipTokens = { "breadcrumb", "breadcrumbs", "cookie", "cookies", "consent", "skip", "skiplink", "sr", "visually" };

    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    public static (List<HtmlSection> Sections, List<IgnoredElement> Ignored) Split(IHtmlDocument doc)
    {
        var ignored = new List<IgnoredElement>();
        var container = doc.QuerySelector("main") ?? doc.QuerySelector("[role=main]") ?? (IElement?)doc.Body;
        if (container is null) return (new List<HtmlSection>(), ignored);

        // Step through wrappers: a container whose only meaningful child is another block (div.container > div.page …).
        while (true)
        {
            var kids = Significant(container, ignored, record: false);
            // A carousel is a component in its own right: never step into it (its slides are items, not sections).
            if (kids.Count == 1 && kids[0].LocalName is "div" or "main" or "section" or "article" && !IsCarousel(kids[0]) && OwnText(container).Length == 0
                && !kids[0].HasAttribute("data-cms-component") && !kids[0].HasAttribute("data-component")
                && Significant(kids[0], ignored, record: false).Count > 1)
                container = kids[0];
            else break;
        }

        var sections = new List<HtmlSection>();
        foreach (var el in Significant(container, ignored, record: true)) sections.Add(Create(el, sections.Count));
        return (sections, ignored);
    }

    /// <summary>Wraps an element as a section with the given index (and numbers it for the preview).</summary>
    public static HtmlSection Create(IElement el, int index, string? splitFrom = null)
    {
        el.SetAttribute(SectionAttribute, index.ToString());
        return new HtmlSection
        {
            Index = index, Element = el, Features = Features(el), Label = LabelOf(el), SplitFrom = splitFrom,
            ExplicitComponent = el.GetAttribute("data-cms-component") ?? el.GetAttribute("data-component")
        };
    }

    /// <summary>
    /// Blocks inside a section that could each be a component of their own (side-by-side cards, a widget next to a
    /// download card…): the section's meaningful children, looking through single wrappers, when there are 2–8 of them
    /// and every one has its own heading, or an image with text.
    /// </summary>
    public static List<IElement> SubBlocks(IElement section)
    {
        // A carousel or slider is one component whose slides are its items – never split it.
        if (IsCarousel(section)) return new List<IElement>();
        var none = new List<IgnoredElement>();
        var scope = section;
        var kids = Significant(scope, none, record: false);
        while (kids.Count == 1) { scope = kids[0]; kids = Significant(scope, none, record: false); }
        if (kids.Count is < 2 or > 8) return new List<IElement>();
        bool Standalone(IElement e) =>
            e.QuerySelector("h1,h2,h3,h4,h5,h6") is not null
            || e.QuerySelector("img,picture,video,iframe,canvas") is not null && Collapse(e.TextContent).Length >= 40;
        return kids.All(Standalone) ? kids : new List<IElement>();
    }

    /// <summary>
    /// True when the element itself is a carousel / slider – marked with an ARIA carousel role, data-carousel /
    /// data-slider, or a slider library class – directly or through single wrappers (section > div.inner > …). A section
    /// that merely contains a carousel next to other content (a heading, a tab list) is not one: see <see cref="HasCarousel"/>.
    /// </summary>
    public static bool IsCarousel(IElement e)
    {
        var none = new List<IgnoredElement>();
        for (var x = e; ; )
        {
            if (CarouselMarked(x)) return true;
            var kids = Significant(x, none, record: false);
            if (kids.Count != 1) return false;
            x = kids[0];
        }
    }

    /// <summary>True when the element is or contains carousel / slider markup anywhere.</summary>
    public static bool HasCarousel(IElement e) => CarouselMarked(e) || e.QuerySelectorAll("*").Take(600).Any(CarouselMarked);

    private static readonly string[] CarouselTokens = { "carousel", "slider", "swiper", "slick", "splide", "glide", "flickity" };

    private static bool CarouselMarked(IElement x) =>
        (x.GetAttribute("aria-roledescription") ?? "").Contains("carousel", StringComparison.OrdinalIgnoreCase)
        || x.HasAttribute("data-carousel") || x.HasAttribute("data-slider")
        || OwnTokens(x).Overlaps(CarouselTokens);

    /// <summary>Child-index path from the root element, so the preview can find the same element after re-parsing.</summary>
    public static string PathOf(IElement el)
    {
        var parts = new List<int>();
        for (var e = el; e.ParentElement is { } p; e = p) parts.Add(p.Children.Index(e));
        parts.Reverse();
        return string.Join(".", parts);
    }

    public static IElement? AtPath(IHtmlDocument doc, string path)
    {
        IElement? e = doc.DocumentElement;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (e is null || !int.TryParse(part, out var i) || i < 0 || i >= e.Children.Length) return null;
            e = e.Children[i];
        }
        return e;
    }

    /// <summary>Element children that carry content, recording why the others are left out.</summary>
    private static List<IElement> Significant(IElement parent, List<IgnoredElement> ignored, bool record)
    {
        var list = new List<IElement>();
        foreach (var el in parent.Children)
        {
            var reason = SkipReason(el);
            if (reason is null) { list.Add(el); continue; }
            if (record && reason != "markup only") ignored.Add(new IgnoredElement(Describe(el), reason));
        }
        return list;
    }

    public static string? SkipReason(IElement el)
    {
        if (SkipTags.Contains(el.LocalName)) return "markup only";
        if (el.LocalName is "header") return "Header – rendered by the header include";
        if (el.LocalName is "footer") return "Footer – rendered by the footer include";
        if (el.LocalName is "nav") return "Navigation – rendered from Structure Groups";
        var role = el.GetAttribute("role");
        if (role is not null && SkipRoles.Contains(role)) return $"role=\"{role}\" – rendered by the Page Template";
        // aria-hidden only hides from screen readers: decorative background videos and images still carry content.
        if (el.HasAttribute("hidden") || el.GetAttribute("aria-hidden") == "true" && el.LocalName is not ("video" or "img" or "picture" or "iframe"))
            return "Hidden element";
        var tokens = OwnTokens(el);
        if (SkipTokens.Any(tokens.Contains)) return "Breadcrumb, cookie banner or accessibility helper – not authored content";
        if (el.TextContent.Trim().Length == 0 && el.QuerySelector("img,video,iframe,picture,svg,canvas") is null
            && !(el.GetAttribute("style") ?? "").Contains("url(", StringComparison.OrdinalIgnoreCase)) return "markup only";
        return null;
    }

    public static SectionFeatures Features(IElement el)
    {
        var text = Collapse(el.TextContent);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in el.QuerySelectorAll("*").Prepend(el)) foreach (var t in OwnTokens(e)) tokens.Add(t);
        var heading = el.QuerySelector("h1,h2,h3,h4");
        var leafTexts = el.QuerySelectorAll("*").Where(e => e.Children.Length == 0).Select(e => e.TextContent.Trim()).Where(t => t.Length > 0).ToList();
        return new SectionFeatures
        {
            H1 = el.QuerySelectorAll("h1").Length,
            Headings = el.QuerySelectorAll("h1,h2,h3,h4,h5,h6").Length,
            Images = el.QuerySelectorAll("img,picture").Length + (HasBackgroundImage(el) ? 1 : 0),
            Videos = el.QuerySelectorAll("video").Length + el.QuerySelectorAll("iframe").Count(f => IsVideoUrl(f.GetAttribute("src"))),
            Links = el.QuerySelectorAll("a[href]").Length,
            Paragraphs = el.QuerySelectorAll("p").Length,
            Table = el.QuerySelector("table") is not null,
            Form = el.QuerySelector("form,input,select,textarea") is not null,
            Blockquote = el.QuerySelector("blockquote") is not null,
            TabList = el.QuerySelector("[role=tablist],[role=tab]") is not null,
            Disclosures = el.QuerySelectorAll("details,[aria-expanded]").Length > 0,
            Map = tokens.Contains("map") || el.QuerySelectorAll("iframe").Any(f => (f.GetAttribute("src") ?? "").Contains("map", StringComparison.OrdinalIgnoreCase)),
            Chart = el.QuerySelector("canvas") is not null || tokens.Contains("chart") || tokens.Contains("graph"),
            Embed = el.QuerySelectorAll("iframe").Any(f => !IsVideoUrl(f.GetAttribute("src"))),
            RepeatedItems = RepeatedGroups(el).Select(g => g.Count).DefaultIfEmpty(0).Max(),
            NumberTexts = leafTexts.Count(t => Regex.IsMatch(t, @"^[\p{Sc}+\-~≈<>]?\s?\d[\d.,]*\s?(%|[kKmMbB]n?|\+|bn|mn|million|billion)?$")),
            YearTexts = leafTexts.Count(t => Regex.IsMatch(t, @"^(1[89]|20)\d\d(s)?$")),
            Dates = el.QuerySelectorAll("time").Length + leafTexts.Count(t => Regex.IsMatch(t, @"\b\d{1,2}\s+[A-Z][a-z]{2,8}\s+\d{4}\b|\b[A-Z][a-z]{2,8}\s+\d{1,2},\s+\d{4}\b")),
            TextLength = text.Length,
            Tokens = tokens,
            HeadingWords = heading is null ? new HashSet<string>() : Words(heading.TextContent)
        };
    }

    /// <summary>Groups of 2+ sibling elements with the same tag and class list, largest first.</summary>
    public static List<List<IElement>> RepeatedGroups(IElement scope)
    {
        var groups = new List<List<IElement>>();
        foreach (var parent in scope.QuerySelectorAll("*").Prepend(scope))
        {
            foreach (var g in parent.Children.Where(c => SkipReason(c) is null or "Hidden element")
                         .GroupBy(Signature).Where(g => g.Count() >= 2))
                groups.Add(g.ToList());
        }
        return groups.OrderByDescending(g => g.Count).ThenByDescending(g => g[0].TextContent.Length).ToList();
    }

    /// <summary>
    /// Tag plus sorted classes, ignoring state classes that differ between otherwise identical items: active, selected,
    /// current, next/prev, visible, clone/duplicate… on their own or as a suffix (swiper-slide-active, slick-current,
    /// carousel__item--active, is-open).
    /// </summary>
    public static string Signature(IElement e) =>
        e.LocalName + "." + string.Join(".", e.ClassList.Where(c => !StateClass.IsMatch(c)).OrderBy(c => c, StringComparer.Ordinal));

    private static readonly Regex StateClass = new(
        @"(^|[-_])(is-|has-)?(active|inactive|selected|open|opened|closed|current|first|last|hidden|show|shown|visible|invisible|next|prev|previous|duplicate|cloned?|initiali[sz]ed|animating|in|out|on|off|focus|focused|expanded|collapsed)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsVideoUrl(string? src) =>
        src is not null && Regex.IsMatch(src, "youtube|youtu\\.be|vimeo|\\.mp4|\\.webm", RegexOptions.IgnoreCase);

    public static bool HasBackgroundImage(IElement e) => (e.GetAttribute("style") ?? "").Contains("url(", StringComparison.OrdinalIgnoreCase);

    /// <summary>Words from class, id, role, aria-label and data-* attribute values of one element.</summary>
    public static HashSet<string> OwnTokens(IElement e)
    {
        var raw = new List<string>();
        raw.AddRange(e.ClassList);
        foreach (var a in e.Attributes)
            if (a.Name is "id" or "role" or "aria-label" || a.Name.StartsWith("data-", StringComparison.Ordinal) && a.Name != SectionAttribute)
                raw.Add(a.Value);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in raw) foreach (var w in SplitWords(r)) set.Add(w);
        return set;
    }

    /// <summary>"hero-banner__title", "heroBanner", "Hero Banner" → hero, banner, title (lower case, singular-ish).</summary>
    public static IEnumerable<string> SplitWords(string text)
    {
        var spaced = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2");
        foreach (Match m in Regex.Matches(spaced, "[A-Za-z]+|[0-9]+"))
        {
            var w = m.Value.ToLowerInvariant();
            if (w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss")) w = w[..^1];
            if (w.Length >= 2) yield return w;
        }
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "of", "to", "in", "for", "on", "with", "at", "by", "an", "or", "our", "is", "are", "from", "as", "ct" };

    public static HashSet<string> Words(string text) =>
        SplitWords(text).Where(w => !Stop.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string OwnText(IElement e) =>
        Collapse(string.Concat(e.ChildNodes.Where(n => n.NodeType == NodeType.Text).Select(n => n.TextContent)));

    public static string LabelOf(IElement el)
    {
        var h = el.QuerySelector("h1,h2,h3,h4");
        if (h is not null && Collapse(h.TextContent) is { Length: > 0 } t) return t.Length > 80 ? t[..77] + "…" : t;
        var text = Collapse(el.TextContent);
        if (text.Length > 0) return text.Length > 60 ? text[..57] + "…" : text;
        return Describe(el);
    }

    public static string Describe(IElement el)
    {
        var cls = el.ClassList.Length > 0 ? "." + string.Join(".", el.ClassList.Take(2)) : "";
        var id = el.Id is { Length: > 0 } i ? "#" + i : "";
        return $"<{el.LocalName}{id}{cls}>";
    }
}
