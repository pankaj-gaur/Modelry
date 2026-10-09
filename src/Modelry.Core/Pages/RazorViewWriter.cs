using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Dom;
using Modelry.Core.CodeGen;
using Modelry.Core.Model;

namespace Modelry.Core.Pages;

/// <summary>Everything needed to write the Razor views for one page.</summary>
public sealed class ViewInput
{
    public required IaWorkbook Workbook { get; init; }
    public required ModelPlan Models { get; init; }
    public required IHtmlDocument Document { get; init; }
    public required PageMatch Match { get; init; }
    /// <summary>Field bindings per section index (sections that are matched and not excluded).</summary>
    public required Dictionary<int, SchemaBinding> Bindings { get; init; }
    public required string SourceName { get; init; }
    /// <summary>Views already generated earlier in this session (from other pages): a different sample becomes a variant view.</summary>
    public HashSet<string> ExistingViews { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ViewFileInfo(string Path, string View, string Kind, string Model, string Source);

public sealed class ViewOutput
{
    public List<GeneratedFile> Files { get; } = new();
    public List<ViewFileInfo> Written { get; } = new();
    public List<Issue> Issues { get; } = new();
    /// <summary>Front-end assets referenced from the views through Url.Asset – the theme must contain them under system/assets.</summary>
    public SortedSet<string> StaticAssets { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Links to other pages left as written in the markup (not mapped to a field).</summary>
    public SortedSet<string> StaticLinks { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>DXA resources used by the views: key → English value.</summary>
    public SortedDictionary<string, string> Resources { get; } = new(StringComparer.Ordinal);
    /// <summary>Per view: fields placed where the HTML showed them, fields placed by default, fields not rendered.</summary>
    public List<(string View, List<string> FromHtml, List<string> Default, List<string> NotRendered)> FieldReport { get; } = new();
}

/// <summary>
/// Writes DXA 2.x Razor views (ASP.NET MVC 5, C# 7.3) from the matched sections of a page and the generated models.
/// <list type="bullet">
/// <item>Each Component Template's view keeps the section's markup and CSS classes; bound text, images and links become
/// model properties with XPM markup; fields the HTML did not show are placed at the end, guarded by <c>if</c>.</item>
/// <item>Repeated items become one loop over the first sample of each item shape. Differences between items become rules:
/// the first item's own attributes and heading level, counters ("2 of 4", "02 / 04", data-index="1"), values taken from the
/// item, and a video instead of an image. Markup that mirrors the items (thumbnail strips, tab buttons) becomes a second loop
/// over the same list.</item>
/// <item>Linked components (slides, cards, list items) get their own partial view, called with their index and count.</item>
/// <item>Fixed UI text becomes a DXA resource; front-end assets go through <c>Url.Asset</c> (theme path); images keep the
/// front-end's <c>&lt;picture&gt;</c>/<c>srcset</c> with URLs from DXA's image resizing (<c>Html.Srcset</c>).</item>
/// <item>A shared <c>_Layout</c> holds the head, header and footer; each Page Template view places the regions.</item>
/// </list>
/// </summary>
public sealed class RazorViewWriter
{
    private readonly ViewInput _in;
    private readonly ViewOutput _out = new();
    private readonly List<string> _snippets = new();
    private const string Mark = "⟦"; // ⟦
    private const string EndMark = "⟧"; // ⟧
    private readonly string _area;
    private readonly string _helpersNs;
    private readonly Dictionary<string, string> _resourceKeyByText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writtenPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileCtx _file = new();

    private sealed class FileCtx
    {
        public int Loops;
        public bool UsesHelpers;
        public string View = "";
        public string NextLoop() { Loops++; return Loops == 1 ? "" : Loops.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>Where field markup is being written: XPM on fields (top of a view), loop index and count expressions.</summary>
    private sealed record Ctx(bool Top, string? Idx, string? Count, Dictionary<IaField, FieldBinding>? Alternates = null);

    private RazorViewWriter(ViewInput input)
    {
        _in = input;
        _area = input.Models.Options.DefaultArea ?? "Site";
        var root = input.Models.Options.RootNamespace;
        _helpersNs = (root.EndsWith(".Models", StringComparison.Ordinal) ? root[..^7] : root) + ".Helpers";
    }

    public static ViewOutput Write(ViewInput input) => new RazorViewWriter(input).Run();

    private ViewOutput Run()
    {
        foreach (var sm in _in.Match.Sections.Where(s => !s.Excluded && s.TemplateTitle is not null))
        {
            try { WriteEntityView(sm); }
            catch (Exception ex) { Warn(sm, $"The view could not be generated ({ex.Message})."); }
        }
        WritePageLayoutAndRegions();
        foreach (var v in _varying)
        {
            var parts = v.Split('\u0001');
            _out.Issues.Add(new Issue(IssueLevel.Info, "Views", 0, $"{_area}:{parts[0]}", $"{parts[1]} differs from item to item and is not one of the item's fields; the first item's value is kept. Map it to a field, or let the front-end script set it."));
        }
        WriteHelpers();
        WriteResources();
        return _out;
    }

    // ================================================================ entity views

    private void WriteEntityView(SectionMatch sm)
    {
        var template = _in.Workbook.Templates.FirstOrDefault(t => IaWorkbook.Same(t.Title, sm.TemplateTitle));
        if (template?.View is null) { Warn(sm, $"Component Template '{sm.TemplateTitle}' has no DXA View in the workbook – no view written."); return; }
        var (vArea, view) = ModelPlanner.SplitView(template.View, _area);
        if (!_in.Bindings.TryGetValue(sm.Section.Index, out var binding)) return;
        var cls = ClassFor(binding.Schema.Title);
        if (cls is null) { Warn(sm, $"Schema '{binding.Schema.Title}' has no generated model class, so no view was written."); return; }

        // A view seen before (earlier on this page or on another page) with different markup becomes a variant view.
        var key = $"{vArea}:{view}";
        var seenHere = _bodies.ContainsKey(key);
        var seenBefore = _in.ExistingViews.Contains(key);
        var viewName = seenHere || seenBefore ? $"{view}_{Compact(_in.Match.Page.PageId)}S{sm.Section.Index + 1}" : view;

        _file = new FileCtx { View = viewName };
        var root = sm.Section.Element;
        root.RemoveAttribute(HtmlSections.SectionAttribute);
        var rendered = new HashSet<IaField>();
        var pending = new List<PendingItemView>();
        Emit(binding, "Model", root, new Ctx(true, null, null), rendered, root, pending, vArea);
        AddAttr(root, "@Html.DxaEntityMarkup()");
        var defaults = AppendUnseen(cls, "Model", root, rendered, top: true);
        Finish(root);
        var body = Render(root);

        var normalized = Regex.Replace(body, @"\s+", " ");
        if (seenHere && _bodies[key] == normalized) { Info(sm, $"Same markup as the earlier section that uses view {key}; not written again."); return; }
        if (!seenHere && !seenBefore) _bodies[key] = normalized;

        var header = Usings(body) + $"@model {_in.Models.Options.RootNamespace}.{cls.Name}\n" +
                     $"@*\n    View {vArea}:{viewName} for Component Template \"{template.Title}\" (schema \"{binding.Schema.Title}\").\n" +
                     $"    Generated by Modelry from {_in.SourceName}, section {sm.Section.Index + 1} (\"{Comment(sm.Section.Label)}\"){(sm.Row is null ? "" : ", " + sm.Row.MapId)}, {DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}.\n" +
                     (viewName != view ? $"    VARIANT of {vArea}:{view}: this section's markup differs from the one that view was made from. Register a Component Template for it, or merge the two.\n" : "") +
                     "*@\n";
        AddFile($"Areas/{vArea}/Views/Entity/{viewName}.cshtml", header + body, $"{vArea}:{viewName}", "Entity", cls.Name,
            $"Section {sm.Section.Index + 1} – {sm.Section.Label}");
        if (viewName != view)
            Info(sm, $"View {key} already exists from {(seenHere ? "an earlier section on this page" : "another page")} with different markup – written as the variant {vArea}:{viewName}.");
        Report($"{vArea}:{viewName}", cls, rendered, defaults);

        foreach (var p in pending) WriteItemView(p);
        foreach (var f in binding.MissingMandatory)
            Info(sm, $"Mandatory field '{f.XmlName}' was not in this section's HTML{(defaults.Contains(f.XmlName) ? "; the view shows it at the end of the component" : "")}.");
    }

    private sealed record PendingItemView(string Path, string View, ModelClass Cls, IElement Holder, HashSet<IaField> Rendered, string Source);

    private void WriteItemView(PendingItemView p)
    {
        var saved = _file;
        _file = new FileCtx { View = p.View };
        foreach (var root in p.Holder.Children) AddAttr(root, "@Html.DxaEntityMarkup()");
        // A "Read more" link in a linked component without a link field of its own goes to the component's page.
        foreach (var a in p.Holder.QuerySelectorAll("a[href]").Where(a => !(a.GetAttribute("href") ?? "").Contains(Mark)).ToList())
        {
            a.SetAttribute("href", Snip("@Url.ItemUrl(Model)"));
            _file.UsesHelpers = true;
        }
        var target = p.Holder.Children.LastOrDefault() ?? p.Holder;
        var defaults = AppendUnseen(p.Cls, "Model", target, p.Rendered, top: true);
        Finish(p.Holder);
        var body = RenderInner(p.Holder);
        var header = Usings(body) + $"@model {_in.Models.Options.RootNamespace}.{p.Cls.Name}\n" +
                     $"@*\n    Item view for {p.Cls.Name} inside {p.Source}. Generated by Modelry from {_in.SourceName}.\n" +
                     "    Rendered by the parent view with @Html.Partial(...), which passes the item's position as ItemIndex and ItemCount.\n*@\n" +
                     "@{\n    var itemIndex = ViewData[\"ItemIndex\"] as int? ?? 0;\n    var itemCount = ViewData[\"ItemCount\"] as int? ?? 1;\n}\n";
        AddFile(p.Path, header + body, p.View, "Item", p.Cls.Name, p.Source);
        Report(p.View, p.Cls, p.Rendered, defaults);
        _file = saved;
    }

    // ================================================================ fields

    private void Emit(SchemaBinding b, string o, IElement scope, Ctx c, HashSet<IaField> rendered, IElement entityRoot, List<PendingItemView> pending, string area)
    {
        var cls = ClassFor(b.Schema.Title);
        if (cls is null) return;
        foreach (var fb in b.Fields)
        {
            var prop = Prop(cls, fb.Field);
            if (prop is null || fb.Element is null || !Attached(fb.Element, scope)) continue;
            var x = $"{o}.{prop.Name}";
            var xpm = c.Top ? $"@Html.DxaPropertyMarkup(() => {x})" : null;
            var optional = !fb.Field.Mandatory;
            switch (fb.Kind)
            {
                case BindKind.Text when !prop.IsList:
                    SetText(fb.Element, Snip($"@{x}"));
                    if (xpm is not null) AddAttr(fb.Element, xpm);
                    if (optional) Wrap(fb.Element, NotEmpty(prop, x));
                    break;
                case BindKind.Number:
                    SetText(fb.Element, Snip($"@{x}"));
                    if (xpm is not null) AddAttr(fb.Element, xpm);
                    break;
                case BindKind.Date when !prop.IsList:
                {
                    var fmt = DateFormat(fb.Element.TextContent);
                    SetText(fb.Element, Snip($"@{x}.Value.ToString(\"{fmt}\", System.Globalization.CultureInfo.InvariantCulture)"));
                    if (fb.Element.LocalName == "time") fb.Element.SetAttribute("datetime", Snip($"@{x}.Value.ToString(\"yyyy-MM-dd\")"));
                    if (xpm is not null) AddAttr(fb.Element, xpm);
                    Wrap(fb.Element, $"{x}.HasValue");
                    break;
                }
                case BindKind.RichText:
                    if (fb.Container)
                    {
                        while (fb.Element.FirstChild is not null) fb.Element.RemoveChild(fb.Element.FirstChild);
                        fb.Element.TextContent = Snip($"@Html.DxaRichText({x})");
                        if (xpm is not null) AddAttr(fb.Element, xpm);
                        if (optional) Wrap(fb.Element, $"{x} != null");
                    }
                    else
                    {
                        var div = fb.Element.Owner!.CreateElement("div");
                        if (fb.Elements.Count == 1 && fb.Element.GetAttribute("class") is { Length: > 0 } cssClass) div.SetAttribute("class", cssClass);
                        div.TextContent = Snip($"@Html.DxaRichText({x})");
                        if (xpm is not null) AddAttr(div, xpm);
                        fb.Element.Before(div);
                        foreach (var e in fb.Elements) e.Remove();
                        if (optional) Wrap(div, $"{x} != null");
                    }
                    break;
                case BindKind.LinkHref when fb.Field.Type == IaFieldType.ExternalLink:
                    fb.Element.SetAttribute("href", Snip($"@{x}"));
                    if (optional) Wrap(fb.Element, $"!string.IsNullOrEmpty({x})");
                    break;
                case BindKind.LinkHref:
                    fb.Element.SetAttribute("href", Snip($"@{x}.Url"));
                    if (xpm is not null) AddAttr(fb.Element, xpm);
                    Wrap(fb.Element, $"{x} != null");
                    break;
                case BindKind.ImageSrc:
                    SetImage(fb.Element, new MediaExpr(x, null, AltOf(prop, x), null, null));
                    if (xpm is not null) AddAttr(fb.Element, xpm);
                    Wrap(fb.Element, $"{x} != null");
                    break;
                case BindKind.Embedded when fb.Embedded is not null:
                {
                    if (c.Alternates is not null && c.Alternates.TryGetValue(fb.Field, out var alt) && alt.Element is not null && alt.Embedded is not null)
                    {
                        // The same media field shows a video in some items and an image in others: both, chosen at runtime.
                        EmitEmbedded(fb, x, xpm, prop);
                        EmitEmbedded(alt, x, xpm, prop);
                        alt.Element.Remove();
                        fb.Element.Before(alt.Element);
                        Wrap2(alt.Element, MediaCondition(alt.Embedded, x, prop), fb.Element, MediaCondition(fb.Embedded, x, prop));
                    }
                    else
                    {
                        EmitEmbedded(fb, x, xpm, prop);
                        if (optional || prop.Kind == ModelValueKind.Model) Wrap(fb.Element, $"{x} != null");
                    }
                    break;
                }
                case BindKind.Items when fb.Items.Count > 0:
                    EmitItems(fb, prop, x, c, entityRoot, pending, area);
                    break;
                default:
                    continue;
            }
            rendered.Add(fb.Field);
        }
    }

    private void EmitEmbedded(FieldBinding fb, string x, string? xpm, ModelProperty prop)
    {
        var e = fb.Embedded!;
        var cls = ClassFor(e.Schema.Title);
        if (cls is null) return;
        var el = fb.Element!;
        if (el.LocalName == "a" && e.Fields.Any(f => f.Kind is BindKind.LinkHref or BindKind.Text))
        {
            EmitLink(e, cls, x, el);
            if (xpm is not null) AddAttr(el, xpm);
            return;
        }
        var image = e.Fields.FirstOrDefault(f => f.Kind == BindKind.ImageSrc);
        var video = e.Fields.FirstOrDefault(f => f.Kind == BindKind.VideoSrc);
        var embed = e.Fields.FirstOrDefault(f => f.Kind == BindKind.EmbedSrc);
        if (image is not null || video is not null || embed is not null)
        {
            var m = MediaOf(cls, x);
            if (image is not null) SetImage(image.Element!, m);
            if (video is not null) SetVideo(video.Element!, m);
            if (embed is not null && cls.Properties.FirstOrDefault(p => p.Field == embed.Field) is { } ep)
                embed.Element!.SetAttribute("src", Snip($"@{x}.{ep.Name}"));
            if (xpm is not null) AddAttr(el, xpm);
            return;
        }
        Emit(e, x, el, new Ctx(false, null, null), new HashSet<IaField>(), el, new List<PendingItemView>(), _area);
    }

    private void EmitLink(SchemaBinding e, ModelClass cls, string x, IElement a)
    {
        var href = LinkHref(cls, x);
        if (href is not null) a.SetAttribute("href", Snip($"@({href})"));
        var text = e.Fields.FirstOrDefault(f => f.Kind == BindKind.Text);
        if (text is not null && cls.Properties.FirstOrDefault(p => p.Field == text.Field) is { } tp) SetText(a, Snip($"@{x}.{tp.Name}"));
        var title = e.Fields.FirstOrDefault(f => f.Kind == BindKind.LinkTitle);
        if (title is not null && cls.Properties.FirstOrDefault(p => p.Field == title.Field) is { } tt)
            a.SetAttribute(a.HasAttribute("aria-label") ? "aria-label" : "title", Snip($"@{x}.{tt.Name}"));
        var newWindow = cls.Properties.FirstOrDefault(p => p.BooleanHelperName is not null && p.Field.XmlName.Contains("window", StringComparison.OrdinalIgnoreCase));
        if (newWindow is not null) a.SetAttribute("target", Snip($"@({x}.{newWindow.BooleanHelperName} ? \"_blank\" : null)"));
    }

    private static string? LinkHref(ModelClass cls, string x)
    {
        var internalProp = cls.Properties.FirstOrDefault(p => p.Kind == ModelValueKind.Link && !p.IsList)?.Name;
        var externalProp = cls.Properties.FirstOrDefault(p => p.Field.Type == IaFieldType.ExternalLink && !p.IsList)?.Name;
        return internalProp is not null && externalProp is not null ? $"{x}.{internalProp} != null ? {x}.{internalProp}.Url : {x}.{externalProp}"
            : internalProp is not null ? $"{x}.{internalProp}?.Url" : externalProp is not null ? $"{x}.{externalProp}" : null;
    }

    // ================================================================ repeated items

    private void EmitItems(FieldBinding fb, ModelProperty prop, string list, Ctx parent, IElement entityRoot, List<PendingItemView> pending, string area)
    {
        var itemCls = ClassFor(fb.ItemSchema ?? fb.Items[0].Schema.Title);
        if (itemCls is null) return;
        var units = RepeatUnits(fb.Elements);
        if (units.Count != fb.Items.Count) units = fb.Elements.ToList();
        var n = Math.Min(units.Count, fb.Items.Count);
        var linked = prop.Field.Type == IaFieldType.ComponentLink;
        var suffix = _file.NextLoop();
        var idx = linked ? "itemIndex" : "i" + suffix;
        var v = linked ? "Model" : "item" + suffix;
        var count = linked ? "itemCount" : $"{list}.Count";

        // The items' values, read before any of them is turned into Razor (for counters and mirrored markup).
        var raw = Enumerable.Range(0, n).Select(k => ItemValues(fb.Items[k], itemCls, VarMark)).ToList();
        List<List<(string Value, string Expr)>> With(string var) => raw.Select(l => l.Select(x => (x.Value, x.Expr.Replace(VarMark, var))).ToList()).ToList();

        // Items with the same shape (the same fields present) share one template; other shapes get their own branch.
        var shapes = Enumerable.Range(0, n).GroupBy(k => Shape(fb.Items[k])).Select(g => g.ToList()).ToList();
        var templates = new List<(IElement El, string? Condition)>();
        var rendered = new HashSet<IaField>();
        foreach (var (shape, si) in shapes.Select((s, i) => (s, i)))
        {
            var k0 = shape[0];
            var rep = units[k0];
            var others = shape.Skip(1).Select(k => (Unit: units[k], Item: fb.Items[k], K: k)).ToList();
            var rules = Rules(rep, k0, others.Select(o => (o.Unit, o.K)).ToList(), With(v), fb.Items, itemCls, v, Bound(fb.Items[k0]));
            var alternates = Alternates(fb.Items[k0], others.Select(o => o.Item));
            if (rep.LocalName == "a" && fb.Items[k0].Fields.Any(f => f.Kind is BindKind.LinkHref or BindKind.Text) && fb.Items[k0].Fields.All(f => f.Element == rep))
            {
                // A list of links (CTAs): the anchor itself is the item.
                EmitLink(fb.Items[k0], itemCls, v, rep);
                foreach (var f in fb.Items[k0].Fields) rendered.Add(f.Field);
            }
            else Emit(fb.Items[k0], v, rep, new Ctx(linked, idx, count, alternates), rendered, rep, pending, area);
            Apply(rules, idx, count, k0);
            if (!linked && parent.Top && list.StartsWith("Model.", StringComparison.Ordinal) && list.Count(ch => ch == '.') == 1)
                AddAttr(rep, $"@Html.DxaPropertyMarkup(() => {list}, {idx})");
            var condition = si == shapes.Count - 1 && shapes.Count > 1 ? "" : shapes.Count == 1 ? null : ShapeCondition(fb.Items[k0], shapes.Where(s => s != shape).SelectMany(s => s).Select(k => fb.Items[k]), itemCls, v);
            templates.Add((rep, condition));
        }
        var all = units.Take(n).ToList();
        foreach (var u in units.Except(templates.Select(t => t.El))) u.Remove();

        // Markup that mirrors the items elsewhere in the component: thumbnails, tab buttons, dots.
        DerivedLoops(entityRoot, fb, itemCls, list, rendered, templates.Select(t => t.El).ToList(), raw, prop);

        var first = templates[0].El;
        var anchor = first.Owner!.CreateComment("anchor");
        first.Before(anchor);
        if (linked)
        {
            var itemView = $"{_file.View}/{itemCls.Name}";
            var path = $"Areas/{area}/Views/Entity/{itemView}.cshtml";
            var holder = first.Owner!.CreateElement("mdlry-root");
            foreach (var (el, cond) in templates)
            {
                holder.AppendChild(el);
                if (cond is not null) Branch(el, cond, templates);
            }
            var call = $"@Html.Partial(\"~/{path}\", {list}[i{suffix}], new ViewDataDictionary {{ {{ \"ItemIndex\", i{suffix} }}, {{ \"ItemCount\", {list}.Count }} }})";
            // A link to several schemas is a list of EntityModel: render the items of this view's type.
            if (prop.Kind == ModelValueKind.AnyEntity)
                call = $"if ({list}[i{suffix}] is {Ns}.{itemCls.Name})\n        {{\n            {call}\n        }}";
            anchor.Replace(first.Owner!.CreateComment(Snip(
                $"@if ({list} != null)\n{{\n    for (var i{suffix} = 0; i{suffix} < {list}.Count; i{suffix}++)\n    {{\n        {call}\n    }}\n}}")));
            pending.Add(new PendingItemView(path, $"{area}:{itemView}", itemCls, holder, rendered, $"{_file.View} ({prop.Name})"));
        }
        else
        {
            IChildNode? after = null;
            foreach (var (el, cond) in templates)
            {
                if (after is not null) after.After(el);
                after = cond is not null ? Branch(el, cond, templates) : el;
            }
            anchor.Replace(first.Owner!.CreateComment(Snip($"@if ({list} != null)\n{{\n    for (var {idx} = 0; {idx} < {list}.Count; {idx}++)\n    {{\n        {ItemVar(v, list, idx, itemCls, prop)}")));
            after!.After(first.Owner!.CreateComment(Snip("    }\n}")));
        }
    }

    /// <summary>if / else if / else around the item templates of the different item shapes.</summary>
    private IChildNode Branch(IElement el, string condition, List<(IElement El, string? Condition)> templates)
    {
        var i = templates.FindIndex(t => t.El == el);
        var opener = i == 0 ? $"@if ({condition})\n{{" : condition.Length == 0 ? "else\n{" : $"else if ({condition})\n{{";
        el.Before(el.Owner!.CreateComment(Snip(opener)));
        var close = el.Owner!.CreateComment(Snip("}"));
        el.After(close);
        return close;
    }

    private static string Shape(SchemaBinding b) => string.Join(",", b.Fields.Where(f => f.Element is not null).Select(f => f.Field.XmlName).OrderBy(s => s, StringComparer.Ordinal));
    private static HashSet<IElement> Bound(SchemaBinding b) => b.All().SelectMany(f => f.Elements.Append(f.Element)).Where(e => e is not null).Select(e => e!).ToHashSet();

    private string ShapeCondition(SchemaBinding shape, IEnumerable<SchemaBinding> others, ModelClass cls, string v)
    {
        var otherFields = others.SelectMany(o => o.Fields).Select(f => f.Field.XmlName).ToHashSet();
        var own = shape.Fields.Where(f => f.Element is not null && !otherFields.Contains(f.Field.XmlName)).Select(f => Prop(cls, f.Field)).Where(p => p is not null).ToList();
        if (own.Count == 0) return "true";
        return string.Join(" || ", own.Take(2).Select(p => NotEmpty(p!, $"{v}.{p!.Name}")));
    }

    /// <summary>Embedded media that is a video in another item where this one is an image (or the other way round).</summary>
    private static Dictionary<IaField, FieldBinding>? Alternates(SchemaBinding rep, IEnumerable<SchemaBinding> others)
    {
        Dictionary<IaField, FieldBinding>? result = null;
        foreach (var fb in rep.Fields.Where(f => f.Kind == BindKind.Embedded && f.Embedded is not null))
        {
            var kinds = fb.Embedded!.Fields.Select(f => f.Kind).ToHashSet();
            var alt = others.SelectMany(o => o.Fields).FirstOrDefault(o => o.Field.XmlName == fb.Field.XmlName && o.Kind == BindKind.Embedded && o.Embedded is not null && o.Element is not null
                && o.Embedded.Fields.Any(f => f.Kind is BindKind.VideoSrc or BindKind.ImageSrc && !kinds.Contains(f.Kind)));
            if (alt is not null) (result ??= new())[fb.Field] = alt;
        }
        return result;
    }

    // ---------------------------------------------------------------- differences between items

    private abstract record Rule(IElement El);
    private sealed record AttrRule(IElement El, string Name, string Razor) : Rule(El);
    private sealed record TextRule(INode Node, IElement El, string Razor) : Rule(El);
    private sealed record TagRule(IElement El, string OtherTag) : Rule(El);

    /// <summary>
    /// Compares the template item with the other items of its shape, element by element (aligned by tag and classes), and
    /// turns each difference into a rule: an attribute only the first item has, a counter, a value taken from the item, a
    /// different heading level for the first item.
    /// </summary>
    private List<Rule> Rules(IElement rep, int k0, List<(IElement Unit, int K)> others, List<List<(string Value, string Expr)>> values,
                             List<SchemaBinding> items, ModelClass cls, string v, HashSet<IElement> bound)
    {
        var rules = new List<Rule>();
        if (others.Count == 0) return rules;
        var video = VideoExprOf(cls, v);
        var hasVideo = Enumerable.Range(0, items.Count).Select(k => items[k].All().Any(f => f.Kind == BindKind.VideoSrc)).ToList();

        void Walk(IElement a, List<(IElement El, int K)> bs)
        {
            var isBound = bound.Contains(a);
            // Attributes (also on bound elements: an id that differs per item must stay unique).
            var names = a.Attributes.Select(x => x.Name).Concat(bs.SelectMany(b => b.El.Attributes.Select(x => x.Name))).Distinct()
                .Where(nm => nm != HtmlSections.SectionAttribute && !nm.StartsWith("data-mdlry", StringComparison.Ordinal))
                // Image and video sources are set from the item's media, not by rules.
                .Where(nm => !(a.LocalName is "img" or "source" or "video" or "picture" && nm is "src" or "srcset" or "sizes" or "poster" or "alt" or "data-src" or "width" or "height" or "type"))
                .ToList();
            foreach (var name in names)
            {
                var mine = a.GetAttribute(name);
                var theirs = bs.Select(b => (b.K, Value: b.El.GetAttribute(name))).ToList();
                if (mine is not null && theirs.All(t => t.Value == mine)) continue;
                if (isBound && !IsReference(name, mine ?? "")) continue;   // the field sets the bound element's own attributes
                if (mine is not null && theirs.All(t => t.Value is not null))
                {
                    var samples = theirs.Select(t => (t.K, t.Value!)).Prepend((k0, mine)).ToList();
                    if (Synthesize(samples, values, "IDX", "COUNT") is { } razor) rules.Add(new AttrRule(a, name, razor));
                    else if (IsReference(name, mine) && KeyPart(samples) is { } key) rules.Add(new AttrRule(a, name, key));
                    else _varying.Add($"{_file.View}\u0001{name}=\"{FieldBinder.Short(mine, 60)}\"");
                    continue;
                }
                if (isBound && !IsReference(name, mine ?? "")) continue;
                if (mine is not null && theirs.All(t => t.Value is null) && k0 == 0)
                {
                    rules.Add(new AttrRule(a, name, $"@(IDX == 0 ? {Lit(mine)} : null)"));
                    continue;
                }
                if (mine is null && video is not null)
                {
                    var with = theirs.Where(t => t.Value is not null).Select(t => t.K).ToHashSet();
                    var all = theirs.Select(t => t.K).Append(k0).ToList();
                    if (with.Count > 0 && all.All(k => with.Contains(k) == hasVideo[k]))
                        rules.Add(new AttrRule(a, name, $"@({video} != null ? {Lit(theirs.First(t => t.Value is not null).Value!)} : null)"));
                }
            }
            if (isBound && a.LocalName != "a") return;
            // Own text (not a bound element; a bound link's own text is its link text).
            var myText = isBound ? new List<INode>() : OwnTextNodes(a);
            if (myText.Count == 1)
            {
                var samples = new List<(int K, string Value)> { (k0, Collapse(myText[0].TextContent)) };
                foreach (var b in bs) { var t = OwnTextNodes(b.El); if (t.Count != 1) { samples.Clear(); break; } samples.Add((b.K, Collapse(t[0].TextContent))); }
                if (samples.Count > 1 && samples.Select(s => s.Value).Distinct().Count() > 1)
                {
                    if (Synthesize(samples, values, "IDX", "COUNT") is { } razor) rules.Add(new TextRule(myText[0], a, razor));
                    else
                    {
                        // Differs per item but is not one of the item's fields: keep the sample, flag it (not a fixed resource).
                        rules.Add(new TextRule(myText[0], a, $"@* Differs per item ({string.Join(" / ", samples.Select(s => s.Value).Distinct().Take(3).Select(x => Comment(FieldBinder.Short(x, 40))))}) – map it to a field. *@{samples[0].Value.Replace("@", "@@")}"));
                        _varying.Add($"{_file.View}\u0001text \"{FieldBinder.Short(samples[0].Value, 60)}\"");
                    }
                }
            }
            // Children, aligned by signature; a heading at the same place with another level is a tag rule.
            var pairs = bs.Select(b => (b.K, Map: Align(a, b.El))).ToList();
            foreach (var child in a.Children)
            {
                var counterparts = pairs.Select(p => (El: p.Map.TryGetValue(child, out var c) ? c : null, p.K)).Where(c => c.El is not null).ToList();
                if (counterparts.Count == 0) continue;
                var others2 = counterparts.Select(c => (c.El!, c.K)).ToList();
                if (others2.All(c => c.Item1.LocalName == child.LocalName)) Walk(child, others2);
                else if (Regex.IsMatch(child.LocalName, "^h[1-6]$") && others2.Select(c => c.Item1.LocalName).Distinct().Count() == 1 && k0 == 0)
                    rules.Add(new TagRule(child, others2[0].Item1.LocalName));
            }
        }
        Walk(rep, others.Select(o => (o.Unit, o.K)).ToList());
        return rules;
    }

    private readonly SortedSet<string> _varying = new(StringComparer.Ordinal);

    /// <summary>id-like attributes (id, aria-controls, href="#…", data-…-id): they must stay unique and keep pointing at each other.</summary>
    private static bool IsReference(string name, string value) =>
        name is "id" or "for" or "aria-controls" or "aria-labelledby" or "aria-describedby" or "aria-owns" or "name"
        || name == "href" && value.StartsWith('#') || name.StartsWith("data-", StringComparison.Ordinal) && Regex.IsMatch(value, "^[a-z][a-z0-9-]*$") && !Regex.IsMatch(name, "area|col|row|size|style|dist|near|state");

    /// <summary>"industry-tab-consumer", "industry-tab-building" … → "industry-tab-@(IDX + 1)" (the varying word becomes the item number).</summary>
    private static string? KeyPart(List<(int K, string Value)> samples)
    {
        var vals = samples.Select(s => s.Value).ToList();
        if (vals.Distinct().Count() != vals.Count) return null;
        var prefix = new string(vals[0].TakeWhile((ch, i) => vals.All(v => v.Length > i && v[i] == ch)).ToArray());
        var rest = vals.Select(v => v[prefix.Length..]).ToList();
        var suffix = new string(rest[0].Reverse().TakeWhile((ch, i) => rest.All(v => v.Length > i && v[v.Length - 1 - i] == ch)).Reverse().ToArray());
        if (rest.Any(r => r.Length <= suffix.Length)) return null;
        return $"{prefix.Replace("@", "@@")}@(IDX + 1){suffix.Replace("@", "@@")}";
    }

    private void Apply(List<Rule> rules, string idx, string count, int k0)
    {
        string R(string razor) => razor.Replace("IDX", idx).Replace("COUNT", count);
        foreach (var r in rules)
        {
            if (!Attached(r.El, null)) continue;
            switch (r)
            {
                case AttrRule a when !(a.El.GetAttribute(a.Name) ?? "").Contains(Mark):
                    a.El.SetAttribute(a.Name, Snip(R(a.Razor)));
                    break;
                case TextRule t when !t.Node.TextContent.Contains(Mark):
                    t.Node.TextContent = Snip(R(t.Razor));
                    break;
                case TagRule g:
                {
                    var clone = Retag(g.El, g.OtherTag);
                    g.El.After(clone);
                    Wrap2(g.El, $"{idx} == {k0}", clone, "");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The same value in every item, expressed with the item's own values and counters: "Slide 2 of 4: Chemistry that
    /// matters" → string.Format(Html.Resource(…), IDX + 1, COUNT, item.Heading). Null when the samples have no common pattern.
    /// </summary>
    private string? Synthesize(List<(int K, string Value)> samples, List<List<(string Value, string Expr)>> values, string idx, string count)
    {
        const char Open = '\u0001', Close = '\u0002';
        var n = values.Count;
        List<string> Tokens(int k, string s)
        {
            foreach (var (val, expr) in values[k].OrderByDescending(x => x.Value.Length))
                if (val.Length >= 3 && s.Contains(val, StringComparison.Ordinal)) s = s.Replace(val, $"{Open}{expr}{Close}");
            return Regex.Split(s, $"(\\d+|{Open}[^{Close}]*{Close})").Where(t => t.Length > 0).ToList();
        }
        var toks = samples.Select(s => Tokens(s.K, s.Value)).ToList();
        if (toks.Select(t => t.Count).Distinct().Count() != 1) return null;
        var parts = new List<(bool Code, string Text)>();
        for (var j = 0; j < toks[0].Count; j++)
        {
            var col = toks.Select(t => t[j]).ToList();
            if (col[0][0] == Open)
            {
                if (col.Distinct().Count() != 1) return null;
                parts.Add((true, col[0][1..^1]));
            }
            else if (Regex.IsMatch(col[0], @"^\d+$") && col.All(c => Regex.IsMatch(c, @"^\d+$")))
            {
                var ks = samples.Select(s => s.K).ToList();
                bool All(Func<string, int, bool> f) => col.Select((c, i) => f(c, ks[i])).All(x => x);
                if (col.Distinct().Count() == 1 && col[0] == n.ToString(CultureInfo.InvariantCulture) && n >= 2) parts.Add((true, count));
                else if (col.Distinct().Count() == 1 && col[0] == n.ToString("00", CultureInfo.InvariantCulture) && n >= 2) parts.Add((true, $"{count}.ToString(\"00\")"));
                else if (col.Distinct().Count() == 1) parts.Add((false, col[0]));
                else if (All((c, k) => c == (k + 1).ToString(CultureInfo.InvariantCulture))) parts.Add((true, $"({idx} + 1)"));
                else if (All((c, k) => c == (k + 1).ToString("00", CultureInfo.InvariantCulture))) parts.Add((true, $"({idx} + 1).ToString(\"00\")"));
                else if (All((c, k) => c == k.ToString(CultureInfo.InvariantCulture))) parts.Add((true, idx));
                else if (All((c, _) => c == n.ToString(CultureInfo.InvariantCulture))) parts.Add((true, count));
                else if (All((c, _) => c == n.ToString("00", CultureInfo.InvariantCulture))) parts.Add((true, $"{count}.ToString(\"00\")"));
                else return null;
            }
            else
            {
                if (col.Distinct().Count() != 1) return null;
                parts.Add((false, col[0]));
            }
        }
        if (!parts.Any(p => p.Code)) return null;
        return Compose(parts);
    }

    /// <summary>Text with code parts: a resource with placeholders when the fixed text has words, else plain Razor.</summary>
    private string Compose(List<(bool Code, string Text)> parts)
    {
        var fixedText = string.Concat(parts.Where(p => !p.Code).Select(p => p.Text));
        if (Regex.IsMatch(fixedText, @"\p{L}{2,}"))
        {
            var args = new List<string>();
            var format = new StringBuilder();
            foreach (var (code, text) in parts)
                if (code) { format.Append('{').Append(args.Count).Append('}'); args.Add(text); }
                else format.Append(text.Replace("{", "{{").Replace("}", "}}"));
            var key = ResourceKey(format.ToString());
            return $"@string.Format(Html.Resource(\"{key}\"), {string.Join(", ", args)})";
        }
        if (parts.Count == 1) return $"@({parts[0].Text})";
        return "@(" + string.Join(" + ", parts.Select(p => p.Code ? $"({p.Text})" : Lit(p.Text))) + ")";
    }

    /// <summary>Text values of an item, with the expression that gives them (for counters and mirrored markup).</summary>
    private List<(string Value, string Expr)> ItemValues(SchemaBinding b, ModelClass cls, string v)
    {
        var list = new List<(string, string)>();
        foreach (var fb in b.Fields)
        {
            var p = Prop(cls, fb.Field);
            if (p is null || fb.Element is null || p.IsList) continue;
            if (fb.Kind == BindKind.Text && p.Kind == ModelValueKind.Text) list.Add((Collapse(fb.Element.TextContent), $"{v}.{p.Name}"));
            else if (fb.Kind == BindKind.Embedded && fb.Embedded is not null && ClassFor(fb.Embedded.Schema.Title) is { } ec)
                foreach (var ef in fb.Embedded.Fields)
                    if (ef.Element is not null && ec.Properties.FirstOrDefault(q => q.Field == ef.Field) is { Kind: ModelValueKind.Text, IsList: false } q)
                    {
                        var val = ef.Kind == BindKind.ImageAlt ? ef.Element.GetAttribute("alt") : ef.Kind == BindKind.LinkTitle ? ef.Preview : ef.Kind == BindKind.Text ? Collapse(ef.Element.TextContent) : null;
                        if (!string.IsNullOrWhiteSpace(val)) list.Add((val!, $"{v}.{p.Name}.{q.Name}"));
                    }
        }
        return list;
    }

    // ---------------------------------------------------------------- mirrored markup

    /// <summary>
    /// Other repeated markup in the component with one element per item – a thumbnail strip, tab buttons, dots – becomes a
    /// second loop over the same list. Texts equal to an item's value use that value (or a field named after the markup, e.g.
    /// thumbnailLabel for a thumbnail), images the item's image (or e.g. thumbnailImage), counters the loop index.
    /// </summary>
    private const string VarMark = "\u00A7";
    private string Ns => _in.Models.Options.RootNamespace;

    /// <summary>The loop's item variable; items of a link to several schemas are cast, and others skipped.</summary>
    private string ItemVar(string v, string list, string idx, ModelClass cls, ModelProperty? prop) =>
        prop?.Kind == ModelValueKind.AnyEntity
            ? $"var {v} = {list}[{idx}] as {Ns}.{cls.Name};\n        if ({v} == null) {{ continue; }}"
            : $"var {v} = {list}[{idx}];";

    private void DerivedLoops(IElement entityRoot, FieldBinding fb, ModelClass itemCls, string list, HashSet<IaField> rendered, List<IElement> templates,
                              List<List<(string Value, string Expr)>> raw, ModelProperty? listProp = null)
    {
        var n = fb.Items.Count;
        if (n < 2) return;
        var groups = HtmlSections.RepeatedGroups(entityRoot)
            .Where(g => g.Count == n && !g.Any(e => templates.Any(t => t == e || t.Contains(e) || e.Contains(t)))
                        && !g.Any(e => e.OuterHtml.Contains(Mark)))
            .ToList();
        // Outermost groups only (a group inside another group's item is handled with it).
        groups = groups.Where(g => !groups.Any(o => o != g && o.Any(e => e != g[0] && e.Contains(g[0])))).ToList();
        foreach (var g in groups)
        {
            var suffix = _file.NextLoop();
            var idx = "i" + suffix; var v = "item" + suffix;
            // Words naming this markup: the item element and the elements leading to its image ("c-hero__thumb").
            var path = new List<IElement> { g[0] };
            for (var m = g[0].QuerySelector("img,picture,video"); m is not null && m != g[0]; m = m.ParentElement) path.Add(m);
            var tokens = path.SelectMany(HtmlSections.OwnTokens).Where(t => t.Length >= 4).ToHashSet();
            bool Named(ModelProperty p) => HtmlSections.SplitWords(p.Field.XmlName).Any(w => w.Length >= 4 && tokens.Any(t => w.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(w, StringComparison.Ordinal)));
            var namedText = itemCls.Properties.FirstOrDefault(p => p.Kind == ModelValueKind.Text && !p.IsList && Named(p) && !rendered.Contains(p.Field));
            var values = Enumerable.Range(0, n).Select(k =>
            {
                var vals = raw[k].Select(x => (x.Value, Expr: x.Expr.Replace(VarMark, v))).ToList();
                if (namedText is not null)
                    vals = vals.Select(x => x.Expr == $"{v}.{itemCls.Properties.FirstOrDefault(p => Role(p) == "heading")?.Name}" ? (x.Value, $"({v}.{namedText.Name} ?? {x.Expr})") : x).ToList();
                return vals;
            }).ToList();
            var rep = g[0];
            var others = g.Skip(1).Select((e, i) => (e, i + 1)).ToList();
            var rules = Rules(rep, 0, others, values, fb.Items, itemCls, v, new HashSet<IElement>());
            if (!rules.Any() && rep.QuerySelector("img,picture,video") is null) continue;
            if (namedText is not null && rules.OfType<TextRule>().Any(r => r.Razor.Contains(namedText.Name)) || rules.OfType<AttrRule>().Any(r => namedText is not null && r.Razor.Contains(namedText.Name)))
                rendered.Add(namedText!.Field);

            // Media: the item's image, preferring a field named after this markup (thumbnailImage for a thumbnail strip).
            var namedMedia = itemCls.Properties.FirstOrDefault(p => p.Kind is ModelValueKind.Model or ModelValueKind.AnyMedia && !p.IsList && Named(p) && MediaOf(p, v) is { Image: not null });
            var mainMedia = itemCls.Properties.Where(p => p.Kind is ModelValueKind.Model or ModelValueKind.AnyMedia && !p.IsList).Select(p => (P: p, M: MediaOf(p, v))).FirstOrDefault(x => x.M is { Image: not null } && x.P != namedMedia);
            var media = namedMedia is not null ? MediaOf(namedMedia, v) : mainMedia.M;
            if (namedMedia is not null && mainMedia.M is { } mm && media is not null) media = media with { Fallback = mm };
            if (namedMedia is not null) rendered.Add(namedMedia.Field);
            var video = VideoExprOf(itemCls, v);
            var videoEl = g.Select(e => e.QuerySelector("video")).FirstOrDefault(e => e is not null);
            var imageEl = rep.QuerySelector("picture") ?? rep.QuerySelector("img");
            Apply(rules, idx, $"{list}.Count", 0);
            if (media is not null && imageEl is not null)
            {
                SetImage(imageEl, media);
                if (videoEl is not null && video is not null && !rep.Contains(videoEl))
                {
                    // Some items show a video in this markup: same choice as in the item itself.
                    videoEl.Remove();
                    imageEl.Before(videoEl);
                    SetVideo(videoEl, itemCls.Properties.Where(p => !p.IsList && p.Kind == ModelValueKind.Model).Select(p => MediaOf(p, v)).FirstOrDefault(x => x?.Video is not null) ?? media);
                    Wrap2(videoEl, $"{video} != null", imageEl, "");
                }
            }
            foreach (var e in g.Skip(1)) e.Remove();
            rep.Before(rep.Owner!.CreateComment(Snip($"@if ({list} != null)\n{{\n    for (var {idx} = 0; {idx} < {list}.Count; {idx}++)\n    {{\n        {ItemVar(v, list, idx, itemCls, listProp)}")));
            rep.After(rep.Owner!.CreateComment(Snip("    }\n}")));
        }

        // Status text outside the loops ("Slide 1 of 4: Earnings…"): count and first item's values.
        var first0 = listProp?.Kind == ModelValueKind.AnyEntity ? $"(({Ns}.{itemCls.Name}){list}[0])" : $"{list}[0]";
        var firstValues = raw[0].Select(x => (x.Value, x.Expr.Replace(VarMark, first0))).ToList();
        foreach (var node in entityRoot.QuerySelectorAll("*").Where(e => !templates.Any(t => t.Contains(e))).SelectMany(OwnTextNodes).ToList())
        {
            var text = Collapse(node.TextContent);
            if (text.Contains(Mark) || !firstValues.Any(fv => fv.Value.Length >= 3 && text.Contains(fv.Value, StringComparison.Ordinal))) continue;
            if (Synthesize(new List<(int, string)> { (0, text) }, new List<List<(string Value, string Expr)>> { firstValues }.Concat(Enumerable.Repeat(new List<(string Value, string Expr)>(), n - 1)).ToList(), "0", $"{list}.Count") is { } razor)
                node.TextContent = Snip($"@if ({list} != null && {list}.Count > 0)\n{{\n    <text>{razor}</text>\n}}");
        }
    }

    // ================================================================ fields the HTML did not show

    private List<string> AppendUnseen(ModelClass cls, string o, IElement target, HashSet<IaField> rendered, bool top)
    {
        var placed = new List<string>();
        var lines = new List<string>();
        foreach (var p in cls.Properties.Where(p => p.Field.Section == IaFieldSection.Content && !rendered.Contains(p.Field)
                                                    && p.BooleanHelperName is null && p.Field.ListType is null && p.Field.Category is null))
        {
            var x = $"{o}.{p.Name}";
            var xpm = top ? $" @Html.DxaPropertyMarkup(() => {x})" : "";
            var css = $"class=\"{Kebab(p.Name)}\"";
            string? markup = p.Kind switch
            {
                ModelValueKind.Text when !p.IsList => $"@if (!string.IsNullOrEmpty({x}))\n{{\n    <p {css}{xpm}>@{x}</p>\n}}",
                ModelValueKind.RichText when !p.IsList => $"@if ({x} != null)\n{{\n    <div {css}{xpm}>@Html.DxaRichText({x})</div>\n}}",
                ModelValueKind.Date when !p.IsList => $"@if ({x}.HasValue)\n{{\n    <time {css} datetime=\"@{x}.Value.ToString(\"yyyy-MM-dd\")\"{xpm}>@{x}.Value.ToString(\"d MMMM yyyy\")</time>\n}}",
                ModelValueKind.Link when !p.IsList => $"@if ({x} != null)\n{{\n    <a {css} href=\"@{x}.Url\"{xpm}>@{x}.Url</a>\n}}",
                ModelValueKind.Model or ModelValueKind.AnyMedia when !p.IsList && MediaOf(p, o) is { Image: not null } m => ImageMarkup(p, m, css, xpm),
                ModelValueKind.Model when !p.IsList && ClassFor(p) is { } lc && LinkHref(lc, x) is { } href && lc.Properties.FirstOrDefault(q => q.Kind == ModelValueKind.Text && Regex.IsMatch(q.Field.XmlName, "text|label", RegexOptions.IgnoreCase)) is { } lt
                    => $"@if ({x} != null)\n{{\n    <a {css} href=\"@({href})\"{xpm}>@{x}.{lt.Name}</a>\n}}",
                ModelValueKind.Model when p.IsList && ClassFor(p) is { } lc2 && LinkHref(lc2, "link") is { } href2 && lc2.Properties.FirstOrDefault(q => q.Kind == ModelValueKind.Text && Regex.IsMatch(q.Field.XmlName, "text|label", RegexOptions.IgnoreCase)) is { } lt2
                    => $"@if ({x} != null)\n{{\n    foreach (var link in {x})\n    {{\n        <a {css} href=\"@({href2})\">@link.{lt2.Name}</a>\n    }}\n}}",
                _ => null
            };
            if (markup is null) continue;
            lines.Add(markup);
            placed.Add(p.Field.XmlName);
            rendered.Add(p.Field);
        }
        if (lines.Count > 0)
        {
            target.AppendChild(target.Owner!.CreateComment(Snip("@* Not in the sample HTML – placed here by default. Move each field where the design needs it, or remove it. *@")));
            foreach (var l in lines) target.AppendChild(target.Owner!.CreateComment(Snip(l)));
        }
        return placed;
    }

    private string ImageMarkup(ModelProperty p, MediaExpr m, string css, string xpm)
    {
        _file.UsesHelpers = true;
        return $"@if ({m.Image} != null)\n{{\n    <img {css} src=\"@({m.Image}.Url)\" srcset=\"@Html.Srcset({m.Image}, 768, 1280, 1920)\" sizes=\"100vw\" alt=\"@({m.Alt ?? "\"\""})\" loading=\"lazy\"{xpm}>\n}}";
    }

    // ================================================================ media

    /// <summary>Expressions for an image/video field: the media item, a URL fallback (DAM / external), alt text, video.</summary>
    private sealed record MediaExpr(string? Image, string? UrlFallback, string? Alt, string? Video, string? VideoUrl)
    {
        public MediaExpr? Fallback { get; init; }
    }

    private MediaExpr? MediaOf(ModelProperty p, string o)
    {
        var x = $"{o}.{p.Name}";
        if (p.Field.Type == IaFieldType.MultimediaLink) return new MediaExpr(x, null, null, null, null);
        return ClassFor(p) is { } c ? MediaOf(c, x) : null;
    }

    private MediaExpr? MediaOf(ModelClass c, string x)
    {
        ModelProperty? P(Func<ModelProperty, bool> f) => c.Properties.FirstOrDefault(q => !q.IsList && f(q));
        var image = P(q => q.Field.Type == IaFieldType.MultimediaLink && !Regex.IsMatch(q.Field.XmlName, "video|mobile|poster|icon|document|file", RegexOptions.IgnoreCase));
        var url = P(q => q.Field.Type == IaFieldType.ExternalLink && Regex.IsMatch(q.Field.XmlName, "image", RegexOptions.IgnoreCase));
        var alt = P(q => q.Kind == ModelValueKind.Text && Regex.IsMatch(q.Field.XmlName, "alt", RegexOptions.IgnoreCase));
        var video = P(q => q.Field.Type == IaFieldType.MultimediaLink && Regex.IsMatch(q.Field.XmlName, "video", RegexOptions.IgnoreCase));
        var videoUrl = P(q => q.Field.Type == IaFieldType.ExternalLink && Regex.IsMatch(q.Field.XmlName, "video", RegexOptions.IgnoreCase));
        if (image is null && video is null) return null;
        string? E(ModelProperty? q) => q is null ? null : $"{x}?.{q.Name}";
        return new MediaExpr(E(image), E(url), E(alt), E(video), E(videoUrl));
    }

    private string? VideoExprOf(ModelClass cls, string v)
    {
        foreach (var p in cls.Properties.Where(p => !p.IsList && p.Kind is ModelValueKind.Model))
            if (MediaOf(p, v) is { Video: { } video }) return video;
        return null;
    }

    private string MediaCondition(SchemaBinding emb, string x, ModelProperty prop)
    {
        var m = ClassFor(prop) is { } c ? MediaOf(c, x) : null;
        if (emb.Fields.Any(f => f.Kind == BindKind.VideoSrc) && m?.Video is { } vid)
            return m.VideoUrl is null ? $"{x} != null && {vid} != null" : $"{x} != null && ({vid} != null || !string.IsNullOrEmpty({m.VideoUrl}))";
        if (m?.Image is { } img)
            return m.UrlFallback is null ? $"{x} != null && {img} != null" : $"{x} != null && ({img} != null || !string.IsNullOrEmpty({m.UrlFallback}))";
        return $"{x} != null";
    }

    /// <summary>
    /// Image from the model, keeping the front-end's markup: &lt;picture&gt; and srcset/sizes stay, with the candidate URLs
    /// produced by DXA's image resizing for the widths the front-end listed; format-specific &lt;source type=…&gt; are dropped
    /// (the CMS serves the uploaded format).
    /// </summary>
    private void SetImage(IElement el, MediaExpr m)
    {
        var img = el.LocalName == "picture" ? el.QuerySelector("img") ?? el : el.LocalName is "img" ? el : el.QuerySelector("img") ?? el;
        var picture = img.ParentElement?.LocalName == "picture" ? img.ParentElement : null;
        var widths = new SortedSet<int>();
        foreach (var s in (picture?.QuerySelectorAll("source").ToList() ?? new List<IElement>()).Append(img))
            foreach (Match w in Regex.Matches(s.GetAttribute("srcset") ?? "", @"\s(\d+)w\b")) widths.Add(int.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture));
        if (picture is not null)
            foreach (var s in picture.QuerySelectorAll("source").ToList())
            {
                if (s.HasAttribute("type") || s.GetAttribute("srcset") is not { } set || !Regex.IsMatch(set, @"\d+w")) { s.Remove(); continue; }
                s.SetAttribute("srcset", Snip($"@Html.Srcset({m.Image}, {string.Join(", ", widths)})"));
                _file.UsesHelpers = true;
            }
        var image = m.Image;
        var url = image is null ? m.UrlFallback ?? "\"\"" : m.UrlFallback is null ? $"{image}?.Url" : $"{image} != null ? {image}.Url : {m.UrlFallback}";
        if (m.Fallback is { } f2 && f2.Image is not null) url = $"{(image is null ? "null" : $"{image}?.Url")} ?? {f2.Image}?.Url";
        if (img.LocalName == "img")
        {
            img.SetAttribute("src", Snip($"@({url})"));
            img.RemoveAttribute("data-src");
            if (widths.Count > 0 && image is not null)
            {
                img.SetAttribute("srcset", Snip($"@Html.Srcset({image}, {string.Join(", ", widths)})"));
                _file.UsesHelpers = true;
            }
            else img.RemoveAttribute("srcset");
            if (m.Alt is not null) img.SetAttribute("alt", Snip($"@({m.Alt})"));
        }
        else
        {
            var style = img.GetAttribute("style") ?? "";
            img.SetAttribute("style", Regex.Replace(style, @"url\([^)]*\)", Snip($"url('@({url})')")));
        }
    }

    private void SetVideo(IElement el, MediaExpr m)
    {
        var src = m.Video is null ? m.VideoUrl ?? "\"\"" : m.VideoUrl is null ? $"{m.Video}?.Url" : $"{m.Video} != null ? {m.Video}.Url : {m.VideoUrl}";
        var start = Regex.Match(el.GetAttribute("src") ?? "", @"#t=[\d.]+$").Value;
        el.SetAttribute("src", Snip($"@({src}){start}"));
        foreach (var s in el.QuerySelectorAll("source").ToList()) s.Remove();
        if (el.HasAttribute("poster") && m.Image is not null) el.SetAttribute("poster", Snip($"@({m.Image}?.Url)"));
    }

    private string? AltOf(ModelProperty prop, string x)
    {
        var target = ClassFor(prop);
        var alt = target?.Properties.FirstOrDefault(p => p.Field.XmlName.Contains("alt", StringComparison.OrdinalIgnoreCase) && p.Kind == ModelValueKind.Text);
        return alt is null ? null : $"{x}.{alt.Name}";
    }

    // ================================================================ resources and assets

    /// <summary>After the fields: fixed UI text becomes DXA resources, front-end asset paths go through Url.Asset.</summary>
    private void Finish(IElement root)
    {
        foreach (var e in root.QuerySelectorAll("*").Prepend(root).ToList())
        {
            if (e.LocalName is "script" or "style" or "code" or "pre" or "svg") continue;
            foreach (var t in OwnTextNodes(e))
            {
                var text = Collapse(t.TextContent);
                if (text.Contains(Mark) || !Regex.IsMatch(text, @"\p{L}{2,}")) continue;
                // Data shown by a widget (chart labels "9 AM", "Mar 24, 2026", "1D") is not UI text.
                if (Regex.IsMatch(text, @"\d") && Regex.Matches(text, @"\p{L}+").Count <= 4) continue;
                t.TextContent = Snip($"@Html.Resource(\"{ResourceKey(text)}\")");
            }
            foreach (var name in new[] { "aria-label", "title", "alt", "placeholder", "aria-roledescription" })
                if (e.GetAttribute(name) is { } val && !val.Contains(Mark) && Regex.IsMatch(val, @"\p{L}{2,}"))
                    e.SetAttribute(name, Snip($"@Html.Resource(\"{ResourceKey(Collapse(val))}\")"));
            foreach (var name in new[] { "src", "data-src", "poster", "srcset", "href" })
            {
                if (e.GetAttribute(name) is not { } val || val.Contains(Mark)) continue;
                if (name == "href" && e.LocalName != "link")
                {
                    if (IsLocal(val) && !val.StartsWith('#')) _out.StaticLinks.Add(val);
                    continue;
                }
                if (name == "srcset")
                {
                    var parts = val.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                    if (parts.All(p => IsLocal(p.Split(' ')[0])))
                    {
                        var razor = string.Join(", ", parts.Select(p => { var bits = p.Split(' ', 2); _out.StaticAssets.Add(bits[0]); return $"@Url.Asset(\"{bits[0]}\"){(bits.Length > 1 ? " " + bits[1] : "")}"; }));
                        e.SetAttribute(name, Snip(razor));
                        _file.UsesHelpers = true;
                    }
                    continue;
                }
                if (!IsLocal(val)) continue;
                var (path, frag) = SplitFragment(val);
                _out.StaticAssets.Add(path);
                e.SetAttribute(name, Snip($"@Url.Asset(\"{path}\"){frag}"));
                _file.UsesHelpers = true;
            }
            if (e.GetAttribute("style") is { } style && !style.Contains(Mark) && Regex.IsMatch(style, @"url\(\s*['""]?(?!data:|https?:|//)[^'"")]+"))
            {
                e.SetAttribute("style", Snip(Regex.Replace(style.Replace("@", "@@"), @"url\(\s*['""]?([^'"")]+)['""]?\s*\)", mm =>
                {
                    var u = mm.Groups[1].Value;
                    if (!IsLocal(u)) return mm.Value;
                    _out.StaticAssets.Add(u);
                    return $"url('@Url.Asset(\"{u}\")')";
                })));
                _file.UsesHelpers = true;
            }
        }
    }

    private static bool IsLocal(string url) => url.Length > 0 && !Regex.IsMatch(url, @"^([a-z][a-z0-9+.-]*:|//|@)", RegexOptions.IgnoreCase) && !url.StartsWith('#');

    private static (string Path, string Fragment) SplitFragment(string url)
    {
        var i = url.IndexOfAny(new[] { '#', '?' });
        return i < 0 ? (url, "") : (url[..i], url[i..]);
    }

    private string ResourceKey(string text)
    {
        if (_resourceKeyByText.TryGetValue(text, out var existing)) return existing;
        var words = Regex.Matches(Regex.Replace(text, @"\{\d+\}", " n "), @"[\p{L}\p{N}]*\p{L}[\p{L}\p{N}]*").Select(m => m.Value).Take(5).ToList();
        var camel = words.Count == 0 ? "text" : words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        if (char.IsDigit(camel[0])) camel = "n" + camel;
        var key = $"{_area.ToLowerInvariant()}.{camel}";
        for (var i = 2; _out.Resources.ContainsKey(key); i++) key = $"{_area.ToLowerInvariant()}.{camel}{i}";
        _out.Resources[key] = text;
        _resourceKeyByText[text] = key;
        return key;
    }

    // ================================================================ page, layout and region views

    private void WritePageLayoutAndRegions()
    {
        var page = _in.Match.Page;
        var pt = _in.Workbook.Templates.FirstOrDefault(t => t.Kind == IaTemplateKind.PageTemplate && IaWorkbook.Same(t.Title, page.PageTemplate));
        if (pt?.View is null)
        {
            _out.Issues.Add(new Issue(IssueLevel.Warning, "Views", 0, page.PageTemplate ?? page.PageId, "The page's Page Template has no DXA View in the workbook, so no page view was written."));
            return;
        }
        var (pArea, pView) = ModelPlanner.SplitView(pt.View, _area);
        var doc = _in.Document;
        _file = new FileCtx { View = pView };

        // Regions where the page's sections were.
        var regionsDone = new List<string>();
        foreach (var sm in _in.Match.Sections)
        {
            var el = sm.Section.Element;
            if (el.ParentElement is null) continue;
            if (sm.Excluded || sm.Region is null) { Replace(el, $"@* Section {sm.Section.Index + 1} (\"{Comment(sm.Section.Label)}\") is not a component – removed. *@"); continue; }
            if (!regionsDone.Contains(sm.Region, StringComparer.OrdinalIgnoreCase)) { regionsDone.Add(sm.Region); Replace(el, $"@Html.DxaRegion(\"{sm.Region}\")"); }
            else el.Remove();
        }
        foreach (var nav in doc.QuerySelectorAll("main nav, main [role=navigation], [class*=breadcrumb]").ToList())
            if (nav.ParentElement is not null)
                Replace(nav, "@* Navigation (breadcrumb / section tabs) is rendered by the Page Template from Structure Groups – add your DXA navigation call here. *@");
        var nested = _in.Workbook.Regions.Where(r => r.RowType == IaRegionRowType.NestedRegion && IaWorkbook.Same(r.RegionSchemaTitle, page.PageSchema) && r.NestedRegionName is not null)
            .Select(r => r.NestedRegionName!).Where(n => !regionsDone.Contains(n, StringComparer.OrdinalIgnoreCase) && n is not ("Header" or "Footer")).Distinct().ToList();
        foreach (var e in doc.QuerySelectorAll($"[{HtmlSections.SectionAttribute}]")) e.RemoveAttribute(HtmlSections.SectionAttribute);

        var main = doc.QuerySelector("main") ?? doc.QuerySelector("[role=main]");
        if (main is not null && nested.Count > 0)
            main.AppendChild(doc.CreateComment(Snip(string.Join("\n", nested.Select(n => $"@Html.DxaRegion(\"{n}\") @* Not used on {page.PageId}; check its position against a page that uses it. *@")))));

        var layoutPath = $"Areas/{pArea}/Views/Shared/_Layout.cshtml";
        var pageModel = _in.Models.Registrations.FirstOrDefault(r => IaWorkbook.Same(r.View, pView) && IaWorkbook.Same(r.Area, pArea) && r.Controller is null)?.ModelType ?? "PageModel";
        var modelType = pageModel == "PageModel" ? "Sdl.Web.Common.Models.PageModel" : $"{_in.Models.Options.RootNamespace}.{pageModel}";

        // Page view: the content area with its regions, inside the shared layout.
        string pageBody;
        if (main is not null)
        {
            Finish(main);
            pageBody = Render(main);
            main.Replace(doc.CreateComment(Snip("@RenderBody()")));
        }
        else pageBody = string.Join("\n", regionsDone.Select(r => $"@Html.DxaRegion(\"{r}\")")) + "\n";
        var pageHeader = Usings(pageBody) + $"@model {modelType}\n@{{\n    Layout = \"~/{layoutPath}\";\n}}\n" +
                         $"@*\n    Page view {pArea}:{pView} for Page Template \"{pt.Title}\". Generated by Modelry from {_in.SourceName} ({page.Label}).\n" +
                         "    Regions are placed where the page's first section of each region was. Head, header and footer are in the shared _Layout.\n*@\n";
        var pageKey = $"{pArea}:{pView}";
        if (_in.ExistingViews.Contains(pageKey))
            _out.Issues.Add(new Issue(IssueLevel.Info, "Views", 0, pt.Title, $"Page view {pageKey} was already generated earlier in this session; not written again."));
        else AddFile($"Areas/{pArea}/Views/Page/{pView}.cshtml", pageHeader + pageBody, pageKey, "Page", pageModel, page.Label);

        // Shared layout: head (title, styles, scripts), header and footer include regions, the page view in between.
        _file = new FileCtx { View = "_Layout" };
        foreach (var h in doc.QuerySelectorAll("body header, body [role=banner]").ToList())
            if (h.ParentElement is not null && (main is null || !main.Contains(h))) Replace(h, "@Html.DxaRegion(\"Header\")");
        foreach (var f in doc.QuerySelectorAll("body footer, body [role=contentinfo]").ToList())
            if (f.ParentElement is not null && (main is null || !main.Contains(f))) Replace(f, "@Html.DxaRegion(\"Footer\")");
        if (doc.QuerySelector("title") is { } title) title.TextContent = Snip("@Model.Title");
        // Page metadata (description, Open Graph, Twitter) comes from the page model, as in DXA's own layouts.
        var metas = doc.QuerySelectorAll("head meta[name=description], head meta[property], head meta[name]").Where(m => Regex.IsMatch(m.GetAttribute("name") ?? m.GetAttribute("property") ?? "", "^(description|keywords|og:|twitter:)")).ToList();
        if (metas.Count > 0)
        {
            metas[0].Before(doc.CreateComment(Snip("@foreach (var meta in Model.Meta)\n{\n    if (meta.Key.StartsWith(\"og:\"))\n    {\n        <meta property=\"@meta.Key\" content=\"@meta.Value\">\n    }\n    else\n    {\n        <meta name=\"@meta.Key\" content=\"@meta.Value\">\n    }\n}")));
            foreach (var m in metas) m.Remove();
        }
        if (doc.QuerySelector("head link[rel=canonical]") is { } canonical) canonical.SetAttribute("href", Snip("@Request.Url.GetLeftPart(UriPartial.Path)"));
        if (doc.DocumentElement is { } html && html.HasAttribute("lang")) html.SetAttribute("lang", Snip("@WebRequestContext.Localization.Language"));
        Finish(doc.DocumentElement!);
        if (_in.ExistingViews.Contains("Layout:" + pArea))
            _out.Issues.Add(new Issue(IssueLevel.Info, "Views", 0, "_Layout", "The shared layout was already generated earlier in this session; not written again."));
        else
        {
            var layoutBody = "<!DOCTYPE html>\n" + Render(doc.DocumentElement!);
            var layout = Usings(layoutBody) + "@using Sdl.Web.Mvc.Configuration\n@model Sdl.Web.Common.Models.PageModel\n" +
                         $"@*\n    Shared layout for the {pArea} Page Template views. Generated by Modelry from {_in.SourceName}.\n" +
                         "    Header and Footer are the include-page regions; each Page Template view renders its regions in @RenderBody().\n*@\n" +
                         layoutBody;
            AddFile(layoutPath, layout, "Layout:" + pArea, "Layout", "PageModel", page.Label);
        }

        // Region views: one generic view per region used on the page (display: contents keeps the page's CSS selectors working).
        foreach (var region in regionsDone)
        {
            var rv = _in.Workbook.RegionViews.FirstOrDefault(r => IaWorkbook.Same(ModelPlanner.SplitView(r.View, _area).View, region));
            var (rArea, rView) = rv is null ? (_area, region) : ModelPlanner.SplitView(rv.View, _area);
            if (_in.ExistingViews.Contains($"Region:{rArea}:{rView}")) continue;
            AddFile($"Areas/{rArea}/Views/Region/{rView}.cshtml",
                "@model Sdl.Web.Common.Models.RegionModel\n" +
                $"@* Region view {rArea}:{rView}. Generated by Modelry. display:contents keeps the front-end's CSS (e.g. main > section) working while XPM still gets a region element. *@\n" +
                "<div class=\"@Model.HtmlClasses\" style=\"display:contents\" @Html.DxaRegionMarkup()>\n    @Html.DxaEntities()\n</div>\n",
                $"{rArea}:{rView}", "Region", "RegionModel", page.Label);
        }
    }

    // ================================================================ helpers and resources files

    private void WriteHelpers()
    {
        if (!_out.Files.Any(f => f.Content.Contains("Html.Srcset(", StringComparison.Ordinal) || f.Content.Contains("Url.Asset(", StringComparison.Ordinal) || f.Content.Contains("Url.ItemUrl(", StringComparison.Ordinal))) return;
        var code = $@"using System.Globalization;
using System.Linq;
using System.Web.Mvc;
using Sdl.Web.Common.Configuration;
using Sdl.Web.Common.Models;
using Sdl.Web.Mvc.Configuration;

namespace {_helpersNs}
{{
    /// <summary>Helpers used by the views Modelry generated. Put this file in the DXA module next to the models.</summary>
    public static class ViewHelpers
    {{
        /// <summary>
        /// srcset for the widths the front-end uses, with URLs from DXA's image resizing (e.g. /media/…_w768_n.jpg).
        /// VERIFY: IMediaHelper.GetResponsiveImageUrl(url, aspect, widthFactor, containerSize) in your DXA version.
        /// </summary>
        public static string Srcset(this HtmlHelper html, MediaItem image, params int[] widths)
        {{
            if (image == null || string.IsNullOrEmpty(image.Url)) return null;
            return string.Join("", "", widths.Select(w =>
                SiteConfiguration.MediaHelper.GetResponsiveImageUrl(image.Url, 0, w.ToString(CultureInfo.InvariantCulture)) + "" "" + w.ToString(CultureInfo.InvariantCulture) + ""w""));
        }}

        /// <summary>
        /// The URL of the page that shows a linked component (e.g. the Read more link of an article card), resolved by DXA.
        /// VERIFY: ILinkResolver.ResolveLink and the format of EntityModel.Id in your DXA version.
        /// </summary>
        public static string ItemUrl(this UrlHelper url, EntityModel entity)
        {{
            if (entity == null || string.IsNullOrEmpty(entity.Id)) return ""#"";
            var uri = entity.Id.StartsWith(""tcm:"") ? entity.Id : ""tcm:0-"" + entity.Id.Split('-')[0];
            return SiteConfiguration.LinkResolver.ResolveLink(uri) ?? ""#"";
        }}

        /// <summary>
        /// A front-end asset (css, js, fonts, icons, decorative images) in the HTML design published by DXA: {{localization path}}/system/assets/{{path}}.
        /// VERIFY: WebRequestContext.Localization.Path in your DXA version.
        /// </summary>
        public static string Asset(this UrlHelper url, string path)
        {{
            return url.Content(""~"" + WebRequestContext.Localization.Path + ""/system/assets/"" + path.TrimStart('/'));
        }}
    }}
}}
";
        AddFile("Helpers/ViewHelpers.cs", code, "ViewHelpers", "Helper", "", "");
    }

    private void WriteResources()
    {
        if (_out.Resources.Count == 0) return;
        var sb = new StringBuilder("Key,Value\n");
        foreach (var (k, v) in _out.Resources) sb.Append(Csv(k)).Append(',').Append(Csv(v)).Append('\n');
        AddFile("Resources/resources.csv", sb.ToString(), "Resources", "Resources", "", "");
        static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ================================================================ DOM and Razor plumbing

    private void AddFile(string path, string content, string view, string kind, string model, string source)
    {
        if (!_writtenPaths.Add(path)) return;
        _out.Files.Add(new GeneratedFile(path, content));
        _out.Written.Add(new ViewFileInfo(path, view, kind, model, source));
    }

    private void Report(string view, ModelClass cls, HashSet<IaField> rendered, List<string> defaults)
    {
        var fromHtml = cls.Properties.Where(p => rendered.Contains(p.Field) && !defaults.Contains(p.Field.XmlName)).Select(p => p.Field.XmlName).ToList();
        var not = cls.Properties.Where(p => !rendered.Contains(p.Field)).Select(p => p.Field.XmlName).ToList();
        _out.FieldReport.Add((view, fromHtml, defaults, not));
    }

    /// <summary>The helpers namespace, when the view calls one of the generated helpers.</summary>
    private string Usings(string body) => Regex.IsMatch(body, @"Html\.Srcset\(|Url\.Asset\(|Url\.ItemUrl\(") ? $"@using {_helpersNs}\n" : "";

    private static bool Attached(IElement el, IElement? scope) => scope is null ? el.Parent is not null : scope == el || scope.Contains(el);

    private static IElement Retag(IElement el, string tag)
    {
        var copy = el.Owner!.CreateElement(tag);
        foreach (var a in el.Attributes) copy.SetAttribute(a.Name, a.Value);
        var clone = (IElement)el.Clone(true);
        while (clone.FirstChild is { } c) copy.AppendChild(c);
        return copy;
    }

    /// <summary>Children of a aligned with children of b by tag and classes (longest common subsequence).</summary>
    private static Dictionary<IElement, IElement> Align(IElement a, IElement b)
    {
        var x = a.Children.ToList(); var y = b.Children.ToList();
        var key = (IElement e) => HtmlSections.Signature(e);
        var dp = new int[x.Count + 1, y.Count + 1];
        for (var i = x.Count - 1; i >= 0; i--)
            for (var j = y.Count - 1; j >= 0; j--)
                dp[i, j] = key(x[i]) == key(y[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var map = new Dictionary<IElement, IElement>();
        int p = 0, q = 0;
        while (p < x.Count && q < y.Count)
        {
            if (key(x[p]) == key(y[q])) { map[x[p]] = y[q]; p++; q++; }
            else if (dp[p + 1, q] >= dp[p, q + 1]) p++;
            else q++;
        }
        // Headings at the same place with another level (h1 in the first slide, h2 in the others).
        foreach (var h in x.Where(e => !map.ContainsKey(e) && Regex.IsMatch(e.LocalName, "^h[1-6]$")))
        {
            var cls = string.Join(".", h.ClassList.OrderBy(c => c, StringComparer.Ordinal));
            var other = y.FirstOrDefault(e => !map.ContainsValue(e) && Regex.IsMatch(e.LocalName, "^h[1-6]$") && string.Join(".", e.ClassList.OrderBy(c => c, StringComparer.Ordinal)) == cls);
            if (other is not null) map[h] = other;
        }
        return map;
    }

    private static List<INode> OwnTextNodes(IElement e) =>
        e.ChildNodes.Where(n => n.NodeType == NodeType.Text && n.TextContent.Trim().Length > 0).ToList();

    /// <summary>The element that repeats for each item: the item itself, or its wrapper when each item sits alone in one (li &gt; a).</summary>
    private static List<IElement> RepeatUnits(List<IElement> items)
    {
        if (items.Count < 2) return items;
        var units = items.ToList();
        while (units.All(u => u.ParentElement is { } p && p.Children.Length == 1) && units.Select(u => u.ParentElement).Distinct().Count() == units.Count
               && units.Select(u => u.ParentElement!.ParentElement).Distinct().Count() == 1)
            units = units.Select(u => u.ParentElement!).ToList();
        return units;
    }

    /// <summary>Puts text into an element without losing icons or spans inside it: its own text, else its longest text-only descendant.</summary>
    private static void SetText(IElement el, string token)
    {
        if (el.Children.Length == 0) { el.TextContent = token; return; }
        // Text split over inline spans ("<span>Year of </span><span>establishment</span>"): the field replaces all of it.
        if (el.Children.All(c => c.LocalName is "span" or "strong" or "em" or "b" or "i" && c.Children.Length == 0 && c.TextContent.Trim().Length > 0)
            && !el.Children.Any(c => HtmlSections.OwnTokens(c).Any(t => t is "sr" or "icon")))
        {
            el.TextContent = token;
            return;
        }
        var own = OwnTextNodes(el);
        if (own.Count > 0)
        {
            own[0].TextContent = token;
            foreach (var t in own.Skip(1)) t.Parent!.RemoveChild(t);
            return;
        }
        var leaf = el.QuerySelectorAll("*").Where(e => e.Children.Length == 0 && e.TextContent.Trim().Length > 0)
            .OrderByDescending(e => e.TextContent.Trim().Length).FirstOrDefault();
        if (leaf is not null) leaf.TextContent = token;
        else el.AppendChild(el.Owner!.CreateTextNode(token));
    }

    private static string DateFormat(string sample)
    {
        var s = Collapse(sample);
        foreach (var f in new[] { "MMM. d, yyyy", "MMM d, yyyy", "MMMM d, yyyy", "d MMMM yyyy", "d MMM yyyy", "dd MMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d.M.yyyy" })
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return f;
        return "d MMMM yyyy";
    }

    private INode Replace(IElement el, string razor)
    {
        var c = el.Owner!.CreateComment(Snip(razor));
        el.Replace(c);
        return c;
    }

    private string Snip(string razor)
    {
        _snippets.Add(razor);
        return $"{Mark}{_snippets.Count - 1}{EndMark}";
    }

    private void AddAttr(IElement el, string razor) => el.SetAttribute($"data-mdlry-{_snippets.Count}", Snip(razor));

    private void Wrap(IElement el, string condition)
    {
        el.Before(el.Owner!.CreateComment(Snip($"@if ({condition})\n{{")));
        el.After(el.Owner!.CreateComment(Snip("}")));
    }

    /// <summary>if (c1) { a } else [if (c2)] { b } – a and b are siblings, a first.</summary>
    private void Wrap2(IElement a, string c1, IElement b, string c2)
    {
        a.Before(a.Owner!.CreateComment(Snip($"@if ({c1})\n{{")));
        a.After(a.Owner!.CreateComment(Snip(c2.Length == 0 ? "}\nelse\n{" : $"}}\nelse if ({c2})\n{{")));
        b.After(b.Owner!.CreateComment(Snip("}")));
    }

    private string Render(IElement root)
    {
        var html = root.ToHtml(new PrettyMarkupFormatter { Indentation = "    ", NewLine = "\n" });
        html = html.Replace("@", "@@"); // literal @ in the front-end markup (emails, CSS) must be escaped in Razor
        html = Regex.Replace(html, $"\\sdata-mdlry-\\d+=\"{Mark}(\\d+){EndMark}\"", m => " " + _snippets[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
        for (var pass = 0; pass < 4 && html.Contains(Mark); pass++)
        {
            html = Regex.Replace(html, $"(?m)^([ \\t]*)<!--{Mark}(\\d+){EndMark}-->", m =>
            {
                var indent = m.Groups[1].Value;
                return string.Join("\n", _snippets[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)].Split('\n').Select(l => indent + l));
            });
            html = Regex.Replace(html, $"<!--{Mark}(\\d+){EndMark}-->", m => _snippets[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
            html = Regex.Replace(html, $"(?m)^([ \\t]*){Mark}(\\d+){EndMark}$", m =>
            {
                var s = _snippets[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)];
                return s.Contains('\n') ? string.Join("\n", s.Split('\n').Select(l => m.Groups[1].Value + l)) : m.Groups[1].Value + s;
            });
            html = Regex.Replace(html, $"{Mark}(\\d+){EndMark}", m => _snippets[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
        }
        html = Regex.Replace(html, "[ \t]+(?=\n)", "");
        html = Regex.Replace(html, "\n{3,}", "\n\n");
        html = Regex.Replace(html, "(?<=[>}])\n\n(?=[ \t]*[@<}])", "\n");
        return FixCodeContext(html.Trim('\n')) + "\n";
    }

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };

    /// <summary>
    /// Razor rejects "@if" / "@for" directly inside a code block ("Once inside code, you do not need to prefix…"). Tracks
    /// code and markup nesting line by line (the markup is pretty-printed: one tag per line) and drops those "@".
    /// </summary>
    private static string FixCodeContext(string html)
    {
        var lines = html.Split('\n');
        var stack = new Stack<string>();   // "{" = code block, otherwise the open markup tag
        var pendingCode = false;           // a code construct (if/for/else) whose "{" comes next
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith("@*", StringComparison.Ordinal)) continue;
            var inCode = stack.Count > 0 && stack.Peek() == "{";
            if (t == "{") { stack.Push("{"); pendingCode = false; continue; }
            if (t.StartsWith('}'))
            {
                if (stack.Count > 0 && stack.Peek() == "{") stack.Pop();
                continue;
            }
            if (Regex.IsMatch(t, @"^@?(if|for|foreach|else|while|using)\b"))
            {
                if (inCode && t.StartsWith('@') && !t.StartsWith("@Html", StringComparison.Ordinal))
                    lines[i] = Regex.Replace(lines[i], @"^(\s*)@", "$1");
                pendingCode = true;
                continue;
            }
            if (t.StartsWith("</", StringComparison.Ordinal))
            {
                var tag = Regex.Match(t, @"^</([\w-]+)").Groups[1].Value;
                if (stack.Count > 0 && stack.Peek() == tag) stack.Pop();
                continue;
            }
            var open = Regex.Match(t, @"^<([a-zA-Z][\w-]*)");
            if (open.Success)
            {
                var tag = open.Groups[1].Value;
                var selfContained = VoidTags.Contains(tag) || t.EndsWith("/>", StringComparison.Ordinal) || t.Contains($"</{tag}>", StringComparison.Ordinal);
                if (!selfContained) stack.Push(tag);
            }
        }
        return string.Join("\n", lines);
    }

    private string RenderInner(IElement holder)
    {
        var html = Render(holder);
        var lines = html.Split('\n').ToList();
        if (lines.Count >= 2 && lines[0].TrimStart().StartsWith("<mdlry-root", StringComparison.Ordinal)) lines.RemoveAt(0);
        var close = lines.FindLastIndex(l => l.Trim() == "</mdlry-root>");
        if (close >= 0) lines.RemoveAt(close);
        return string.Join("\n", lines.Select(l => l.StartsWith("    ", StringComparison.Ordinal) ? l[4..] : l)).Trim('\n') + "\n";
    }

    private ModelClass? ClassFor(string schemaTitle) => _in.Models.ClassFor(schemaTitle);
    private ModelClass? ClassFor(ModelProperty p) => p.ModelName is null ? null : _in.Models.Classes.FirstOrDefault(c => c.Name == p.ModelName);
    private static ModelProperty? Prop(ModelClass cls, IaField f) => cls.Properties.FirstOrDefault(p => p.Field.XmlName == f.XmlName);
    private static string Role(ModelProperty p) => FieldBinder.Role(p.Field);
    private static string NotEmpty(ModelProperty p, string x) => p.Kind == ModelValueKind.Text && !p.IsList ? $"!string.IsNullOrEmpty({x})" : $"{x} != null";
    private static string Lit(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string Collapse(string s) => HtmlSections.Collapse(s);
    private static string Compact(string s) => Regex.Replace(s, "[^A-Za-z0-9]", "");
    private static string Kebab(string s) => Regex.Replace(s, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
    private void Warn(SectionMatch sm, string msg) => _out.Issues.Add(new Issue(IssueLevel.Warning, "Views", sm.Section.Index + 1, sm.TemplateTitle ?? sm.Section.Label, msg));
    private void Info(SectionMatch sm, string msg) => _out.Issues.Add(new Issue(IssueLevel.Info, "Views", sm.Section.Index + 1, sm.TemplateTitle ?? sm.Section.Label, msg));
    private static string Comment(string s) => s.Replace("*@", "* @").Replace("\"", "'");

    // ================================================================ README

    /// <summary>README for the views zip.</summary>
    public static string Readme(ViewInput input, ViewOutput output)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# DXA views – {input.Match.Page.Label}");
        sb.AppendLine();
        sb.AppendLine($"Generated by Modelry from `{input.SourceName}` on {DateTime.UtcNow.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC, for the models in `{input.Models.Options.RootNamespace}`.");
        sb.AppendLine("Target: DXA 2.x on ASP.NET MVC 5 (Razor, C# 7.3). Copy `Areas/` and `Helpers/` into your DXA module next to the generated models, and import `Resources/resources.csv` into the module's resources.");
        sb.AppendLine();
        sb.AppendLine("| View | Kind | Model | From |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var w in output.Written.Where(w => w.Kind is not ("Helper" or "Resources"))) sb.AppendLine($"| `{w.View}` | {w.Kind} | `{w.Model}` | {w.Source.Replace("|", "\\|")} |");
        sb.AppendLine();
        sb.AppendLine("## How the views are built");
        sb.AppendLine();
        sb.AppendLine("- **Markup** is the front-end's, classes and structure unchanged. Text, images, video and links matched to a field come from the model, with `@Html.DxaEntityMarkup()` on the component and `@Html.DxaPropertyMarkup(...)` on its fields for Experience Manager. Rich text uses `@Html.DxaRichText(...)`; optional fields are wrapped in `@if`.");
        sb.AppendLine("- **Repeated items** are one loop over the first sample of each item shape (items with other fields – an image tile among number tiles – get their own branch). Differences between the items are rules: attributes only the first item has (`data-active`, `aria-current`), the first item's heading level, counters (`2 of 4`, `02 / 04`, `data-index`), and a video where other items have an image.");
        sb.AppendLine("- **Mirrored markup** – a thumbnail strip, tab buttons – is a second loop over the same list, using the item's values (or a field named after it, e.g. `thumbnailLabel`, `thumbnailImage`).");
        sb.AppendLine("- **Linked components** (slides, cards, list items that are components of their own) have their own item view under `Entity/<ParentView>/`, rendered with `@Html.Partial(...)`, which passes `ItemIndex` and `ItemCount`. Each item has its own XPM markup.");
        sb.AppendLine("- **Fields the HTML did not show** are placed at the end of the component, guarded by `@if` and marked with a comment – move them where the design needs them.");
        sb.AppendLine("- **Fixed UI text** (button labels, aria-labels, status text) uses `@Html.Resource(\"key\")`, with `string.Format` where it contains values or counters. The keys and English values are in `Resources/resources.csv`.");
        sb.AppendLine("- **Images** keep the front-end's `<picture>`, `srcset` and `sizes`; the URLs come from DXA's image resizing for the widths the front-end listed (`Html.Srcset`, in `Helpers/ViewHelpers.cs`). Format-specific `<source type=…>` elements were dropped – the CMS serves the uploaded format.");
        sb.AppendLine("- **Front-end assets** (css, js, fonts, icons, decorative images) are referenced through `Url.Asset(\"path\")`, i.e. `/system/assets/<path>` in the published HTML design.");
        sb.AppendLine("- **Layout**: `Views/Shared/_Layout.cshtml` has the `<head>`, the Header and Footer include regions and `@RenderBody()`; each Page Template view has the content area with its regions.");
        sb.AppendLine();
        sb.AppendLine("## Check before relying on it");
        sb.AppendLine();
        sb.AppendLine("- The views were generated, not compiled in your DXA solution. Build once with Razor view compilation (`MvcBuildViews`) or open each page.");
        sb.AppendLine("- `Helpers/ViewHelpers.cs` uses `SiteConfiguration.MediaHelper.GetResponsiveImageUrl` and `WebRequestContext.Localization.Path` – check both against your DXA version (marked VERIFY).");
        sb.AppendLine("- `Views/web.config` must import `Sdl.Web.Mvc.Html` (and the helpers namespace, unless you keep the `@using` lines).");
        sb.AppendLine("- Query-driven lists (Content Source = Query in the mapping) loop over the list property; fill it in your Content List controller or model builder.");
        sb.AppendLine("- Variant views (`<View>_<Page>S<n>`) mean the same Component Template was used for different markup: register a Component Template for the variant, or merge the two views.");

        if (output.FieldReport.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Fields per view");
            sb.AppendLine();
            sb.AppendLine("| View | From the HTML | Placed by default | Not rendered |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var (view, html, def, not) in output.FieldReport)
                sb.AppendLine($"| `{view}` | {string.Join(", ", html)} | {string.Join(", ", def)} | {string.Join(", ", not)} |");
            sb.AppendLine();
            sb.AppendLine("Not rendered: keywords, numbers, query settings and nested lists without a sample – add them where the design needs them.");
        }
        if (output.StaticAssets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Front-end assets the views reference (put them in the HTML design under system/assets)");
            sb.AppendLine();
            foreach (var a in output.StaticAssets) sb.AppendLine($"- `{a}`");
        }
        if (output.StaticLinks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Links left as written (not mapped to a field)");
            sb.AppendLine();
            foreach (var a in output.StaticLinks) sb.AppendLine($"- `{a}`");
        }
        if (output.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Findings");
            sb.AppendLine();
            foreach (var i in output.Issues.OrderBy(i => i.Level)) sb.AppendLine($"- **{i.Level}** – {i.Item}: {i.Message}");
        }
        return sb.ToString();
    }
}
