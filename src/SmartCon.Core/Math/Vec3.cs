namespace SmartCon.Core.Math;

/// <summary>
/// Lightweight immutable 3D vector for pure math in Core.
/// Used instead of Revit XYZ in algorithms (VectorUtils, ConnectorAligner),
/// so Core remains fully testable without Revit runtime.
/// XYZ <-> Vec3 conversion is in SmartCon.Revit/Extensions/XYZExtensions.cs.
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    /// <summary>Zero vector.</summary>
    public static readonly Vec3 Zero = new(0, 0, 0);

    /// <summary>Unit vector along X axis.</summary>
    public static readonly Vec3 BasisX = new(1, 0, 0);

    /// <summary>Unit vector along Y axis.</summary>
    public static readonly Vec3 BasisY = new(0, 1, 0);

    /// <summary>Unit vector along Z axis.</summary>
    public static readonly Vec3 BasisZ = new(0, 0, 1);

    /// <summary>Squared length of the vector.</summary>
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 v) => new(-v.X, -v.Y, -v.Z);
    public static Vec3 operator *(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vec3 operator *(double s, Vec3 v) => new(v.X * s, v.Y * s, v.Z * s);

    public override string ToString() => $"({X:F6}, {Y:F6}, {Z:F6})";
}
