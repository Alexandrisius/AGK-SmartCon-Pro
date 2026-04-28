namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Capabilities of a catalog provider.
/// </summary>
/// <param name="SupportsWrite">Whether the provider supports write operations.</param>
/// <param name="SupportsSearch">Whether the provider supports search.</param>
/// <param name="SupportsTags">Whether the provider supports tags.</param>
/// <param name="SupportsBatchImport">Whether the provider supports batch import.</param>
/// <param name="SupportsVersionHistory">Whether the provider supports version history.</param>
/// <param name="ProviderKind">Kind of provider.</param>
public sealed record FamilyCatalogCapabilities(
    bool SupportsWrite,
    bool SupportsSearch,
    bool SupportsTags,
    bool SupportsBatchImport,
    bool SupportsVersionHistory,
    CatalogProviderKind ProviderKind);
