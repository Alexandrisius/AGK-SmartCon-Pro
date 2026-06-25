namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Task for <see cref="IFamilyDataExtractionService.Extract"/> after successful
/// loadable-family import into managed storage.
/// </summary>
/// <remarks>
/// v2.0.0: <c>HasTypeCatalog</c> removed. The Type Catalog (.txt) was a
/// pre-bake-in concept; ADR-033 bakes types into the managed .rfa itself,
/// so there is no .txt sidecar in managed storage to consult at extraction
/// time. The boolean was always <c>false</c> for managed paths, making the
/// if/else branch around <c>SaveExtractionResultAsync</c> dead code.
/// </remarks>
public sealed record LoadableFamilyAttributeTask(
    string CatalogItemId,
    string ManagedRfaPath,
    string? VersionId,
    string? FileId);
