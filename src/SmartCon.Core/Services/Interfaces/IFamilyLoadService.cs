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

    /// <summary>
    /// Reloads an already-loaded family from <paramref name="file"/> while
    /// preserving the set of family types/symbols currently loaded in the
    /// project. Used by the Stale Update command (Issue #101) to avoid
    /// pulling in every type defined in the .rfa when the user originally
    /// loaded only a subset via <see cref="LoadFamilySymbolAsync"/>.
    /// </summary>
    /// <remarks>
    /// Implementation strategy (Revit API): for each already-loaded
    /// <c>FamilySymbol</c> name call <c>Document.LoadFamilySymbol</c> with the
    /// same <c>IFamilyLoadOptions</c> instance. Each call reloads the family
    /// definition (geometry/parameters) when the .rfa has changed, and
    /// applies <paramref name="overwriteParameterValues"/> to that symbol.
    /// Types that exist in the .rfa but were never loaded into the project
    /// stay unloaded. See Issue #101 root-cause analysis and Exa research
    /// (revitapidocs.com/2026 OnFamilyFound: triggered only when family is
    /// both loaded and changed; forum Autodesk "Reloading multiple family
    /// types": cyclic <c>LoadFamilySymbol</c> per type is the documented
    /// workaround for REVIT-68222).
    /// <para>
    /// Falls back to <see cref="LoadFamilyAsync"/> when the family is not yet
    /// loaded in the project (fresh load — no types to preserve) or when no
    /// loaded symbols can be enumerated.
    /// </para>
    /// </remarks>
    /// <param name="file">Resolved family file (path + catalog ids).</param>
    /// <param name="overwriteParameterValues">
    /// When <c>true</c>, existing parameter values on each loaded symbol are
    /// overwritten with the values from the .rfa. When <c>false</c> ("Сохранить
    /// параметры" mode), existing parameter values are preserved.</param>
    /// <param name="onStatusMessage">Optional status callback (see LoadFamilyAsync).</param>
    /// <param name="onSharedDecision">Optional shared-nested decision callback (see LoadFamilyAsync).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FamilyLoadResult> ReloadFamilyPreservingLoadedTypesAsync(
        FamilyResolvedFile file,
        bool overwriteParameterValues,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        CancellationToken ct = default);
}
