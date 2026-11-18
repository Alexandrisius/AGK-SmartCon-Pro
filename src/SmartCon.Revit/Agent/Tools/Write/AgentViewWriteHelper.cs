using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

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

    internal static ViewVisibilityState ReadVisibility(Document doc, View view)
    {
        var state = new ViewVisibilityState();
        foreach (Category category in doc.Settings.Categories)
        {
            try
            {
                if (!view.CanCategoryBeHidden(category.Id))
                    continue;
                if (view.GetCategoryHidden(category.Id))
                    state.HiddenCategoryIds.Add(category.Id.GetValue());
            }
            catch (Exception)
            {
                // Some categories reject both calls (import/analytical); skip them.
            }
        }

        return state;
    }

    internal static void MirrorVisibility(
        Document doc,
        View source,
        View target,
        List<string> warnings)
    {
        var desired = new HashSet<long>(ReadVisibility(doc, source).HiddenCategoryIds);
        var applied = 0;
        string? lastError = null;

        foreach (Category category in doc.Settings.Categories)
        {
            try
            {
                if (!target.CanCategoryBeHidden(category.Id))
                    continue;

                var shouldHide = desired.Contains(category.Id.GetValue());
                if (target.GetCategoryHidden(category.Id) == shouldHide)
                    continue;

                target.SetCategoryHidden(category.Id, shouldHide);
                applied++;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        if (lastError is not null)
        {
            warnings.Add("Some categories could not be mirrored to '" + target.Name +
                         "' (" + applied + " applied): " + lastError);
        }
    }

    internal static void ApplyVisibilityState(Document doc, View view, ViewVisibilityState state)
    {
        var desired = new HashSet<long>(state.HiddenCategoryIds);
        foreach (Category category in doc.Settings.Categories)
        {
            try
            {
                if (!view.CanCategoryBeHidden(category.Id))
                    continue;

                var shouldHide = desired.Contains(category.Id.GetValue());
                if (view.GetCategoryHidden(category.Id) != shouldHide)
                    view.SetCategoryHidden(category.Id, shouldHide);
            }
            catch (Exception)
            {
                // Categories that reject both calls cannot be restored; the
                // caller reports the overall result.
            }
        }
    }

    internal static void ApplyVisibility(
        Document doc,
        View view,
        ViewVisibilitySpec spec,
        List<string> warnings)
    {
        if (spec.Categories is not null)
        {
            foreach (var entry in spec.Categories)
            {
                if (!TryResolveCategory(doc, entry, out var categoryId))
                {
                    warnings.Add("Category '" + (entry.Category ?? entry.CategoryId?.ToString()) +
                                 "' was not found in the document and was skipped.");
                    continue;
                }

                try
                {
                    if (!view.CanCategoryBeHidden(categoryId))
                    {
                        warnings.Add("Category '" + entry.Category +
                                     "' cannot be hidden in view '" + view.Name + "'.");
                        continue;
                    }

                    view.SetCategoryHidden(categoryId, entry.Hidden);
                }
                catch (Exception ex)
                {
                    warnings.Add("Category '" + entry.Category + "' could not be changed: " +
                                 ex.Message);
                }
            }
        }

        if (spec.Worksets is { Count: > 0 })
        {
            warnings.Add("Workset visibility is not supported: the active document is not " +
                         "workshared, so there are no user worksets.");
        }
    }

    internal static bool TryResolveCategory(
        Document doc,
        CategoryVisibilitySpec spec,
        out ElementId categoryId)
    {
        categoryId = ElementId.InvalidElementId;

        if (spec.CategoryId.HasValue)
        {
            var byId = Category.GetCategory(doc, ElementIdCompat.Create(spec.CategoryId.Value));
            if (byId is not null)
            {
                categoryId = byId.Id;
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(spec.Category))
            return false;

        var name = spec.Category!;
        if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
        {
            var category = Category.GetCategory(doc, builtIn);
            if (category is not null)
            {
                categoryId = category.Id;
                return true;
            }
        }

        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                categoryId = category.Id;
                return true;
            }
        }

        return false;
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
