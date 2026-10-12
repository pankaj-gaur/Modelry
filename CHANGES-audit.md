# Modelry – Audit, phase 1 (9 Oct 2026)

After sign-in, Tridion users choose **Build from an IA workbook** (the existing wizard) or **Audit an existing site**.
Audit: choose a publication → background scan with live progress → dashboard → detail table per building block →
item pop-up. Read-only: nothing in the CMS is created, changed or published.

No new NuGet packages.

## What the audit shows
- **Dashboard tiles**, in three groups:
  - Content model: Schemas (split by purpose), Component Templates, Page Templates
  - Content: Components, Multimedia, Pages, each with a published / not-published bar
  - Taxonomy and site structure: Categories (with their keyword count), Keywords, Structure Groups (inactive ones counted)
- **Every tile** shows its BluePrint origin split: local, shared, localized.
- **Detail table** (click a tile):
  - Server-side search, Local / Shared / Localized filter, Published / Not published / Unknown filter (Components, Multimedia, Pages)
  - Sortable columns, 100 rows per page
  - Common columns: Title + ID, folder or Structure Group path, BluePrint (with "from" publication), last modified, modified by
  - Schemas: purpose, components using it, Component Templates linked
  - Component Templates: view, controller, linked schemas, dynamic
  - Page Templates: view, includes, pages using it
  - Components / Multimedia: schema, publish status, published-to targets (dates on hover)
  - Categories: keywords, XML name, publishable
  - Keywords: category, key, abstract
  - Structure Groups: directory, URL path, pages, active
  - Pages: URL path (Structure Group), Page Template, publish status, targets
- **Pop-up** (click a row), read live from the CMS:
  - General, versions and BluePrint details
  - Schemas: fields and region definition
  - Templates: DXA metadata
  - Components: content and metadata XML; multimedia: file name, size and type
  - Pages: file name, Page Template, component presentations per region
  - Categories: their keywords
  - Components and pages: where and when they were published
- **Export to Excel**: a Summary sheet (publication, CMS version, run by, counts, notes, checks not available) plus one sheet per
  building block with every row. Columns that could not be filled are marked "(n/a)" with the reason as a cell comment.
- Each finished audit is reused for 30 minutes; "Run again" forces a fresh read. Audits are kept for 2 hours per session.
- "Audit CMS" link in the header; a "What would you like to do?" page after sign-in.

## Tridion versions (9.1 – 10.x)
- **One proxy for every version.** The CoreService201701 proxy is used unchanged.
- **Version read at login.** The CM version comes from the API version already read at login (`CmsCapabilities`) and is
  shown as a badge in the header tooltip, on the chooser, the audit rail and the dashboard, and in the Excel summary.
- **Version-tolerant scan.** Tables come from `GetListXml`; columns are XML attributes.
  - An attribute an older CM does not return leaves the cell empty, and the column is reported as "n/a" with the reason, instead of failing.
  - Every per-item call (templates, Structure Groups, categories, where-used lists, publish info) is caught.
  - A failure is noted and the scan carries on. When a call fails entirely, the scan falls back:
    - **publish info** → the list's published flag;
    - **where-used lists** → schema and Page Template are shown as "n/a", and multimedia is recognised by its list icon.
- Tested against simulated 10.x-like and 9.1-like responses: the 9.1 case completes, with the missing columns marked n/a and explained in the notes.
- **Below 9.1:** sign-in shows a warning (not blocked).
- **OAuth login failure:** the error now suggests Windows authentication if the CM is older than 9.5, since Access Management is not available there.
- **Build on 9.x:** the Build card and the wizard show a warning ("tested on Tridion Sites 10 – check the results in the CMS"). Nothing is blocked.

## How the Core Service scan works
1. Folder tree read level by level, one `GetListXml` per folder (Extended columns), up to `MaxParallelCalls` in parallel.
   This gives schemas, templates and components with their folder path.
2. Structure Group tree the same way, giving pages; each Structure Group is then read for its directory and active flag.
3. Categories of the publication, then the keywords of each category.
4. Schema purposes (one list call; a read only where the list omits the purpose). Templates are read one by one for the DXA columns.
5. Which schema each component uses, and which Page Template each page uses: one "where used" list per schema / Page Template
   (not one read per item). Components on Multimedia schemas move to the Multimedia table.
6. Publish status:
   - per item (`GetListPublishInfo`) up to `MaxPublishInfoItems`, which gives targets and dates;
   - above that limit, from the list's `IsPublished` flag (published / not published only).

Limits (appsettings `Audit` section): MaxParallelCalls 4, MaxTemplateReads 1000, MaxStructureGroupReads 5000,
MaxCategoryReads 500, PublishInfoMode Auto (Auto / PerItem / List / Off), MaxPublishInfoItems 10000, ReuseMinutes 30, PageSize 100.

