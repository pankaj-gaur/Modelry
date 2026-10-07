using Modelry.Core.Excel;
using Modelry.Core.Model;

namespace Modelry.Core.Import;

public enum PlanItemKind { Category, Keyword, Schema }

/// <summary>One item to create, in dependency order.</summary>
public sealed class CreationStep
{
    public required PlanItemKind Kind { get; init; }
    public CategoryPlan? Category { get; init; }
    public KeywordPlan? Keyword { get; init; }
    public SchemaPlan? Schema { get; init; }
    public required string Label { get; init; }
    /// <summary>Items (created in this import) that must exist first.</summary>
    public List<string> DependsOn { get; } = new();
    /// <summary>References that cannot be satisfied first because of a circular reference – applied after creation.</summary>
    public List<string> Deferred { get; } = new();
}

/// <summary>
/// Orders categories, keywords and schemas so that every item is created after everything it references:
///   schema  → embedded schemas (hard), categories of keyword fields (hard), allowed link-target schemas (soft),
///             nested-region and allowed component schemas of region definitions (soft)
///   keyword → its category (hard), parent keyword (hard)
///   category→ keyword metadata schema (soft)
/// Hard dependencies must exist at creation time; soft dependencies are honoured too, and only when they form a
/// cycle (e.g. A links to B and B links to A, or a schema linking to itself) is the reference applied afterwards.
/// Items that already exist in Tridion are not part of the graph – they are always available.
/// </summary>
public static class DependencyOrder
{
    private sealed class Node
    {
        public required CreationStep Step { get; init; }
        public int Rank { get; init; }
        public HashSet<Node> Hard { get; } = new();
        public HashSet<Node> Soft { get; } = new();
    }

    private static readonly Dictionary<IaSchemaPurpose, int> PurposeRank = new()
    {
        [IaSchemaPurpose.Embedded] = 0, [IaSchemaPurpose.Multimedia] = 1, [IaSchemaPurpose.Metadata] = 2,
        [IaSchemaPurpose.Component] = 3, [IaSchemaPurpose.TemplateParameters] = 4, [IaSchemaPurpose.Bundle] = 5, [IaSchemaPurpose.Region] = 6
    };

