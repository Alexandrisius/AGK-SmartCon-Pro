using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using View = Autodesk.Revit.DB.View;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Activates the mini-project's saved starting view right after
/// «Редактировать» opened it via <c>OpenAndActivateDocument</c> (Issue #205).
/// The presentation settings (Fine + ShadedWithEdges) are already baked into
/// the views at staging time — this helper only switches the active view and
/// zooms to fit, with no transaction, so the freshly opened document stays
/// clean. Legacy mini-projects (staged before #205, no starting view
/// configured) fall back to the first non-template 3D view.
/// </summary>
public static class MiniProjectViewActivator
{
    public static void Activate(UIDocument uiDoc)
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", nameof(Activate)));
        try
        {
            var target = ResolveTargetView(uiDoc.Document);
            if (target is null) return;

            if (uiDoc.ActiveGraphicalView?.Id == target.Id)
            {
                // The document opened already on its starting view — the
                // saved zoom comes from the template, so ZoomToFit below is
                // still needed to frame the placed instances.
                SmartConLogger.Debug($"Starting view '{target.Name}' is already active — ZoomToFit only");
            }
            else
            {
                uiDoc.ActiveView = target;
            }

            uiDoc.GetOpenUIViews()
                .FirstOrDefault(uv => uv.ViewId == target.Id)
                ?.ZoomToFit();
            SmartConLogger.Info(
                $"Mini-project view activated: '{target.Name}' ({target.ViewType}) + ZoomToFit");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Cannot activate the mini-project view: {ex.GetType().Name}: {ex.Message}. " +
                "[Action: переключитесь на 3D-вид вручную — редактированию это не мешает]");
        }
    }

    private static View? ResolveTargetView(Document doc)
    {
        var startingViewId = ReadStartingViewId(doc);
        if (startingViewId is not null
            && doc.GetElement(startingViewId) is View startingView
            && !startingView.IsTemplate)
        {
            return startingView;
        }

        // Legacy mini-projects (pre-#205): no starting view configured —
        // fall back to the single default 3D view they all carry.
        var view3d = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate)
            .OrderBy(v => v.Id.GetValue())
            .FirstOrDefault();
        if (view3d is null)
            SmartConLogger.Warn(
                "Mini-project has neither a configured starting view nor a 3D view — " +
                "staying on the default view. [Action: переимпортируйте системное семейство]");
        else
            SmartConLogger.Debug(
                $"No starting view configured (legacy mini-project) — falling back to 3D '{view3d.Name}'");
        return view3d;
    }

    private static ElementId? ReadStartingViewId(Document doc)
    {
        try
        {
            var settings = StartingViewSettings.GetStartingViewSettings(doc);
            var viewId = settings?.ViewId;
            if (viewId is null || viewId == ElementId.InvalidElementId) return null;
            return viewId;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"StartingViewSettings read failed: {ex.Message} — using 3D fallback");
            return null;
        }
    }
}
