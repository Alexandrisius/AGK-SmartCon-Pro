namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One extraction task for background attribute extraction from a staged system-family .rvt.
/// </summary>
/// <param name="CatalogItemId">Target catalog item the extracted attributes will be linked to.</param>
/// <param name="ManagedRvtPath">Absolute path to the managed .rvt file in catalog storage.</param>
public sealed record SystemFamilyExtractionTask(
    string CatalogItemId,
    string ManagedRvtPath,
    IReadOnlyList<string> TypeNames,
    string? VersionId,
    string? FileId);
