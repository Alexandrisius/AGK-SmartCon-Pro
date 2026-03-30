namespace SmartCon.Core.Math;

/// <summary>
/// Лёгкий иммутабельный 3D-вектор для чистой математики в Core.
/// Используется вместо Revit XYZ в алгоритмах (VectorUtils, ConnectorAligner),
/// чтобы Core оставался полностью тестируемым без Revit runtime.
/// Конвертация XYZ ↔ Vec3 — в SmartCon.Revit/Extensions/XYZExtensions.cs.
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 BasisX = new(1, 0, 0);
    public static readonly Vec3 BasisY = new(0, 1, 0);
    public static readonly Vec3 BasisZ = new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 v) => new(-v.X, -v.Y, -v.Z);
    public static Vec3 operator *(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vec3 operator *(double s, Vec3 v) => new(v.X * s, v.Y * s, v.Z * s);

    public override string ToString() => $"({X:F6}, {Y:F6}, {Z:F6})";
}
