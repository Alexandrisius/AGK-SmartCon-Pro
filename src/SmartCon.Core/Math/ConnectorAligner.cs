using static SmartCon.Core.Tolerance;

namespace SmartCon.Core.Math;

/// <summary>
/// Alignment algorithm for two connectors (pure math on Vec3).
/// Computes a set of transforms (offset + rotations) that the Revit layer
/// applies via ElementTransformUtils.MoveElement / RotateElement.
/// 
/// Algorithm steps (docs/pipeconnect/algorithms.md):
/// 1. Move: align Origins
/// 2. Rotate BasisZ: make anti-parallel (connectors face each other)
/// 3. Snap BasisX: round angle to nearest multiple of 15 degrees ("nice" angle)
/// 4. Position correction: after rotations Origin may have shifted -> recalculate in Revit layer
/// </summary>
public static class ConnectorAligner
{
    /// <summary>
    /// BasisX snap step in degrees. Covers 0, 15, 30, 45, 60, 75, 90...
    /// </summary>
    public const double BasisXSnapStepDegrees = 15.0;

    /// <summary>
    /// Compute a set of transforms to align the dynamic connector to the static connector.
    /// </summary>
    /// <param name="staticOrigin">Static connector origin</param>
    /// <param name="staticBasisZ">Static connector BasisZ (direction)</param>
    /// <param name="staticBasisX">Static connector BasisX (orientation)</param>
    /// <param name="dynamicBasisZ">Dynamic connector BasisZ</param>
    /// <param name="dynamicBasisX">Dynamic connector BasisX</param>
    /// <param name="dynamicOrigin">Dynamic connector origin</param>
    public static AlignmentResult ComputeAlignment(
        Vec3 staticOrigin, Vec3 staticBasisZ, Vec3 staticBasisX,
        Vec3 dynamicOrigin, Vec3 dynamicBasisZ, Vec3 dynamicBasisX)
    {
        // Step 1: move vector
        var initialOffset = staticOrigin - dynamicOrigin;

        // Step 2: rotate BasisZ for anti-parallelism
        var basisZRotation = ComputeBasisZRotation(dynamicBasisZ, staticBasisZ);

        // If BasisZ was rotated, dynamicBasisX must also be rotated
        var rotatedDynamicBasisX = basisZRotation is not null
            ? RotateVector(dynamicBasisX, basisZRotation.Axis, basisZRotation.AngleRadians)
            : dynamicBasisX;

        // Step 3: snap BasisX to a "nice" angle
        // planeNormal = -staticBasisZ (anti-parallel direction — connector plane normal)
        var basisXSnap = ComputeBasisXSnap(rotatedDynamicBasisX, staticBasisX, staticBasisZ);

        return new AlignmentResult
        {
            InitialOffset = initialOffset,
            BasisZRotation = basisZRotation,
            BasisXSnap = basisXSnap,
            RotationCenter = staticOrigin
        };
    }

    /// <summary>
    /// Step 2: compute rotation for BasisZ anti-parallelism.
    /// Target vector: targetZ = -static.BasisZ.
    /// </summary>
    internal static RotationStep? ComputeBasisZRotation(Vec3 dynamicBasisZ, Vec3 staticBasisZ)
    {
        var targetZ = -staticBasisZ;
        var angle = VectorUtils.AngleBetween(dynamicBasisZ, targetZ);

        // Already anti-parallel
        if (angle < VectorUtils.Tolerance)
        {
            return null;
        }

        Vec3 axis;

        // Co-directional (angle approx PI between dynamic and target, i.e. dynamic approx static)
        if (System.Math.Abs(angle - System.Math.PI) < VectorUtils.Tolerance)
        {
            axis = VectorUtils.FindPerpendicularAxis(dynamicBasisZ);
            return new RotationStep(axis, System.Math.PI);
        }

        // General case: axis = cross(dynamic, target), normalized
        var cross = VectorUtils.CrossProduct(dynamicBasisZ, targetZ);
        axis = VectorUtils.Normalize(cross);

        return new RotationStep(axis, angle);
    }

