using Modelry.Core.Gateway;
using Modelry.Core.Model;

namespace Modelry.Core.Import;

/// <summary>
/// Executes a validated plan:
///   step 2 – folders (parents first);
///   step 3 – categories, keywords and schemas in dependency order (plan.CreationOrder): every item is created after
///            everything it references, with its complete definition (embedded schemas, categories, allowed link
///            targets, region definition, keyword metadata schema);
///   step 4 – only references that were part of a circular dependency are applied afterwards.
/// Failures are logged; items that depend on a failed item are reported and skipped.
/// </summary>
public sealed class ImportExecutor
{
    private readonly ITridionGateway _gw;
    private readonly string _comment;
    public ImportExecutor(ITridionGateway gw, string checkInComment) { _gw = gw; _comment = checkInComment; }

    public async Task<ImportResult> ExecuteAsync(IaWorkbook wb, ImportPlan plan, ImportContext ctx, CancellationToken ct = default,
        IProgress<ImportProgress>? progress = null)
    {
        var result = new ImportResult();
        var tracker = new ProgressTracker(progress, plan.FoldersToCreate.Count + plan.CreationOrder.Count);
        void Log(IssueLevel lvl, string step, string item, string msg, string? id = null)
        {
            var e = new LogEntry(DateTime.UtcNow, lvl, step, item, msg, id);
            result.Log.Add(e);
            tracker.Entry(e);
        }

        if (plan.HasErrors) { Log(IssueLevel.Error, "Validate", "Plan", "Plan has errors – nothing executed."); return result; }
        try
        {
            await new Run(this, wb, plan, ctx, ct, result, tracker, Log).ExecuteAsync();
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            Log(IssueLevel.Warning, "Cancelled", "Import", "Cancelled by user – items created before cancellation remain in Tridion.");
        }
        Log(IssueLevel.Info, "Done", "Summary", $"Created {result.Created}, skipped {result.Skipped}, failed {result.Failed} schema(s){(result.Cancelled ? " – cancelled" : "")}.");
        return result;
    }

    /// <summary>State of one execution (id registries, failures, deferred work).</summary>
    private sealed class Run
    {
        private readonly ImportExecutor _x;
        private readonly IaWorkbook _wb;
        private readonly ImportPlan _plan;
        private readonly ImportContext _ctx;
        private readonly CancellationToken _ct;
        private readonly ImportResult _result;
        private readonly ProgressTracker _tracker;
        private readonly Action<IssueLevel, string, string, string, string?> _log;
        private readonly SchemaResolver _resolver;

        private readonly Dictionary<string, string> _folderIds;
        private readonly Dictionary<string, string> _categoryIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string, string), string> _keywordIds = new();
        private readonly Dictionary<SchemaPlan, string> _schemaIds = new();
        private readonly HashSet<object> _failed = new();          // SchemaPlan / CategoryPlan / KeywordPlan
        private readonly List<SchemaPlan> _deferredSchemas = new();
        private readonly List<CategoryPlan> _deferredCategories = new();

        public Run(ImportExecutor x, IaWorkbook wb, ImportPlan plan, ImportContext ctx, CancellationToken ct, ImportResult result,
            ProgressTracker tracker, Action<IssueLevel, string, string, string, string?> log)
        {
            _x = x; _wb = wb; _plan = plan; _ctx = ctx; _ct = ct; _result = result; _tracker = tracker; _log = log;
            _resolver = new SchemaResolver(plan.Schemas, ctx.ExistingSchemas);
            _folderIds = new Dictionary<string, string>(ctx.FolderIdsByPath, StringComparer.OrdinalIgnoreCase);
            foreach (var c in ctx.Categories) _categoryIds[c.Title] = c.Id;
            foreach (var (catId, list) in ctx.KeywordsByCategoryId)
            {
                var catTitle = ctx.Categories.First(c => c.Id == catId).Title.ToLowerInvariant();
                foreach (var k in list) _keywordIds[(catTitle, k.Title.ToLowerInvariant())] = k.Id;
            }
            foreach (var cp in plan.Categories.Where(c => c.Action != PlanAction.Create)) _categoryIds[cp.Category.Title] = cp.ExistingId!;
            foreach (var kp in plan.Keywords.Where(k => k.Action != PlanAction.Create))
                _keywordIds[(kp.Keyword.CategoryTitle.ToLowerInvariant(), kp.Keyword.Title.ToLowerInvariant())] = kp.ExistingId!;
            foreach (var sp in plan.Schemas.Where(s => s.Action == PlanAction.Skip)) { _schemaIds[sp] = sp.ExistingId!; result.Skipped++; }
        }

