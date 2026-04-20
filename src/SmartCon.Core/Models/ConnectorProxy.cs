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
    /// <summary>Owning element ID.</summary>
    public required ElementId OwnerElementId { get; init; }

    /// <summary>Index of this connector within the owning element.</summary>
    public required int ConnectorIndex { get; init; }

    /// <summary>Connector origin point (internal units).</summary>
    public required XYZ Origin { get; init; }

    /// <summary>Connector normal direction (BasisZ).</summary>
    public required XYZ BasisZ { get; init; }

    /// <summary>Connector reference direction (BasisX).</summary>
    public required XYZ BasisX { get; init; }

    /// <summary>Connector radius (internal units).</summary>
    public required double Radius { get; init; }

    /// <summary>MEP domain (piping, duct, etc.).</summary>
    public required Domain Domain { get; init; }

    /// <summary>Connection type code parsed from Description.</summary>
    public required ConnectionTypeCode ConnectionTypeCode { get; init; }

    /// <summary>Whether this connector is unconnected.</summary>
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
