using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Provides the curated list of pickable Revit model categories with
/// localized labels (auto-assignment editor, #241). Implemented in
/// SmartCon.Revit — <c>LabelUtils.GetLabelFor</c> is Revit API and
/// FamilyManager cannot reference it (dependency rule).
/// </summary>
public interface IRevitCategoryLabelService
{
    /// <summary>The curated model categories (MEP-focused plus common
    /// architecture/model categories) with labels in the Revit session
    /// language. The list is stable within a session.</summary>
    IReadOnlyList<RevitCategoryLabel> GetModelCategories();
}
