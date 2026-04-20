namespace SmartCon.Core.Math;

/// <summary>
/// Basic vector operations for alignment algorithms (ConnectorAligner).
/// All calculations in Internal Units (decimal feet, I-02).
/// Works with Vec3 (pure math, testable without Revit runtime).
/// XYZ <-> Vec3 conversion is in SmartCon.Revit/Extensions/Vec3Extensions.cs.
/// </summary>
public static class VectorUtils
{
    /// <summary>
    /// Double comparison tolerance (approx 1e-9, Revit standard).
    /// </summary>
    public const double Tolerance = Core.Tolerance.Default;

    /// <summary>
    /// Cross product a x b.
    /// </summary>
    public static Vec3 CrossProduct(Vec3 a, Vec3 b)
    {
        return new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
    }

    /// <summary>
    /// Dot product a . b.
    /// </summary>
    public static double DotProduct(Vec3 a, Vec3 b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    /// <summary>
    /// Vector length.
    /// </summary>
    public static double Length(Vec3 v)
    {
        return System.Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    /// <summary>
    /// Normalize a vector. Returns a unit vector of the same direction.
    /// Throws ArgumentException if length is approximately 0.
    /// </summary>
    public static Vec3 Normalize(Vec3 v)
    {
        var len = Length(v);

        if (len < Tolerance)
        {
            throw new ArgumentException("Cannot normalize a zero-length vector.", nameof(v));
        }

        return new Vec3(v.X / len, v.Y / len, v.Z / len);
    }

    /// <summary>
    /// Angle between two vectors in radians [0, PI].
    /// </summary>
    public static double AngleBetween(Vec3 a, Vec3 b)
    {
        var dot = DotProduct(a, b);
        var lenA = Length(a);
        var lenB = Length(b);

        if (lenA < Tolerance || lenB < Tolerance)
        {
            return 0.0;
        }

        var cosAngle = dot / (lenA * lenB);

        // Clamp to protect against rounding errors
        cosAngle = System.Math.Max(-1.0, System.Math.Min(1.0, cosAngle));

        return System.Math.Acos(cosAngle);
    }

    /// <summary>
    /// Angle between two vectors in a plane with the given normal.
    /// Returns signed angle [-PI, PI].
    /// Used to compute BasisX rotation angle (step 3 of ConnectorAligner).
    /// </summary>
    public static double AngleBetweenInPlane(Vec3 from, Vec3 to, Vec3 planeNormal)
    {
        var cross = CrossProduct(from, to);
        var dot = DotProduct(from, to);
        var sinComponent = DotProduct(cross, planeNormal);

        return System.Math.Atan2(sinComponent, dot);
    }

    /// <summary>
    /// Parallel check: vectors are co-directional or anti-parallel.
    /// </summary>
    public static bool IsParallel(Vec3 a, Vec3 b)
    {
        var cross = CrossProduct(a, b);
        return cross.LengthSquared < Tolerance * Tolerance;
    }

    /// <summary>
    /// Anti-parallel check: vectors point in strictly opposite directions.
    /// </summary>
    public static bool IsAntiParallel(Vec3 a, Vec3 b)
    {
        if (!IsParallel(a, b))
        {
            return false;
        }

        return DotProduct(a, b) < 0;
    }

    /// <summary>
    /// Co-directional check: vectors point in the same direction.
    /// </summary>
    public static bool IsCodirectional(Vec3 a, Vec3 b)
    {
        if (!IsParallel(a, b))
        {
            return false;
        }

        return DotProduct(a, b) > 0;
    }

    /// <summary>
    /// Finds a vector perpendicular to the given one.
    /// Used for 180-degree rotation when BasisZ is co-directional (step 2 of ConnectorAligner).
    /// </summary>
    public static Vec3 FindPerpendicularAxis(Vec3 v)
    {
        var normalized = Normalize(v);

        // Choose the vector least collinear with v
        var candidate = System.Math.Abs(normalized.X) < Core.Tolerance.CollinearityThreshold
            ? Vec3.BasisX
            : Vec3.BasisY;

        var perp = CrossProduct(normalized, candidate);
        return Normalize(perp);
    }

    /// <summary>
    /// Round angle to nearest multiple of stepDegrees.
    /// Input/output in radians. stepDegrees in degrees (15, 30, 45...).
    /// Used for BasisX snap to a "nice" angle (step 3 of ConnectorAligner).
    /// </summary>
    public static double RoundToNearestAngle(double angleRadians, double stepDegrees)
    {
        var stepRadians = stepDegrees * System.Math.PI / 180.0;

        if (stepRadians < Tolerance)
        {
            return angleRadians;
        }

        var steps = System.Math.Round(angleRadians / stepRadians);
        return steps * stepRadians;
    }

    /// <summary>
    /// Distance between two points.
    /// </summary>
    public static double DistanceTo(Vec3 a, Vec3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Check equality of two points/vectors with tolerance.
    /// </summary>
    public static bool IsAlmostEqual(Vec3 a, Vec3 b, double tolerance = Tolerance)
    {
        return DistanceTo(a, b) < tolerance;
    }

    /// <summary>
    /// Check whether a vector is zero (length &lt; Tolerance).
    /// </summary>
    public static bool IsZero(Vec3 v)
    {
        return v.LengthSquared < Tolerance * Tolerance;
    }

    [Obsolete("Use Vec3 operators instead")]
    public static Vec3 Subtract(Vec3 a, Vec3 b) => a - b;

    [Obsolete("Use Vec3 operators instead")]
    public static Vec3 Add(Vec3 a, Vec3 b) => a + b;

    [Obsolete("Use Vec3 operators instead")]
    public static Vec3 Multiply(Vec3 v, double scalar) => v * scalar;

    [Obsolete("Use Vec3 operators instead")]
    public static Vec3 Negate(Vec3 v) => -v;
}
