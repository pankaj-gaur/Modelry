using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Modelry.Core.Audit.Aem;

/// <summary>A node of the demo repository: properties plus ordered child nodes (like JCR).</summary>
public sealed class DemoNode
{
    public string Name { get; }
    public string Path { get; }
    public Dictionary<string, object> Props { get; } = new();
    public List<DemoNode> Children { get; } = new();

    public DemoNode(string name, string path, string type) { Name = name; Path = path; Props["jcr:primaryType"] = type; }

    public DemoNode Add(string name, string type, params (string Key, object Value)[] props)
    {
        var n = new DemoNode(name, Path == "/" ? "/" + name : $"{Path}/{name}", type);
        foreach (var (k, v) in props) n.Props[k] = v;
        Children.Add(n);
        return n;
    }

    public DemoNode? Find(string name) => Children.FirstOrDefault(c => c.Name == name);
    public IEnumerable<DemoNode> Descendants() => Children.SelectMany(c => new[] { c }.Concat(c.Descendants()));
}

/// <summary>
/// Demo AEM author: an in-memory repository with a small corporate site, served through the same HTTP endpoints the real
/// gateway calls (Sling GET servlet .json / .N.json / .infinity.json and /bin/querybuilder.json). Demo mode for AEM runs the
/// real <see cref="AemAuditGateway"/> against this handler, so the AEM audit can be shown – and exercised – without an instance.
/// It also contains deliberate issues for the checks to find.
/// </summary>
public sealed class DemoAemHandler : HttpMessageHandler
{
    private static readonly Lazy<DemoAemHandler> Shared = new(() => new DemoAemHandler(cloud: true));
    public static DemoAemHandler Instance => Shared.Value;
    /// <summary>True: replication properties as AEM as a Cloud Service writes them (_publish / _preview); false: AEM 6.5 style.</summary>
    private readonly bool _cloud;
    public static readonly AemConnection Connection = new("https://author.demo-aem.local", false, "admin", "admin");
    public DemoNode Root { get; } = new("", "/", "rep:root");

    public static HttpClient CreateClient() => new(Instance, disposeHandler: false);

    public DemoAemHandler(bool cloud) { _cloud = cloud; Build(); }

