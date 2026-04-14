using Autodesk.Revit.DB;
using SmartCon.Core.Math;

namespace SmartCon.Core.Models;

/// <summary>
/// Immutable snapshot of connector state at the time of selection.
/// Do not store between transactions — recreate from actual Connector (I-05).
/// All dimensions in Internal Units (decimal feet, I-02).
/// </summary>
public sealed record ConnectorProxy
{
    public required ElementId OwnerElementId { get; init; }
    public required int ConnectorIndex { get; init; }
    public required XYZ Origin { get; init; }
    public required XYZ BasisZ { get; init; }
    public required XYZ BasisX { get; init; }
    public required double Radius { get; init; }
    public required Domain Domain { get; init; }
    public required ConnectionTypeCode ConnectionTypeCode { get; init; }
    public required bool IsFree { get; init; }

    /// <summary>
    /// Origin as Vec3 for Core algorithms (ConnectorAligner, VectorUtils).
    /// Conversion without Revit runtime: access to .X/.Y/.Z is compile-time (I-09).
    /// </summary>
    public Vec3 OriginVec3 => new(Origin.X, Origin.Y, Origin.Z);

    /// <summary>BasisZ as Vec3.</summary>
    public Vec3 BasisZVec3 => new(BasisZ.X, BasisZ.Y, BasisZ.Z);

    /// <summary>BasisX as Vec3.</summary>
    public Vec3 BasisXVec3 => new(BasisX.X, BasisX.Y, BasisX.Z);
}
