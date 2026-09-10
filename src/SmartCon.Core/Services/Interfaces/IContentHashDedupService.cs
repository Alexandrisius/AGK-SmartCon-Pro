using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Content-hash dedup service. Combines the cross-version hash search
/// with the name/category lookup to produce the final
/// <see cref="FamilyBatchImportStatus"/> for a batch-import row.
/// </summary>
/// <remarks>
/// Business rules (Issue #126, hash-first order):
/// <list type="bullet">
/// <item>Hash matches any version (current or archived) of ANY catalog
/// item → <see cref="FamilyBatchImportStatus.Duplicate"/>. The matched
/// item is the canonical "existing" item regardless of its name.</item>
/// <item>Hash does not match (or no hash available) and the identity is
/// in the catalog → <see cref="FamilyBatchImportStatus.Existing"/>.</item>
/// <item>Hash does not match (or no hash available) and the identity is
/// NOT in the catalog → <see cref="FamilyBatchImportStatus.New"/>.</item>
/// <item>Cross-source separation: <c>"loadable"</c> hashes are never
/// compared against <c>"system"</c> hashes and vice versa.</item>
/// <item>Issue #192 (identity per source): for <c>"loadable"</c> the
/// identity is the normalized file name (stable, user-controlled). For
/// <c>"system"</c> the identity is the <c>BuiltInCategory</c> ordinal —
/// the category display name is document/template/locale-dependent
/// metadata (e.g. «Материалы изоляции воздуховодов» vs «Изоляция
/// воздуховодов» for the same OST_DuctInsulations) and the BuiltInCategory
/// ordinal is already part of the system content hash, so a hash match
/// for a system family is never a cross-name duplicate and the no-hash
/// fallback looks the item up by <paramref name="revitCategoryId"/>, not
/// by name.</item>
/// </list>
/// </remarks>
public interface IContentHashDedupService
{
    /// <summary>
    /// Check a single item against the catalog.
    /// </summary>
    /// <param name="normalizedName">Normalized family name (from
    /// <c>FamilyNameNormalizer.Normalize</c>). For <c>"system"</c> this is
    /// display metadata only — it never decides the identity.</param>
    /// <param name="contentHash">Computed content hash, or <c>null</c> if
    /// extraction failed (the method will fall back to identity-only
    /// dedup).</param>
    /// <param name="familySource"><c>"loadable"</c> or <c>"system"</c> —
    /// used for cross-source separation in the hash search and for the
    /// identity rule (Issue #192).</param>
    /// <param name="revitCategoryId"><c>BuiltInCategory</c> ordinal of a
    /// system-family row; required for the category-based identity of
    /// <c>"system"</c> rows, ignored for <c>"loadable"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ContentHashDedupResult> CheckAsync(
        string normalizedName,
        FamilyContentHash? contentHash,
        string familySource,
        int? revitCategoryId = null,
        CancellationToken ct = default);
}
