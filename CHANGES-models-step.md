# Modelry – DXA model generation step (7 Oct 2026)

Copy the files below over your repo (same paths). 6 new, 18 changed. Nothing deleted.

## New
- src/Modelry.Core/CodeGen/ModelPlan.cs – language-neutral plan (classes, properties, view registrations); reusable later for AEM Sling Models
- src/Modelry.Core/CodeGen/ModelPlanner.cs – workbook → plan (naming, field type mapping, registrations, findings)
- src/Modelry.Core/CodeGen/DxaCSharpWriter.cs – plan → DXA 2.x C# (C# 7.3) + area registration
- src/Modelry.Core/CodeGen/ModelPackage.cs – zip with README.md and ModelMap.csv
- src/Modelry.Web/Controllers/ModelsController.cs – Generate (POST) and Download (GET, zip kept 2 h)
- src/Modelry.Web/Views/Wizard/Models.cshtml – step 6 of 7: settings, preview, findings, download

## Changed
- Core: Model/IaModels.cs (ViewModelType, IaRegionView), Excel/IaFormat.cs ("View Model Type" header, read-only), Excel/IaExcelReader.cs (reads View Model Type and "(reference – region view)" rows)
- Web: Services/WizardState.cs (ModelRun, ModelSettings), Controllers/WizardController.cs (Models GET; upload resets model run), Controllers/HomeController.cs (routes to Models), Models/ViewModels.cs (ModelsViewModel)
- Views: _Rail.cshtml (Models step), Upload.cshtml (Create templates is coral), Schemas.cshtml (Create models), Templates.cshtml (Create models after a run, "Skip to model creation"), Import/Result.cshtml (Create models), Finish.cshtml (models card, "Start again with a new CMS" → Account/ChangeCms), step numbers "of 7" in Login, Import/DryRun, Import/Progress, Templates/DryRun
- wwwroot/css/site.css – disclosure (details/summary) styling and inline code

## Verified / not verified
- Whole solution incl. Razor views compiled with .NET 8 SDK, 0 errors / 0 warnings – but against stubs for ClosedXML, Serilog and System.ServiceModel (NuGet unavailable here). Not run.
- Generated SABIC models compiled with LangVersion 7.3, 0 errors / 0 warnings – against stub DXA types, not the real Sdl.Web.* assemblies.
- Not run against a CMS or DXA site. See README.md in the models zip, "Check before relying on it".
