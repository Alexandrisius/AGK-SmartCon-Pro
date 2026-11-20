using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>Validation helpers of create_view_filter.</summary>
public sealed partial class CreateViewFilterTool
{
    private static bool IsCategoryHidden(View view, ElementId categoryId)
    {
        try
        {
            return view.GetCategoryHidden(categoryId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAcceptable(
        Document doc,
        IReadOnlyList<ElementId> categories,
        ElementFilter filter,
        out string? reason)
    {
        reason = null;
        try
        {
            var set = new HashSet<ElementId>(categories);
            if (ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(
                    doc, set, filter))
            {
                return true;
            }

            reason = "The rule set is not acceptable for these categories: a rule references a " +
                     "parameter that does not apply to the categories.";
            return false;
        }
        catch (Exception ex)
        {
            reason = "Revit rejected the filter rules: " + ex.Message;
            return false;
        }
    }

    private static bool CategoriesMatch(
        ParameterFilterElement filter,
        IReadOnlyList<ElementId> categoryIds)
    {
        try
        {
            var existing = filter.GetCategories();
            if (existing.Count != categoryIds.Count)
                return false;

            foreach (var id in categoryIds)
            {
                var found = false;
                foreach (var candidate in existing)
                {
                    if (candidate.GetValue() == id.GetValue())
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
