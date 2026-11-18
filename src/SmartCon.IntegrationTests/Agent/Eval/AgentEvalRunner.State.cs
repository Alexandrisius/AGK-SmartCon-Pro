using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.IntegrationTests.Agent.Eval;

internal sealed partial class AgentEvalRunner
{
    private static string CaptureState(string taskId, Document document, EvalModelSeed seed)
    {
        switch (taskId)
        {
            case "apply_rename_view":
                return ViewName(document, seed.TargetAId);
            case "apply_rename_level":
                return LevelName(document, seed.Level2Id);
            case "apply_template":
                return TemplateSignature(document, seed.TargetAId);
            case "apply_view_range":
                return ViewRangeSignature(document, seed.TargetAId);
            case "apply_crop_active":
            case "apply_crop_extents":
                return CropSignature(document, seed.TargetAId);
            case "apply_visibility":
                return VisibilitySignature(document, seed.TargetBId);
            case "apply_configure_view":
                return ViewName(document, seed.TargetBId) + "#" +
                       TemplateSignature(document, seed.TargetBId);
            case "apply_copy_settings":
                return TemplateSignature(document, seed.TargetCId) + "#" +
                       CropSignature(document, seed.TargetCId) + "#" +
                       VisibilitySignature(document, seed.TargetCId);
            case "apply_set_parameter":
                return string.Join("|", seed.WallIds.Select(id => Comment(document, id)));
            default:
                return string.Empty;
        }
    }

    private static string ViewName(Document document, long viewId) =>
        document.GetElement(ElementIdCompat.Create(viewId))?.Name ?? "missing";

    private static string LevelName(Document document, long levelId) =>
        document.GetElement(ElementIdCompat.Create(levelId))?.Name ?? "missing";

    private static string TemplateSignature(Document document, long viewId)
    {
        if (document.GetElement(ElementIdCompat.Create(viewId)) is not View view)
            return "missing";

        return view.ViewTemplateId.GetValue().ToString(CultureInfo.InvariantCulture);
    }

    private static string ViewRangeSignature(Document document, long viewId)
    {
        try
        {
            if (document.GetElement(ElementIdCompat.Create(viewId)) is not ViewPlan plan)
                return "not-a-plan";

            var range = plan.GetViewRange();
            return string.Join("|",
                range.GetLevelId(PlanViewPlane.TopClipPlane).GetValue(),
                range.GetOffset(PlanViewPlane.TopClipPlane).ToString("F4", CultureInfo.InvariantCulture),
                range.GetLevelId(PlanViewPlane.CutPlane).GetValue(),
                range.GetOffset(PlanViewPlane.CutPlane).ToString("F4", CultureInfo.InvariantCulture),
                range.GetLevelId(PlanViewPlane.BottomClipPlane).GetValue(),
                range.GetOffset(PlanViewPlane.BottomClipPlane).ToString("F4", CultureInfo.InvariantCulture),
                range.GetLevelId(PlanViewPlane.ViewDepthPlane).GetValue(),
                range.GetOffset(PlanViewPlane.ViewDepthPlane).ToString("F4", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string CropSignature(Document document, long viewId)
    {
        try
        {
            if (document.GetElement(ElementIdCompat.Create(viewId)) is not View view)
                return "missing";

            var box = view.CropBox;
            var shape = view.GetCropRegionShapeManager()?.ShapeSet ?? false;
            return string.Join("|",
                view.CropBoxActive,
                view.CropBoxVisible,
                shape,
                box?.Min.X.ToString("F4", CultureInfo.InvariantCulture),
                box?.Min.Y.ToString("F4", CultureInfo.InvariantCulture),
                box?.Max.X.ToString("F4", CultureInfo.InvariantCulture),
                box?.Max.Y.ToString("F4", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string VisibilitySignature(Document document, long viewId)
    {
        try
        {
            if (document.GetElement(ElementIdCompat.Create(viewId)) is not View view)
                return "missing";

            var category = Category.GetCategory(document, new ElementId(BuiltInCategory.OST_Furniture));
            return category is null ? "no-category" : view.GetCategoryHidden(category.Id).ToString();
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string Comment(Document document, long elementId) =>
        document.GetElement(ElementIdCompat.Create(elementId))
            ?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
            ?.AsString() ?? string.Empty;
}
