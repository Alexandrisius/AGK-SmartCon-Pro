using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Resolves family file paths for loading into Revit.
/// Converts relative cached paths to absolute paths.
/// </summary>
public interface IFamilyFileResolver
{
    /// <summary>Resolve the absolute path for a version's file.</summary>
    Task<FamilyResolvedFile> ResolveForLoadAsync(string versionId, CancellationToken ct = default);

    /// <summary>Get the canonical root directory for the file cache.</summary>
    string GetCanonicalRoot();
}
