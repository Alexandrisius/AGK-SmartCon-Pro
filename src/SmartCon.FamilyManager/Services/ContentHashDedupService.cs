using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Content-hash dedup service. Combines the cross-version hash search
/// with the name-based lookup to produce the final
/// <see cref="FamilyBatchImportStatus"/> for a batch-import row.
/// </summary>
/// <remarks>
/// Business rules (Issue #126, hash-first order):
/// <list type="bullet">
/// <item>Hash matches any version (current or archived) of ANY catalog
/// item -> <see cref="FamilyBatchImportStatus.Duplicate"/>. The matched
/// item is the canonical "existing" item regardless of its name. When
/// the matched item's normalized name differs from the row's name, the
/// result is a cross-name duplicate (<see cref="ContentHashDedupResult.IsCrossNameDuplicate"/>)
/// and the batch dialog shows a warning icon.</item>
/// <item>Hash does not match (or no hash available) and name is in the
/// catalog -> <see cref="FamilyBatchImportStatus.Existing"/>.</item>
/// <item>Hash does not match (or no hash available) and name is NOT in
/// the catalog -> <see cref="FamilyBatchImportStatus.New"/>.</item>
/// <item>Name/content conflict (hash matches item A, but the row's name
/// belongs to a different item B): content wins — the row is a Duplicate
/// of A. A Warn is logged; the default action stays Skip so the user
/// decides consciously.</item>
/// <item>Cross-source separation enforced in SQL (family_source filter).</item>
/// <item>Issue #192: system-family identity is the BuiltInCategory
/// ordinal (already part of the system content hash), never the category
/// display name — a hash match for a system row is therefore never a
/// cross-name duplicate, and the no-hash fallback looks the item up by
/// <c>revit_category_id</c>.</item>
/// </list>
/// </remarks>
public sealed class ContentHashDedupService : IContentHashDedupService
{
    private readonly IFamilyCatalogProvider _catalogProvider;

    public ContentHashDedupService(IFamilyCatalogProvider catalogProvider)
    {
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
    }

    public async Task<ContentHashDedupResult> CheckAsync(
        string normalizedName,
        FamilyContentHash? contentHash,
        string familySource,
        int? revitCategoryId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(normalizedName))
            return new ContentHashDedupResult(
                FamilyBatchImportStatus.Error,
                null, null, null);

        var isSystem = string.Equals(familySource, "system", StringComparison.OrdinalIgnoreCase);

        using var _scope = SmartConLogger.BeginScope("Dedup",
            ("Method", nameof(CheckAsync)),
            ("NormalizedName", normalizedName),
            ("FamilySource", familySource),
            ("HasHash", contentHash is not null));

