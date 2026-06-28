using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Content-hash dedup service. Combines the name-based lookup with the
/// cross-version hash search to produce the final
/// <see cref="FamilyBatchImportStatus"/> for a batch-import row.
/// </summary>
/// <remarks>
/// Business rules (from the business plan):
/// <list type="bullet">
/// <item>If the normalized name is NOT in the catalog → <see cref="FamilyBatchImportStatus.New"/>
/// (hash is not checked — dedup only applies when names match).</item>
/// <item>If the name matches but no hash is available → <see cref="FamilyBatchImportStatus.Existing"/>
/// (fallback to name-only dedup).</item>
/// <item>If the name matches and the hash matches any version (current or
/// archived) → <see cref="FamilyBatchImportStatus.Duplicate"/>.</item>
/// <item>If the name matches but the hash does not match any version →
/// <see cref="FamilyBatchImportStatus.Existing"/>.</item>
/// <item>Cross-source separation: <c>"loadable"</c> hashes are never
/// compared against <c>"system"</c> hashes and vice versa.</item>
/// </list>
/// </remarks>
public interface IContentHashDedupService
{
    /// <summary>
    /// Check a single item against the catalog.
    /// </summary>
    /// <param name="normalizedName">Normalized family name (from
    /// <c>FamilyNameNormalizer.Normalize</c>).</param>
    /// <param name="contentHash">Computed content hash, or <c>null</c> if
    /// extraction failed (the method will fall back to name-only
    /// dedup).</param>
    /// <param name="familySource"><c>"loadable"</c> or <c>"system"</c> —
    /// used for cross-source separation in the hash search.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ContentHashDedupResult> CheckAsync(
        string normalizedName,
        FamilyContentHash? contentHash,
        string familySource,
        CancellationToken ct = default);
}
