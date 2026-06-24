using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// High-level import orchestration service.
/// Copies files into managed storage and registers them in the catalog.
/// </summary>
public interface IFamilyImportService
{
    /// <summary>Import a single family file into the published catalog.</summary>
    Task<FamilyImportResult> ImportFileAsync(FamilyImportRequest request, CancellationToken ct = default);

    /// <summary>Import all .rfa files from a folder with progress.</summary>
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);

    /// <summary>
    /// Update an existing catalog item with a new file version.
    /// Increments version label, copies file to managed storage, updates name from file.
    /// </summary>
    Task<FamilyImportResult> UpdateFamilyAsync(FamilyUpdateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Import a batch of family files with user-selected actions (Increment/Overwrite/Skip).
    /// </summary>
    Task<FamilyBatchImportResult> ImportBatchAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyImportProgress>? progress,
        CancellationToken ct = default);

    /// <summary>
    /// v2.0.0: compute the next version label for an existing catalog item
    /// (e.g. <c>"v1"</c> + 1 = <c>"v2"</c>). Returns <c>"v1"</c> if the
    /// item has no prior versions (shouldn't happen for an existing item
    /// but defensive). Used by the VM to allocate the canonical managed
    /// path up front, before staging writes the file.
    /// </summary>
    Task<string> GetNextVersionLabelAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>
    /// v2.0.0: compute the absolute canonical managed path for a file
    /// that the VM intends to stage into managed storage. Returns
    /// <c>null</c> when no active database is selected (the caller must
    /// surface this as a user error, not silently fall back to temp).
    ///
    /// Invariant:
    /// <c>absolutePath = "{dbRoot}/files/&lt;catalogItemId&gt;/&lt;versionLabel&gt;/&lt;fileName&gt;"</c>.
    /// Keeping this single source of truth here means staging helpers and
    /// the import service always agree on the path layout (no more
    /// mismatched GUIDs between staged .rvt and catalog_item.id).
    /// </summary>
    string? ComputeManagedFilePath(
        string catalogItemId,
        string versionLabel,
        string fileName,
        string extension);
}
