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
/// <para>
/// Phase 27: <c>Snapshot</c> added. When non-null, the extractor uses the
/// in-memory snapshot to produce <see cref="FamilyExtractionResult"/> WITHOUT
/// re-opening the managed .rfa. When null (legacy/error path), the old
/// <c>ExtractFromManagedFile</c> path is used as fallback.
/// </para>
/// </remarks>
/// <param name="Snapshot">In-memory snapshot from Phase 1 Prepare, or
/// <c>null</c> if Prepare did not produce one.</param>
public sealed record LoadableFamilyAttributeTask(
    string CatalogItemId,
    string ManagedRfaPath,
    string? VersionId,
    string? FileId,
    FamilySnapshot? Snapshot = null);
