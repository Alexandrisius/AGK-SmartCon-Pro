using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Content-hash dedup service. Combines the name-based lookup with the
/// cross-version hash search to produce the final
/// <see cref="FamilyBatchImportStatus"/> for a batch-import row.
/// </summary>
/// <remarks>
/// Business rules:
/// <list type="bullet">
/// <item>Name NOT in catalog -> <see cref="FamilyBatchImportStatus.New"/>
/// (hash is not checked — dedup only applies when names match).</item>
/// <item>Name matches but no hash available ->
/// <see cref="FamilyBatchImportStatus.Existing"/> (fallback to name-only).</item>
/// <item>Name matches and hash matches any version (current or archived) ->
/// <see cref="FamilyBatchImportStatus.Duplicate"/>.</item>
/// <item>Name matches but hash does not match any version ->
/// <see cref="FamilyBatchImportStatus.Existing"/>.</item>
/// <item>Cross-source separation enforced in SQL (family_source filter).</item>
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(normalizedName))
            return new ContentHashDedupResult(
                FamilyBatchImportStatus.Error,
                null, null, null);

        using var _scope = SmartConLogger.BeginScope("Dedup",
            ("Method", nameof(CheckAsync)),
            ("NormalizedName", normalizedName),
            ("FamilySource", familySource),
            ("HasHash", contentHash is not null));

        var existingByName = await _catalogProvider
            .FindByNormalizedNameAsync(normalizedName, ct)
            .ConfigureAwait(false);

        if (existingByName is null)
        {
            SmartConLogger.Info(
                $"Dedup result: New (name '{normalizedName}' not in catalog)");
            return new ContentHashDedupResult(
                FamilyBatchImportStatus.New,
                ExistingCatalogItemId: null,
                ExistingVersionLabel: null,
                HashMatch: null);
        }

        if (contentHash is null)
        {
            SmartConLogger.Info(
                $"Dedup result: Existing (name '{normalizedName}' found, no hash to compare) " +
                "[Action: content hash was not computed — fallback to name-only dedup]");
            return new ContentHashDedupResult(
                FamilyBatchImportStatus.Existing,
                ExistingCatalogItemId: existingByName.Id,
                ExistingVersionLabel: existingByName.CurrentVersionLabel,
                HashMatch: null);
        }

        var match = await _catalogProvider
            .FindByContentHashAcrossVersionsAsync(
                contentHash.HexString,
                contentHash.FormatVersion,
                familySource,
                ct)
            .ConfigureAwait(false);

        if (match is not null)
        {
            var matchType = match.IsCurrentVersion ? "current" : "archived";
            SmartConLogger.Info(
                $"Dedup result: Duplicate (name '{normalizedName}', hash matches " +
                $"{matchType} version {match.MatchedVersionLabel} of item {match.CatalogItemId})");
            return new ContentHashDedupResult(
                FamilyBatchImportStatus.Duplicate,
                ExistingCatalogItemId: existingByName.Id,
                ExistingVersionLabel: existingByName.CurrentVersionLabel,
                HashMatch: match);
        }

        SmartConLogger.Info(
            $"Dedup result: Existing (name '{normalizedName}' found, hash does not match " +
            $"any version — content changed)");
        return new ContentHashDedupResult(
            FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: existingByName.Id,
            ExistingVersionLabel: existingByName.CurrentVersionLabel,
            HashMatch: null);
    }
}
