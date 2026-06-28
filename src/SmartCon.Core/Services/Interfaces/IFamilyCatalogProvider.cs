using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Read-only access to the family catalog.
/// </summary>
public interface IFamilyCatalogProvider
{
    /// <summary>Get catalog capabilities.</summary>
    FamilyCatalogCapabilities GetCapabilities();

    /// <summary>Search catalog items by query.</summary>
    Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(FamilyCatalogQuery query, CancellationToken ct = default);

    /// <summary>Get a single catalog item by ID.</summary>
    Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default);

    /// <summary>Get versions for a catalog item.</summary>
    Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>Get file record by ID.</summary>
    Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default);

    /// <summary>Get total item count.</summary>
    Task<int> GetItemCountAsync(CancellationToken ct = default);

    /// <summary>Get available Revit major versions for a catalog item's current version.</summary>
    Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>Find a catalog item by normalized name (exact match).</summary>
    Task<FamilyCatalogItem?> FindByNormalizedNameAsync(string normalizedName, CancellationToken ct = default);

    /// <summary>
    /// Cross-version content-hash search. Looks for a matching
    /// <c>content_hash</c> across ALL versions (current and archived) of
    /// ALL catalog items, filtered by <paramref name="familySource"/> for
    /// cross-source separation and by <paramref name="hashFormatVersion"/>
    /// for format-version safety.
    /// </summary>
    /// <returns>A <see cref="ContentHashMatch"/> if found; <c>null</c> otherwise.</returns>
    Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(
        string hexHash,
        int hashFormatVersion,
        string familySource,
        CancellationToken ct = default);

    Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default);
}
