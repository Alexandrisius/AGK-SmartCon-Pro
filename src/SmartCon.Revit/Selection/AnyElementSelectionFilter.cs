using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.Revit.Selection;

internal sealed class AnyElementSelectionFilter : ISelectionFilter
{
    private readonly HashSet<string> _loggedRejections = new(StringComparer.Ordinal);

    public bool AllowElement(Element elem)
    {
        if (elem is FamilyInstance) return true;

        var bic = CategoryCompat.GetBuiltInCategory(elem.Category);
        if (bic == BuiltInCategory.INVALID)
        {
            LogRejectionOnce(elem, "no resolvable BuiltInCategory");
            return false;
        }

        if (!SystemCategoryRegistry.SupportedCategories.Contains(bic))
        {
            LogRejectionOnce(elem, $"BuiltInCategory={bic} not in the supported set");
            return false;
        }

        return true;
    }

    public bool AllowReference(Reference reference, XYZ position) => false;

    /// <summary>
    /// #182: AllowElement fires on EVERY preselection highlight (hundreds of
    /// calls per picking session) — logging each rejection would flood
    /// smartcon.log with thousands of DBG lines and stall the picker. The
    /// diagnostic value is preserved by logging the FIRST rejection per
    /// (class, category, reason) key; repeats are silently counted by Revit.
    /// </summary>
    private void LogRejectionOnce(Element elem, string reason)
    {
        var key = $"{elem.GetType().Name}|{elem.Category?.Name ?? "(none)"}|{reason}";
        if (!_loggedRejections.Add(key)) return;

        var categoryName = elem.Category?.Name ?? "(no category)";
        SmartConLogger.Debug(
            $"Picker rejected {elem.GetType().Name} (category='{categoryName}', name='{elem.Name}') — {reason}");
    }
}
