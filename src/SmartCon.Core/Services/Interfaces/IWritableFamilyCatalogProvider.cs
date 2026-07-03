using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IWritableFamilyCatalogProvider
{
    Task<FamilyImportResult> ImportAsync(FamilyImportRequest request, CancellationToken ct = default);
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);
    Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? categoryId, IReadOnlyList<string>? tags, ContentStatus? status, string? manufacturer = null, CancellationToken ct = default);
    Task<bool> DeleteItemAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Switch the active version of a catalog item to an existing version.
    /// Updates <c>catalog_items.current_version_label</c> and synchronizes
    /// <c>content_hash</c>/<c>hash_format_version</c> on the item to match the
    /// activated version (so content-hash deduplication stays consistent).
    ///
    /// Precondition: a row with the given <paramref name="versionLabel"/> must
    /// exist in <c>catalog_versions</c> for <paramref name="catalogItemId"/>.
    /// </summary>
    /// <returns>Result with previous/active labels for audit.</returns>
    Task<SetActiveVersionResult> SetActiveVersionAsync(string catalogItemId, string versionLabel, CancellationToken ct = default);

    /// <summary>
    /// Delete a non-active version of a catalog item. Hard delete — removes:
    /// <list type="bullet">
    ///   <item><c>catalog_versions</c> row(s) for the label (CASCADE removes family_files, family_types, extracted_attribute_values, family_nested_shared_families via FK).</item>
    ///   <item><c>family_assets</c> rows explicitly (bound by (catalog_item_id, version_label), not FK to versions).</item>
    ///   <item>Physical files on disk under <c>{dbRoot}/files/{catalogItemId}/{versionLabel}/</c>.</item>
    /// </list>
    ///
    /// Precondition: <paramref name="versionLabel"/> must NOT be the active version.
    /// </summary>
    /// <returns>Result with deletion counts for diagnostics.</returns>
    Task<DeleteVersionResult> DeleteVersionAsync(string catalogItemId, string versionLabel, CancellationToken ct = default);
}
