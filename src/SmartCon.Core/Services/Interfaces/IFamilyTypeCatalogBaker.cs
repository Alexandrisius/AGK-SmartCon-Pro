using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Bakes a Type Catalog (.txt) into a Revit family (.rfa) by creating every
/// type listed in the catalog inside the family document and saving the result.
/// </summary>
/// <remarks>
/// Implementations must perform all Revit API access on the Revit UI thread (I-01).
/// The caller is expected to invoke this service from a context that can be
/// marshalled onto the UI thread (e.g. via <see cref="IFamilyManagerAwaitableEvent"/>).
/// </remarks>
public interface IFamilyTypeCatalogBaker
{
    /// <summary>
    /// Opens the source .rfa, creates all types described by the parsed catalog,
    /// and saves the baked family to the requested output path.
    /// </summary>
    /// <param name="sourceRfaPath">Path to the original .rfa file.</param>
    /// <param name="catalog">Parsed Type Catalog entries and parameter names.</param>
    /// <param name="outputRfaPath">Destination path for the baked .rfa.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FamilyTypeCatalogBakingResult> BakeAsync(
        string sourceRfaPath,
        TypeCatalogParseResult catalog,
        string outputRfaPath,
        CancellationToken ct = default);
}
