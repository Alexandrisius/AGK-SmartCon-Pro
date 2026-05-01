namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Kind of catalog provider.
/// </summary>
public enum CatalogProviderKind
{
    /// <summary>Local SQLite-based catalog.</summary>
    Local = 0,
    /// <summary>Remote server catalog.</summary>
    Remote = 1,
    /// <summary>Corporate network catalog.</summary>
    Corporate = 2,
    /// <summary>Public read-only catalog.</summary>
    PublicReadOnly = 3
}
