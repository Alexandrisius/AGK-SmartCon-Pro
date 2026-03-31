using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Описание зависимости параметра коннектора от параметра семейства.
/// Используется в IParameterResolver для определения способа изменения радиуса.
/// IsInstance относится к корневому параметру (RootParamName если задан, иначе DirectParamName).
/// </summary>
public sealed record ParameterDependency(
    BuiltInParameter? BuiltIn,       // для MEP Curve: RBS_PIPE_DIAMETER_PARAM
    string? SharedParamName,         // legacy
    string? Formula,                 // формула directParam: "diameter / 2"
    bool IsInstance,                 // false = параметр типа (нужен ChangeTypeId)
    string? DirectParamName = null,  // FamilyParameter напрямую связанный с CONNECTOR_RADIUS
    string? RootParamName   = null   // корневой параметр (фигурирует в size_lookup / формуле)
);
