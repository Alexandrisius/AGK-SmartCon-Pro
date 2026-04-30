using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Resolves family file paths from managed storage for loading into Revit.
/// </summary>
public interface IFamilyFileResolver
{
    /// <summary>
    /// Resolve the absolute path for a version's file.
    /// Selects the best match for the target Revit version (exact match, then fallback).
    /// </summary>
    Task<FamilyResolvedFile> ResolveForLoadAsync(string catalogItemId, int targetRevitVersion, CancellationToken ct = default);

    /// <summary>Get the active database root directory path.</summary>
    string? GetDatabaseRoot();
}
