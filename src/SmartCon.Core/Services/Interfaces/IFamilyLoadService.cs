using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Loads a family file into the active Revit project.
/// IMPORTANT: Does NOT accept Document as parameter.
/// Revit implementation resolves Document via IRevitContext internally.
/// Must only be called from ExternalEvent handler (I-01).
/// </summary>
public interface IFamilyLoadService
{
    /// <summary>Load a family file into the active project.</summary>
    /// <param name="onStatusMessage">Optional callback for status messages during load (e.g. shared family replacement).</param>
    /// <param name="onSharedDecision">
    /// Optional callback invoked once per conflicting shared nested family
    /// (Revit fires OnSharedFamilyFound only for nested families that are both
    /// loaded in the project AND changed in the source .rfa).
    /// Must execute on Revit main thread and block until the user chooses.
    /// When null, the service falls back to its default behaviour
    /// (FamilySource.Family + overwriteParameterValues = true).
    /// </param>
    /// <param name="nestedSharedNames">
    /// Optional list of shared nested family names extracted from the .rfa at
    /// import time and persisted in the catalog. Used as a fallback for the
    /// dialog name when the Revit API cannot supply one (REVIT-198137 in Revit
    /// 2023 and Revit 2024 prior to 24.3.0.13). When null or empty, the
    /// dialog may show a placeholder.
    /// </param>
    Task<FamilyLoadResult> LoadFamilyAsync(
        FamilyResolvedFile file,
        FamilyLoadOptions options,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default);

    /// <summary>Load a single family symbol (type) from a family file into the active project.</summary>
    /// <param name="filePath">Absolute path to the .rfa file. Type Catalog .txt must be adjacent if used.</param>
    /// <param name="typeName">Name of the type/symbol to load.</param>
    /// <param name="onStatusMessage">Optional callback for status messages during load.</param>
    /// <param name="onSharedDecision">
    /// Optional callback invoked once per conflicting shared nested family
    /// (see LoadFamilyAsync for details). When null, default behaviour is used.
    /// </param>
    /// <param name="nestedSharedNames">
    /// Optional list of shared nested family names extracted from the .rfa at
    /// import time. See LoadFamilyAsync for details.
    /// </param>
    /// <param name="catalogItemId">
    /// Optional catalog item id. When provided AND
    /// <paramref name="nestedSharedNames"/> is null, the service resolves
    /// the list from the catalog DB (the same path LoadFamilyAsync uses).
    /// This is the missing lookup for the Type Catalog load path
    /// (REVIT-198137 affects this path most strongly because every type
    /// re-resolves the parent family and triggers OnSharedFamilyFound).
    /// </param>
    Task<FamilyLoadResult> LoadFamilySymbolAsync(
        string filePath,
        string typeName,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        string? catalogItemId = null,
        CancellationToken ct = default);
}