## New
- src/Modelry.Core/Audit/AuditModels.cs – rows, snapshot, detail, options, `IAuditGateway`
- src/Modelry.Core/Audit/AuditCatalog.cs – tile titles, columns per building block, scan steps
- src/Modelry.Core/Audit/AuditFinalizer.cs – cross-counts (components per schema, pages per template / Structure Group, keywords per category)
- src/Modelry.Core/Audit/AuditQuery.cs – server-side filter, sort, paging
- src/Modelry.Core/Audit/AuditExcelWriter.cs – Excel export
- src/Modelry.Core/Gateway/CmsCapabilities.cs – version parsing and feature flags
- src/Modelry.Core/Gateway/InMemoryTridionGateway.Audit.cs – demo audit (real demo items + deterministic sample content)
- src/Modelry.Tridion/CoreServiceTridionGateway.Audit.cs – Core Service scan and item detail
- src/Modelry.Web/Controllers/AuditController.cs – pages, JSON API (status, cancel, rows, item), export
- src/Modelry.Web/Services/AuditJobs.cs – background runs, reuse, cancel
- src/Modelry.Web/Models/AuditViewModels.cs
- src/Modelry.Web/Views/Audit/Index.cshtml, Dashboard.cshtml, Unsupported.cshtml
- src/Modelry.Web/Views/Home/Choose.cshtml
- src/Modelry.Web/Views/Shared/_AuditLayout.cshtml, _AuditRail.cshtml
- src/Modelry.Web/wwwroot/js/audit.js

## Changed
- src/Modelry.Core/Gateway/InMemoryTridionGateway.cs – `partial`, implements `IAuditGateway`
- src/Modelry.Tridion/CoreServiceTridionGateway.cs – `partial`, implements `IAuditGateway` (declaration line only)
- src/Modelry.Web/Controllers/HomeController.cs – after sign-in, Tridion goes to the Build / Audit chooser
- src/Modelry.Web/Controllers/AccountController.cs – below-minimum warning; OAuth hint for older CMs
- src/Modelry.Web/Services/TridionSession.cs – `Capabilities`
- src/Modelry.Web/Program.cs – `AuditOptions`, `AuditJobStore`
- src/Modelry.Web/appsettings.json – `Audit` section
- src/Modelry.Web/Views/Shared/_Layout.cshtml – "Audit CMS" link, version in the connection tooltip, `TempData["Notice"]`
- src/Modelry.Web/Views/Shared/_WizardLayout.cshtml – 9.x warning on Build steps
- src/Modelry.Web/Views/Start/Index.cshtml – audit bullet, supported versions
- src/Modelry.Web/Views/_ViewImports.cshtml – `Modelry.Core.Audit`, `Modelry.Core.Gateway`
- src/Modelry.Web/wwwroot/css/site.css – chooser, tiles, audit table, pop-up (appended)

## What was and wasn't tested
- **Compiled:** the whole solution except the real Core Service gateway, with the blocked NuGet packages (ClosedXML,
  AngleSharp, Serilog, WCF) replaced by stand-ins.
- **Demo mode, end to end in a browser:**
  - chooser → publication → progress → dashboard → every table, filters, sort → pop-ups → stop an audit
  - at 1440 px and 390 px
- **`CoreServiceTridionGateway.Audit.cs`:** compiled and run against a hand-written fake proxy, simulating a 10.x-like and a 9.1-like CM. **Not run against a real Tridion instance.**
- **Excel export:** compiled, **not run** (ClosedXML couldn't be restored in the sandbox). It uses the same ClosedXML calls as `IaExcelWriter`, plus cell comments, font size and date format.

## VERIFY on the first real run (per CM version)
- `GetListXml` returns `XElement` in your proxy (`XmlElement` is handled too).
- The attributes returned with `ListBaseColumns.Extended`:
  - the dashboard's audit notes list "List columns returned by this CMS";
  - Modelry expects `Modified`, `FromPub`, `IsLocalized`, `IsPublished`, `Icon`;
  - who changed an item last is normally not in list data, so that column is filled from the pop-up only.
- `UsingItemsFilterData.InRepository` + `IncludedVersions` (results are also filtered by publication number, so a missing property only costs speed).
- `PublishInfoData.TargetType` / `PublishedAt` / `Repository` – the target names shown in "Published to".
- `StructureGroupData.Directory` / `IsActive`.
- The format of `GetApiVersion` on 9.1 / 9.5 / 9.6 (expected "9.5.0.xxxx"). If it is a different scheme, the badge shows the raw string and nothing is switched off.
