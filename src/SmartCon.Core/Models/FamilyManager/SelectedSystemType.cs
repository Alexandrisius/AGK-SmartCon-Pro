using Autodesk.Revit.DB;

namespace SmartCon.Core.Models.FamilyManager;

public sealed record SelectedSystemType(
    string UniqueId,
    string Name,
    string CategoryName,
    BuiltInCategory Category);
