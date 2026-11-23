using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Read/apply helpers shared by the view write tools. All view-range and crop
/// values are exchanged in millimetres; conversion to internal units happens here.
/// </summary>
internal static partial class AgentViewWriteHelper
{
    private static readonly char[] ForbiddenNameChars =
    {
        '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '\'', '~', '\r', '\n', '\t'
    };

    internal static bool TryGetView(
        Document doc,
        long viewId,
        out View view,
        out string errorCode,
        out string reason)
    {
        view = null!;
        errorCode = string.Empty;
        reason = string.Empty;

        if (viewId <= 0)
        {
            errorCode = "invalid_arguments";
            reason = "viewId must be a positive element id.";
            return false;
        }

        var element = doc.GetElement(ElementIdCompat.Create(viewId));
        if (element is not View found)
        {
            errorCode = "view_not_found";
            reason = "View " + viewId + " does not exist in the active document.";
            return false;
        }

        view = found;
        return true;
    }

    internal static bool TryGetLevel(
        Document doc,
        long levelId,
        out Level level,
        out string reason)
    {
        level = null!;
        reason = string.Empty;

        var element = doc.GetElement(ElementIdCompat.Create(levelId));
        if (element is not Level found)
        {
            reason = "Level " + levelId + " does not exist in the active document.";
            return false;
        }

        level = found;
        return true;
    }

    /// <summary>Returns an error message or null when the name is acceptable.</summary>
    internal static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "The new name must not be empty.";

        var trimmed = name!.Trim();
        if (trimmed.Length > 250)
            return "The new name is longer than 250 characters.";

        if (trimmed.IndexOfAny(ForbiddenNameChars) >= 0)
            return "The new name contains characters that Revit forbids: \\ : { } [ ] | ; < > ? ' ~";

        return null;
    }

    internal static Dictionary<string, long> CollectViewNamesByName(Document doc)
    {
        var names = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (view.ViewType == ViewType.ProjectBrowser
                || view.ViewType == ViewType.SystemBrowser
                || view.ViewType == ViewType.Internal)
            {
                continue;
            }

            names[view.Name] = view.Id.GetValue();
        }

        return names;
    }

    internal static bool TryResolveTemplate(
        Document doc,
        View view,
        long templateId,
        out View template,
        out string errorCode,
        out string reason)
    {
        template = null!;
        errorCode = string.Empty;
        reason = string.Empty;

        var element = doc.GetElement(ElementIdCompat.Create(templateId));
        if (element is not View resolved || !resolved.IsTemplate)
        {
            errorCode = "invalid_template";
            reason = "Element " + templateId + " is not a view template.";
            return false;
        }

        if (view.IsTemplate)
        {
            errorCode = "view_is_template";
            reason = "View '" + view.Name + "' is itself a template; a template cannot use another template.";
            return false;
        }

        if (!AreFamiliesCompatible(doc, view, resolved))
        {
            errorCode = "template_not_applicable";
            reason = "Template '" + resolved.Name + "' belongs to a different view family and " +
                     "cannot be applied to '" + view.Name + "'.";
            return false;
        }

        template = resolved;
        return true;
    }

    internal static bool AreFamiliesCompatible(Document doc, View view, View template)
    {
        try
        {
            var viewFamilyType = doc.GetElement(view.GetTypeId()) as ViewFamilyType;
            var templateFamilyType = doc.GetElement(template.GetTypeId()) as ViewFamilyType;
            if (viewFamilyType is null || templateFamilyType is null)
                return true;

            return viewFamilyType.ViewFamily == templateFamilyType.ViewFamily;
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal static ViewRangeSpec? ReadViewRange(View view)
    {
        if (view is not ViewPlan plan)
            return null;

        PlanViewRange range;
        try
        {
            range = plan.GetViewRange();
        }
        catch (Exception)
        {
            return null;
        }

        return new ViewRangeSpec
        {
            Top = ReadPlane(range, PlanViewPlane.TopClipPlane),
            CutPlane = ReadPlane(range, PlanViewPlane.CutPlane),
            Bottom = ReadPlane(range, PlanViewPlane.BottomClipPlane),
            ViewDepth = ReadPlane(range, PlanViewPlane.ViewDepthPlane)
        };
    }

    internal static void ApplyViewRange(ViewPlan plan, ViewRangeSpec spec)
    {
        var range = plan.GetViewRange();
        ApplyPlane(range, spec.Top, PlanViewPlane.TopClipPlane);
        ApplyPlane(range, spec.CutPlane, PlanViewPlane.CutPlane);
        ApplyPlane(range, spec.Bottom, PlanViewPlane.BottomClipPlane);
        ApplyPlane(range, spec.ViewDepth, PlanViewPlane.ViewDepthPlane);
        plan.SetViewRange(range);
    }

    internal static string? CategoryName(Document doc, long categoryId)
    {
        var category = Category.GetCategory(doc, ElementIdCompat.Create(categoryId));
        return category?.Name;
    }

    private static ViewRangePlaneSpec ReadPlane(PlanViewRange range, PlanViewPlane plane)
    {
        var levelId = range.GetLevelId(plane);
        return new ViewRangePlaneSpec
        {
            LevelId = levelId == ElementId.InvalidElementId ? null : levelId.GetValue(),
            OffsetMm = AgentUnitsFormat.ToMillimeters(range.GetOffset(plane))
        };
    }

    private static void ApplyPlane(
        PlanViewRange range,
        ViewRangePlaneSpec? spec,
        PlanViewPlane plane)
    {
        if (spec is null)
            return;

        if (spec.LevelId.HasValue)
            range.SetLevelId(plane, ElementIdCompat.Create(spec.LevelId.Value));
        if (spec.OffsetMm.HasValue)
            range.SetOffset(plane, ToInternal(spec.OffsetMm.Value));
    }


    private static double ToInternal(double millimeters) =>
        UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);

    internal static string Serialize(object value) => AgentJson.Serialize(value).GetRawText();

    internal static T? Deserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        // net48 does not narrow nullable references after IsNullOrWhiteSpace.
        using var document = JsonDocument.Parse(json!);
        return AgentJson.Deserialize<T>(document.RootElement);
    }
}
