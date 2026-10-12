# Modelry – AI executive summary of audits (10 Oct 2026)

The audit dashboard can now ask the AI Orchestration Engine for a short, non-technical summary written for business users.
The summary covers what the result means, the next steps (Now / Next / Later) and the one decision to take. It is limited to **100 words**, is **English always**, and shows a **language tab** when a target language is configured.

## How it works
1. **Generate AI summary** on the dashboard (above the health score).
2. The server builds a compact data block from the audit and calls `POST /orchestrate`. The call takes 1.5–3 minutes and runs in the background; the page checks for the result every 5 seconds, so no browser request waits on the AI service. Users can keep working on the dashboard meanwhile.
3. The answer is a structured JSON object (headline, status, summary, up to 3 actions, decision). If it runs over `MaxWords` it is shortened: trailing actions are dropped first, then the longer texts are cut. The panel then says "shortened to the word limit".
4. The summary is saved beside the saved audit (`{id}.summary.json`). It is shown again when the audit is reopened and is not paid for twice. **Regenerate** asks for confirmation first.
5. **Excel export:** "Executive summary" becomes the first sheet, plus "Summary (xx)" when there is a translation.

**What is sent:** scores, area scores, counts per building block (live / not live), the names of the checks that found issues with their counts, and the previous audit's score.
- **Not sent:** item titles, paths, IDs, file names, user names, the CMS host, or the publication or site name.
- A unit test checks this.

## Configuration
**`appsettings.json` → `Orchestrator`**

| Setting | Default | |
|---|---|---|
| Enabled | false | Off: the button is hidden, no calls are made, nothing is validated. |
| BaseUrl | https://ai-orchestration-worker.pankaj-gaur.workers.dev | Must be https. |
| ApiKey | – | **Never in appsettings.json.** Development: `dotnet user-secrets set "Orchestrator:ApiKey" "<key>" --project src/Modelry.Web`. Production: environment variable `Orchestrator__ApiKey`. |
| TimeoutSeconds | 240 | HttpClient timeout. |
| ProxyUrl | "" | Outbound proxy. Empty uses Tridion:ProxyUrl; when that is empty too, the system proxy. |

When Enabled is true, start-up fails with a clear message if BaseUrl is not https, ApiKey is missing, or TimeoutSeconds is outside 30–900.

> **Note:** Modelry.Web.csproj now has a `UserSecretsId`, so `dotnet user-secrets set` works straight away. User-secrets are only read in the Development environment.

**`src/Modelry.Web/ai-summary.json` → `AuditSummary`** – read again whenever the file changes, so no rebuild or restart is needed.

| Setting | Default | |
|---|---|---|
| MaxWords | 100 | Limit for the whole summary, all fields together. |
| TargetLanguage | "" | e.g. "ar" or "hi". Set: the summary also appears in that language, in its own tab. Empty: English only. |
| NoTranslationLanguage | "en" | Sent as targetLanguage when TargetLanguage is empty, so the service does not translate into its default (Hindi). |
| Providers | [] | Empty = all active providers. |
| MaxFindings | 15 | Most severe checks with issues included in the prompt. |
| SystemPrompt / PromptTemplate | (business-advisor prompt) | Placeholders: `{{auditData}}` (required in PromptTemplate), `{{maxWords}}`. |
| ResponseSchema | (headline, status, summary, actions, decision) | Remove it for free-form prose; the panel then shows plain text. |

## Hosting – request limits
**Not affected.**
- No browser request waits on the AI call. The start request returns at once, and status checks take milliseconds.
- So none of these limits are hit: the IIS out-of-process request limit (2 minutes by default), Azure App Service's limit of about 230 seconds, or a reverse proxy's timeout.
- The outgoing call itself runs inside the app with its own 240-second timeout.

**What can still interrupt a call.**
- An **app-pool recycle or idle shutdown** while a summary is being written. The default idle shutdown is 20 minutes of no requests. The page polls every 5 seconds while it is open, so this mainly happens if the page is closed and the site goes idle.
- If it happens, the summary fails with "Modelry was restarted while the summary was being written". Generate it again.

