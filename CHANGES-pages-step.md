# Modelry – Pages step, stage 1 (7 Oct 2026)

One page at a time: choose the page (Page ID + name), upload its HTML zip, review which component each section becomes
(live thumbnails), download the Razor views. Nothing is written to the CMS in this stage.

New NuGet dependency: AngleSharp 1.8.3 (HTML parsing) in Modelry.Core. Restore packages before building.

## New
- src/Modelry.Core/Pages/HtmlSections.cs – splits the page into sections (skips header, footer, nav, breadcrumb, hidden)
- src/Modelry.Core/Pages/SectionMatcher.cs – aligns sections with the page's Page-Schema Mapping rows
- src/Modelry.Core/Pages/FieldBinder.cs – finds each schema field's element in a section
- src/Modelry.Core/Pages/RazorViewWriter.cs – entity, page and region .cshtml + README
- src/Modelry.Web/Controllers/PagesController.cs – upload, review, choices, views zip, sandboxed preview
- src/Modelry.Web/Services/PageRunStore.cs – safe zip extraction, 2-hour expiry
- src/Modelry.Web/Services/PageAnalyzer.cs – split + match + choices + bindings
- src/Modelry.Web/Views/Pages/Index.cshtml, Review.cshtml
- src/Modelry.Web/wwwroot/js/pages.js

