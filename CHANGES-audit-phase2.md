# Modelry – Audit, phase 2 (9 Oct 2026)

Builds on phase 1 (CHANGES-audit.md). Adds:
- usage and orphan checks
- publish health
- content-model hygiene
- site-structure checks
- multimedia checks
- saved audits with comparison
- a health score

Workflow audit (and checked-out items) is not included, as agreed. No new NuGet packages.

## What's new on the dashboard
- **Health score** 0–100 with a grade (A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 40, E), plus a score per category: Content model,
  Publishing, Site structure, Multimedia, Usage.
  - Each check scores 1 − flagged / checked and is weighted by severity (Error 3, Warning 2, Info 1).
  - A category is the weighted average of its checks; the overall score is the average of the categories that could be checked.
  - Checks that could not run (data not on this CMS version, limit hit, needs a deep scan) are left out and shown as
    "Not checked" / "Needs deep scan" with the reason – they never count as pass or fail.
- **Findings** grouped by category. Each check shows how many items it flagged out of how many it checked.
  Click a check to list the flagged items with the reason; click an item for the pop-up, which now starts with its findings.
- **Detail tables**:
  - a Findings column (check IDs, hover for the title) and a "With findings" filter;
  - for components, multimedia and pages, one column per publish target (e.g. Staging, Live) with the last publish date there, sortable;
  - new columns:
    - Component Templates: used on pages
    - Components: on pages, used by
    - Multimedia: file name, type, size, used by
    - Categories: used in schemas
    - Keywords: used by
    - Structure Groups: sub-groups
    - Pages: real URL with file name and extension, file name, component presentations
- **Run deep scan** (button, or tick "Deep scan" when starting). Reads "where used" for every component, multimedia component,
  keyword and category. Counts usage in any publication, so an item in a master publication used only by child sites
  still counts as used.
- **Saved audits.**
  - Every finished audit is saved on the Modelry server and listed on the Audit page; any of them can be reopened as a dashboard.
  - "Compare with" on the dashboard shows:
    - health then and now, per category;
    - the score over time across all saved audits;
    - counts per building block, with items added / removed and newly / no-longer published;
    - each check's count then and now.
  - Both audits are re-checked with the current rules first, so a rule change in Modelry doesn't look like a change in the site.
- **Excel export**:
  - health score and category scores on the Summary sheet;
  - a new Findings sheet (every check, whether it ran, checked / flagged counts, every flagged item);
  - a Findings column and a Last published column on each building-block sheet.

## The checks
| ID | Category | Severity | Check | Needs |
|---|---|---|---|---|
| M01 | Content model | Warning | Component schemas without a Component Template | template reads |
| M02 | Content model | Warning | Component Templates without a DXA view | template reads |
| M03 | Content model | Warning | Component Templates not linked to any schema | template reads |
| M04 | Content model | Info | Page Templates without includes | template reads |
| M05 | Content model | Warning | Duplicate titles (schemas, CTs, PTs of the same kind) | – |
| M06 | Content model | Info | Suspicious names (copy, test, temp, old, backup, delete …, stray spaces) | – |
| M07 | Content model | Info | Naming convention per building block (regex in Audit:Naming) | configuration |
| P01 | Publishing | Warning | Changed since last publish | per-item publish info |
| P02 | Publishing | Info | Published to some targets only (e.g. Staging but not Live) | 2+ targets |
| P03 | Publishing | Info | Pages never published | publish status |
| P04 | Publishing | Info | Published pages not changed for Audit:StaleMonths (12) | publish status + dates |
| S01 | Site structure | Info | Empty Structure Groups | – |
| S02 | Site structure | Info | Inactive Structure Groups | SG reads |
| S03 | Site structure | Warning | Pages without component presentations | page reads |
| S04 | Site structure | Error | Duplicate page file names in one Structure Group (same URL) | page reads |
| S05 | Site structure | Info | File names with capitals, spaces or special characters | page reads |
| MM01 | Multimedia | Warning | Files over Audit:LargeFileKb (2048 KB) | multimedia reads |
| U01 | Usage | Warning | Schemas with no components | where-used lists |
| U02 | Usage | Info | Component Templates not used on any page | page reads |
| U03 | Usage | Info | Page Templates not used by any page | where-used or page reads |
| U04 | Usage | Warning | Components not used anywhere | deep scan |
| U05 | Usage | Info | Components not placed on any page (standard scan only; replaced by U04 in a deep scan) | page reads |
| U06 | Usage | Warning | Unused multimedia | deep scan |
| U07 | Usage | Info | Unused keywords (non-abstract) | deep scan |
| U08 | Usage | Info | Categories not used in any schema | deep scan |

## How the Core Service scan grew (steps 7 and 8)
- **Pages**: each page is read (up to Audit:MaxPageReads 3000) for its file name and component presentations.
  - Covers native regions on 9.x+, including nested regions, and the classic list.
  - Page Templates are read once for their file extension, to build the URL.
  - On a CM without where-used lists (older 9.x), the page reads also fill each page's Page Template.