        // Step 1 (hash-first, Issue #126): content identity is the hash,
        // the name is mutable metadata. A single indexed lookup covers
        // ALL versions of ALL items, independent of the row's name.
        if (contentHash is not null)
        {
            var match = await _catalogProvider
                .FindByContentHashAcrossVersionsAsync(
                    contentHash.HexString,
                    contentHash.FormatVersion,
                    familySource,
                    ct)
                .ConfigureAwait(false);

            if (match is not null)
            {
                // Issue #192: for system families the BuiltInCategory
                // ordinal is part of the hash itself, so a hash match is
                // always the same category — a display-name difference
                // (document/template/locale-dependent) is not a cross-name
                // duplicate. For loadable families the file name is the
                // stable user-controlled identity and the rule stands.
                var isCrossName = !isSystem && !string.Equals(
                    match.MatchedItemNormalizedName, normalizedName, StringComparison.Ordinal);
                var matchType = match.IsCurrentVersion ? "current" : "archived";

                if (isCrossName)
                {
                    // Name/content conflict check: does the row's name
                    // belong to a DIFFERENT catalog item? Content wins,
                    // but the user should be able to audit the decision.
                    var nameOwner = await _catalogProvider
                        .FindByNormalizedNameAsync(normalizedName, familySource, ct)
                        .ConfigureAwait(false);
                    if (nameOwner is not null && nameOwner.Id != match.CatalogItemId)
                    {
                        SmartConLogger.Warn(
                            $"Dedup name/content conflict: content matches item " +
                            $"'{match.MatchedItemName}' (id={match.CatalogItemId}) but name " +
                            $"'{normalizedName}' belongs to item '{nameOwner.Name}' (id={nameOwner.Id}). " +
                            $"Content wins — row is a Duplicate of '{match.MatchedItemName}'. " +
                            $"[Action: review the row in the batch dialog; default action is Skip]");
                    }
                    SmartConLogger.Info(
                        $"Dedup result: CrossNameDuplicate (row '{normalizedName}' matches {matchType} " +
                        $"version {match.MatchedVersionLabel} of differently-named item " +
                        $"'{match.MatchedItemName}', id={match.CatalogItemId})");
                }
                else
                {
                    SmartConLogger.Info(
                        $"Dedup result: Duplicate (name '{normalizedName}', hash matches " +
                        $"{matchType} version {match.MatchedVersionLabel} of item {match.CatalogItemId})");
                }

                return new ContentHashDedupResult(
                    FamilyBatchImportStatus.Duplicate,
                    ExistingCatalogItemId: match.CatalogItemId,
                    ExistingVersionLabel: match.CurrentVersionLabel ?? match.MatchedVersionLabel,
                    HashMatch: match,
                    IsCrossNameDuplicate: isCrossName);
            }
        }

        // Step 2 (Issue #192): system-family identity is the category
        // ordinal, not the unstable display name — without it the
        // name lookup would miss an existing item whose category name
        // differs per document and produce a duplicate catalog row.
        // A category MISS falls through to the legacy name lookup:
        // pre-V22 rows may have revit_category_id = NULL (the backfill
        // task is optional) and must keep matching by name.
        if (isSystem && revitCategoryId.HasValue)
        {
            var existingByCategory = await _catalogProvider
                .FindByRevitCategoryIdAsync(revitCategoryId.Value, familySource, ct)
                .ConfigureAwait(false);

            if (existingByCategory is not null)
            {
                SmartConLogger.Info(
                    $"Dedup result: Existing (system category id={revitCategoryId.Value} found as " +
                    $"'{existingByCategory.Name}', " +
                    $"{(contentHash is null
                        ? "no hash to compare — category-only dedup"
                        : "hash does not match any version — content changed")})");
                return new ContentHashDedupResult(
                    FamilyBatchImportStatus.Existing,
                    ExistingCatalogItemId: existingByCategory.Id,
                    ExistingVersionLabel: existingByCategory.CurrentVersionLabel,
                    HashMatch: null);
            }
        }

        // Step 3: no hash match — fall back to the name lookup.
        // Issue #201: the lookup is source-scoped — a "system" row must
        // never match a loadable item with the same name (and vice versa).
        var existingByName = await _catalogProvider
            .FindByNormalizedNameAsync(normalizedName, familySource, ct)
            .ConfigureAwait(false);

        if (existingByName is null)
        {
            SmartConLogger.Info(
                $"Dedup result: New (name '{normalizedName}' not in catalog" +
                $"{(contentHash is null ? ", no hash computed" : ", hash not found")})");
            return new ContentHashDedupResult(
                FamilyBatchImportStatus.New,
                ExistingCatalogItemId: null,
                ExistingVersionLabel: null,
                HashMatch: null);
        }

        SmartConLogger.Info(
            $"Dedup result: Existing (name '{normalizedName}' found, " +
            $"{(contentHash is null
                ? "no hash to compare — name-only dedup"
                : "hash does not match any version — content changed")})");
        return new ContentHashDedupResult(
            FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: existingByName.Id,
            ExistingVersionLabel: existingByName.CurrentVersionLabel,
            HashMatch: null);
    }
}
