namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Where the shared nested family name displayed in the dialog came from.
/// </summary>
public enum SharedFamilyNameSource
{
    /// <summary>
    /// Name was provided by the Revit API via <c>IFamilyLoadOptions.OnSharedFamilyFound</c>.
    /// This is the normal path on Revit 2024.3+ / 2025+.
    /// </summary>
    RevitApi = 0,

    /// <summary>
    /// Revit API did not provide a usable name (REVIT-198137 in Revit 2023 / 2024 < 24.3.0.13);
    /// the name was resolved from the catalog DB (extracted at import time).
    /// </summary>
    CatalogDb = 1,

    /// <summary>
    /// No name was available — extractor failed and Revit API returned null.
    /// The dialog shows a generic placeholder; user must use judgment.
    /// </summary>
    FallbackPlaceholder = 2
}
