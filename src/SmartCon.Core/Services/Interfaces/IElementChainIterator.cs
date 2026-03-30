using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Обход цепочек соединённых MEP-элементов (BFS через AllRefs).
/// Реализация: SmartCon.Revit/Selection/ElementChainIterator.cs
/// </summary>
public interface IElementChainIterator
{
    /// <summary>
    /// Строит ConnectionGraph начиная с элемента.
    /// stopAtElements — элементы, на которых BFS останавливается (не включаются в граф).
    /// </summary>
    ConnectionGraph BuildGraph(Document doc, ElementId startElementId,
        IReadOnlySet<ElementId>? stopAtElements = null);

    /// <summary>
    /// Свободные коннекторы на границах цепочки (IsFree == true).
    /// </summary>
    IReadOnlyList<ConnectorProxy> GetChainEndConnectors(
        Document doc, ConnectionGraph graph);
}
