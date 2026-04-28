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
}
