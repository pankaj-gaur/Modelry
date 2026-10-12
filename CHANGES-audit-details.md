# Modelry – audit: created / modified details, schema and multimedia columns, wider tables (11 Oct 2026)

## Why the columns were empty
| Column | Cause | Fix |
|---|---|---|
| **Modified by** (all tables) | Tridion's folder lists carry the modified date but not the user. | New item-details step (below) reads it from each item. |
| **Created / Created by** | Never read. | Read in the item-details step; shown in a new "Created" column. |
| **Multimedia: File name, Type, Size** (n/a) | 9,518 multimedia items, above the old read limit of 5,000 (Audit:MaxMultimediaReads). | Read in the item-details step, in batches, up to Audit:MaxItemDetailReads (50,000 items). |
| **Multimedia / Components: Schema**, and the schema's component count | The "where used" results were filtered to the audited publication's own IDs. Shared items are listed with their owning publication's ID (e.g. 040 or 020 masters), so they were dropped. | Items are matched by item number, whatever publication prefix they carry. The item-details step also reads each component's schema directly. |

## Item-details step (Tridion)
**What it reads.** Step 7 now reads every schema, template, component, multimedia component, category, keyword and Structure Group (pages are already read in the page step). Each item gives:
- created date and user, last modified date and user;
- for components: the schema, and whether it is multimedia;
- for multimedia: file name, size and type. The binary itself is not downloaded.

**How it reads.**
- Items are read in batches of 50 with the Core Service `ReadItems` call where the proxy has it, otherwise one by one.
- If a batch fails, that batch is read one by one.
- Components that turn out to be multimedia move to the Multimedia table.

**Settings (`Audit` in appsettings.json).**
- `ReadItemDetails` (true): off skips the step; the columns then show n/a.
- `MaxItemDetailReads` (50000): above it, the step is skipped with a note.
- `ItemReadBatchSize` (50).
- `MaxMultimediaReads` is no longer used.

**Time.** A publication with about 26,000 items needs about 530 batch reads, a few minutes extra on top of the scan. Components are read with their content, so watch the CMS's load on the first run.

## AEM
Created date and user come from `jcr:created` / `jcr:createdBy` on each page, fragment, asset and template.

## Tables
**Layout.** Same as the other steps: the full workflow panel on the left (280 px) and the content in the second column, all the way down.
- On the dashboard only, the second column is wider: about 1,350 px on a 1,920 px screen, instead of about 910 px.
- Table cells are tighter, and column headings wrap onto two lines.
- The workflow panel stays at the top of the page; it no longer follows the scroll. This applies to every step.
- **Header fix:** the header no longer floats over the page while scrolling (it covered the dashboard tiles). It scrolls away with the page on every step.
- On the dashboard, the header and footer are as wide as the wider content, so the edges line up.

**Combined columns.**
- "Created" shows date and user together.
- "Last modified" shows date and user together.
- Dates show the day; the time is in the tooltip.

**Columns this audit could not fill** (e.g. "Used by" in a standard scan) are left out instead of shown empty. A line above the table names them and says why.

**Rows per page.** Both tables (the building-block table and a finding's items) have a **Rows per page** choice next to the pager: 20 (default), 50 or 100. Changing it goes back to page 1. The default comes from Audit:PageSize, now 20.

**Measured with the demo data at 1,920 px:**
- Multimedia scrolls about 100 px sideways.
- Pages (13 columns) scrolls about 300 px.

**Excel export:** new "Created" and "Created by" columns.

## Files
- Core:
  - Audit/AuditModels.cs – AuditRow.Created / CreatedBy; ReadItemDetails, MaxItemDetailReads, ItemReadBatchSize
  - Audit/AuditQuery.cs – sort and search by created date and user
  - Audit/AuditExcelWriter.cs – Created, Created by columns
  - Audit/Aem/AemAuditGateway.cs – jcr:created / jcr:createdBy
  - Gateway/InMemoryTridionGateway.Audit.cs – demo created dates and users
- Tridion: CoreServiceTridionGateway.Audit.cs – item-details step; where-used matching across publications; page reads give version info; modified / created availability judged from the data
- Web:
  - Controllers/AuditController.cs – created and createdBy in the rows API; pageSize (20 / 50 / 100) on the rows and findings APIs
  - Models/AuditViewModels.cs – created / createdBy availability for the page script
  - Views/Shared/_AuditLayout.cshtml – wider content column on the dashboard
  - Views/Shared/_Layout.cshtml – wide-page class for header and footer
  - Views/Audit/Dashboard.cshtml – Rows per page choices
  - Models/AuditViewModels.cs – default page size 20
  - wwwroot/js/audit.js – combined date/user columns, unavailable columns left out, short dates
  - wwwroot/css/site.css – wide-layout styles (appended)
  - appsettings.json – new Audit settings

## Testing
- **Fake Core Service, 10.x style:** batch reads used. Created and modified dates and users were filled for every building block, and schema and multimedia details were filled.
- **Fake Core Service, 9.1 style:** with no batch reads it fell back to one read per item. A component that was really multimedia moved to the Multimedia table.
- **Browser, Tridion demo:** table widths measured at 1,920 and 1,440 px. No script errors. Tests: 27 passed.

## VERIFY on the first real run
- `ICoreService.ReadItems(string[], ReadOptions)` on your proxy. Without it, items are read one by one: same result, slower.
- `VersionInfo.Creator` / `Revisor` titles, and the time the step takes on a publication of this size.
