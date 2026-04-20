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
    /// <summary>Element ID of the snapshot target.</summary>
    public required ElementId ElementId { get; init; }

    /// <summary>True for MEPCurve (Pipe), false for FamilyInstance.</summary>
    public required bool IsMepCurve { get; init; }

    /// <summary>FamilyInstance transform origin (null for MEPCurve).</summary>
    public XYZ? FiOrigin { get; init; }

    /// <summary>FamilyInstance transform BasisX.</summary>
    public XYZ? FiBasisX { get; init; }

    /// <summary>FamilyInstance transform BasisY.</summary>
    public XYZ? FiBasisY { get; init; }

    /// <summary>FamilyInstance transform BasisZ.</summary>
    public XYZ? FiBasisZ { get; init; }

    /// <summary>Start point of Location.Curve (MEPCurve only).</summary>
    public XYZ? CurveStart { get; init; }

    /// <summary>End point of Location.Curve (MEPCurve only).</summary>
    public XYZ? CurveEnd { get; init; }

    /// <summary>Origin of the first connector (universal fallback for FlexPipe).</summary>
    public XYZ? FirstConnectorOrigin { get; init; }

    /// <summary>Index of the first connector used for fallback positioning.</summary>
    public int FirstConnectorIndex { get; init; }

    /// <summary>Primary connector radius at snapshot time (internal units).</summary>
    public double ConnectorRadius { get; init; }

    /// <summary>FamilySymbol ElementId for type restoration.</summary>
    public ElementId? FamilySymbolId { get; init; }

    /// <summary>Per-connector radii for multi-port elements.</summary>
    public IReadOnlyDictionary<int, double> ConnectorRadii { get; init; } = new Dictionary<int, double>();

    /// <summary>Connection records for reconnect on rollback.</summary>
    public IReadOnlyList<ConnectionRecord> Connections { get; init; } = [];
}