    /// <summary>
    /// Step 3: snap BasisX to the nearest "nice" angle.
    /// Skipped if staticBasisZ is parallel to global Y axis — in that case
    /// rotation around Y tilts the element out of the horizontal plane (visual artifact).
    /// </summary>
    internal static RotationStep? ComputeBasisXSnap(
        Vec3 dynamicBasisX, Vec3 staticBasisX, Vec3 staticBasisZ)
    {
        // If rotation axis (staticBasisZ) is parallel to global Y — skip.
        // Rotation around Y tilts fittings (elbows, tees) out of their natural plane.
        var bzNorm = VectorUtils.Normalize(staticBasisZ);
        var dotWithY = System.Math.Abs(VectorUtils.DotProduct(bzNorm, Vec3.BasisY));
        if (dotWithY > Tolerance.AxisParallelDot)
            return null;

        var currentAngle = VectorUtils.AngleBetweenInPlane(dynamicBasisX, staticBasisX, staticBasisZ);
        var snappedAngle = VectorUtils.RoundToNearestAngle(currentAngle, BasisXSnapStepDegrees);
        var deltaAngle = snappedAngle - currentAngle;

        if (System.Math.Abs(deltaAngle) < VectorUtils.Tolerance)
        {
            return null;
        }

        // Rotation axis = staticBasisZ (connector plane normal)
        return new RotationStep(VectorUtils.Normalize(staticBasisZ), deltaAngle);
    }

    /// <summary>
    /// Rotate a vector around an arbitrary axis by a given angle (Rodrigues' formula).
    /// Used to predict dynamicBasisX position after BasisZ rotation.
    /// </summary>
    internal static Vec3 RotateVector(Vec3 v, Vec3 axis, double angleRadians)
    {
        var k = VectorUtils.Normalize(axis);
        var cosA = System.Math.Cos(angleRadians);
        var sinA = System.Math.Sin(angleRadians);

        // Rodrigues' rotation formula: v' = v*cos(a) + (k×v)*sin(a) + k*(k·v)*(1-cos(a))
        var kCrossV = VectorUtils.CrossProduct(k, v);
        var kDotV = VectorUtils.DotProduct(k, v);

        return v * cosA + kCrossV * sinA + k * (kDotV * (1.0 - cosA));
    }

    /// <summary>
    /// Computes rotation around the connector Z-axis (connectorBasisZ) so that BasisY
    /// of the element coincides with (or is closest to) global Y axis (0,1,0)
    /// with quantization by 15-degree steps.
    ///
    /// Algorithm:
    /// 1. Project current elementBasisY onto the plane perpendicular to connectorBasisZ.
    /// 2. Project global Y onto the same plane.
    /// 3. Compute angle from current projection to nearest 15-degree multiple relative to globalY projection.
    ///
    /// Returns null if the configuration is degenerate (BasisZ parallel to global Y).
    /// </summary>
    /// <param name="connectorBasisZ">Connector Z-axis (rotation axis)</param>
    /// <param name="elementBasisY">Current element BasisY in world coordinates</param>
    /// <param name="rotationCenter">Rotation center (usually static connector origin)</param>
    public static RotationStep? ComputeGlobalYAlignmentSnap(
        Vec3 connectorBasisZ,
        Vec3 elementBasisY,
        Vec3 rotationCenter)
    {
        var globalY = Vec3.BasisY; // (0, 1, 0)

        // Project element BasisY onto the plane perpendicular to connectorBasisZ
        var projCurrent = ProjectOntoPlane(elementBasisY, connectorBasisZ);
        var projTargetY = ProjectOntoPlane(globalY, connectorBasisZ);

        // If global Y projection degenerates (BasisZ parallel to Y) — no alignment possible
        if (projTargetY.LengthSquared < RadiusComparison * RadiusComparison)
            return null;

        if (projCurrent.LengthSquared < RadiusComparison * RadiusComparison)
            return null;

        var fromNorm = VectorUtils.Normalize(projCurrent);
        var toNorm = VectorUtils.Normalize(projTargetY);

        // Angle from current BasisY to globalY in the plane (-PI, PI]
        var currentAngle = VectorUtils.AngleBetweenInPlane(fromNorm, toNorm, connectorBasisZ);

        // Round to nearest 15-degree multiple
        var snappedAngle = VectorUtils.RoundToNearestAngle(currentAngle, BasisXSnapStepDegrees);
        var delta = snappedAngle - currentAngle;

        if (System.Math.Abs(delta) < VectorUtils.Tolerance)
            return null;

        return new RotationStep(VectorUtils.Normalize(connectorBasisZ), delta);
    }

    /// <summary>
    /// Project vector v onto a plane with normal n (v minus its component along n).
    /// </summary>
    private static Vec3 ProjectOntoPlane(Vec3 v, Vec3 n)
    {
        var nLenSq = n.LengthSquared;
        if (nLenSq < VectorUtils.Tolerance * VectorUtils.Tolerance) return v;
        var nUnit = VectorUtils.Normalize(n);
        var dot = VectorUtils.DotProduct(v, nUnit);
        return v - nUnit * dot;
    }
}
