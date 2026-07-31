using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyRevitOperations
{
    SelectedElementsAnalysis PickSelectedElements();

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
