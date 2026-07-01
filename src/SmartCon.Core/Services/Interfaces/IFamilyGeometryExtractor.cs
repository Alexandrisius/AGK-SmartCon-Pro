using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Extracts tessellated 3D geometry from a managed <c>.rfa</c> file
/// (ADR-042). Implementations MUST run on the Revit UI thread (I-01)
/// because <c>OpenDocumentFile</c> / <c>element.get_Geometry(Options)</c>
/// / <c>Face.Triangulate()</c> are all Revit API calls — callers marshal
/// via <c>IFamilyManagerAwaitableEvent.RaiseAsync&lt;T&gt;</c>.
/// </summary>
public interface IFamilyGeometryExtractor
{
    /// <summary>
    /// Opens the managed <c>.rfa</c> at <paramref name="managedRfaPath"/>,
    /// traverses every <c>GenericForm</c> element (and shared nested
    /// <c>FamilyInstance</c> via <c>GetSubComponentIds</c>), tessellates
    /// each <c>Solid</c>'s faces via <c>Face.Triangulate</c>, and returns
    /// a <see cref="FamilyGeometryPreview"/> that can be written as GLB.
    /// </summary>
    /// <param name="managedRfaPath">Absolute path to the managed
    /// <c>.rfa</c> file (per-version, in <c>{dbRoot}/files/{itemId}/{label}/</c>).</param>
    /// <param name="catalogItemId">Catalog item id — propagated to the
    /// resulting <see cref="FamilyGeometryPreview.CatalogItemId"/>.</param>
    /// <param name="versionLabel">Version label — propagated to the
    /// resulting <see cref="FamilyGeometryPreview.VersionLabel"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="FamilyGeometryPreview"/> with at least one mesh,
    /// or <c>null</c> when the family has no visible geometry / an error
    /// occurred (logged by the implementation, NOT rethrown — the pipeline
    /// treats <c>null</c> as "skip GLB write").</returns>
    Task<FamilyGeometryPreview?> ExtractAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionLabel,
        CancellationToken ct = default);
}