        private void Log(IssueLevel lvl, string step, string item, string msg, string? id = null) => _log(lvl, step, item, msg, id);

        public async Task ExecuteAsync()
        {
            await FoldersAsync();
            _tracker.Step(3, _plan.CreationOrder.Count);
            foreach (var step in _plan.CreationOrder)
            {
                _ct.ThrowIfCancellationRequested();
                _tracker.Item(step.Label);
                switch (step.Kind)
                {
                    case PlanItemKind.Category: await CategoryAsync(step.Category!); break;
                    case PlanItemKind.Keyword: await KeywordAsync(step.Keyword!); break;
                    case PlanItemKind.Schema: await SchemaAsync(step.Schema!); break;
                }
            }
            _tracker.EndStep();
            await DeferredAsync();
        }

        // ------------------------------------------------------------------ folders
        private async Task FoldersAsync()
        {
            _tracker.Step(2, _plan.FoldersToCreate.Count);
            foreach (var path in _plan.FoldersToCreate.OrderBy(p => p.Count(c => c == '/')))
            {
                _ct.ThrowIfCancellationRequested();
                _tracker.Item(path);
                var idx = path.LastIndexOf('/');
                var parentPath = idx < 0 ? "." : path[..idx];
                var title = idx < 0 ? path : path[(idx + 1)..];
                if (!_folderIds.TryGetValue(parentPath, out var parentId)) { Log(IssueLevel.Error, "Folders", path, "Parent folder missing."); continue; }
                try
                {
                    var node = await _x._gw.CreateFolderAsync(parentId, title);
                    _folderIds[path] = node.Id;
                    Log(IssueLevel.Info, "Folders", path, "Folder created.", node.Id);
                }
                catch (Exception ex)
                {
                    // Created by someone else after the check? Then use it instead of failing.
                    var existing = await Find(() => _x._gw.GetSubFoldersAsync(parentId), n => n.Title, n => n.Id, title);
                    if (existing is not null) { _folderIds[path] = existing; Log(IssueLevel.Warning, "Folders", path, AlreadyExists, existing); }
                    else Log(IssueLevel.Error, "Folders", path, ex.Message);
                }
            }
            _tracker.EndStep();
        }

        // ------------------------------------------------------------------ categories & keywords
        private async Task CategoryAsync(CategoryPlan cp)
        {
            var c = cp.Category;
            string? kmsId = null;
            if (c.KeywordMetadataSchema is not null)
            {
                kmsId = TryResolveSchema(c.KeywordMetadataSchema, out var state);
                if (kmsId is null && state == RefState.Pending) _deferredCategories.Add(cp);
                else if (kmsId is null) Log(IssueLevel.Warning, "Categories", c.Title, $"Keyword metadata schema not set: {state.ToString().ToLower()}.");
            }
            try
            {
                var id = await _x._gw.CreateCategoryAsync(_ctx.Target.PublicationId, c, kmsId);
                _categoryIds[c.Title] = id;
                Log(IssueLevel.Info, "Categories", c.Title, kmsId is null ? "Category created." : "Category created (with keyword metadata schema).", id);
            }
            catch (Exception ex)
            {
                var existing = await Find(() => _x._gw.GetCategoriesAsync(_ctx.Target.PublicationId), n => n.Title, n => n.Id, c.Title);
                if (existing is not null) { _categoryIds[c.Title] = existing; _deferredCategories.Remove(cp); Log(IssueLevel.Warning, "Categories", c.Title, AlreadyExists, existing); }
                else { _failed.Add(cp); _deferredCategories.Remove(cp); Log(IssueLevel.Error, "Categories", c.Title, ex.Message); }
            }
        }

