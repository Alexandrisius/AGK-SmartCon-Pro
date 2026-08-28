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
    /// FHV15 (#249): the type-DEPENDENT sections (GEOM metrics, DEF
    /// offsets, CONN positions) are measured at a deterministic reference
    /// type — the first (Ordinal) name of <paramref name="preferredTypeNames"/>
    /// intersected with the document's named types, or the document's
    /// first named type by default — inside a rolled-back transaction, so
    /// the user's current-type choice never shifts the content hash.
    /// </summary>
    /// <param name="familyDoc">Open family document (from
    /// <c>OpenDocumentFile</c> or <c>EditFamily</c> or the active family
    /// editor document).</param>
    /// <param name="preferredTypeNames">Optional preferred reference-type
    /// names (the verifier's type-set rule: a partially loaded embedded
    /// copy compares against a restricted file snapshot — pass the
    /// restriction set so both sides measure at the same intersection
    /// type). <c>null</c> = the document's first named type.</param>
    FamilySnapshot ExtractFromFamilyDocument(
        Document familyDoc,
        IReadOnlyCollection<string>? preferredTypeNames = null);

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

    /// <summary>
    /// Extract a <see cref="SystemFamilySnapshot"/> from a staged
    /// mini-project (.rvt) during database actualization (ADR-056).
    /// Type discovery: placed instances first (domain truth for placed
    /// categories); when nothing is placed (Phase-2 categories copied
    /// without placement) ALL types of the category are collected —
    /// the caller trims them to the catalog's authoritative type list.
    /// </summary>
    /// <param name="stagedDoc">Open staged mini-project document.</param>
    /// <param name="builtInCategory">The system category to extract.</param>
    SystemFamilySnapshot ExtractSystemCategoryFromStagedProject(
        Document stagedDoc,
        BuiltInCategory builtInCategory);

    /// <summary>
    /// Extract a single <see cref="SystemTypeSnapshot"/> (parameters,
    /// compound structure, routing preferences) for one system type in an
    /// open project document. Used by the system-type synchronizer
    /// (Issue #104) to read the reference data of a type from the catalog
    /// mini-project before writing it into the active project.
    /// </summary>
    /// <param name="projectDoc">Open project document (.rvt) containing the
    /// type.</param>
    /// <param name="typeId">Element id of the <c>ElementType</c> to extract.</param>
    SystemTypeSnapshot ExtractSingleSystemType(Document projectDoc, ElementId typeId);

    /// <summary>
    /// Extracts 3D tessellated geometry for EACH family type by iterating
    /// <c>FamilyManager.CurrentType</c> inside a Transaction+RollBack
    /// (I-03b). Type-dependent extrusions that return empty
    /// <c>GeometryElement</c> under one type become visible after
    /// <c>fm.CurrentType = targetType</c>. The document must already be
    /// open (caller responsible for <c>OpenDocumentFile</c>) — this method
    /// does NOT open or close the document.
    /// </summary>
    /// <param name="familyDoc">Open family document.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One <see cref="FamilyGeometryPerType"/> entry per type
    /// that produces non-empty geometry. Families with no types return
    /// a single entry with <c>TypeName = ""</c>.</returns>
    IReadOnlyList<FamilyGeometryPerType> ExtractGeometryPerType(
        Document familyDoc,
        CancellationToken ct = default);
}
