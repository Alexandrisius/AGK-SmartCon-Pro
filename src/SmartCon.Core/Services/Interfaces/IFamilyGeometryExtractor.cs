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
    /// iterates all family types via <c>FamilyManager.CurrentType</c> inside
    /// a Transaction+RollBack (I-03b), traverses every visible
    /// <c>GenericForm</c> per type, tessellates each <c>Solid</c>'s faces,
    /// and returns a list of <see cref="FamilyGeometryPerType"/> — one per
    /// type that produces non-empty geometry.
    /// </summary>
    /// <param name="managedRfaPath">Absolute path to the managed
    /// <c>.rfa</c> file.</param>
    /// <param name="familyName">Family display name (without extension).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-type geometry list, or <c>null</c> on error / no
    /// visible geometry (logged by the implementation, NOT rethrown).</returns>
    Task<IReadOnlyList<FamilyGeometryPerType>?> ExtractAsync(
        string managedRfaPath,
        string familyName,
        CancellationToken ct = default);
}
