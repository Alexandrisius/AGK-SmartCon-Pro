using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyTypeRepository
{
    Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default);

    /// <summary>
    /// v2.0.0 (ADR-036): synchronises the type list for a given
    /// (catalogItemId, versionId, fileId) triple inside a single transaction.
    /// Replaces the old <c>SaveTypesAsync</c> / <c>SaveTypesForRunAsync</c> pair
    /// which suffered from a ghost-type bug: UPSERT (without DELETE) kept stale
    /// rows whenever a type was removed from the .rfa between imports.
    ///
    /// Semantics:
    /// <list type="number">
    /// <item>DELETE all existing types matching (catalogItemId, versionId, fileId)</item>
    /// <item>INSERT (or UPSERT on conflict) the supplied <paramref name="types"/> list</item>
    /// <item>Return a {identityKey → typeId} map for downstream attribute-value persistence.
    /// The key is <c>SystemTypeIdentityKey.Build(familyKey, familyName, typeName)</c>
    /// ("TOKEN|NAME", upper-invariant; Issue #191) — for loadable/legacy rows
    /// without a family token it degrades to "|NAME".</item>
    /// </list>
    /// </summary>
    /// <param name="runId">Extraction run id (audit trail). Pass <c>"no-run"</c> for synthetic callers (orchestrators) that do not need a run record.</param>
    /// <param name="versionId">Optional. When non-null, only types for that version are replaced; types of other versions are preserved.</param>
    /// <param name="fileId">Optional. Same scoping as <paramref name="versionId"/>.</param>
    /// <param name="types">Final list of types that should exist after this call. Any name not in the list is removed.</param>
    Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
        string catalogItemId,
        string? versionId,
        string? fileId,
        string runId,
        IReadOnlyList<FamilyTypeDescriptor> types,
        CancellationToken ct = default);

    Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default);
}
