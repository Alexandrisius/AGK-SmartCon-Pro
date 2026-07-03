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

    /// <summary>
    /// Bakes the parsed Type Catalog into an <b>already-open</b> family document
    /// (Phase 27B Prepare). Creates every type listed in the catalog inside the
    /// document and regenerates, but does <b>not</b> save or close — the caller
    /// (Prepare) holds the document open for the later Phase 3 SaveAs from the
    /// held-open reference. Eliminates the re-open that <see cref="BakeAsync"/>
    /// performs when baking in Commit.
    /// </summary>
    /// <param name="familyDoc">The already-open family <c>Document</c> (passed
    /// as <see cref="object"/> to respect I-09 — Core receives Revit types as
    /// opaque carriers). Must be a family document (<c>IsFamilyDocument</c>
    /// = <c>true</c>).</param>
    /// <param name="catalog">Parsed Type Catalog entries and parameter names.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Baking result with <see cref="FamilyTypeCatalogBakingResult.OutputRfaPath"/>
    /// = <c>null</c> (no save performed) on success.</returns>
    Task<FamilyTypeCatalogBakingResult> BakeInExistingDocumentAsync(
        object familyDoc,
        TypeCatalogParseResult catalog,
        CancellationToken ct = default);
}
