# Modelry

**Modelry** builds a CMS content model from one Information Architecture (IA) workbook – schemas, templates and taxonomy – checked first, created in dependency order, and logged for audit. Built by Content Bloom.

## The journey

1. **Target CMS** – choose *Tridion Sites* (full support) or *Adobe Experience Manager* (preview: connect, validate and see the mapping; creation is the next release).
2. **Connect** – Tridion: Windows account (basicHttp) or OAuth client; AEM: user + password or access token against the author instance. *Explore with demo data* runs everything against an in-memory Tridion.
3. **Upload IA** – once. Modelry reads it and shows what it contains; every later step uses it. Replace it any time.
4. **Schemas** – pick the target folder, run the check (nothing changes), then create with live progress.
5. **Templates** – pick the template folder, the base DXA Component / Page Template, header / footer includes and (optionally) region constraints; check; create.
6. **Finish** – what was created, downloadable logs, next steps.

*Export from CMS* (header) writes an existing folder's schemas or templates back to a workbook.

The journey rail on the left shows each step's outcome and lets you go back. The UI follows the Content Bloom web style system (Roboto / Roboto Slab, warm canvas, accent buttons); the logo's petal colours are used only for status – teal in progress, pink errors, amber warnings.

> **Upgrading from the earlier "Tridion IA Utility":** projects and namespaces are now `Modelry.*`. Move your generated
> `Connected Services/CoreService/Reference.cs` from `src/TridionIA.Tridion/…` to `src/Modelry.Tridion/…` before building.

## Solution layout

| Project | Purpose |
|---|---|
| `Modelry.Core` | IA model, Excel reader/writer (ClosedXML), export service, import planner (validation + ordering) and executor, demo gateway. No Tridion dependency. |
| `Modelry.Tridion` | Core Service gateway, OAuth token client, wsHttp binding + bearer header. Compiles the real gateway only when the proxy exists. |
| `Modelry.Web` | MVC UI, JSON tree API, session handling, audit logging (Serilog). |

## Setup

1. **Generate the Core Service proxy** (once per Tridion version; behind a corporate proxy, save the WSDL first – see the script header):
   `src/Modelry.Tridion/Connected Services/CoreService/generate-proxy.ps1 -WsdlUrl https://<cms>/webservices/CoreService201701.svc?singleWsdl`
   This creates `Reference.cs`; the build then defines `CORESERVICE_PROXY` automatically. Without it the app builds and runs in demo mode only.
2. **Configure** `src/Modelry.Web/appsettings.json` → `Tridion` section (dev overrides in `appsettings.Development.json`):
   - `DefaultAuthMode` – `Windows` (default) or `OAuth`; users can switch on the login screen
   - `CoreServiceUrl` – placeholder shown on the login screen, e.g. `https://cms.example.com/webservices/CoreService201701.svc/basicHttp`
   - `AccessManagementTokenUrl`, `Scope`, `OAuthBinding` – OAuth mode only
   - `AllowHttpHosts` – dev hosts allowed over plain HTTP (e.g. `dev-cm-service.sabic.com`); all other hosts require HTTPS
   - `AllowedHosts` – hosts users may connect to (recommended – prevents the server being used to call arbitrary URLs)
   - `ProxyUrl` – optional explicit proxy; empty = Windows system proxy settings
   - `AllowDemoMode` – set `false` in production
3. **Authentication modes**
   - **Windows (default):** `BasicHttpBinding` with Windows (NTLM/Kerberos) HTTP authentication – `Transport` over HTTPS, `TransportCredentialOnly` over HTTP (dev only). The user needs Tridion rights to read publications/folders and create folders, categories, keywords and schemas. The password is kept encrypted (ASP.NET Core Data Protection) in the server-side session for the session's lifetime and is never written to cookies or logs.
   - **OAuth:** client credentials from Access Management; the bearer token is sent on each call. Requires a CM endpoint configured for HTTPS + OAuth (ask your Tridion admins). Default contract is basicHttp (`ICoreService`); for an OAuth-enabled wsHttp endpoint with *transport* security set `OAuthBinding` = `wsHttp` and `<UseSessionAwareContract>true</UseSessionAwareContract>` in `Modelry.Tridion.csproj`.
   - `.NET 8` cannot use wsHttp endpoints with **Message** security (the Tridion default). The proxy generator's warnings about this are expected.
