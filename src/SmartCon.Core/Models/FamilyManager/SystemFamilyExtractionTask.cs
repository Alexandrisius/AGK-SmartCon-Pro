namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One extraction task for background attribute extraction from a staged system-family .rvt.
/// </summary>
/// <param name="CatalogItemId">Target catalog item the extracted attributes will be linked to.</param>
/// <param name="ManagedRvtPath">Absolute path to the managed .rvt file in catalog storage.</param>
/// <param name="Snapshot">Phase 27: in-memory snapshot from Phase 1 Prepare.
/// When non-null, the extractor produces <see cref="FamilyExtractionResult"/>
/// from the snapshot WITHOUT re-opening the staged .rvt. <c>null</c> for the
/// legacy re-extraction path.</param>
/// <param name="RevitMajorVersion">Revit major version for
/// <see cref="FamilyExtractionResult.RevitMajorVersion"/>. Defaults to 0
/// when not provided (legacy callers).</param>
public sealed record SystemFamilyExtractionTask(
    string CatalogItemId,
    string ManagedRvtPath,
    IReadOnlyList<string> TypeNames,
    string? VersionId,
    string? FileId,
    SystemFamilySnapshot? Snapshot = null,
    int RevitMajorVersion = 0);
