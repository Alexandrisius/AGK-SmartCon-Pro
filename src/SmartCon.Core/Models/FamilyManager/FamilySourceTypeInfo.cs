namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Core-level DTO for system-family source types. v2.0.0 redesign:
/// this record lives in <c>SmartCon.Core</c> so the
/// <see cref="FamilyBatchImportItem"/> public API does not pull in
/// <c>Autodesk.Revit.DB.BuiltInCategory</c>. The Revit-aware
/// <c>SelectedSystemType</c> stays in the VM layer and is mapped into
/// and out of <see cref="FamilySourceTypeInfo"/> at the
/// Revit/managed boundary (see <c>FamilyManagerMainViewModel.Import.cs</c>
/// and <c>SystemFamilyImportOrchestrator.cs</c>).
/// </summary>
/// <param name="UniqueId">Revit unique id of the type element. Used by
/// the system-family extractor to identify the type inside the
/// managed <c>.rvt</c>.</param>
/// <param name="Name">Display name of the type.</param>
/// <param name="CategoryName">Display name of the parent category (e.g.
/// "OST_PipeFitting").</param>
/// <param name="CategoryId">Numeric <c>BuiltInCategory</c> integer cast to
/// <see cref="int"/>. The orchestrator never touches the
/// <c>Autodesk.Revit.DB</c> enum from <c>SmartCon.Core</c>, so the
/// ordinal is carried across the boundary as a plain integer.</param>
public sealed record FamilySourceTypeInfo(
    string UniqueId,
    string Name,
    string CategoryName,
    int CategoryId);