4. **Connectivity:** the machine running the app must reach the Core Service (and the token URL in OAuth mode). If only your browser reaches the CM (corporate proxy / security client), either set `ProxyUrl`, or host the app on a server inside the network. Windows authentication through an authenticating proxy is often blocked – hosting inside the network is the reliable option. Check from the host with `Test-NetConnection <cm-host> -Port 443` (or 80).
5. Build and run: `dotnet run --project src/Modelry.Web` (https://localhost:7243). Host on IIS (ASP.NET Core Hosting Bundle 8) or as a Windows service behind HTTPS.

## Security

- The client secret is used once to obtain a token and is never stored; the token is kept server-side in session (idle timeout `SessionMinutes`).
- Only the CMS URL, token URL and client ID are remembered (HTTP-only cookie).
- Anti-forgery on all POSTs, HTTPS-only cookies, upload size limit, uploads deleted after 24h.
- Audit log (`logs/modelry-*.log`): logins, folder creation, exports, dry runs, imports with results.

## How much of the publication is read

- **Check:** the target folder and its sub-folders (create vs skip), one publication-wide list of schema titles (one call), a read of only the existing schemas the workbook refers to, the workbook's categories and their keywords, multimedia types (cached 30 min) and Component Templates (only when Region Definitions list them).
- **Create:** reuses the check's snapshot when started within `Tridion:CheckReuseMinutes` (default 30) for the same workbook and folder (and, for templates, the same choices). Items someone else created after the check are skipped and reported. Tick "Check the publication again before creating" to force a fresh read.

## Import behaviour

Dry run (no changes) → folders → categories → keywords (parents first) → schemas pass 1 in dependency order (embedded schemas topologically; no link constraints) → pass 2 (allowed target schemas, region definitions) → keyword metadata schemas on new categories → check-in with comment. Existing schemas with the same title in the target folder are **skipped and reported**. The import is re-planned against a fresh snapshot when you confirm. A results workbook can be downloaded.

Not applied (reported only): Format Area, Max Length. Help text is appended to the field description.

## Template import

Separate from the schema import – own export, dry run, progress page and results file (browse page → *Templates* section).

1. Select the target folder in the template publication (e.g. its *Building Blocks*); relative paths such as `Templates/Component` are created below it.
2. Choose the **base Component Template** and **base Page Template** (lists show all templates visible in that publication) – use existing DXA templates so the DXA building blocks and template metadata schema are cloned.
3. Enter the default **header / footer include** paths for Page Templates (rows can override with their own includes, or `(none)`).
4. Optionally tick **Apply template constraints to region schemas** – the *Allowed Component Templates* of *Region Definitions* replace the region schemas' allowed-schema constraints (only region schemas local to that publication).

Per template the utility copies the base, then sets title, description, DXA view (metadata field `view`), dynamic flag (`isRepositoryPublished` + template property), priority, linked schemas (CT), page schema and includes (PT), and checks it in. Only the configured metadata fields are changed; everything else stays as on the base. Field names are configurable in `appsettings.json` → `Templates`. Linked / page schemas must already exist in the publication. DXA's built-in Data Presentation template is never created by the utility.

## First-run verification checklist (Core Service API)

The gateway was written against the Core Service API without access to your instance. Lines marked `VERIFY` in `CoreServiceTridionGateway.cs` use members to confirm on first build / test:

- `ReadSchemaFields`, `ConvertSchemaFieldsToXsd`, `SchemaData.Xsd`
- `SchemaData.RegionDefinition`, `RegionDefinitionData`, `OccurrenceConstraintData`, `TypeConstraintData` (`BasedOnSchema`, `BasedOnComponentTemplate`), `NestedRegionData`
- `SchemaPurpose` enum member names (incl. `Region`)
- `PublicationData.RootFolder`
- Templates: `Copy(id, destinationId, makeUnique, options)`, `PageTemplateData.PageSchema`, `ComponentTemplateData.RelatedSchemas / Priority / IsRepositoryPublishable`

Recommended test order: demo mode → login → browse → export a known folder → dry run that export into an empty test folder → import → export again and compare.

## IA workbook format

See the README sheet in `wwwroot/samples/IA_Template.xlsx`. Column headers are defined once in `Modelry.Core/Excel/IaFormat.cs`.
