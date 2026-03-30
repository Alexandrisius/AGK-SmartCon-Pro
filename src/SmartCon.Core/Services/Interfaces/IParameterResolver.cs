using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Определяет каким параметром управляется размер коннектора и меняет его значение.
/// Реализация: SmartCon.Revit/Parameters/RevitParameterResolver.cs
/// </summary>
public interface IParameterResolver
{
    /// <summary>
    /// Параметр(ы), от которых зависит радиус коннектора.
    /// </summary>
    IReadOnlyList<ParameterDependency> GetConnectorRadiusDependencies(
        Document doc, ElementId elementId, int connectorIndex);

    /// <summary>
    /// Установить нужный радиус, меняя зависимые параметры.
    /// Возвращает true при успехе.
    /// </summary>
    bool TrySetConnectorRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits);
}
