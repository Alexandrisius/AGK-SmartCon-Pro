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

    /// <summary>
    /// Подбирает типоразмер фитинга-переходника так, чтобы:
    /// 1. Коннектор со стороны статического элемента (staticConnIdx) совпадал точно с staticRadius.
    /// 2. Среди подходящих типов — коннектор динамической стороны (dynConnIdx) был максимально близок к dynRadius.
    /// Если типа с точным static нет — применяет тип с минимальным отклонением static (fallback).
    /// Возвращает: StaticExact=true если нашли точное совпадение; AchievedDynRadius — фактический
    /// радиус dynConn после смены типа (может отличаться от dynRadius).
    /// Вызывать ВНУТРИ транзакции.
    /// </summary>
    (bool StaticExact, double AchievedDynRadius) TrySetFittingTypeForPair(
        Document doc, ElementId fittingId,
        int staticConnIdx, double staticRadius,
        int dynConnIdx,    double dynRadius);
}