    public static void Build(IaWorkbook wb, ImportPlan plan, SchemaResolver resolver, IReadOnlyDictionary<string, CategoryPlan> catPlans, List<Issue> issues)
    {
        var nodes = new List<Node>();
        var schemaNodes = new Dictionary<SchemaPlan, Node>();
        var catNodes = new Dictionary<CategoryPlan, Node>();
        var kwNodes = new Dictionary<(string, string), Node>();
        var order = 0;

        foreach (var cp in plan.Categories.Where(c => c.Action == PlanAction.Create))
            nodes.Add(catNodes[cp] = new Node { Rank = order++, Step = new CreationStep { Kind = PlanItemKind.Category, Category = cp, Label = $"Category: {cp.Category.Title}" } });
        foreach (var kp in plan.Keywords.Where(k => k.Action == PlanAction.Create))
            nodes.Add(kwNodes[(kp.Keyword.CategoryTitle.ToLowerInvariant(), kp.Keyword.Title.ToLowerInvariant())] = new Node
                { Rank = order++, Step = new CreationStep { Kind = PlanItemKind.Keyword, Keyword = kp, Label = $"Keyword: {kp.Keyword.CategoryTitle} / {kp.Keyword.Title}" } });
        foreach (var sp in plan.Schemas.Where(s => s.Action == PlanAction.Create))
            nodes.Add(schemaNodes[sp] = new Node
                { Rank = 1000 + PurposeRank[sp.Schema.Purpose] * 1000 + order++, Step = new CreationStep { Kind = PlanItemKind.Schema, Schema = sp, Label = $"Schema: {sp.FolderPath}/{sp.Schema.Title}" } });

        Node? SchemaNode(string reference) =>
            resolver.Resolve(reference) is { Kind: ResolutionKind.Import, Plan: { } p } && schemaNodes.TryGetValue(p, out var n) ? n : null;
        Node? CategoryNode(string? title) =>
            title is not null && catPlans.TryGetValue(title, out var cp) && catNodes.TryGetValue(cp, out var n) ? n : null;

        // ---- edges
        foreach (var (sp, node) in schemaNodes)
        {
            foreach (var f in wb.Fields.Where(f => IaWorkbook.Same(f.SchemaTitle, sp.Schema.Title)))
            {
                if (f.Type == IaFieldType.EmbeddedSchema && f.EmbeddedSchema is not null && SchemaNode(f.EmbeddedSchema) is { } e) node.Hard.Add(e);
                if (f.Type == IaFieldType.Keyword && CategoryNode(f.Category) is { } c) node.Hard.Add(c);
                if (f.Type is IaFieldType.ComponentLink or IaFieldType.MultimediaLink)
                    foreach (var t in f.AllowedTargetSchemas)
                        if (SchemaNode(t) is { } tn) node.Soft.Add(tn);
            }
            foreach (var r in wb.RegionRowsOf(sp.Schema.Title))
            {
                if (r.NestedRegionSchema is not null && SchemaNode(r.NestedRegionSchema) is { } nn) node.Soft.Add(nn);
                foreach (var s in r.AllowedComponentSchemas)
                    if (SchemaNode(s) is { } an) node.Soft.Add(an);
            }
            if (node.Soft.Remove(node)) node.Step.Deferred.Add($"{node.Step.Label} (self-reference)");
        }
        foreach (var (cp, node) in catNodes)
            if (cp.Category.KeywordMetadataSchema is not null && SchemaNode(cp.Category.KeywordMetadataSchema) is { } k) node.Soft.Add(k);
        foreach (var (key, node) in kwNodes)
        {
            var kw = node.Step.Keyword!.Keyword;
            if (CategoryNode(kw.CategoryTitle) is { } c) node.Hard.Add(c);
            if (kw.ParentKeyword is not null && kwNodes.TryGetValue((key.Item1, kw.ParentKeyword.ToLowerInvariant()), out var parent)) node.Hard.Add(parent);
        }
        foreach (var n in nodes)
        {
            n.Step.DependsOn.AddRange(n.Hard.Concat(n.Soft).Distinct().OrderBy(d => d.Rank).Select(d => d.Step.Label));
        }

        // ---- topological sort (Kahn); cycles broken on soft edges only
        var done = new HashSet<Node>();
        while (done.Count < nodes.Count)
        {
            var pending = nodes.Where(n => !done.Contains(n)).OrderBy(n => n.Rank).ToList();
            var ready = pending.Where(n => n.Hard.All(done.Contains) && n.Soft.All(done.Contains)).ToList();
            if (ready.Count > 0)
            {
                foreach (var n in ready) { plan.CreationOrder.Add(n.Step); done.Add(n); }
                continue;
            }
            // Cycle: create the first item whose hard dependencies exist; its pending soft references are applied afterwards.
            var breaker = pending.FirstOrDefault(n => n.Hard.All(done.Contains));
            if (breaker is null)
            {
                foreach (var n in pending)
                {
                    var row = n.Step.Schema?.Schema.Row ?? n.Step.Keyword?.Keyword.Row ?? n.Step.Category?.Category.Row ?? 0;
                    issues.Add(new Issue(IssueLevel.Error, n.Step.Kind == PlanItemKind.Schema ? IaFormat.SchemasSheet : n.Step.Kind == PlanItemKind.Keyword ? IaFormat.KeywordsSheet : IaFormat.CategoriesSheet,
                        row, n.Step.Label, "Circular dependency that cannot be resolved (e.g. embedded schemas embedding each other)."));
                    plan.CreationOrder.Add(n.Step); done.Add(n);
                }
                break;
            }
            foreach (var d in breaker.Soft.Where(d => !done.Contains(d)))
                breaker.Step.Deferred.Add(d.Step.Label);
            plan.CreationOrder.Add(breaker.Step); done.Add(breaker);
        }

        foreach (var s in plan.CreationOrder.Where(s => s.Deferred.Count > 0))
            issues.Add(new Issue(IssueLevel.Info, "Plan", 0, s.Label,
                $"Circular reference – created first, then updated with: {string.Join(", ", s.Deferred)}."));
    }
}
