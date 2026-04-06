using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Снимок состояния элемента для отката при кнопке − (DecrementChainDepth).
/// Захватывается ДО операции + (ДО DisconnectAll, ДО перемещения).
/// Для FamilyInstance хранит полный Transform (Origin + BasisX/Y/Z) —
/// единственный надёжный способ восстановления ориентации.
/// </summary>
public sealed record ElementSnapshot
{
    public required ElementId ElementId { get; init; }
    public required bool IsMepCurve { get; init; }

    // FamilyInstance: полный Transform
    public XYZ? FiOrigin { get; init; }
    public XYZ? FiBasisX { get; init; }
    public XYZ? FiBasisY { get; init; }
    public XYZ? FiBasisZ { get; init; }

    // MEPCurve: начало/конец Location.Curve (только для Line-based: Pipe)
    public XYZ? CurveStart { get; init; }
    public XYZ? CurveEnd { get; init; }

    // Универсальная позиция: Origin первого коннектора (для FlexPipe и fallback)
    public XYZ? FirstConnectorOrigin { get; init; }
    public int FirstConnectorIndex { get; init; }

    // Размер
    public double ConnectorRadius { get; init; }
    public ElementId? FamilySymbolId { get; init; }

    // Per-connector радиусы (для многопортовых элементов с разными DN)
    public IReadOnlyDictionary<int, double> ConnectorRadii { get; init; } = new Dictionary<int, double>();

    // Соединения (из графа)
    public IReadOnlyList<ConnectionRecord> Connections { get; init; } = [];
}