- **Multimedia**: each multimedia component is read (up to Audit:MaxMultimediaReads 5000) for file name, size and type. The binary itself is not downloaded.
- **Deep scan**: one where-used list per category, keyword, multimedia component and component, in that order, up to Audit:MaxUsageItems (20000) in total.
  Kinds that don't fit the remaining budget are marked "not read" rather than partly read. Failed lists are left out of the usage checks, never counted as unused.

New settings (appsettings `Audit`), all with defaults:
- `MaxPageReads` 3000, `MaxMultimediaReads` 5000, `MaxUsageItems` 20000
- `LargeFileKb` 2048, `StaleMonths` 12
- `Naming` {} – e.g. `"ComponentTemplates": "^[A-Z][A-Za-z ]+ CT$"`
- `SaveSnapshots` true, `SnapshotFolder` "App_Data/audit-snapshots", `KeepSnapshots` 20

**IIS:** the app pool identity needs write access to the snapshot folder (by default `App_Data\audit-snapshots` under the
site folder). If it can't write, the audit still works and the dashboard notes that it was not saved.
Saved audits are grouped by Core Service URL and publication, and are visible to everyone using this Modelry instance against that CMS.

## New
- src/Modelry.Core/Audit/AuditRules.cs – the checks, findings, health score
- src/Modelry.Core/Audit/AuditHistory.cs – save / load (gzip JSON + meta), comparison
- src/Modelry.Web/Services/AuditSnapshotStore.cs – saved audits on disk, pruning, safe ids
- src/Modelry.Web/Views/Audit/Compare.cshtml

## Changed
- src/Modelry.Core/Audit/AuditModels.cs – snapshot: Deep, SavedId, Findings, Rules, Health; new options; `IAuditGateway.ScanPublicationAsync(…, deep, …)`
- src/Modelry.Core/Audit/AuditCatalog.cs – new columns, scan steps 7–8, deep-scan columns
- src/Modelry.Core/Audit/AuditFinalizer.cs – sub-groups, templates/components on pages, deep columns marked, rules run
- src/Modelry.Core/Audit/AuditQuery.cs – findings filter, per-target sort
- src/Modelry.Core/Audit/AuditExcelWriter.cs – health, Findings sheet, Findings / Last published columns
- src/Modelry.Core/Gateway/InMemoryTridionGateway.Audit.cs – demo: pages, multimedia, deep usage and deliberate issues to find
- src/Modelry.Tridion/CoreServiceTridionGateway.Audit.cs – page reads, multimedia reads, deep where-used, SG parents
- src/Modelry.Web/Controllers/AuditController.cs – deep scan, save, Open (saved), Compare, findings API, findings in rows/pop-up
- src/Modelry.Web/Services/AuditJobs.cs – deep flag, opened-from-history flag
- src/Modelry.Web/Models/AuditViewModels.cs – saved audits, compare model, rule titles for the script
- src/Modelry.Web/Views/Audit/Index.cshtml – deep-scan option, saved audits list
- src/Modelry.Web/Views/Audit/Dashboard.cshtml – health, findings, deep scan, compare, findings filter
- src/Modelry.Web/wwwroot/js/audit.js – findings list, per-target columns, findings column and filter
- src/Modelry.Web/wwwroot/css/site.css – health, findings, compare (appended)
- src/Modelry.Web/Program.cs – AuditSnapshotStore
- src/Modelry.Web/appsettings.json – new Audit settings

## What was and wasn't tested
- **Compiled:** the whole solution except the real Core Service gateway, with stand-ins for the NuGet packages blocked in the sandbox.
- **Demo mode, end to end in a browser:**
  - standard and deep scan, health and findings
  - finding → item list → pop-up
  - findings filter, per-target sort
  - compare page, saved-audit list and reopen
  - phone width
- **Save / load and comparison:** a round trip was checked in a unit probe – every count, value, publish target, finding and the health score survive.
- **Core Service scan:** compiled and run against a fake proxy that imitates a 10.x CM (everything available, deep scan) and a 9.1 CM
  (no extended list columns, no where-used, no publish info). The 9.1 run completes and the affected checks show as "Not checked".
- **Not run against a real Tridion yet. Excel export not run** (ClosedXML unavailable here).

## VERIFY on the first real run
Phase-1 items, plus:
- `PageData.Regions` / `ComponentPresentations`, `PageData.FileName`, `PageTemplateData.FileExtension`
- `ComponentData.BinaryContent` (`Filename`, `FileSize`, `MultimediaType`)
- where-used with `ItemType.Publication` / `Folder` / `StructureGroup` in `UsingItemsFilterData.ItemTypes`
- deep-scan duration on SABIC: roughly one call per item at 4 in parallel – try it on a smaller publication first.
