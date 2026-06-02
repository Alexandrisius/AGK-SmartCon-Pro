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
    Task<FamilyLoadResult> LoadFamilyAsync(FamilyResolvedFile file, FamilyLoadOptions options, Action<string>? onStatusMessage = null, CancellationToken ct = default);

    /// <summary>Load a single family symbol (type) from a family file into the active project.</summary>
    /// <param name="filePath">Absolute path to the .rfa file. Type Catalog .txt must be adjacent if used.</param>
    /// <param name="typeName">Name of the type/symbol to load.</param>
    /// <param name="onStatusMessage">Optional callback for status messages during load.</param>
    Task<FamilyLoadResult> LoadFamilySymbolAsync(string filePath, string typeName, Action<string>? onStatusMessage = null, CancellationToken ct = default);
}
