using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Read access to the content-hash analytics of catalog versions
/// (Issue #249, Phase 4): the canonical content sections
/// (<c>section_hashes</c>/<c>section_strings</c> JSON columns) and the
/// per-type content hashes (<c>family_type_hashes</c>). Powers the batch
/// dialog's "what changed" diff against the active version without
/// re-opening the family file.
/// </summary>
public interface IContentHashAnalyticsRepository
{
    /// <summary>
    /// Section name → section SHA-256 hex of the given version label, or
    /// <c>null</c> when the analytics were not computed yet (legacy
    /// version pending the <c>section-hashes-v1</c> backfill). Any Revit
    /// variant answers — the content is identical across variants.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> GetSectionHashesAsync(
        string catalogItemId, string versionLabel, CancellationToken ct);

    /// <summary>
    /// Section name → canonical substring of the given version label, or
    /// <c>null</c> when not computed yet.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> GetSectionStringsAsync(
        string catalogItemId, string versionLabel, CancellationToken ct);

    /// <summary>
    /// Per-type content hashes of the given version label (empty when
    /// not computed yet — the <c>type-hashes-v1</c> backfill is pending
    /// or the family is typeless).
    /// </summary>
    Task<IReadOnlyList<FamilyTypeHashEntry>> GetTypeHashesAsync(
        string catalogItemId, string versionLabel, CancellationToken ct);
}
