namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One parameter definition parsed from a Revit shared parameter file (ФОП).
/// Pure data carrier — the parser lives in Core and never touches Revit API.
/// </summary>
public sealed record SharedParameterEntry(
    Guid ParameterGuid,
    string Name,
    string DataType,
    string? DataCategory,
    string? GroupName,
    string? Description);