    // ------------------------------------------------------------------ HTTP

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path == "/bin/querybuilder.json") return Json(Query(HttpUtility.ParseQueryString(uri.Query)));
        if (path == "/libs/granite/security/currentuser.json") return Json(new Dictionary<string, object> { ["authorizableId"] = "admin" });
        if (!path.EndsWith(".json", StringComparison.Ordinal)) return Status(HttpStatusCode.NotFound);
        var withoutExt = path[..^5];
        var depth = 0;
        var dot = withoutExt.LastIndexOf('.');
        if (dot > withoutExt.LastIndexOf('/'))
        {
            var sel = withoutExt[(dot + 1)..];
            depth = sel == "infinity" ? int.MaxValue : int.TryParse(sel, out var d) ? d : 0;
            withoutExt = withoutExt[..dot];
        }
        var node = Resolve(withoutExt);
        return node is null ? Status(HttpStatusCode.NotFound) : Json(Render(node, depth));
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode code) => Task.FromResult(new HttpResponseMessage(code));

    private static Task<HttpResponseMessage> Json(object body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    });

    private DemoNode? Resolve(string path)
    {
        var node = Root;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Find(seg);
            if (node is null) return null;
        }
        return node;
    }

    private static Dictionary<string, object> Render(DemoNode n, int depth, bool withPath = false)
    {
        var o = new Dictionary<string, object>(n.Props);
        if (withPath) o["jcr:path"] = n.Path;
        if (depth > 0) foreach (var c in n.Children) o[c.Name] = Render(c, depth == int.MaxValue ? depth : depth - 1);
        return o;
    }

    /// <summary>The QueryBuilder predicates the gateway uses: path (+ path.self), type, property / property.value, paging.</summary>
    private Dictionary<string, object> Query(System.Collections.Specialized.NameValueCollection q)
    {
        var root = Resolve(q["path"] ?? "/");
        var hits = new List<DemoNode>();
        if (root is not null)
        {
            if (q["path.self"] == "true") hits.Add(root);
            hits.AddRange(root.Descendants());
        }
        if (q["type"] is { } type) hits = hits.Where(h => (string)h.Props["jcr:primaryType"] == type).ToList();
        if (q["property"] is { } prop)
        {
            var value = q["property.value"];
            hits = hits.Where(h =>
            {
                var parts = prop.Split('/');
                DemoNode? n = h;
                foreach (var seg in parts[..^1]) n = n?.Find(seg);
                return n is not null && n.Props.TryGetValue(parts[^1], out var v) && string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), value, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
        hits = hits.OrderBy(h => h.Path, StringComparer.Ordinal).ToList();
        var depth = int.TryParse(q["p.nodedepth"], out var d) ? d : 0;
        var limit = int.TryParse(q["p.limit"], out var l) ? l : 10;
        var offset = int.TryParse(q["p.offset"], out var off) ? off : 0;
        var page = hits.Skip(offset).Take(limit).Select(h => Render(h, depth, withPath: true)).ToList();
        return new Dictionary<string, object>
        {
            ["success"] = true, ["results"] = page.Count, ["total"] = hits.Count, ["more"] = offset + page.Count < hits.Count, ["offset"] = offset, ["hits"] = page
        };
    }

    // ------------------------------------------------------------------ demo content

    private static readonly DateTime Base = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    /// <summary>Sling's JSON date format.</summary>
    private static string Ecma(DateTime d) => d.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000'", CultureInfo.InvariantCulture);

    private void Build()
    {
        var rnd = new Random(4242);
        var content = Root.Add("content", "sling:Folder");
        var conf = Root.Add("conf", "sling:Folder").Add("sabic-demo", "sling:Folder").Add("settings", "sling:Folder");
        var apps = Root.Add("apps", "sling:Folder");

        // components
        var comps = apps.Add("sabic", "sling:Folder").Add("components", "sling:Folder");
        foreach (var (name, title, group, super) in new[]
        {
            ("page", "Page", ".hidden", "core/wcm/components/page/v3/page"), ("title", "Title", "SABIC - Content", "core/wcm/components/title/v3/title"),
            ("text", "Text", "SABIC - Content", "core/wcm/components/text/v2/text"), ("image", "Image", "SABIC - Content", "core/wcm/components/image/v3/image"),
            ("teaser", "Teaser", "SABIC - Content", "core/wcm/components/teaser/v2/teaser"), ("container", "Container", "SABIC - Structure", "core/wcm/components/container/v1/container"),
            ("contentfragment", "Content Fragment", "SABIC - Content", "core/wcm/components/contentfragment/v1/contentfragment"),
            ("experiencefragment", "Experience Fragment", "SABIC - Structure", "core/wcm/components/experiencefragment/v2/experiencefragment"),
            ("carousel", "Carousel", "SABIC - Content", "core/wcm/components/carousel/v1/carousel"), ("legacy-banner", "Copy of Banner (old)", "SABIC - Legacy", ""),
        })
        {
            var c = comps.Add(name, "cq:Component", ("jcr:title", title), ("componentGroup", group), ("jcr:created", Ecma(Base.AddDays(-300))));
            if (super.Length > 0) c.Props["sling:resourceSuperType"] = super;
            c.Add(name + ".html", "nt:file");
            c.Add("cq:dialog", "nt:unstructured");
        }
        apps.Add("cq", "sling:Folder").Add("core", "sling:Folder").Add("components", "sling:Folder").Add("system-thing", "cq:Component", ("jcr:title", "System"));

        // templates
        var templates = conf.Add("wcm", "cq:Page").Add("templates", "cq:Page");
        foreach (var (name, title, status) in new[] { ("page-content", "Content Page", "enabled"), ("landing", "Landing Page", "enabled"),
                                                      ("legacy-campaign", "Legacy Campaign", "disabled"), ("event", "Event Page", "enabled") })
        {
            var t = templates.Add(name, "cq:Template");
            t.Add("jcr:content", "cq:PageContent", ("jcr:title", title), ("status", status), ("cq:lastModified", Ecma(Base.AddDays(-rnd.Next(30, 400)))),
                ("cq:lastModifiedBy", "admin"));
            t.Add("structure", "cq:Page");
        }

        // Content Fragment Models
        var models = conf.Add("dam", "sling:Folder").Add("cfm", "sling:Folder").Add("models", "sling:Folder");
        foreach (var (name, title, fields) in new[] { ("article", "Article", new[] { "title", "summary", "body", "image" }),
                                                      ("product", "Product", new[] { "name", "grade", "datasheet", "industries" }),
                                                      ("person", "Person", new[] { "name", "role", "photo" }) })
        {
            var m = models.Add(name, "cq:Template");
            var jc = m.Add("jcr:content", "cq:PageContent", ("jcr:title", title), ("status", "enabled"), ("jcr:lastModified", Ecma(Base.AddDays(-200))));
            var items = jc.Add("model", "cq:PageContent").Add("cq:dialog", "nt:unstructured").Add("content", "nt:unstructured").Add("items", "nt:unstructured");
            var i = 0;
            foreach (var f in fields)
                items.Add("field" + i++, "nt:unstructured", ("name", f), ("fieldLabel", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f)),
                    ("metaType", f is "image" or "photo" or "datasheet" ? "reference" : f == "body" ? "text-multi" : "text-single"));
        }

        // tags
        var tags = content.Add("cq:tags", "sling:Folder");
        foreach (var (ns, nsTitle, values) in new[] { ("sabic", "SABIC", new[] { "industry/automotive", "industry/healthcare", "industry/packaging", "industry/construction", "topic/sustainability", "topic/innovation", "topic/old-campaign" }),
                                                       ("region", "Region", new[] { "europe", "middle-east", "asia", "americas" }) })
        {
            var nsNode = tags.Add(ns, "cq:Tag", ("jcr:title", nsTitle));
            foreach (var v in values)
            {
                var cur = nsNode;
                foreach (var seg in v.Split('/'))
                    cur = cur.Find(seg) ?? cur.Add(seg, "cq:Tag", ("jcr:title", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(seg.Replace('-', ' '))));
            }
        }

        // DAM: assets and Content Fragments
        var dam = content.Add("dam", "sling:Folder").Add("sabic-demo", "sling:OrderedFolder");
        var images = dam.Add("images", "sling:OrderedFolder");
        var docs = dam.Add("documents", "sling:OrderedFolder");
        var assetPaths = new List<string>();
        string[] imageNames = { "hero-polymers.jpg", "plant-jubail.jpg", "team-riyadh.jpg", "Recycling Plant.png", "logo.svg", "chemicals-lab.jpg",
                                "automotive-parts.jpg", "packaging-film.jpg", "old-campaign-2019.jpg", "healthcare-device.jpg", "earth-day.png", "uploading-video.mp4" };
        foreach (var (name, i) in imageNames.Select((n, i) => (n, i)))
        {
            var a = images.Add(name, "dam:Asset");
            var jc = a.Add("jcr:content", "dam:AssetContent", ("jcr:lastModified", Ecma(Base.AddDays(-rnd.Next(5, 500)))), ("jcr:lastModifiedBy", "editor" + (i % 3)),
                ("dam:assetState", name == "uploading-video.mp4" ? "processing" : "processed"));
            var format = name.EndsWith(".png") ? "image/png" : name.EndsWith(".svg") ? "image/svg+xml" : name.EndsWith(".mp4") ? "video/mp4" : "image/jpeg";
            var size = name is "plant-jubail.jpg" or "hero-polymers.jpg" ? 4_800_000 + i * 1000 : 60_000 + rnd.Next(900_000);
            var meta = jc.Add("metadata", "nt:unstructured", ("dc:format", format), ("dam:size", size));
            if (i % 4 != 2) meta.Props["dc:title"] = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(name).Replace('-', ' '));
            if (i % 3 == 0) meta.Props["cq:tags"] = new[] { "sabic:industry/automotive" };
            var rend = jc.Add("renditions", "nt:folder");
            rend.Add("original", "nt:file");
            if (format.StartsWith("image")) { rend.Add("cq5dam.thumbnail.140.100.png", "nt:file"); rend.Add("cq5dam.web.1280.1280.jpeg", "nt:file"); }
            if (i % 5 == 1) Activate(jc, Base.AddDays(-rnd.Next(1, 60)), preview: false);
            assetPaths.Add(a.Path);
        }
        foreach (var name in new[] { "sustainability-report-2025.pdf", "LNP-datasheet.pdf", "investor-presentation.pdf", "old-brochure-2018.pdf" })
        {
            var a = docs.Add(name, "dam:Asset");
            var jc = a.Add("jcr:content", "dam:AssetContent", ("jcr:lastModified", Ecma(Base.AddDays(-rnd.Next(30, 700)))), ("jcr:lastModifiedBy", "editor1"), ("dam:assetState", "processed"));
            jc.Add("metadata", "nt:unstructured", ("dc:format", "application/pdf"), ("dam:size", 300_000 + rnd.Next(5_000_000)), ("dc:title", Path.GetFileNameWithoutExtension(name)));
            jc.Add("renditions", "nt:folder").Add("original", "nt:file");
            Activate(jc, Base.AddDays(-rnd.Next(1, 90)), preview: false);
            assetPaths.Add(a.Path);
        }

        var fragments = dam.Add("content-fragments", "sling:OrderedFolder");
        var fragmentPaths = new List<string>();
        foreach (var (folder, model, titles) in new[] { ("articles", "article", new[] { "Circular economy at SABIC", "New polymer grade launched", "Plant safety milestone", "Test article – do not use" }),
                                                         ("products", "product", new[] { "LNP Thermocomp", "ULTEM Resin", "Copy of LEXAN Sheet" }) })
        {
            var f = fragments.Add(folder, "sling:OrderedFolder");
            foreach (var (title, i) in titles.Select((t, i) => (t, i)))
            {
                var a = f.Add(title.ToLowerInvariant().Replace(' ', '-').Replace("–", ""), "dam:Asset");
                var jc = a.Add("jcr:content", "dam:AssetContent", ("contentFragment", true), ("jcr:title", title),
                    ("jcr:lastModified", Ecma(Base.AddDays(-rnd.Next(2, 300)))), ("jcr:lastModifiedBy", "editor" + (i % 3)));
                var data = jc.Add("data", "nt:unstructured", ("cq:model", $"/conf/sabic-demo/settings/dam/cfm/models/{model}"));
                var master = data.Add("master", "nt:unstructured", (model == "article" ? "title" : "name", title));
                master.Props[model == "article" ? "image" : "datasheet"] = model == "article" ? assetPaths[i % imageNames.Length] : $"{docs.Path}/LNP-datasheet.pdf";
                if (i == 0) master.Props["related"] = $"{f.Path}/new-polymer-grade-launched";
                if (!title.StartsWith("Test")) Activate(jc, Base.AddDays(-rnd.Next(1, 200)), preview: true);
                if (i == 1 && model == "article") jc.Props["jcr:lastModified"] = Ecma(Base.AddDays(-1));   // edited after publish
                fragmentPaths.Add(a.Path);
            }
        }

        // Experience Fragments
        var xfRoot = content.Add("experience-fragments", "sling:OrderedFolder").Add("sabic-demo", "sling:OrderedFolder");
        foreach (var (name, title) in new[] { ("header", "Header"), ("footer", "Footer"), ("promo-2019", "Promo 2019 (old)") })
        {
            var x = xfRoot.Add(name, "cq:Page");
            var jc = x.Add("jcr:content", "cq:PageContent", ("jcr:title", title), ("sling:resourceType", "cq/experience-fragments/components/experiencefragment"),
                ("cq:lastModified", Ecma(Base.AddDays(-rnd.Next(10, 400)))), ("cq:lastModifiedBy", "admin"));
            if (name != "promo-2019") Activate(jc, Base.AddDays(-5), preview: true);
            var master = x.Add("master", "cq:Page");
            var mjc = master.Add("jcr:content", "cq:PageContent", ("sling:resourceType", "cq/experience-fragments/components/xfpage"));
            mjc.Add("root", "nt:unstructured", ("sling:resourceType", "sabic/components/container"))
               .Add("image", "nt:unstructured", ("sling:resourceType", "sabic/components/image"), ("fileReference", $"{images.Path}/logo.svg"));
        }

        // Site
        var site = content.Add("sabic-demo", "cq:Page");
        Page(site, "SABIC (demo)", "landing", rnd, 0, Array.Empty<string>(), fragmentPaths, assetPaths, published: true);
        site.Find("jcr:content")!.Props["cq:conf"] = "/conf/sabic-demo";
        var en = site.Add("en", "cq:Page");
        Page(en, "English", "landing", rnd, 1, Array.Empty<string>(), fragmentPaths, assetPaths, published: true);
        var structure = new (string Path, string Title, string Template)[]
        {
            ("products", "Products", "landing"), ("products/polymers", "Polymers", "page-content"), ("products/polymers/lnp", "LNP Compounds", "page-content"),
            ("products/polymers/ultem", "ULTEM", "page-content"), ("products/chemicals", "Chemicals", "page-content"), ("products/chemicals/methanol", "Methanol", "page-content"),
            ("sustainability", "Sustainability", "landing"), ("sustainability/circular-economy", "Circular Economy", "page-content"),
            ("sustainability/Reports 2025", "Reports 2025", "page-content"), ("news", "News", "landing"),
            ("news/polymer-launch", "New polymer grade", "page-content"), ("news/safety-milestone", "Safety milestone", "page-content"),
            ("news/earth-day", "Earth Day", "event"), ("news/old-campaign-2019", "Campaign 2019", "legacy-campaign"),
            ("careers", "Careers", "page-content"), ("careers/graduates", "Graduates", "page-content"), ("about", "About us", "page-content"),
            ("about/leadership", "Leadership", "page-content"), ("about/test-page", "Test page", "page-content"), ("contact", "Contact", "removed-template"),
        };
        var created = new Dictionary<string, DemoNode> { [""] = en };
        var allPages = structure.Select(s => $"{en.Path}/{s.Path}").ToArray();
        foreach (var (i, s) in structure.Select((s, i) => (i, s)))
        {
            var parentPath = s.Path.Contains('/') ? s.Path[..s.Path.LastIndexOf('/')] : "";
            var node = created[parentPath].Add(s.Path.Split('/')[^1], "cq:Page");
            created[s.Path] = node;
            Page(node, s.Title, s.Template, rnd, i + 2, allPages, fragmentPaths, assetPaths,
                published: s.Path is not ("news/old-campaign-2019" or "about/test-page" or "careers/graduates"), deactivated: s.Path == "news/old-campaign-2019",
                empty: s.Path == "contact");
        }
        // German live copy (MSM): inherits, one page with cancelled inheritance
        var de = site.Add("de", "cq:Page");
        Page(de, "Deutsch", "landing", rnd, 40, Array.Empty<string>(), fragmentPaths, assetPaths, published: true, liveCopy: true);
        var deProducts = de.Add("produkte", "cq:Page");
        Page(deProducts, "Produkte", "landing", rnd, 41, Array.Empty<string>(), fragmentPaths, assetPaths, published: true, liveCopy: true);
        var deNews = de.Add("nachrichten", "cq:Page");
        Page(deNews, "Nachrichten", "page-content", rnd, 42, Array.Empty<string>(), fragmentPaths, assetPaths, published: true, liveCopy: true, cancelled: true);
    }

    private void Activate(DemoNode jc, DateTime at, bool preview)
    {
        jc.Props["cq:lastReplicationAction"] = "Activate";
        jc.Props["cq:lastReplicated"] = Ecma(at);
        jc.Props["cq:lastReplicatedBy"] = "publisher";
        if (!_cloud) return;
        jc.Props["cq:lastReplicationAction_publish"] = "Activate";
        jc.Props["cq:lastReplicated_publish"] = Ecma(at);
        jc.Props["cq:lastReplicatedBy_publish"] = "publisher";
        if (preview)
        {
            jc.Props["cq:lastReplicationAction_preview"] = "Activate";
            jc.Props["cq:lastReplicated_preview"] = Ecma(at.AddHours(-3));
            jc.Props["cq:lastReplicatedBy_preview"] = "author1";
        }
    }

    private void Page(DemoNode page, string title, string template, Random rnd, int n, string[] allPages, List<string> fragments, List<string> assets,
        bool published, bool deactivated = false, bool liveCopy = false, bool cancelled = false, bool empty = false)
    {
        var modified = Base.AddDays(-rnd.Next(1, 520));
        var jc = page.Add("jcr:content", "cq:PageContent", ("jcr:title", title), ("cq:template", $"/conf/sabic-demo/settings/wcm/templates/{template}"),
            ("sling:resourceType", "sabic/components/page"), ("cq:lastModified", Ecma(modified)), ("cq:lastModifiedBy", n % 2 == 0 ? "a.sharma" : "m.alharbi"),
            ("jcr:created", Ecma(modified.AddDays(-30))), ("jcr:createdBy", "admin"));
        if (n % 3 == 0) jc.Props["cq:tags"] = new[] { n % 2 == 0 ? "sabic:industry/healthcare" : "sabic:topic/sustainability", "region:europe" };
        if (liveCopy) jc.Props["jcr:mixinTypes"] = cancelled ? new[] { "cq:LiveRelationship", "cq:LiveSyncCancelled" } : new[] { "cq:LiveRelationship" };
        if (published)
        {
            Activate(jc, modified.AddDays(rnd.Next(0, 10)), preview: n % 4 != 0);
            if (n % 7 == 3) jc.Props["cq:lastModified"] = Ecma(modified.AddDays(20));   // edited after publish
        }
        if (deactivated)
        {
            jc.Props["cq:lastReplicationAction"] = "Deactivate";
            jc.Props["cq:lastReplicated"] = Ecma(Base.AddDays(-60));
            if (_cloud)
            {
                jc.Props["cq:lastReplicationAction_publish"] = "Deactivate";
                jc.Props["cq:lastReplicated_publish"] = Ecma(Base.AddDays(-60));
            }
        }
        if (empty) return;
        var root = jc.Add("root", "nt:unstructured", ("sling:resourceType", "sabic/components/container"));
        var container = root.Add("container", "nt:unstructured", ("sling:resourceType", "sabic/components/container"));
        container.Add("title", "nt:unstructured", ("sling:resourceType", "sabic/components/title"), ("jcr:title", title));
        container.Add("text", "nt:unstructured", ("sling:resourceType", "sabic/components/text"), ("text", $"<p>About {title}.</p>"));
        container.Add("image", "nt:unstructured", ("sling:resourceType", "sabic/components/image"), ("fileReference", assets[n % 10]));
        if (n % 2 == 0)
            container.Add("teaser", "nt:unstructured", ("sling:resourceType", "sabic/components/contentfragment"), ("fragmentPath", fragments[n % 4]));
        if (allPages.Length > 0)
            container.Add("link", "nt:unstructured", ("sling:resourceType", "sabic/components/teaser"),
                ("linkURL", allPages[(n * 7) % allPages.Length] + ".html"), ("fragmentPath", n == 5 ? fragments[5] : ""));
        root.Add("footer", "nt:unstructured", ("sling:resourceType", "sabic/components/experiencefragment"),
            ("fragmentVariationPath", "/content/experience-fragments/sabic-demo/footer/master"));
        if (n == 3) container.Add("old-link", "nt:unstructured", ("sling:resourceType", "sabic/components/teaser"),
            ("linkURL", "/content/sabic-demo/en/news/old-campaign-2019.html"));
    }
}
