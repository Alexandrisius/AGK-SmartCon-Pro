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
    /// Minimum projection length of a global axis onto the connector plane for it
    /// to be usable as an orientation reference (below this the axis is too close
    /// to the connector Z — its projection is degenerate).
    /// </summary>
    private const double MinReferenceProjection = 0.3;

    /// <summary>
    /// Select the global axis (X/Y/Z) used as the orientation reference for snap
    /// operations on a connector with the given BasisZ. Priority goes to global Y
    /// (legacy GlobalYSnap behaviour — horizontal networks snap to it naturally);
    /// when its projection onto the connector plane is degenerate (vertical
    /// connectors), the best-projected of Z/X wins. One reference axis for both
    /// attach (BasisX snap) and editor rotation (global snap) — this keeps every
    /// element of the chain on ONE global angular grid, so users always see
    /// "clean" angles (0/15/30/45…) instead of arbitrary parent-dependent ones.
    /// </summary>
    public static Vec3 SelectGlobalReferenceAxis(Vec3 connectorBasisZ)
    {
        var axisNorm = VectorUtils.Normalize(connectorBasisZ);

        var projY = ProjectOntoPlane(Vec3.BasisY, axisNorm);
        if (projY.LengthSquared >= MinReferenceProjection * MinReferenceProjection)
            return Vec3.BasisY;

        var projZ = ProjectOntoPlane(Vec3.BasisZ, axisNorm);
        var projX = ProjectOntoPlane(Vec3.BasisX, axisNorm);

        return projZ.LengthSquared >= projX.LengthSquared ? Vec3.BasisZ : Vec3.BasisX;
    }

    /// <summary>
    /// Select the reference direction for "upright" orientation of a connector
    /// whose Z-axis (rotation axis) is <paramref name="axisZ"/>: global +Z (world
    /// up) whenever its projection onto the plane ⊥ axisZ is usable; when the
    /// axis is vertical (riser / plan-placed family — rotation around it cannot
    /// change uprightness), falls back to global Y, then X (horizontal grid).
    /// </summary>
    public static Vec3 SelectUprightReference(Vec3 axisZ)
    {
        var axisNorm = VectorUtils.Normalize(axisZ);

        var projUp = ProjectOntoPlane(Vec3.BasisZ, axisNorm);
        if (projUp.LengthSquared >= MinReferenceProjection * MinReferenceProjection)
            return Vec3.BasisZ;

        var projY = ProjectOntoPlane(Vec3.BasisY, axisNorm);
        if (projY.LengthSquared >= MinReferenceProjection * MinReferenceProjection)
            return Vec3.BasisY;

        return Vec3.BasisX;
    }

    /// <summary>
    /// Compute a rotation around the connector Z-axis that makes the element
    /// upright: the FREE CONNECTOR's BasisY (the family's "height" direction —
    /// Tammik: "the height is relative to the y axis of the coordinate system of
    /// the connector") lands exactly on the projection of the upright reference
    /// (<see cref="SelectUprightReference"/>) onto the connector plane.
    ///
    /// Why the connector BasisY and not FamilyInstance.GetTransform().BasisY:
    /// the connector BasisY always lies in the rotation plane (⊥ its own BasisZ),
    /// so its projection never degenerates — the family transform basis can be
    /// parallel to the rotation axis for some families (e.g. Kan-therm elbows),
    /// which silently disabled the reset, and it also ignores FacingFlipped /
    /// HandFlipped. The directional up target is unique — the element can never
    /// land upside down (no 0°/180° ambiguity).
    ///
    /// Sign convention: right-hand rule around the axis — RotateElement(axis, θ)
    /// brings 'from' onto 'to' with θ = signed angle from→to (Tammik cable-tray
    /// pattern: Location.Rotate(axis, BasisY.AngleOnPlaneTo(target, axis))). Our
    /// AngleBetweenInPlane uses the same convention.
    /// Returns null when already upright or the configuration is degenerate.
    /// </summary>
    public static RotationStep? ComputeUprightRotation(Vec3 axisZ, Vec3 connectorBasisY)
    {
        var axisNorm = VectorUtils.Normalize(axisZ);
        var referenceAxis = SelectUprightReference(axisNorm);
        var projTarget = ProjectOntoPlane(referenceAxis, axisNorm);
        var projCurrent = ProjectOntoPlane(connectorBasisY, axisNorm);

        if (projTarget.LengthSquared < RadiusComparison * RadiusComparison)
            return null;
        if (projCurrent.LengthSquared < RadiusComparison * RadiusComparison)
            return null;

        var fromNorm = VectorUtils.Normalize(projCurrent);
        var toNorm = VectorUtils.Normalize(projTarget);

        var delta = VectorUtils.AngleBetweenInPlane(fromNorm, toNorm, axisNorm);

        if (System.Math.Abs(delta) < VectorUtils.Tolerance)
            return null;

        return new RotationStep(axisNorm, delta);
    }

    /// <summary>
    /// Compute a rotation around the connector Z-axis that snaps the element's
    /// basis vector to a multiple of <paramref name="stepDegrees"/> relative to
    /// the projection of the selected global reference axis (<see cref="SelectGlobalReferenceAxis"/>)
    /// onto the connector plane. Pass a tiny step (e.g. 0.01°) for an exact
    /// "zero the angle" rotation. Returns null when already snapped or the
    /// configuration is degenerate.
    /// </summary>
    public static RotationStep? ComputeGlobalAxisSnap(
        Vec3 connectorBasisZ,
        Vec3 elementBasis,
        double stepDegrees)
    {
        var referenceAxis = SelectGlobalReferenceAxis(connectorBasisZ);
        var projTarget = ProjectOntoPlane(referenceAxis, connectorBasisZ);
        var projCurrent = ProjectOntoPlane(elementBasis, connectorBasisZ);

        if (projTarget.LengthSquared < RadiusComparison * RadiusComparison)
            return null;
        if (projCurrent.LengthSquared < RadiusComparison * RadiusComparison)
            return null;

        var fromNorm = VectorUtils.Normalize(projCurrent);
        var toNorm = VectorUtils.Normalize(projTarget);

        var currentAngle = VectorUtils.AngleBetweenInPlane(fromNorm, toNorm, connectorBasisZ);
        var snappedAngle = VectorUtils.RoundToNearestAngle(currentAngle, stepDegrees);

        // Right-hand rule: RotateElement(axis, θ) rotates 'from' TOWARDS 'to',
        // shrinking the signed from→to angle by θ — so θ = current - snapped.
        var delta = currentAngle - snappedAngle;

        if (System.Math.Abs(delta) < VectorUtils.Tolerance)
            return null;

        return new RotationStep(VectorUtils.Normalize(connectorBasisZ), delta);
    }

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

        // Step 3: snap BasisX to a "nice" angle on the global grid
        // planeNormal = -staticBasisZ (anti-parallel direction — connector plane normal)
        var basisXSnap = ComputeBasisXSnap(rotatedDynamicBasisX, staticBasisZ);

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
    /// Step 3: snap BasisX to the nearest "nice" angle on the GLOBAL angular grid —
    /// the reference is the projection of the selected global axis
    /// (<see cref="SelectGlobalReferenceAxis"/>) onto the connector plane, NOT the
    /// parent's BasisX. Parent-BasisX is arbitrary (family connector orientation),
    /// which used to put every element on a different "dirty" angle; the global
    /// grid gives clean 15°-multiple angles everywhere and matches the editor
    /// rotation snap. Skipped if staticBasisZ is parallel to global Y axis —
    /// rotation around Y tilts the element out of the horizontal plane.
    /// </summary>
    internal static RotationStep? ComputeBasisXSnap(
        Vec3 dynamicBasisX, Vec3 staticBasisZ)
    {
        // If rotation axis (staticBasisZ) is parallel to global Y — skip.
        // Rotation around Y tilts fittings (elbows, tees) out of their natural plane.
        var bzNorm = VectorUtils.Normalize(staticBasisZ);
        var dotWithY = System.Math.Abs(VectorUtils.DotProduct(bzNorm, Vec3.BasisY));
        if (dotWithY > Tolerance.AxisParallelDot)
            return null;

        var referenceAxis = SelectGlobalReferenceAxis(staticBasisZ);
        var projTarget = ProjectOntoPlane(referenceAxis, staticBasisZ);
        if (projTarget.LengthSquared < RadiusComparison * RadiusComparison)
            return null;

        var targetNorm = VectorUtils.Normalize(projTarget);
        var currentAngle = VectorUtils.AngleBetweenInPlane(dynamicBasisX, targetNorm, staticBasisZ);
        var snappedAngle = VectorUtils.RoundToNearestAngle(currentAngle, BasisXSnapStepDegrees);

        // Right-hand rule: RotateElement(axis, θ) shrinks the signed from→to
        // angle by θ — so the snap delta is (current - snapped).
        var deltaAngle = currentAngle - snappedAngle;

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
    /// of the element lands on the global angular grid with quantization by 15-degree
    /// steps. Kept for backward compatibility — delegates to the generalized
    /// <see cref="ComputeGlobalAxisSnap"/> (global reference axis with Y priority).
    /// </summary>
    /// <param name="connectorBasisZ">Connector Z-axis (rotation axis)</param>
    /// <param name="elementBasisY">Current element BasisY in world coordinates</param>
    /// <param name="rotationCenter">Rotation center (usually static connector origin)</param>
    public static RotationStep? ComputeGlobalYAlignmentSnap(
        Vec3 connectorBasisZ,
        Vec3 elementBasisY,
        Vec3 rotationCenter)
    {
        return ComputeGlobalAxisSnap(connectorBasisZ, elementBasisY, BasisXSnapStepDegrees);
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
