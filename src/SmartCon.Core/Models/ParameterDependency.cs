using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Description of connector parameter dependency on a family parameter.
/// Used in IParameterResolver to determine how to change the radius.
/// IsInstance refers to the root parameter (RootParamName if set, otherwise DirectParamName).
/// </summary>
public sealed record ParameterDependency(
    BuiltInParameter? BuiltIn,       // for MEP Curve: RBS_PIPE_DIAMETER_PARAM
    string? SharedParamName,         // legacy
    string? Formula,                 // directParam formula: "diameter / 2"
    bool IsInstance,                 // false = type parameter (requires ChangeTypeId)
    string? DirectParamName = null,  // FamilyParameter directly linked to CONNECTOR_RADIUS
    string? RootParamName = null,    // root parameter (appears in size_lookup / formula)
    bool IsDiameter = false          // true = parameter stores diameter, not radius
);
