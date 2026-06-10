namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One extraction task for background attribute extraction from a staged system-family .rvt.
/// </summary>
/// <param name="CatalogItemId">Target catalog item the extracted attributes will be linked to.</param>
/// <param name="TempRvtPath">Absolute path to the staged .rvt file.</param>
/// <param name="TypeNames">Names of system-family types to extract attributes for.</param>
/// <param name="VersionId">Optional version id (null for ad-hoc extractions).</param>
/// <param name="FileId">Optional file id of the staged .rvt in managed storage.</param>
public sealed record SystemFamilyExtractionTask(
    string CatalogItemId,
    string TempRvtPath,
    IReadOnlyList<string> TypeNames,
    string? VersionId,
    string? FileId);
