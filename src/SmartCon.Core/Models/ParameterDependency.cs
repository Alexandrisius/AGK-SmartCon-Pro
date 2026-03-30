using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Описание зависимости параметра коннектора от параметра семейства.
/// Используется в IParameterResolver для определения способа изменения радиуса.
/// </summary>
public sealed record ParameterDependency(
    BuiltInParameter? BuiltIn,
    string? SharedParamName,
    string? Formula,
    bool IsInstance
);
