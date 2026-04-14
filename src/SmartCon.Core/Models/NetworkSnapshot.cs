using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Snapshot of element state for rollback on the minus button (DecrementChainDepth).
/// Captured BEFORE the plus operation (BEFORE DisconnectAll, BEFORE move).
/// For FamilyInstance stores full Transform (Origin + BasisX/Y/Z) —
/// the only reliable way to restore orientation.
/// </summary>
public sealed record ElementSnapshot
{
    public required ElementId ElementId { get; init; }
    public required bool IsMepCurve { get; init; }

    // FamilyInstance: full Transform
    public XYZ? FiOrigin { get; init; }
    public XYZ? FiBasisX { get; init; }
    public XYZ? FiBasisY { get; init; }
    public XYZ? FiBasisZ { get; init; }

    // MEPCurve: start/end of Location.Curve (only for Line-based: Pipe)
    public XYZ? CurveStart { get; init; }
    public XYZ? CurveEnd { get; init; }

    // Universal position: Origin of first connector (for FlexPipe and fallback)
    public XYZ? FirstConnectorOrigin { get; init; }
    public int FirstConnectorIndex { get; init; }

    // Size
    public double ConnectorRadius { get; init; }
    public ElementId? FamilySymbolId { get; init; }

    // Per-connector radii (for multi-port elements with different DN)
    public IReadOnlyDictionary<int, double> ConnectorRadii { get; init; } = new Dictionary<int, double>();

    // Connections (from graph)
    public IReadOnlyList<ConnectionRecord> Connections { get; init; } = [];
}