        private async Task KeywordAsync(KeywordPlan kp)
        {
            var k = kp.Keyword;
            var item = $"{k.CategoryTitle} / {k.Title}";
            if (!_categoryIds.TryGetValue(k.CategoryTitle, out var catId)) { Fail(kp, "Keywords", item, $"category '{k.CategoryTitle}' was not created"); return; }
            var parents = new List<string>();
            if (k.ParentKeyword is not null)
            {
                if (_keywordIds.TryGetValue((k.CategoryTitle.ToLowerInvariant(), k.ParentKeyword.ToLowerInvariant()), out var pid)) parents.Add(pid);
                else { Fail(kp, "Keywords", item, $"parent keyword '{k.ParentKeyword}' was not created"); return; }
            }
            try
            {
                var id = await _x._gw.CreateKeywordAsync(catId, k, parents);
                _keywordIds[(k.CategoryTitle.ToLowerInvariant(), k.Title.ToLowerInvariant())] = id;
                Log(IssueLevel.Info, "Keywords", item, "Keyword created.", id);
            }
            catch (Exception ex)
            {
                var existing = await Find(() => _x._gw.GetKeywordsAsync(catId), n => n.Title, n => n.Id, k.Title);
                if (existing is not null)
                {
                    _keywordIds[(k.CategoryTitle.ToLowerInvariant(), k.Title.ToLowerInvariant())] = existing;
                    Log(IssueLevel.Warning, "Keywords", item, AlreadyExists, existing);
                }
                else { _failed.Add(kp); Log(IssueLevel.Error, "Keywords", item, ex.Message); }
            }
        }

        // ------------------------------------------------------------------ schemas
        private async Task SchemaAsync(SchemaPlan sp)
        {
            var item = $"{sp.FolderPath}/{sp.Schema.Title}";
            if (!_folderIds.TryGetValue(sp.FolderPath, out var folderId)) { Fail(sp, "Schemas", item, "folder not available"); return; }
            var model = BuildModel(sp, out var deferred, out var problem);
            if (model is null) { Fail(sp, "Schemas", item, problem!); return; }
            try
            {
                var id = await _x._gw.CreateSchemaAsync(folderId, model);
                _schemaIds[sp] = id; _result.Created++;
                if (deferred.Count > 0) _deferredSchemas.Add(sp);
                var refs = model.ContentFields.Concat(model.MetadataFields).Sum(f => f.AllowedTargetSchemaIds.Count + (f.EmbeddedSchemaId is null ? 0 : 1) + (f.CategoryId is null ? 0 : 1));
                Log(IssueLevel.Info, "Schemas", item,
                    $"{sp.Schema.Purpose} schema created ({model.ContentFields.Count + model.MetadataFields.Count} fields, {refs} references{(model.Region is null ? "" : ", region definition")})" +
                    (deferred.Count > 0 ? $"; to be completed after: {string.Join(", ", deferred)}." : "."), id);
            }
            catch (Exception ex)
            {
                var existing = await Find(() => _x._gw.ListSchemasInFolderAsync(folderId), n => n.Title, n => n.Id, sp.Schema.Title);
                if (existing is not null)
                {
                    _schemaIds[sp] = existing; _result.Skipped++;
                    Log(IssueLevel.Warning, "Schemas", item, AlreadyExists + " Skipped – its definition was not changed.", existing);
                }
                else Fail(sp, "Schemas", item, ex.Message);
            }
        }

        private const string AlreadyExists = "Already exists – created by someone else after the check.";

        /// <summary>After a failed create: looks for an item with the same title (case-insensitive) and returns its id.</summary>
        private static async Task<string?> Find<T>(Func<Task<IReadOnlyList<T>>> list, Func<T, string> title, Func<T, string> id, string wanted)
        {
            try
            {
                var hit = (await list()).FirstOrDefault(x => IaWorkbook.Same(title(x), wanted));
                return hit is null ? null : id(hit);
            }
            catch { return null; }
        }

