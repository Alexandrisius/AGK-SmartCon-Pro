using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Extracts metadata from family files.
/// MVP: file-level metadata only (name, size, hash, timestamps).
/// Post-MVP: deep extraction via Revit API (types, parameters, category, preview).
/// </summary>
public interface IFamilyMetadataExtractionService
{
    /// <summary>Extract metadata from a family file at the given path.</summary>
    Task<FamilyMetadataExtractionResult> ExtractAsync(string filePath, CancellationToken ct = default);
}
