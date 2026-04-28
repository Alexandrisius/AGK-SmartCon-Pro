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
    Task<FamilyLoadResult> LoadFamilyAsync(FamilyResolvedFile file, FamilyLoadOptions options, CancellationToken ct = default);
}