        private enum RefState { Available, Pending, Failed, Missing }

        /// <summary>Resolves a schema reference to an id if it already exists / was created.</summary>
        private string? TryResolveSchema(string reference, out RefState state)
        {
            var r = _resolver.Resolve(reference);
            switch (r.Kind)
            {
                case ResolutionKind.Existing: state = RefState.Available; return r.Existing!.Id;
                case ResolutionKind.Import when _schemaIds.TryGetValue(r.Plan!, out var id): state = RefState.Available; return id;
                case ResolutionKind.Import when _failed.Contains(r.Plan!): state = RefState.Failed; return null;
                case ResolutionKind.Import: state = RefState.Pending; return null;
                default: state = RefState.Missing; return null;
            }
        }

        /// <summary>
        /// Builds the full definition. Hard references (embedded schemas, categories) must be available.
        /// Soft references (link targets, region definition) not yet available because of a cycle are returned in 'deferred'.
        /// </summary>
        private SchemaWriteModel? BuildModel(SchemaPlan sp, out List<string> deferred, out string? problem)
        {
            deferred = new List<string>(); problem = null;
            var schema = sp.Schema;
            var content = new List<FieldWriteModel>();
            var metadata = new List<FieldWriteModel>();
            foreach (var section in new[] { IaFieldSection.Content, IaFieldSection.Metadata })
            {
                foreach (var f in _wb.FieldsOf(schema.Title, section))
                {
                    string? embId = null, catId = null;
                    var targets = new List<string>();
                    if (f.Type == IaFieldType.EmbeddedSchema)
                    {
                        embId = TryResolveSchema(f.EmbeddedSchema!, out var st);
                        if (embId is null) { problem = $"embedded schema '{f.EmbeddedSchema}' is {Describe(st)}"; return null; }
                    }
                    if (f.Type == IaFieldType.Keyword && !_categoryIds.TryGetValue(f.Category!, out catId))
                    { problem = $"category '{f.Category}' was not created"; return null; }
                    foreach (var t in f.AllowedTargetSchemas)
                    {
                        var id = TryResolveSchema(t, out var st);
                        if (id is not null) targets.Add(id);
                        else if (st == RefState.Pending) deferred.Add(t);
                        else Log(IssueLevel.Warning, "Schemas", $"{schema.Title}.{f.XmlName}", $"Allowed target '{t}' left out: {Describe(st)}.");
                    }
                    var fw = new FieldWriteModel { Field = f, EmbeddedSchemaId = embId, CategoryId = catId, AllowedTargetSchemaIds = targets };
                    (section == IaFieldSection.Content ? content : metadata).Add(fw);
                }
            }

            RegionWriteModel? region = null;
            if (schema.Purpose == IaSchemaPurpose.Region)
            {
                var rows = _wb.RegionRowsOf(schema.Title).ToList();
                if (rows.Count > 0)
                {
                    var constraint = rows.FirstOrDefault(x => x.RowType == IaRegionRowType.Constraint);
                    var r = new RegionWriteModel { MinOccurs = constraint?.MinOccurs, MaxOccurs = constraint?.MaxOccurs };
                    var regionDeferred = new List<string>();
                    foreach (var c in rows.Where(x => x.RowType == IaRegionRowType.Constraint))
                    {
                        foreach (var s in c.AllowedComponentSchemas)
                        {
                            var id = TryResolveSchema(s, out var st);
                            if (id is not null) r.AllowedSchemaIds.Add(id);
                            else if (st == RefState.Pending) regionDeferred.Add(s);
                            else Log(IssueLevel.Warning, "Schemas", schema.Title, $"Allowed component schema '{s}' left out: {Describe(st)}.");
                        }
                        foreach (var t in c.AllowedComponentTemplates)
                            if (_ctx.ComponentTemplates.FirstOrDefault(x => IaWorkbook.Same(x.Title, t)) is { } tpl) r.AllowedTemplateIds.Add(tpl.Id);
                    }
                    foreach (var n in rows.Where(x => x.RowType == IaRegionRowType.NestedRegion))
                    {
                        var id = TryResolveSchema(n.NestedRegionSchema!, out var st);
                        if (id is not null) r.NestedRegions.Add(new NestedRegionWriteModel { Name = n.NestedRegionName!, RegionSchemaId = id, Mandatory = n.Mandatory });
                        else if (st == RefState.Pending) regionDeferred.Add(n.NestedRegionSchema!);
                        else Log(IssueLevel.Warning, "Schemas", schema.Title, $"Nested region '{n.NestedRegionName}' left out: {Describe(st)}.");
                    }
                    // A region definition is written as a whole – apply it now only if every reference is available.
                    if (regionDeferred.Count == 0) region = r; else deferred.AddRange(regionDeferred);
                }
            }

            var mmTypes = schema.AllowedMultimediaTypes
                .SelectMany(ext => _ctx.MultimediaTypes.Where(m => m.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)).Select(m => m.Id))
                .Distinct().ToList();
            return new SchemaWriteModel
            {
                Schema = schema, CheckInComment = _x._comment, ContentFields = content, MetadataFields = metadata,
                MultimediaTypeIds = mmTypes, Region = region
            };
        }

        private static string Describe(RefState s) => s switch
        {
            RefState.Failed => "a dependency that failed to be created",
            RefState.Pending => "not created yet",
            RefState.Missing => "not found",
            _ => "available"
        };

        // ------------------------------------------------------------------ deferred (circular) references
        private async Task DeferredAsync()
        {
            var schemas = _deferredSchemas.Where(s => !_failed.Contains(s)).ToList();
            var categories = _deferredCategories.Where(c => !_failed.Contains(c) && _categoryIds.ContainsKey(c.Category.Title)).ToList();
            _tracker.AddToTotal(schemas.Count + categories.Count);
            _tracker.Step(4, schemas.Count + categories.Count);
            foreach (var sp in schemas)
            {
                _ct.ThrowIfCancellationRequested();
                var item = $"{sp.FolderPath}/{sp.Schema.Title}";
                _tracker.Item("Schema: " + item);
                var model = BuildModel(sp, out var stillDeferred, out var problem);
                if (model is null) { Log(IssueLevel.Warning, "Deferred references", item, $"Not completed: {problem}.", _schemaIds[sp]); continue; }
                foreach (var d in stillDeferred) Log(IssueLevel.Warning, "Deferred references", item, $"'{d}' could not be applied (not created).", _schemaIds[sp]);
                try
                {
                    await _x._gw.UpdateSchemaAsync(_schemaIds[sp], model);
                    Log(IssueLevel.Info, "Deferred references", item, "Circular references applied (allowed targets / region definition).", _schemaIds[sp]);
                }
                catch (Exception ex) { Log(IssueLevel.Error, "Deferred references", item, ex.Message, _schemaIds[sp]); }
            }
            foreach (var cp in categories)
            {
                _ct.ThrowIfCancellationRequested();
                _tracker.Item("Category: " + cp.Category.Title);
                var kmsId = TryResolveSchema(cp.Category.KeywordMetadataSchema!, out var st);
                if (kmsId is null) { Log(IssueLevel.Warning, "Deferred references", cp.Category.Title, $"Keyword metadata schema not set: {Describe(st)}."); continue; }
                try
                {
                    await _x._gw.SetCategoryKeywordMetadataSchemaAsync(_categoryIds[cp.Category.Title], kmsId);
                    Log(IssueLevel.Info, "Deferred references", cp.Category.Title, "Keyword metadata schema set.", _categoryIds[cp.Category.Title]);
                }
                catch (Exception ex) { Log(IssueLevel.Error, "Deferred references", cp.Category.Title, ex.Message); }
            }
            _tracker.EndStep();
        }

        private void Fail(object planItem, string step, string item, string reason)
        {
            _failed.Add(planItem);
            if (planItem is SchemaPlan) _result.Failed++;
            Log(IssueLevel.Error, step, item, $"Not created: {reason}.");
        }
    }
}
