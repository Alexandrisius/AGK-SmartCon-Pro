using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Работа с таблицами поиска семейств (FamilySizeTableManager).
/// Реализация: SmartCon.Revit/Parameters/RevitLookupTableService.cs
/// </summary>
public interface ILookupTableService
{
    /// <summary>
    /// Существует ли в таблице поиска строка с данным радиусом?
    /// </summary>
    bool ConnectorRadiusExistsInTable(Document doc, ElementId familySymbolId,
        double radiusInternalUnits);

    /// <summary>
    /// Ближайший существующий в таблице радиус.
    /// </summary>
    double GetNearestAvailableRadius(Document doc, ElementId familySymbolId,
        double targetRadiusInternalUnits);
}
