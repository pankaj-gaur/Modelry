# Modelry – Audit for Adobe Experience Manager (9 Oct 2026)

The audit from phases 1 and 2 now works for AEM as well as Tridion. That covers the dashboard, health score, findings, deep scan,
saved audits, compare and Excel. It supports AEM 6.5 and AEM as a Cloud Service with one implementation, using the existing AEM sign-in
(user + password, or access token). It is read-only. No new NuGet packages.

**Not tested against a real AEM instance.** It was tested against a simulated AEM author (see "Testing" below).

## How it reads AEM
Only standard read APIs on the author instance:
- QueryBuilder (`/bin/querybuilder.json`, paged, `p.hits=full`) for lists;
- the Sling GET servlet (`.json`, `.N.json`, `.infinity.json`) for single nodes and page content.

**Scope.** You pick a **site root** under `/content`. The configuration folder comes from the site's `cq:conf` (otherwise
`/conf/<site>`) and the DAM folder is `/content/dam/<site>`. Both can be overridden on the start page under "Scope: configuration and DAM folders".

| Audit tile | AEM | Read from |
|---|---|---|
| Content Fragment Models | models + enabled status | `<conf>/settings/dam/cfm/models` |
| Components | `cq:Component` (AEM's own folders skipped), resource type, group, super type, pages using it | `/apps` (Audit:AemComponentsRoot to narrow) |
| Templates | editable templates, enabled / disabled, pages using them | `<conf>/settings/wcm/templates` |
| Fragments | Content Fragments (model, on pages, used by) + Experience Fragments | DAM folder, `/content/experience-fragments/<site>` |
| Assets | format, size, dc:title, processing state, used by | DAM folder |
| Tag namespaces / Tags | tag ID, used by | `/content/cq:tags` |
| Site sections | pages with child pages; language roots marked | page tree |
| Pages | template, page name, component count, links, references | page tree + each page's content |

**Publish status:** from the replication properties.
- AEM as a Cloud Service: `cq:lastReplicationAction_publish` / `_preview`, so separate Publish and Preview columns with dates.
- AEM 6.5: `cq:lastReplicationAction`, so a single Publish column.
- Only "Activate" counts as published.

**MSM:** from the `jcr:content` mixins.
- `cq:LiveRelationship` → "Live copy"
- `cq:LiveSyncCancelled` → "Inheritance cancelled"

The blueprint is read from the site root's `cq:LiveSyncConfig`.

**Page crawl (standard scan):** each page's `jcr:content` is read once, up to Audit:MaxPageReads. It yields:
- the components on the page (by resource type);
- fragments, assets and tags it references;
- links to other pages of the site.

**Deep scan:**
- also reads every Content Fragment and Experience Fragment;
- then counts, for every fragment, asset and tag, how many pages, fragments and assets of this site refer to it.

Other sites using the same items are not counted; the dashboard notes this.

**Version:**
- Hosts `*.adobeaemcloud.com` → "AEM as a Cloud Service".
- Otherwise the version is read from `/libs/granite/operations/content/systemoverview/export.json` when the user may read it (usually admin).
- Failing that → "AEM (version unknown)". It never blocks sign-in.

## Checks for AEM
Shared with Tridion, using AEM wording:
- duplicate titles, suspicious names, naming convention;
- changed since last publish, published to some tiers only (pages; Preview vs Publish on Cloud Service), pages never published, stale published pages;
- pages without components, page names with capitals / spaces / special characters, large assets;
- Content Fragment Models with no fragments, components not used on any page, templates not used;
- fragments not used anywhere / not placed on a page, unused assets, unused tags.

New for AEM:
- **A01 Pages on a disabled or missing template** (Warning)
- **A02 Assets without a title or description** (images; alt-text source) (Info)
- **A03 Assets not fully processed** (`dam:assetState` ≠ processed) (Warning)
- **A04 Unpublished pages still linked from published pages** (404s on the live site) (Warning)
- **A05 Live copies with cancelled inheritance** (Info)

Tridion-only checks are not shown for AEM: DXA view, includes, CTs without schemas, schemas without CTs, empty or inactive Structure Groups, duplicate file names in a Structure Group, categories unused in schemas.

## Other changes
- AEM users also get the "What would you like to do?" page (Build preview / Audit) and the "Audit CMS" link.
- **Demo mode for AEM** ("Explore with demo data" on the AEM sign-in). It runs the real AEM audit code against an in-memory demo author
  that has a small corporate site with deliberate issues, so the AEM audit can be shown without an instance.
- Saved audits are grouped by AEM author URL; AEM demo audits are kept separately from Tridion demo audits.
- Excel export, compare and every label use AEM terms for AEM audits (Site, MSM, Fragments, Assets, Tags, Site sections …).

## New
- src/Modelry.Core/Audit/Aem/AemJson.cs – HTTP/JSON client (Basic or Bearer), QueryBuilder paging, Sling date parsing
- src/Modelry.Core/Audit/Aem/AemAuditGateway.cs – the AEM scan, page crawl, deep scan, item pop-up
- src/Modelry.Core/Audit/Aem/DemoAem.cs – demo AEM author (in-memory repository + HTTP handler, 6.5 or Cloud Service style)

## Changed
- Core:
  - Audit/AuditCatalog.cs – titles, columns, steps and origin labels per CMS
  - Audit/AuditRules.cs – checks per CMS, AEM wording, A01–A05, findings per kind
  - Audit/AuditModels.cs – `Snapshot.Cms`, `AuditRoot`, `IAuditGateway.ListAuditRootsAsync`, per-run scope options
  - Audit/AuditFinalizer.cs – AEM paths as item keys
  - Audit/AuditExcelWriter.cs – AEM labels, findings per kind
  - Gateway/CmsCapabilities.cs – AEM versions
  - Gateway/InMemoryTridionGateway.Audit.cs – publications as audit roots
- Tridion: CoreServiceTridionGateway.Audit.cs – publications as audit roots
- Web:
  - Controllers/AuditController.cs – opens a Tridion or AEM gateway; site roots; scope fields
  - Controllers/AccountController.cs – AEM demo, AEM version at sign-in
  - Controllers/HomeController.cs – chooser for AEM
  - Services/TridionSession.cs – AEM connection, AEM demo
  - Services/AemConnectionClient.cs – version detection
  - Services/AuditJobs.cs – step names per CMS
  - Models/AuditViewModels.cs – audit roots, CMS-aware client model
  - Program.cs – "aem-audit" HttpClient (timeout and proxy from the Tridion settings)
  - Views: Audit/Index, Audit/Dashboard, Audit/Compare, Home/Choose, Start/Index, Account/Login (demo for AEM), Shared/_Layout, Shared/_AuditRail
  - wwwroot/js/audit.js – origin labels per CMS

## Testing
- **Against the simulated author:** the AEM audit was run in both styles (AEM as a Cloud Service, with Publish + Preview tiers; and AEM 6.5, with a single tier), standard and deep, including every pop-up. Every check that should fire on the demo site's deliberate issues did.
- **In the browser:** sign-in demo → chooser → site list → progress (AEM steps) → dashboard → every table and pop-up → findings → deep scan → compare.
- **Tridion regression:** the Tridion demo audit and the fake-proxy Core Service probe were rerun unchanged.
- **Not tested against a real AEM instance.** The simulated author answers the way I expect AEM to; the points below are where a real instance may differ.

## VERIFY on the first real AEM run
- **Permissions:** QueryBuilder needs read access to `/content`, `/conf`, `/apps` and `/content/cq:tags`.
  - On AEM as a Cloud Service, use a technical account or a local development token with read rights there.
  - `/apps` is not readable for many authors; components then show as "not found" in the audit notes.
- **Query cost:** QueryBuilder with `p.hits=full&p.nodedepth=1–2` on large DAM folders. Watch the author's load and query-limit warnings, and start with a small site.
- **Large pages:** `.infinity.json` can answer "300 Multiple Choices"; the code then falls back to `.5.json`.
- **Property names to confirm:**
  - template `jcr:content/status`;
  - Content Fragment model `jcr:content/data/cq:model`;
  - model fields under `jcr:content/model/cq:dialog/content/items`;
  - `dam:assetState`;
  - Cloud Service replication property suffixes (`_publish`, `_preview`).
- **Version endpoint:** `systemoverview/export.json` usually needs admin rights; otherwise the version shows as unknown, which affects nothing else.
