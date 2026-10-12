# Modelry 1.1 – complete source

This zip is the whole solution (not a patch): v1.0 plus
1. Audit phase 1 – dashboard, detail tables, item pop-up, Excel export, Tridion version handling (CHANGES-audit.md)
2. Audit phase 2 – health score, findings, deep scan, saved audits, compare (CHANGES-audit-phase2.md)
3. Audit for AEM – AEM 6.5 / Cloud Service audit, AEM demo (CHANGES-audit-aem.md)
4. Health score changes and "How is this score calculated?" (CHANGES-audit-scoring.md)
5. AI executive summary of audits through the AI Orchestration Engine (CHANGES-ai-summary.md)

The footer of every page shows "Modelry 1.1". If you do not see it after
deploying, the server is still running an older build.

## Deploy
1. Replace your local Modelry folder with this one, or extract into an empty folder. The solution file must sit at the top: `Modelry.sln`, `src\...`.
2. Copy your generated Core Service proxy in: `src\Modelry.Tridion\Connected Services\CoreService\Reference.cs` (it is not in the repo).
3. Copy your own `appsettings.json` values back if you changed them. The `Audit` section is new; keep it.
4. `dotnet publish src\Modelry.Web -c Release -o <publish folder>`, then point IIS at the publish folder and recycle the app pool.
5. Give the app pool write access to the audit snapshot folder (Audit:SnapshotFolder, default `App_Data\audit-snapshots`),
   or set it to a folder outside the site, e.g. `D:\ModelryData\audit-snapshots`.
6. AI executive summary (optional): set the API key as an environment variable of the app pool, never in appsettings.json:
   `Orchestrator__ApiKey` = your key, and `Orchestrator__Enabled` = `true` (or `"Enabled": true` in appsettings.json).
   IIS: Configuration Editor → system.webServer/aspNetCore → environmentVariables, or the publish web.config.
   The server must reach https://ai-orchestration-worker.pankaj-gaur.workers.dev (set Orchestrator:ProxyUrl or Tridion:ProxyUrl
   if it needs a proxy). Prompts, language and word limit: `ai-summary.json` next to appsettings.json – read again when it changes.

## Where to see the new features
- After sign-in: "What would you like to do?" → Start an audit (or the "Audit CMS" link in the header).
- Dashboard: health score and category bars at the top, then building-block tiles, then Findings.
- "Run deep scan" button, "Compare with" under the category bars, saved audits on the Audit start page.
- "AI executive summary" panel above the health score (only when Orchestrator:Enabled is true, or when a saved summary exists).
