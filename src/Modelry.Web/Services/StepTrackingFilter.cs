using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Modelry.Web.Services;

/// <summary>
/// Remembers the furthest wizard step the user has opened, so the journey rail can mark the steps they jumped over as
/// skipped (e.g. from Upload IA straight to DXA page: Schemas, Templates and DXA models show as skipped until done).
/// </summary>
public sealed class StepTrackingFilter : IResultFilter
{
    /// <summary>Step order as shown on the rail.</summary>
    public static readonly string[] Steps = { "cms", "connect", "ia", "schemas", "templates", "models", "pages", "finish" };

    private readonly WizardState _wizard;
    public StepTrackingFilter(WizardState wizard) => _wizard = wizard;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ViewResult) return;
        var controller = context.RouteData.Values["controller"] as string;
        var action = context.RouteData.Values["action"] as string;
        var step = (controller, action) switch
        {
            ("Wizard", "Upload") => "ia",
            ("Wizard", "Schemas") => "schemas",
            ("Wizard", "Templates") or ("Templates", _) => "templates",
            ("Wizard", "Models") => "models",
            ("Pages", _) => "pages",
            ("Wizard", "Finish") => "finish",
            _ => null
        };
        if (step is null) return;
        var index = Array.IndexOf(Steps, step);
        if (index > _wizard.Data.FurthestStep) _wizard.Update(d => d.FurthestStep = index);
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
