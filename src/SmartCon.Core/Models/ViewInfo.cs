using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

public sealed record ViewInfo
{
    public string Name { get; init; } = string.Empty;
    public ElementId Id { get; init; } = ElementId.InvalidElementId;
    public string ViewType { get; init; } = string.Empty;
}
