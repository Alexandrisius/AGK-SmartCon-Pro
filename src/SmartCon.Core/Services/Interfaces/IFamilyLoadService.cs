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
    Task<FamilyLoadResult> LoadFamilyAsync(FamilyResolvedFile file, FamilyLoadOptions options, Action<string>? onStatusMessage = null, Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null, CancellationToken ct = default);

    /// <summary>Load a single family symbol (type) from a family file into the active project.</summary>
    /// <param name="filePath">Absolute path to the .rfa file. Type Catalog .txt must be adjacent if used.</param>
    /// <param name="typeName">Name of the type/symbol to load.</param>
    /// <param name="onStatusMessage">Optional callback for status messages during load.</param>
    /// <param name="onSharedDecision">
    /// Optional callback invoked once per conflicting shared nested family
    /// (see LoadFamilyAsync for details). When null, default behaviour is used.
    /// </param>
    Task<FamilyLoadResult> LoadFamilySymbolAsync(string filePath, string typeName, Action<string>? onStatusMessage = null, Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null, CancellationToken ct = default);
}
