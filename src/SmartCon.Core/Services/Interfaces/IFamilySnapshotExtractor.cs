using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Extracts structured snapshots from open Revit documents for content-hash
/// computation. All methods must be called on the Revit UI thread (I-01) —
/// the caller is responsible for marshalling via
/// <c>IFamilyManagerAwaitableEvent.RaiseAsync</c>.
/// </summary>
public interface IFamilySnapshotExtractor
{
    /// <summary>
    /// Extract a <see cref="FamilySnapshot"/> (parameters, types, values,
    /// geometry, shared nested names) from an open family document.
    /// The document must be a family document (<c>IsFamilyDocument == true</c>).
    /// Call <c>Document.Regenerate()</c> before calling this method if the
    /// document was just modified (e.g. after bake-in) so geometry and
    /// formula values are up to date.
    /// </summary>
    /// <param name="familyDoc">Open family document (from
    /// <c>OpenDocumentFile</c> or <c>EditFamily</c> or the active family
    /// editor document).</param>
    FamilySnapshot ExtractFromFamilyDocument(Document familyDoc);

    /// <summary>
    /// Extract a <see cref="SystemFamilySnapshot"/> (category + types +
    /// parameter values) from an open project document.
    /// </summary>
    /// <param name="projectDoc">Open project document (.rvt).</param>
    /// <param name="typeUniqueIds">UniqueIds of the system type elements
    /// to include in the snapshot.</param>
    /// <param name="builtInCategory">The <c>BuiltInCategory</c> of the
    /// system family (used for the <see cref="SystemFamilySnapshot.CategoryId"/>
    /// and to validate the types).</param>
    SystemFamilySnapshot ExtractFromProject(
        Document projectDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory builtInCategory);
}
