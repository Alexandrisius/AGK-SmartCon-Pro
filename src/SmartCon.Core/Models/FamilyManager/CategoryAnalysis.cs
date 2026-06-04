using Autodesk.Revit.DB;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Результат анализа одной системной категории в активном проекте Revit.
/// Содержит ТОЛЬКО типы, реально размещённые в модели (WhereElementIsNotElementType()).
/// BuiltInCategory — value-type carrier (допустим в Core по I-09).
/// </summary>
public sealed record CategoryAnalysis(
    BuiltInCategory Category,
    string DisplayName,
    IReadOnlyList<SystemTypeInfo> Types)
{
    public int TypeCount => Types.Count;
}

/// <summary>
/// Информация об одном типе системного семейства (после фильтрации по размещённым).
/// Name — human-readable имя типа в Revit.
/// UniqueId — для последующего копирования через ElementTransformUtils.CopyElements.
/// </summary>
public sealed record SystemTypeInfo(
    string Name,
    string UniqueId);