## Rate limit
The service allows 20 calls an hour in total and 5 an hour per IP. The **per-IP limit counts the Modelry server's IP**, so all Modelry users share 5 calls an hour.
- Summaries are saved and reused.
- Regenerate needs a confirm click.
- A 429 shows: "The AI service's hourly limit has been reached for this server…"
- Nothing is retried automatically.

## Logging
Each call logs method, path, status, duration and the result id.
- Never logged: the API key or the prompt.
- HttpClient's own trace logging of request headers has `x-api-key` redacted. A test caught this: without the redaction, the key appeared at Trace level.

## Files
**New**
- src/Modelry.Web/Services/Orchestrator/
  - OrchestratorOptions.cs – options and start-up validator
  - OrchestratorModels.cs – request, response, final answer (raw artifact kept as JsonElement), health
  - OrchestratorClient.cs – `IOrchestratorClient` / `OrchestratorClient`: OrchestrateAsync, GetResultAsync (null on 404), GetHealthAsync
  - OrchestratorException.cs – StatusCode, Error, Code, IsRateLimited, IsUnauthorized, IsTimeout
  - OrchestratorServiceCollectionExtensions.cs – `services.AddOrchestrator(configuration)`: typed HttpClient, no retry handler
- src/Modelry.Web/Services/AuditSummaryService.cs – settings file reader, background summary jobs, save and load
- src/Modelry.Core/Audit/AuditAiSummary.cs – settings, summary model, word limit, prompt data builder
- src/Modelry.Web/ai-summary.json – prompts and summary settings
- src/Modelry.Web/wwwroot/js/audit-summary.js – the dashboard panel
- tests/Modelry.Web.Tests/ – xUnit project, added to Modelry.sln
  - OrchestratorClientTests.cs – fake HttpMessageHandler: success, 401, 429 (one call only), 500 JSON, 500 HTML, ok:false, timeout, caller cancel, configured timeout, 404 result, health, options validation, key and prompt not logged
  - AuditSummaryTests.cs – no names in the prompt, JSON-fence parsing, plain text, 100-word trimming, language tab, settings validation

**Changed**
- src/Modelry.Web/Modelry.Web.csproj – UserSecretsId
- src/Modelry.Web/Program.cs – AddOrchestrator, settings provider, summary service
- src/Modelry.Web/appsettings.json – `Orchestrator` section (no key)
- src/Modelry.Web/Controllers/AuditController.cs – `GET`/`POST api/audit/{id}/summary`, summary in the Excel export
- src/Modelry.Web/Services/AuditSnapshotStore.cs – SaveSummary / LoadSummary; pruning also deletes summaries
- src/Modelry.Web/Models/AuditViewModels.cs – `AiSummaryEnabled`
- src/Modelry.Web/Views/Audit/Dashboard.cshtml – the panel
- src/Modelry.Web/wwwroot/css/site.css – panel styles (appended)
- src/Modelry.Core/Audit/AuditExcelWriter.cs – executive summary sheets
- Modelry.sln – test project
- BUILD-INFO.md – key setup

## Testing
- **Tests:** all 26 tests passed. NuGet is blocked in the build environment, so they ran through a small stand-in for xUnit. On your machine: `dotnet test tests/Modelry.Web.Tests`.
- **Browser:** end to end against a fake orchestrator that answers like the real one (6-second delay), for the Tridion demo and the AEM demo:
  - progress display;
  - 429 message and "Try again";
  - 500 with an HTML body;
  - English summary, 82 words;
  - reload shows the saved summary without a new call;
  - Regenerate with confirm;
  - Arabic tab shown right to left;
  - reopening a saved audit;
  - phone width.
- **Not tested:** against the real orchestrator (the build environment cannot reach it), and the Excel export at runtime (ClosedXML is not available there).

