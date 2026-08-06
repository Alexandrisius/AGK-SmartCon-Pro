using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyRevitOperations
{
    /// <summary>
    /// Interactive picker for "Импорт выделенных элементов". Returns
    /// <c>null</c> when the user cancels the pick (Esc) — a normal gesture
    /// the caller must not treat as an import error. An explicit pick is
    /// always the import intent: NO insulation-host filtering is applied
    /// (#181 scope correction — that filter exists only for
    /// <see cref="AnalyzeActiveProject"/> in SmartCon mini-projects).
    /// </summary>
    SelectedElementsAnalysis? PickSelectedElements();

    IReadOnlyList<CategoryAnalysis> AnalyzeActiveProject(Document activeDoc);

    /// <summary>
    /// v2.0.0: Create a clean mini-project with the specified system types
    /// and instances, then SaveAs directly to the supplied managed storage
    /// path. No temp staging. The staged file is marked as a SmartCon
    /// reference mini-project (Issue #188) BEFORE SaveAs so the marker
    /// persists in the saved file.
    /// </summary>
    /// <param name="catalogItemId">Owning catalog item for the mini-project
    /// marker when known (precomputed or existing id); null is allowed.</param>
    CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName,
        string managedRvtPath,
        string? catalogItemId = null);
}