## Changed
- Core: Model/IaModels.cs, Excel/IaFormat.cs, Excel/IaExcelReader.cs (Page Inventory + Page-Schema Mapping sheets), Modelry.Core.csproj (AngleSharp)
- Web: Services/WizardState.cs, Controllers/WizardController.cs, Models/ViewModels.cs, Program.cs (PageRunStore),
  Views/Shared/_Rail.cshtml (Pages = step 7 of 8), Views/Wizard/Models.cshtml (Create pages), Views/Wizard/Finish.cshtml (Pages card),
  wwwroot/css/site.css, and "Step n of 8" in Login, Upload, Schemas, Templates, Import/*, Templates/DryRun, Models, Finish

## Update – real homepage feedback (7 Oct 2026)
- Mapping rows whose Component Template is not a real template (DXA Data Presentation, "(slide – rendered by Hero Carousel CT)")
  are items inside the row before them, not sections: no longer offered or reported as "not found"; their schema is used
  for the parent's items (e.g. Industry Profile cards in the What We Do Content List).
- A section holding several components side by side (investors widget + annual report card) is split into its blocks
  when that matches more mapping rows; the review screen says which section a block was split from.
- Thumbnails: fixed/sticky site headers are hidden, and split blocks are cropped horizontally to themselves.
- Changed: Core/Pages/HtmlSections.cs, SectionMatcher.cs, FieldBinder.cs; Web/Services/PageAnalyzer.cs, PageRunStore.cs;
  Web/Controllers/PagesController.cs; Web/Views/Pages/Review.cshtml; Web/wwwroot/js/pages.js

## Stage 2 – create images, components and the page in Tridion (7 Oct 2026)
From the review screen: "Create components and page" → choose a component folder, an images folder and a Structure Group
(sub-Structure Groups can be created in the picker) → Check (reads only) → Create (background job, live progress, log).
- Images, videos and linked documents in the zip → multimedia components (schema by file type, alt text into metadata,
  identical files once, same-name files in the images folder reused).
- Each mapped section → a component; slides and list items → their own components, linked into the parent.
- Mandatory fields not in the HTML get placeholders: [TBC], the list value naming the linked items' type, the keyword
  matching the item's title (else the first), today's date, 0, #. A mandatory image or link that can't be filled = problem.
- The page: title and file name from the Page Inventory, its Page Template, components in their regions with their CTs.
- Existing components / multimedia / page with the same title are reused, never changed. Nothing is published.
New: Core/Pages/ContentXml.cs, Core/Pages/PageContent.cs, Tridion/StreamUpload.cs, Web/Views/Pages/Create.cshtml
Changed: Core/Gateway/ITridionGateway.cs, InMemoryTridionGateway.cs (content + demo BluePrint), Tridion/CoreServiceTridionGateway.cs,
Tridion/TridionOptions.cs (StreamUploadUrl, MultimediaUploadShare), Web/Controllers/PagesController.cs, TreeController.cs,
ImportController.cs, Web/Services/ImportJobs.cs, WizardState.cs, Web/Models/ViewModels.cs, Web/Views/Pages/Review.cshtml,
Web/Views/Import/Progress.cshtml, Result.cshtml, Web/Views/Shared/_FolderPicker.cshtml, Web/wwwroot/js/picker.js

## Fixes – section regression, upload token, component metadata (8 Oct 2026)
- Carousel handling narrowed: a section is treated as a carousel (never split, never stepped into) only when it, or a
  single wrapper inside it, carries carousel markup. A section that merely contains a slider is matched as before.
  Carousel markup now only adds to carousel rows (+3); other rows lose nothing (the earlier −2 penalty is gone).
  Side-by-side blocks with the same classes (two .c-col columns) are split again when that matches more rows.
- A section left unmatched now says which row came closest, its score, and why it was not used.
- Multimedia upload: UploadBinaryByteArray gets the signed AccessTokenData from GetCurrentUser (was null →
  "Value cannot be null. Parameter name: accessToken").
- Component metadata follows the schema as it is in the CMS (ReadSchemaFields): no metadata fields there → no metadata
  and no metadata schema (fixes "Unable to find …core:Metadata."); otherwise only the fields the CMS schema defines, in its
  namespace and order.
Changed: Core/Pages/HtmlSections.cs, Core/Pages/SectionMatcher.cs, Tridion/CoreServiceTridionGateway.cs, Tridion/StreamUpload.cs
- Follow-up: when the CMS schema has metadata fields, an (empty) <Metadata> root is always sent, even when the IA gives
  no values – the CM rejects a missing one with "Unable to find …:Metadata" (seen on SABIC – Video and Hero Banner).
  Check step warns about schema titles used twice; create errors name the schema and its TCM URI.

## Labels, Models step buttons, page presentations (8 Oct 2026)
- "Create DXA page" → "Create DXA Views and Pages" everywhere (step buttons, rail, Finish, page title);
  "Generate views (.zip)" → "Generate DXA Views (.zip)".
- DXA models step shows the "Create DXA Views and Pages" step button before and after generating, like the other steps.
- Page creation reads the saved page back. If the CM kept none of the presentations, they are set again on the page's own
  regions (matched by name, ignoring case); if that also fails, on the page itself (DXA then uses each CT's regionName).
  The log states what the CMS kept per region, and flags a shortfall as an error with the region names to check.
Changed: Web/Views/Shared/_StepButtons.cshtml, _Rail.cshtml, Web/Views/Wizard/Models.cshtml, Finish.cshtml,
Web/Views/Pages/Index.cshtml, Review.cshtml, Web/Views/Import/Result.cshtml, Core/Gateway/ITridionGateway.cs,
InMemoryTridionGateway.cs, Core/Pages/PageContent.cs, Tridion/CoreServiceTridionGateway.cs

## DXA views rebuilt from the model (8 Oct 2026)
Decisions: keep the front-end's <picture>/srcset (URLs from DXA image resizing), fixed UI text → DXA resources, linked
items → own item views, a template used with different markup → variant view, assets → theme paths (Url.Asset), empty
optional field → element dropped, wrapper kept, carousel/tab controls stay with counts from the items, fields not in the
HTML placed at the end, shared _Layout + thin page views, heading levels as in the HTML.
- Repeated items: one loop per item shape (if/else for e.g. image tiles vs number tiles); differences between items become
  rules – first item's attributes (data-active, aria-current), first item's heading level (h1 vs h2), counters
  ("2 of 4", "02 / 04", data-carousel-go), values from the item, ids made unique per item (news-@(i + 1)), video vs image.
- Mirrored markup (thumbnail strip, tab buttons) becomes a second loop over the same list, using the item's values or a
  field named after it (thumbnailLabel, thumbnailImage).
- Linked components: Entity/<ParentView>/<ItemClass>.cshtml rendered with Html.Partial + ItemIndex/ItemCount; items of a
  link to several schemas are filtered by type; "Read more" in an item without a link field → Url.ItemUrl(Model).
- Layout: Model.Meta for description / Open Graph, canonical from the request, lang from the localization.
- Output also has Helpers/ViewHelpers.cs (Srcset, Asset, ItemUrl – VERIFY against your DXA version) and
  Resources/resources.csv; README lists per view the fields from the HTML, placed by default, and not rendered, plus the
  per-item values that could not be derived.
- Razor: no "@" on if/for directly inside code blocks (MVC 5 rejects it).
- Field binding: nested lists inside an item no longer swallow the item (article cards keep headline/date/image); dates
  bind before other text; tab/tile lists are not rich text; an icon field binds only an icon; a label split over spans binds
  its paragraph.
Changed: Core/Pages/RazorViewWriter.cs (rewritten), Core/Pages/FieldBinder.cs, Web/Controllers/PagesController.cs,
Web/Views/Pages/Review.cshtml