## VERIFY on the first real call
- Whether `targetLanguage: "en"` skips translation, or translates English into English. Either is harmless; if "en" is rejected, set NoTranslationLanguage to "".
- What `finalAnswer.translated` holds when a responseSchema is sent. The panel handles both translated JSON (shown with the same layout) and translated plain text.
- That the providers return JSON matching the schema. If they return prose, the panel shows it as text, still within the word limit.

## Fix (10 Oct 2026): responseSchema is sent as a string
The real service answered `400 validation_error: responseSchema must be a string (a JSON schema/example description)`.
- Modelry now sends the schema from ai-summary.json as compact JSON **text**.
- In ai-summary.json, `ResponseSchema` can stay a JSON object (it is converted) or be written as a string (it is sent as written).
- Changed files:
  - src/Modelry.Web/Services/Orchestrator/OrchestratorModels.cs – `OrchestrateRequest.ResponseSchema` is now `string?`
  - src/Modelry.Core/Audit/AuditAiSummary.cs – `ResponseSchemaText`; validation accepts an object or a string
  - src/Modelry.Web/Services/AuditSummaryService.cs – sends `ResponseSchemaText`
  - tests/Modelry.Web.Tests/OrchestratorClientTests.cs, AuditSummaryTests.cs – updated and new test (27 tests, all pass)

## Change (10 Oct 2026): summary written automatically, and Export to PDF
**AI summary as the last step of the audit.** When Orchestrator:Enabled is true, every new scan (including Run again and Run deep scan) gets a ninth progress step, "Writing the AI executive summary".
- The dashboard opens once the summary is ready, already showing it (usually 2–3 minutes after the scan).
- While that step runs, the Stop button reads **Skip the summary**. It opens the dashboard straight away, and the panel offers "Generate AI summary".
- If the call fails (hourly limit, timeout, service error), the audit still completes. The dashboard shows the message with **Try again**.
- The summary is saved with the audit and shown again when the saved scan is opened.
- Saved scans without a summary (older audits, or skipped and failed summaries) do **not** call the AI automatically when opened. They show the **Generate AI summary** button instead.
- Each scan uses one of the server's AI calls (5 an hour per IP, shared by everyone on this server).

**Export to PDF.** A new button next to Export to Excel opens a print-ready report in a new tab, and the browser's print dialog opens on it. Choose **Save as PDF** as the destination.
- The report has: the AI executive summary (and the translation, when configured), health score with area bars and how it is calculated, building blocks (totals, published / not published, BluePrint or MSM), every check with flagged / checked / score, checks that could not run, findings with up to 25 items each (the rest are in the Excel export), and audit notes.
- A4, page numbers ("Page x of y"), table headers repeated on every page.
- No new NuGet package: the browser makes the PDF. A one-click server-side .pdf download needs a PDF library (e.g. PdfSharp/MigraDoc, MIT); that is not included, because NuGet cannot be reached from the build environment to compile and test it.

**Files**
- New:
  - Views/Audit/Report.cshtml – the report
  - wwwroot/css/report.css – screen and print styles
- Changed:
  - Controllers/AuditController.cs – summary step in Run; Report action
  - Services/AuditJobs.cs – optional summary step (StepNames, summaryStep in the status)
  - Services/AuditSummaryService.cs – RunAsync (awaited) besides Start; skipped state
  - Models/AuditViewModels.cs – AuditReportViewModel
  - Views/Audit/Dashboard.cshtml – Export to PDF button; progress steps from the job
  - wwwroot/js/audit.js – "Skip the summary" during the last step
  - wwwroot/js/audit-summary.js – skipped summary shown as a note with "Generate AI summary"

**Testing**
- Against the fake orchestrator, Tridion demo:
  - the summary step shows in the progress, and the dashboard opens with the summary;
  - with the hourly limit hit, the dashboard opens with the message and Try again;
  - Skip the summary opens the dashboard with Generate AI summary;
  - a saved scan opens with its saved summary and makes no new AI call.
- PDF: printed with Chromium's own print-to-PDF for a Tridion demo audit (7 A4 pages) and an AEM demo audit.
- Tests: 27 passed.
