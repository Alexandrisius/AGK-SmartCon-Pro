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
    /// path. No temp staging.
    /// </summary>
    CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName,
        string managedRvtPath);
}
