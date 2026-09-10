using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Bakes the presentation view into a staged mini-project (Issue #205): a 3D
/// view (created when the default template has none) with
/// <see cref="ViewDetailLevel.Fine"/> + <see cref="DisplayStyle.ShadingWithEdges"/>,
/// registered as the document's starting view. Wire mini-projects start on
/// their floor plan instead — <c>Wire</c> elements are plan-view-only
/// (<c>Wire.Create</c> requires a floor/ceiling plan view), so a 3D starting
/// view would open empty.
/// </summary>
/// <remarks>
/// Everything is configured at staging time inside the background document,
/// BEFORE <c>SaveAs</c>: the «Редактировать» command then merely activates the
/// saved starting view after <c>OpenAndActivateDocument</c> — no transaction,
/// the freshly opened document stays clean. Failures degrade (Warn + skip the
/// affected setting) instead of failing the whole staging: presentation is a
/// nicety, the staged types are the payload.
/// </remarks>
public static class MiniProjectViewConfigurator
{
    /// <summary>"{3D}" is not assignable via API (curly braces prohibited), so a
    /// created view gets an explicit name instead of the auto-generated one.</summary>
    private const string Created3DViewName = "SmartCon 3D";

    public static void Configure(ITransactionService tx, Document doc, BuiltInCategory category)
    {
        using var _scope = SmartConLogger.BeginScope("MiniProject",
            ("Method", nameof(Configure)),
            ("Category", category.ToString()));

        string? startingViewName = null;
        var committed = tx.RunInTransaction(doc, "Configure mini-project views", txDoc =>
        {
            var view3d = FindOrCreate3DView(txDoc);
            if (view3d is null) return;

            ApplyPresentation(view3d);

            var startingView = (View)view3d;
            if (category == BuiltInCategory.OST_Wire)
            {
                var plan = FindFloorPlanView(txDoc);
                if (plan is not null)
                {
                    ApplyPresentation(plan);
                    startingView = plan;
                }
            }

            if (SetStartingView(txDoc, startingView))
                startingViewName = startingView.Name;
        });

        if (startingViewName is not null && committed)
            SmartConLogger.Info(
                $"Presentation view configured: DetailLevel=Fine, DisplayStyle=ShadingWithEdges, " +
                $"StartingView='{startingViewName}'");
        else if (!committed)
            SmartConLogger.Warn(
                "'Configure mini-project views' rolled back — the mini-project opens on the default view. " +
                "[Action: переимпортируйте системное семейство и проверьте лог транзакции выше]");
    }

    private static View3D? FindOrCreate3DView(Document doc)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate)
            .OrderBy(v => v.Id.GetValue())
            .FirstOrDefault();
        if (existing is not null) return existing;

        var viewFamilyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
        if (viewFamilyType is null)
        {
            SmartConLogger.Warn(
                "No 3D view in the template and no ThreeDimensional ViewFamilyType to create one — " +
                "presentation view not configured. " +
                "[Action: добавьте 3D-вид в дефолтный шаблон Revit и переимпортируйте семейство]");
            return null;
        }

        var created = View3D.CreateIsometric(doc, viewFamilyType.Id);
        try { created.Name = Created3DViewName; }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Cannot name the created 3D view '{Created3DViewName}': {ex.Message} — keeping the auto name. " +
                "[Action: не критично — имя вида нигде не матчится]");
        }
        return created;
    }

    private static void ApplyPresentation(View view)
    {
        try { view.DetailLevel = ViewDetailLevel.Fine; }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Cannot set DetailLevel=Fine on '{view.Name}': {ex.Message}. " +
                "[Action: проверьте, что шаблон вида не блокирует детализацию]");
        }
        try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Cannot set DisplayStyle=ShadingWithEdges on '{view.Name}': {ex.Message}. " +
                "[Action: проверьте, что шаблон вида не блокирует стиль отображения]");
        }
    }

    private static ViewPlan? FindFloorPlanView(Document doc)
    {
        var plan = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel is not null)
            .OrderBy(v => v.GenLevel!.Elevation)
            .FirstOrDefault();
        if (plan is null)
            SmartConLogger.Warn(
                "No floor plan view in the wire mini-project — starting view falls back to 3D " +
                "(wire instances are invisible there). " +
                "[Action: проверьте, что дефолтный шаблон Revit содержит план этажа]");
        return plan;
    }

    private static bool SetStartingView(Document doc, View view)
    {
        try
        {
            var settings = StartingViewSettings.GetStartingViewSettings(doc);
            if (settings is null || !settings.IsAcceptableStartingView(view.Id))
            {
                SmartConLogger.Warn(
                    $"'{view.Name}' is not acceptable as a starting view — «Редактировать» falls back to the 3D view. " +
                    "[Action: откройте мини-проект и назначьте стартовый вид вручную, затем переимпортируйте]");
                return false;
            }
            settings.ViewId = view.Id;
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Cannot set the starting view to '{view.Name}': {ex.Message}. " +
                "[Action: мини-проект откроется на 3D-виде; при повторении переимпортируйте семейство]");
            return false;
        }
    }
}
